// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.Sockets;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The NTRIP session against a fake caster: what it does for each kind of reply, when it falls
/// back to NTRIP 1.0, and how it waits between attempts. Waits are injected, so nothing sleeps.
/// </summary>
[TestFixture]
public class NtripSessionTests
{
    private const string Garbage = "garbage that is no reply at all\r\n\r\n";
    private static readonly byte[] Rtcm = [0xD3, 0x00, 0x04, 0x01, 0x02, 0x03, 0x04];

    private sealed class Run : IDisposable
    {
        public readonly ConcurrentQueue<TimeSpan> Waits = new();
        public readonly ConcurrentQueue<NtripSessionStatus> Statuses = new();
        public readonly ConcurrentQueue<bool> StreamStarts = new();
        public readonly CancellationTokenSource Stop = new();
        public int BodyBytes;
        public Task Task = Task.CompletedTask;
        public NtripSession Session = null!;

        public void Dispose() => Stop.Cancel();
        public NtripSessionStatus Last => Statuses.LastOrDefault() ?? NtripSessionStatus.Idle;
    }

    /// <param name="stopAfterWaits">Cancels the session once it has waited this many times, so a retrying run ends.</param>
    private static Run Start(NtripFakeCaster caster, int stopAfterWaits = int.MaxValue, NtripTimeouts? timeouts = null,
        Func<string?>? gga = null, ConcurrentDictionary<string, NtripVersion>? memory = null, int ggaInterval = 0)
    {
        var run = new Run();
        var config = new NtripConfiguration
        {
            CasterAddress = "127.0.0.1", CasterPort = caster.Port, MountPoint = "M",
            Username = "u", Password = "p", GgaIntervalSeconds = ggaInterval,
        };
        run.Session = new NtripSession(config, data => Interlocked.Add(ref run.BodyBytes, data.Length),
            () => Volatile.Read(ref run.BodyBytes) > 0, gga ?? (() => null))
        {
            Timeouts = timeouts ?? new NtripTimeouts(),
            Delay = (span, _) =>
            {
                run.Waits.Enqueue(span);
                if (run.Waits.Count >= stopAfterWaits) run.Stop.Cancel();
                return Task.CompletedTask;
            },
            StreamStarted = chunked => { Volatile.Write(ref run.BodyBytes, 0); run.StreamStarts.Enqueue(chunked); },
            VersionMemory = memory,
        };
        run.Session.StatusChanged += s => run.Statuses.Enqueue(s);
        run.Task = Task.Run(() => run.Session.RunAsync(run.Stop.Token));
        return run;
    }

