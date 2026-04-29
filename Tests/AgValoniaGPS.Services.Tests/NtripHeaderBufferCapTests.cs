using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Regression test for issue #286: a malicious caster — or MITM — that
/// streams bytes without the \r\n\r\n header terminator must not be able
/// to grow the client's header buffer until the tablet OOMs. The fix caps
/// the buffer at 8 KiB and disconnects cleanly.
/// </summary>
[TestFixture]
public class NtripHeaderBufferCapTests
{
    [Test]
    public async Task ReceiveLoop_HeaderExceedsCap_DisconnectsCleanly()
    {
        // Spin up a loopback TCP server that accepts the NTRIP request
        // and then streams 9 KiB of garbage with no header terminator.
        // The pre-fix code would have grown _headerBuffer indefinitely.
        // The post-fix code caps at 8 KiB and disconnects.
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverDone = new TaskCompletionSource<bool>();
        var clientDisconnected = new ManualResetEventSlim(initialState: false);

        // Server side: accept, drain the request, blast 9 KiB of garbage,
        // then keep the socket open so we can observe the *client* closing.
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                // Drain the client's NTRIP request (it's <1KB)
                var requestBuffer = new byte[2048];
                _ = await stream.ReadAsync(requestBuffer, 0, requestBuffer.Length);

                // Stream 9 KiB of 'X' — no \r\n\r\n terminator anywhere.
                var garbage = Encoding.ASCII.GetBytes(new string('X', 9 * 1024));
                await stream.WriteAsync(garbage, 0, garbage.Length);
                await stream.FlushAsync();

                // Keep the socket open *long enough* that any disconnect we
                // observe must come from the client deciding to close
                // (not from the server timing out and closing on its own).
                // The buffer-cap fix should disconnect within milliseconds of
                // receiving the 9 KiB; without the fix the client would keep
                // buffering and only disconnect when the server eventually
                // closes — which we never do here.
                serverDone.SetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            catch
            {
                // Client closed first — expected outcome
                serverDone.TrySetResult(true);
            }
        });

        var service = new NtripClientService(
            NSubstitute.Substitute.For<IGpsService>(),
            NullLogger<NtripClientService>.Instance);
        service.ConnectionStatusChanged += (_, args) =>
        {
            if (!args.IsConnected) clientDisconnected.Set();
        };

        var config = new NtripConfiguration
        {
            CasterAddress = "127.0.0.1",
            CasterPort = port,
            MountPoint = "TEST",
            Username = "u",
            Password = "p",
            SubnetAddress = "127.0.0",
            UdpForwardPort = 0,        // 0 disables the forward target; harmless for this test
            GgaIntervalSeconds = 0,    // skip the GGA timer
        };

        try
        {
            await service.ConnectAsync(config);
        }
        catch
        {
            // ConnectAsync may throw if the GGA/UDP plumbing trips; the
            // receive-loop fix is what we're testing, so as long as we
            // don't get stuck we're fine.
        }

        // The fix disconnects within ms of the 9 KiB arriving. Use a tight
        // 2s window: well below the server's 30s hold so any disconnect
        // observed must be the client's own decision to close.
        bool disconnectedInTime = clientDisconnected.Wait(TimeSpan.FromSeconds(2));

        Assert.That(disconnectedInTime, Is.True,
            "NtripClientService must close the connection when the header buffer exceeds the cap");
        Assert.That(service.IsConnected, Is.False);

        listener.Stop();
        service.Dispose();
    }

    [Test]
    public async Task ReceiveLoop_NormalSizedHeader_DoesNotTripTheCap()
    {
        // Sanity check: a well-formed 200 OK response well under the cap
        // must NOT trip the disconnect. Otherwise the cap would break
        // legitimate connections.
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connected = new ManualResetEventSlim(initialState: false);

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                var requestBuffer = new byte[2048];
                _ = await stream.ReadAsync(requestBuffer, 0, requestBuffer.Length);

                // Send a valid 200 OK header well under the 8 KiB cap
                var response = Encoding.ASCII.GetBytes(
                    "ICY 200 OK\r\nServer: Test\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length);
                await stream.FlushAsync();

                // Hold the socket so the test can observe IsConnected=true
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch { /* test will assert below */ }
        });

        var service = new NtripClientService(
            NSubstitute.Substitute.For<IGpsService>(),
            NullLogger<NtripClientService>.Instance);
        service.ConnectionStatusChanged += (_, args) =>
        {
            if (args.IsConnected) connected.Set();
        };

        var config = new NtripConfiguration
        {
            CasterAddress = "127.0.0.1",
            CasterPort = port,
            MountPoint = "TEST",
            Username = "u",
            Password = "p",
            SubnetAddress = "127.0.0",
            UdpForwardPort = 0,
            GgaIntervalSeconds = 0,
        };

        try { await service.ConnectAsync(config); } catch { /* see above */ }

        bool gotConnected = connected.Wait(TimeSpan.FromSeconds(3));

        Assert.That(gotConnected, Is.True,
            "A normal-sized header (well under 8 KiB) must not trip the cap or break connection");

        listener.Stop();
        service.Dispose();
    }
}
