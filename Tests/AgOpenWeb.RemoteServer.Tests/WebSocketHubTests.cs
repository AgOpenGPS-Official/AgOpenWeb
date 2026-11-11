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

    /// <summary>A socket that sends the given text commands, then closes.</summary>
    private sealed class ScriptedSocket(params string[] commands) : WebSocket
    {
        private int _next;
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
            if (_next >= commands.Length)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = System.Text.Encoding.UTF8.GetBytes(commands[_next++]);
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
    }
}
