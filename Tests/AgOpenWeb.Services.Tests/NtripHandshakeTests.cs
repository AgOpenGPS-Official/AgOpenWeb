using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// NTRIP handshake and reconnect (AgOpenGPS #1219): only a real acceptance counts as
/// connected, rejections say why, and a user disconnect stops the retry loop.
/// </summary>
[TestFixture]
[NonParallelizable]
public class NtripHandshakeTests
{
    // ── Reply classification ─────────────────────────────────────────────

    [TestCase("ICY 200 OK", NtripReply.Accepted)]
    [TestCase("HTTP/1.1 200 OK\r\nContent-Type: gnss/data", NtripReply.Accepted)]
    [TestCase("HTTP/1.0 200 OK", NtripReply.Accepted)]
    [TestCase("SOURCETABLE 200 OK\r\nServer: NTRIP Caster", NtripReply.Rejected)]
    [TestCase("HTTP/1.1 401 Unauthorized", NtripReply.Rejected)]
    [TestCase("HTTP/1.1 404 Not Found", NtripReply.Rejected)]
    [TestCase("ERROR - Bad Password", NtripReply.Rejected)]
    [TestCase("HTTP/1.1 503 Service Unavailable", NtripReply.RejectedRetry)]
    [TestCase("garbage", NtripReply.RejectedRetry)]
    public void Classify(string header, NtripReply expected)
    {
        Assert.That(NtripResponse.Classify(header).Reply, Is.EqualTo(expected));
    }

    [Test]
    public void SourceTable_IsReportedAsAMissingMountPoint()
    {
        Assert.That(NtripResponse.Classify("SOURCETABLE 200 OK").Reason, Does.Contain("Mount point not found"));
    }

    // ── Fake caster ──────────────────────────────────────────────────────

    /// <summary>Loopback caster: for each connection, reads the request and runs the script.</summary>
    private sealed class FakeCaster : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Accepted;

