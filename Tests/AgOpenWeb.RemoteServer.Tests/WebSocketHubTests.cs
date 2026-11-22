// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.WebSockets;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The hub sends each browser its frames from a queue of its own. Working Day scenarios: a
/// phone in a pocket (its connection open, not reading) stopped every browser's map for a
/// minute and kept the deadman from running (the broadcast waited on every send); a phone
/// loading the page stopped them for 8 s (its seed held its send gate).
/// </summary>
[TestFixture]
public class WebSocketHubTests
{
    /// <summary>A WebSocket that records what it is sent; it can stop reading.</summary>
    private sealed class FakeSocket : WebSocket
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile TaskCompletionSource? _stuck;
        private WebSocketState _state = WebSocketState.Open;
        public ConcurrentQueue<byte[]> Received { get; } = new();
        public bool Aborted { get; private set; }

        /// <summary>Sends hang until <see cref="Read"/> (a phone in a pocket).</summary>
        public void StopReading() => _stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Read() { var s = _stuck; _stuck = null; s?.TrySetResult(); }
        public void Leave() => _closed.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            _stuck?.TrySetException(new WebSocketException("aborted"));
            _closed.TrySetException(new WebSocketException("aborted"));
        }

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            await _closed.Task.WaitAsync(ct);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
        {
            if (_stuck is { } stuck) await stuck.Task.WaitAsync(ct);
            Received.Enqueue(buffer.ToArray());
        }
    }

    private static byte[] Frame(byte tag) => new byte[] { 200, tag };

    private static async Task Until(Func<bool> ok, int ms = 2000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(10);
    }

    private static IEnumerable<byte> Tags(FakeSocket s) => s.Received.Where(f => f.Length == 2 && f[0] == 200).Select(f => f[1]);

    [Test]
    public async Task A_browser_that_stops_reading_holds_up_nobody()
    {
        var hub = new WebSocketHub(new ControlAuthority());
        var tablet = new FakeSocket();
        var phone = new FakeSocket();
        var tabletTask = hub.HandleAsync(tablet, CancellationToken.None);
        var phoneTask = hub.HandleAsync(phone, CancellationToken.None);
        await Until(() => hub.ClientCount == 2);
        phone.StopReading();

        for (byte i = 0; i < 20; i++)
        {
            var b = hub.BroadcastAsync(Frame(i));
            Assert.That(b.IsCompleted, Is.True, "a broadcast only queues");
        }
        await Until(() => Tags(tablet).Count() == 20);

        Assert.That(Tags(tablet), Is.EqualTo(Enumerable.Range(0, 20).Select(i => (byte)i)), "the tablet got every frame, in order");
        phone.Read();
        await Until(() => Tags(phone).Count() == 20);
        Assert.That(Tags(phone).Count(), Is.EqualTo(20), "the phone gets its frames once it reads again");
        tablet.Leave(); phone.Leave();
        await Task.WhenAll(tabletTask, phoneTask);
    }

    [Test]
    public async Task A_browser_that_never_reads_again_is_cut_off_and_its_control_ends()
    {
        var authority = new ControlAuthority();
        var revoked = new List<string>();
        authority.Revoked += r => revoked.Add(r);
        var hub = new WebSocketHub(authority) { StallLimitMs = 300 };
        var phone = new FakeSocket();
        var task = hub.HandleAsync(phone, CancellationToken.None);
        await Until(() => hub.ClientCount == 1);
        phone.StopReading();

        for (int i = 0; i < 20; i++) { await hub.BroadcastAsync(Frame(1)); await Task.Delay(50); }
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(phone.Aborted, Is.True);
        Assert.That(hub.ClientCount, Is.EqualTo(0));
        Assert.That(revoked, Is.EqualTo(new[] { "client disconnected" }), "it held control: the failsafe ran");
    }

    [Test]
    public async Task What_is_broadcast_while_the_seed_is_built_goes_out_after_the_seed()
    {
        // A coverage delta drained while a new browser's seed is built must not arrive before
        // the seed's snapshot (whose reset init would throw it away).
        WebSocketHub hub = null!;
        hub = new WebSocketHub(new ControlAuthority())
        {
            SeedProvider = () =>
            {
                hub.BroadcastAsync(Frame(9));     // broadcast meanwhile
                return new[] { Frame(1), Frame(2) };
            },
        };
        var tablet = new FakeSocket();
        var task = hub.HandleAsync(tablet, CancellationToken.None);
        await Until(() => Tags(tablet).Count() == 3);
        await hub.BroadcastAsync(Frame(10));
        await Until(() => Tags(tablet).Count() == 4);

        Assert.That(Tags(tablet), Is.EqualTo(new byte[] { 1, 2, 9, 10 }));
        tablet.Leave();
        await task;
    }

    [Test]
    public async Task A_command_from_a_screen_without_control_is_refused_and_it_is_told_once()
    {
        var hub = new WebSocketHub(new ControlAuthority()) { IsRestrictedCommand = _ => true };
        var refused = new List<(string Id, bool Tell)>();
        hub.CommandRefused = (_, id, tell) => refused.Add((id, tell));
        var ran = new List<string>();
        hub.CommandHandler = (id, _) => ran.Add(id);

        var tablet = new FakeSocket();
        var tabletTask = hub.HandleAsync(tablet, CancellationToken.None);
        await Until(() => hub.ClientCount == 1);
        var phone = new ScriptedSocket("config.set|tool.width:9", "config.set|tool.width:10");
        var phoneTask = hub.HandleAsync(phone, CancellationToken.None);
        await phoneTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(ran, Is.Empty, "the observer changed nothing");
        Assert.That(refused, Is.EqualTo(new[] { ("config.set", true), ("config.set", false) }));
        tablet.Leave();
        await tabletTask;
    }

    // A tester's run on the simulator (bug report 19 Nov 2026, "tractor left field mostly
    // undone"): the log said only "[AutoSteer] Off: browser control ended: presence lost
    // (deadman)". Nothing said whether the browser's link was still open, whether the host had
    // cut it off, or what the page went through; the dump could not tell a frozen tab from a
    // network gap. The hub now logs each browser's arrival, its leaving and why, and a deadman
    // with the state of the link.

    [Test]
    public async Task A_deadman_is_logged_with_the_state_of_the_link()
    {
        var authority = new ControlAuthority();
        var log = new ConcurrentQueue<string>();
        var hub = new WebSocketHub(authority) { Log = log.Enqueue };
        var mac = new FakeSocket();
        var task = hub.HandleAsync(mac, CancellationToken.None, "192.168.4.20");
        await Until(() => hub.ClientCount == 1);
        mac.StopReading(); // a frozen tab: nothing read, no presence
        await hub.BroadcastAsync(Frame(1));
        await hub.BroadcastAsync(Frame(2));

        await Task.Delay(1700); // past the deadman's 1.5 s
        Assert.That(authority.SweepStale(), Is.True);
        mac.Read();
        mac.Leave();
        await task;

        var lines = log.ToArray();
        Assert.That(lines[0], Does.Match(@"^\[Browser\] \w{6} connected from 192\.168\.4\.20, has control; 1 connected$"));
        var deadman = lines.Single(l => l.Contains("deadman"));
        Assert.That(deadman, Does.Match(@"deadman, no presence from it for 1\.\d s; its socket is Open, last heard from it 1\.\d s ago, "));
        Assert.That(deadman, Does.Match(@"[1-9]\d* frames \(0 KB\) waiting to go to it$"), "the frames it did not take");
        Assert.That(lines.Last(), Does.Match(@"left after 1\.\d s: the page closed the link; last heard from it 0\.0 s ago$"),
            "its close is the last it said; control had already gone to the deadman");
    }

    [Test]
    public async Task A_browser_cut_off_for_not_reading_is_logged_as_cut_off_with_its_control()
    {
        var log = new ConcurrentQueue<string>();
        var hub = new WebSocketHub(new ControlAuthority()) { StallLimitMs = 300, Log = log.Enqueue };
        var phone = new FakeSocket();
        var task = hub.HandleAsync(phone, CancellationToken.None);
        await Until(() => hub.ClientCount == 1);
        phone.StopReading();

        for (int i = 0; i < 20; i++) { await hub.BroadcastAsync(Frame(1)); await Task.Delay(50); }
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(log.Last(), Does.Match(@"left after \d\.\d s: cut off: it took nothing it was sent for 0\.\d s, 0 KB waiting; last heard from it \d\.\d s ago; it had control$"));
    }

    [Test]
    public async Task What_the_page_saw_of_itself_is_logged_and_an_observer_is_named()
    {
        var log = new ConcurrentQueue<string>();
        var hub = new WebSocketHub(new ControlAuthority()) { Log = log.Enqueue };
        var tablet = new FakeSocket();
        var tabletTask = hub.HandleAsync(tablet, CancellationToken.None);
        await Until(() => hub.ClientCount == 1);
        var mac = new ScriptedSocket("diag.page|its timers stopped for 85.2 s (hidden): the system paused the page");
        await hub.HandleAsync(mac, CancellationToken.None, "192.168.4.21").WaitAsync(TimeSpan.FromSeconds(3));
        tablet.Leave();
        await tabletTask;

        var lines = log.ToArray();
        Assert.That(lines, Has.Some.Match(@"^\[Browser\] \w{6} connected from 192\.168\.4\.21, observer; 2 connected$"));
        Assert.That(lines, Has.Some.Match(@"^\[Browser\] \w{6} page: its timers stopped for 85\.2 s \(hidden\): the system paused the page$"));
        Assert.That(lines, Has.Some.Match(@"^\[Browser\] \w{6} left after .*: the page closed the link; last heard from it 0\.0 s ago$"));
        Assert.That(lines.Last(), Does.EndWith("; it had control"), "the tablet that had control");
    }

    // A tester's field, 21 Nov 2026: an outer boundary of 325 points, sent as the page's
    // "draw on map" sends it, became a field of 68 points and 1.36 ha (it was 13.24 ha). The hub
    // read into 1024 bytes and took each piece of a longer command as a command of its own, so
    // the first 1024 bytes were the whole boundary and the other pieces unknown commands.
    [Test]
    public async Task A_command_longer_than_the_read_buffer_arrives_whole()
    {
        var hub = new WebSocketHub(new ControlAuthority());
        var ran = new List<(string Id, string Arg)>();
        hub.CommandHandler = (id, arg) => ran.Add((id, arg));
        var points = string.Join(';', Enumerable.Range(0, 325).Select(i =>
            FormattableString.Invariant($"{-676.4 + i * 2.2:0.000},{481.2 - i * 1.6:0.000}")));

        var page = new ScriptedSocket("boundary.fromMapPoints|" + points, "boundary.hard");
        await hub.HandleAsync(page, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(points.Length, Is.GreaterThan(4 * 1024), "the command is several buffers long");
        Assert.That(ran.Select(c => c.Id), Is.EqualTo(new[] { "boundary.fromMapPoints", "boundary.hard" }),
            "one command, and no piece of it taken for another");
        Assert.That(ran[0].Arg, Is.EqualTo(points));
        Assert.That(ran[0].Arg.Split(';'), Has.Length.EqualTo(325));
    }

    [Test]
    public async Task A_letter_cut_by_the_read_buffer_is_read_as_one_letter()
    {
        // A bug report's description in Portuguese: the two bytes of "ã" fall either side of
        // the buffer's end.
        var hub = new WebSocketHub(new ControlAuthority());
        var ran = new List<(string Id, string Arg)>();
        hub.CommandHandler = (id, arg) => ran.Add((id, arg));
        string text = new string('a', 1023 - "app.bugReport.send|".Length) + "ção, não virou" + new string('b', 1500);

        var page = new ScriptedSocket("app.bugReport.send|" + text);
        await hub.HandleAsync(page, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(ran, Is.EqualTo(new[] { ("app.bugReport.send", text) }));
    }

    [Test]
    public async Task A_command_over_the_limit_is_dropped_whole_and_the_next_one_runs()
    {
        var log = new ConcurrentQueue<string>();
        var hub = new WebSocketHub(new ControlAuthority()) { Log = log.Enqueue };
        var ran = new List<string>();
        hub.CommandHandler = (id, _) => ran.Add(id);

        var page = new ScriptedSocket(
            "boundary.fromMapPoints|" + new string('1', WebSocketHub.MaxCommandBytes),
            "boundary.fromMapPoints|" + new string('1', WebSocketHub.MaxCommandBytes - "boundary.fromMapPoints|".Length),
            "boundary.hard");
        await hub.HandleAsync(page, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(ran, Is.EqualTo(new[] { "boundary.fromMapPoints", "boundary.hard" }),
            "the one over the limit ran in no part; the one at the limit and the next one ran");
        Assert.That(log.Where(l => l.Contains("was dropped")).ToArray(),
            Has.Length.EqualTo(1).And.Some.Match(@"^\[Browser\] \w{6}: a command over 1024 KB was dropped \(boundary\.fromMapPoints\)$"));
    }

    /// <summary>A socket that sends the given text commands, then closes. A command longer
    /// than the reader's buffer comes in pieces, as from a real socket.</summary>
    private sealed class ScriptedSocket(params string[] commands) : WebSocket
    {
        private int _next;
        private byte[]? _sending;
        private int _sent;
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_sending == null && _next >= commands.Length)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            _sending ??= System.Text.Encoding.UTF8.GetBytes(commands[_next++]);
            int n = Math.Min(buffer.Count, _sending.Length - _sent);
            Array.Copy(_sending, _sent, buffer.Array!, buffer.Offset, n);
            _sent += n;
            bool end = _sent == _sending.Length;
            if (end) { _sending = null; _sent = 0; }
            return Task.FromResult(new WebSocketReceiveResult(n, WebSocketMessageType.Text, end));
        }
    }
}