    private static async Task Finished(Run run) => Assert.That(await Task.WhenAny(run.Task, Task.Delay(10000)), Is.SameAs(run.Task), "the session ended");

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }

    private static Task Accept(NetworkStream s) => NtripFakeCaster.SendAsync(s, "ICY 200 OK\r\n\r\n");

    // ── Accepting ────────────────────────────────────────────────────────

    [Test]
    public async Task AFirstRequest_IsNtrip20_AndStreamsTheBody()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) => { await Accept(s); await s.WriteAsync(Rtcm); await Task.Delay(3000); });
        using var run = Start(caster);

        Assert.That(WaitFor(() => run.Last.State == NtripState.Streaming), Is.True);
        string request = caster.Requests.First();
        Assert.That(request, Does.StartWith("GET /M HTTP/1.1\r\n"));
        Assert.That(request, Does.Contain("Ntrip-Version: Ntrip/2.0\r\n"));
        Assert.That(request, Does.Contain("Authorization: Basic "));
        Assert.That(run.BodyBytes, Is.EqualTo(Rtcm.Length));
        Assert.That(run.Last.Message, Does.StartWith("Connected to 127.0.0.1:"));
    }

    [Test]
    public async Task ARepliedChunkedBody_IsAnnouncedToTheCaller()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) =>
        {
            await NtripFakeCaster.SendAsync(s, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n");
            await Task.Delay(2000);
        });
        using var run = Start(caster);

        Assert.That(WaitFor(() => run.StreamStarts.Count == 1), Is.True);
        Assert.That(run.StreamStarts.Single(), Is.True);
    }

    [Test]
    public async Task APositionIsPutInTheRequest_AndSentAgainOnTheInterval()
    {
        string? received = null;
        using var caster = new NtripFakeCaster(async (_, _, s) =>
        {
            await Accept(s);
            var buffer = new byte[256];
            int n = await s.ReadAsync(buffer);
            received = System.Text.Encoding.ASCII.GetString(buffer, 0, n);
        });
        using var run = Start(caster, gga: () => "$GPGGA,TEST*00", ggaInterval: 1);

        Assert.That(WaitFor(() => received != null), Is.True);
        Assert.That(caster.Requests.First(), Does.Contain("Ntrip-GGA: $GPGGA,TEST*00\r\n"));
        Assert.That(received, Is.EqualTo("$GPGGA,TEST*00\r\n"));
    }

    // ── NTRIP 1.0 ────────────────────────────────────────────────────────

    [TestCase(Garbage)]
    [TestCase("HTTP/1.1 400 Bad Request\r\n\r\n")]
    [TestCase("HTTP/1.1 505 HTTP Version Not Supported\r\n\r\n")]
    public async Task ACasterThatDoesNotUnderstandNtrip20_GetsTheNtrip10Request_Once(string reply)
    {
        using var caster = new NtripFakeCaster(async (connection, _, s) =>
        {
            if (connection == 1) await NtripFakeCaster.SendAsync(s, reply);
            else { await Accept(s); await s.WriteAsync(Rtcm); await Task.Delay(3000); }
        });
        using var run = Start(caster);

        Assert.That(WaitFor(() => run.Last.State == NtripState.Streaming), Is.True);
        Assert.That(caster.Requests.Select(r => r.Split("\r\n")[0]),
            Is.EqualTo(new[] { "GET /M HTTP/1.1", "GET /M HTTP/1.0" }));
        Assert.That(caster.Requests.Last(), Does.Not.Contain("Ntrip-Version"));
        Assert.That(run.Last.Version, Is.EqualTo(NtripVersion.V1));
    }

    [TestCase("HTTP/1.1 401 Unauthorized\r\n\r\n", NtripCategory.LoginRejected)]
    [TestCase("HTTP/1.1 403 Forbidden\r\n\r\n", NtripCategory.LoginRejected)]
    [TestCase("HTTP/1.1 404 Not Found\r\n\r\n", NtripCategory.UnknownMountpoint)]
    [TestCase("SOURCETABLE 200 OK\r\nServer: Test\r\n\r\nSTR;OTHER\r\nENDSOURCETABLE\r\n", NtripCategory.UnknownMountpoint)]
    [TestCase("ERROR - Bad Password\r\n\r\n", NtripCategory.LoginRejected)]
    public async Task AWrongLoginOrMountPoint_NeverGetsTheNtrip10Request_AndStopsTheSession(string reply, NtripCategory category)
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, reply));
        using var run = Start(caster);

        await Finished(run);
        Assert.That(caster.Accepted, Is.EqualTo(1));
        Assert.That(run.Last.State, Is.EqualTo(NtripState.GaveUp));
        Assert.That(run.Last.LastError, Is.EqualTo(category));
        Assert.That(run.Waits, Is.Empty, "retrying cannot fix it");
    }

    [Test]
    public async Task ACasterThatClosesWithoutAReply_IsRetriedWithNtrip20_NotNtrip10()
    {
        using var caster = new NtripFakeCaster((_, _, _) => Task.CompletedTask);
        using var run = Start(caster, stopAfterWaits: 3);

        await Finished(run);
        Assert.That(caster.Requests.Select(r => r.Split("\r\n")[0]), Has.All.EqualTo("GET /M HTTP/1.1"));
        Assert.That(run.Statuses.Any(s => s.LastError == NtripCategory.CasterUnreachable), Is.True);
    }

    [Test]
    public async Task ACasterThatNeverReplies_IsRetriedWithNtrip20_NotNtrip10()
    {
        using var caster = new NtripFakeCaster(async (_, _, _) => await Task.Delay(3000));
        using var run = Start(caster, stopAfterWaits: 2, timeouts: new NtripTimeouts { Header = TimeSpan.FromMilliseconds(200) });

        await Finished(run);
        Assert.That(caster.Requests.Select(r => r.Split("\r\n")[0]), Has.All.EqualTo("GET /M HTTP/1.1"));
        Assert.That(run.Statuses.Any(s => s.Message.StartsWith("No answer from")), Is.True);
    }

    [Test]
    public async Task TheVersionThatDeliveredCorrections_IsTriedFirstOnTheNextConnect()
    {
        var memory = new ConcurrentDictionary<string, NtripVersion>();
        using var caster = new NtripFakeCaster(async (connection, request, s) =>
        {
            if (request.Contains("HTTP/1.1")) { await NtripFakeCaster.SendAsync(s, Garbage); return; }
            await Accept(s);
            await s.WriteAsync(Rtcm);   // then the caster closes, and the session connects again
        });
        using var run = Start(caster, stopAfterWaits: 2, memory: memory);

        await Finished(run);
        string[] lines = caster.Requests.Select(r => r.Split("\r\n")[0]).ToArray();
        Assert.That(lines[..2], Is.EqualTo(new[] { "GET /M HTTP/1.1", "GET /M HTTP/1.0" }));
        Assert.That(lines[2], Is.EqualTo("GET /M HTTP/1.0"), "the probe is not repeated");
        Assert.That(memory.Values, Is.EquivalentTo(new[] { NtripVersion.V1 }));
    }

    [Test]
    public async Task APositionIsSentRightAfterAnNtrip10Answer()
    {
        string? received = null;
        using var caster = new NtripFakeCaster(async (connection, _, s) =>
        {
            if (connection == 1) { await NtripFakeCaster.SendAsync(s, Garbage); return; }
            await Accept(s);
            var buffer = new byte[256];
            int n = await s.ReadAsync(buffer);
            received = System.Text.Encoding.ASCII.GetString(buffer, 0, n);
        });
        using var run = Start(caster, gga: () => "$GPGGA,TEST*00", ggaInterval: 30);

        Assert.That(WaitFor(() => received != null), Is.True);
        Assert.That(received, Is.EqualTo("$GPGGA,TEST*00\r\n"));
    }

    [Test]
    public async Task WithAGgaIntervalOf0_NoPositionIsSent_NotInTheRequestNorAfterAnNtrip10Answer()
    {
        using var caster = new NtripFakeCaster(async (connection, _, s) =>
        {
            if (connection == 1) await NtripFakeCaster.SendAsync(s, Garbage);
            else { await Accept(s); await Task.Delay(2000); }
        });
        using var run = Start(caster, gga: () => "$GPGGA,TEST*00", ggaInterval: 0);

        Assert.That(WaitFor(() => run.Last.State == NtripState.Connected), Is.True);
        Assert.That(caster.Requests, Has.None.Contain("Ntrip-GGA"));
    }

    // ── Retrying ─────────────────────────────────────────────────────────

    [Test]
    public async Task AHandlerThatThrows_IsAStreamLost_NotTheEndOfTheSession()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) => { await Accept(s); await s.WriteAsync(Rtcm); await Task.Delay(2000); });
        var run = new Run();
        var config = new NtripConfiguration { CasterAddress = "127.0.0.1", CasterPort = caster.Port, MountPoint = "M", GgaIntervalSeconds = 0 };
        var session = new NtripSession(config, _ => throw new InvalidOperationException("boom"), () => false, () => null)
        {
            Delay = (span, _) => { run.Waits.Enqueue(span); if (run.Waits.Count >= 2) run.Stop.Cancel(); return Task.CompletedTask; },
        };
        session.StatusChanged += s => run.Statuses.Enqueue(s);

        await session.RunAsync(run.Stop.Token);

        Assert.That(caster.Accepted, Is.GreaterThanOrEqualTo(2), "it tried again");
        Assert.That(run.Statuses.Any(s => s.LastError == NtripCategory.StreamLost && s.Message.Contains("boom")), Is.True);
        Assert.That(run.Statuses.Any(s => s.State == NtripState.GaveUp), Is.False);
    }

    [Test]
    public async Task ABusyCaster_IsRetriedWithAGrowingBackoff_ThatStaysAtTheLastStep()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 503 Service Unavailable\r\n\r\n"));
        using var run = Start(caster, stopAfterWaits: 7);

        await Finished(run);
        Assert.That(run.Waits.Select(w => w.TotalSeconds), Is.EqualTo(new double[] { 1, 2, 4, 8, 15, 15, 15 }));
        Assert.That(run.Statuses.Select(s => s.LastError), Does.Contain(NtripCategory.CasterBusy));
    }

    [Test]
    public async Task TheBackoffStartsOverOnceCorrectionsArrived()
    {
        using var caster = new NtripFakeCaster(async (connection, _, s) =>
        {
            await Accept(s);
            if (connection == 1) await s.WriteAsync(Rtcm);   // streams, then closes
        });
        using var run = Start(caster, stopAfterWaits: 3);

        await Finished(run);
        // 1st: the stream that delivered ends (a fresh start), 2nd and 3rd: nothing came, so the steps grow.
        Assert.That(run.Waits.Select(w => w.TotalSeconds), Is.EqualTo(new double[] { 1, 2, 4 }));
        Assert.That(run.Statuses.Select(s => s.LastError), Does.Contain(NtripCategory.StreamLost));
        Assert.That(run.Statuses.Select(s => s.LastError), Does.Contain(NtripCategory.NoCorrections));
    }

    [Test]
    public async Task ACasterThatGoesSilent_EndsTheSessionAfterTheNoDataTimeout()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) => { await Accept(s); await Task.Delay(5000); });
        using var run = Start(caster, stopAfterWaits: 1, timeouts: new NtripTimeouts { NoData = TimeSpan.FromMilliseconds(300) });

        await Finished(run);
        Assert.That(run.Statuses.Any(s => s.LastError == NtripCategory.NoCorrections && s.Message.StartsWith("Connected, but nothing is coming in")), Is.True);
    }

    [Test]
    public async Task ANetworkChange_EndsTheWait()
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 503 Service Unavailable\r\n\r\n"));
        var run = new Run();
        var config = new NtripConfiguration { CasterAddress = "127.0.0.1", CasterPort = caster.Port, MountPoint = "M", GgaIntervalSeconds = 0 };
        var session = new NtripSession(config, _ => { }, () => false, () => null)
        {
            Delay = (_, ct) => { waited.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); },   // a wait that never ends
            NetworkChanged = () => changed.Task,
        };
        run.Task = Task.Run(() => session.RunAsync(run.Stop.Token));

        await waited.Task;
        int before = caster.Accepted;
        changed.SetResult();
        Assert.That(WaitFor(() => caster.Accepted > before), Is.True, "tried again at once");
        run.Dispose();
    }

    // ── Stopping ─────────────────────────────────────────────────────────

    [Test]
    public async Task Cancelling_StopsAStreamingSession_AndClosesTheConnection()
    {
        using var caster = new NtripFakeCaster(async (_, _, s) => { await Accept(s); await s.WriteAsync(Rtcm); await Task.Delay(10000); });
        using var run = Start(caster);
        Assert.That(WaitFor(() => run.Last.State == NtripState.Streaming), Is.True);

        run.Stop.Cancel();

        await Finished(run);
        Assert.That(run.Last.State, Is.EqualTo(NtripState.Stopped));
    }

    [Test]
    public async Task ASettingNoConnectionCanUse_IsReported_NotSwallowed()
    {
        var run = new Run();
        var config = new NtripConfiguration { CasterAddress = "127.0.0.1", CasterPort = 99999, MountPoint = "M" };
        var session = new NtripSession(config, _ => { }, () => false, () => null);
        string? message = null;
        session.StatusChanged += s => { if (s.State == NtripState.GaveUp) message = s.Message; };

        await session.RunAsync(run.Stop.Token);

        Assert.That(message, Does.StartWith("Cannot connect:"));
    }
}