        public FakeCaster(Func<NetworkStream, Task> script)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch { return; }
                    Interlocked.Increment(ref Accepted);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var buf = new byte[2048];
                                _ = await stream.ReadAsync(buf, 0, buf.Length);
                                await script(stream);
                            }
                            catch { /* client went away */ }
                        }
                    });
                }
            });
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }

    private static async Task Send(NetworkStream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b, 0, b.Length);
        await s.FlushAsync();
    }

    private static (NtripClientService svc, ConcurrentQueue<NtripConnectionEventArgs> events) Client()
    {
        var svc = new NtripClientService(NSubstitute.Substitute.For<IGpsService>(),
            NullLogger<NtripClientService>.Instance);
        var events = new ConcurrentQueue<NtripConnectionEventArgs>();
        svc.ConnectionStatusChanged += (_, e) => events.Enqueue(e);
        return (svc, events);
    }

    private static NtripConfiguration Config(int port) => new()
    {
        CasterAddress = "127.0.0.1", CasterPort = port, MountPoint = "M",
        Username = "u", Password = "p", SubnetAddress = "127.0.0", UdpForwardPort = 0,
        GgaIntervalSeconds = 0,
    };

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    [Test]
    public async Task Connected_IsRaisedOnlyAfterTheCasterAccepts()
    {
        using var caster = new FakeCaster(async s =>
        {
            await Task.Delay(400);
            await Send(s, "ICY 200 OK\r\n");
            await Send(s, "\xD3\x00\x13rtcm");
            await Task.Delay(3000);
        });
        var (svc, events) = Client();
        await svc.ConnectAsync(Config(caster.Port));

        Assert.That(svc.IsConnected, Is.False, "request sent, reply not in yet");
        Assert.That(events.Any(e => e.IsConnected), Is.False);
        Assert.That(svc.IsActive, Is.True);

        Assert.That(WaitFor(() => svc.IsConnected, 2000), Is.True);
        Assert.That(events.Count(e => e.IsConnected), Is.EqualTo(1));
        svc.Dispose();
    }

    [Test]
    public async Task SourceTable_IsARejection_WithoutRetrying()
    {
        using var caster = new FakeCaster(async s =>
        {
            await Send(s, "SOURCETABLE 200 OK\r\nServer: Test\r\n\r\nSTR;OTHER;...\r\nENDSOURCETABLE\r\n");
        });
        var (svc, events) = Client();
        await svc.ConnectAsync(Config(caster.Port));

        Assert.That(WaitFor(() => events.Any(e => e.Message?.StartsWith("Rejected") == true), 2000), Is.True);
        Assert.That(events.Last().Message, Does.Contain("Mount point not found"));
        Thread.Sleep(1800); // past the first 1 s backoff
        Assert.That(caster.Accepted, Is.EqualTo(1), "a missing mount point isn't retried");
        Assert.That(svc.IsConnected, Is.False);
        Assert.That(events.Any(e => e.IsConnected), Is.False);
        Assert.That(svc.IsActive, Is.True, "still requested, so editing the profile reconnects");
        svc.Dispose();
    }

    [Test]
    public async Task CloseBeforeAnyRtcm_SaysSo_AndRetries()
    {
        // Some casters answer 200 and then drop a bad mount point or account.
        using var caster = new FakeCaster(async s => await Send(s, "HTTP/1.1 200 OK\r\n\r\n"));
        var (svc, events) = Client();
        await svc.ConnectAsync(Config(caster.Port));

        Assert.That(WaitFor(() => events.Any(e => e.Message?.Contains("before sending corrections") == true), 2000), Is.True);
        Assert.That(WaitFor(() => caster.Accepted >= 2, 3000), Is.True, "reconnects with backoff");
        await svc.DisconnectAsync();
        svc.Dispose();
    }

    [Test]
    public async Task ARetryableRejection_DuringAReconnect_StillRetries()
    {
        using var caster = new FakeCaster(async s => await Send(s, "HTTP/1.1 503 Service Unavailable\r\n\r\n"));
        var (svc, _) = Client();
        await svc.ConnectAsync(Config(caster.Port));
        // 1 s then 2 s backoff: a third attempt proves the second rejection restarted the loop.
        Assert.That(WaitFor(() => caster.Accepted >= 3, 5000), Is.True);
        await svc.DisconnectAsync();
        svc.Dispose();
    }

    [Test]
    public async Task ACasterThatKeepsClosing_IsRetriedWithGrowingBackoff()
    {
        // Each attempt connects fine and is then dropped: the backoff must still grow
        // (1 s, 2 s, 4 s…) rather than restart at 1 s after every TCP connect.
        using var caster = new FakeCaster(_ => Task.CompletedTask);
        var (svc, _) = Client();
        await svc.ConnectAsync(Config(caster.Port));
        Thread.Sleep(5500); // attempts at ~0, 1, 3 s; the next is due at ~7 s
        Assert.That(caster.Accepted, Is.EqualTo(3));
        await svc.DisconnectAsync();
        svc.Dispose();
    }

    [Test]
    public async Task Disconnect_DuringBackoff_StopsReconnecting()
    {
        using var caster = new FakeCaster(_ => Task.CompletedTask); // close right away
        var (svc, events) = Client();
        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => events.Any(e => e.Message?.Contains("reconnecting") == true), 2000), Is.True);

        await svc.DisconnectAsync();
        Thread.Sleep(2500); // the 1 s backoff would have reconnected by now
        Assert.That(caster.Accepted, Is.EqualTo(1));
        Assert.That(svc.IsActive, Is.False);
        Assert.That(events.Last().Message, Is.EqualTo("Disconnected"));
        svc.Dispose();
    }

    [Test]
    public async Task FailedConnect_LeavesNothingOpen()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens here now

        var (svc, events) = Client();
        Assert.ThrowsAsync<SocketException>(() => svc.ConnectAsync(Config(closedPort)));
        Assert.That(svc.IsConnected, Is.False);
        Assert.That(events.Last().Message, Does.StartWith("Connection failed"));

        // A later connect works (nothing half-open is left behind).
        using var caster = new FakeCaster(async s => { await Send(s, "ICY 200 OK\r\n"); await Task.Delay(2000); });
        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => svc.IsConnected, 2000), Is.True);
        svc.Dispose();
    }
}
