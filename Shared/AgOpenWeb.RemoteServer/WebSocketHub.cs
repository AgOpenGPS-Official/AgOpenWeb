// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Raw-WebSocket fan-out hub — the SignalR replacement. Holds the set of
// connected clients and broadcasts pre-encoded binary frames (WireCodec) to all
// of them. Each client has its own outgoing queue and its own sender task: a
// broadcast only queues the frame and returns, the sender writes the client's frames
// in order, one SendAsync at a time (the WebSocket API forbids two at once). So a
// browser that stops reading (a phone in a pocket, out of Wi-Fi range) or one loading
// a large seed holds up only itself, never the others' maps or the deadman sweep
// (Working Day scenarios: MAP-FROZEN-BY-A-PHONE, MAP-PAUSED-WHILE-A-BROWSER-LOADS,
// DEADMAN-BLOCKED-BY-A-PHONE). A client whose queue makes no progress for
// StalledMs, or grows past MaxQueuedBytes, is cut off; the page reconnects and gets a
// fresh seed. The receive loop carries client→host commands.
// Each browser's arrival, its leaving and why, a deadman with the state of its link, and
// what the page saw of itself (diag.page: hidden, frozen, its timers paused, the network
// gone) go to the app's log (Log), so a bug report dump says why a screen went quiet.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace AgOpenWeb.RemoteServer;

public sealed class WebSocketHub
{
    /// <summary>A client whose queued frames make no progress for this long is cut off: it
    /// has stopped reading. Long enough for a full seed (an 18 MB coverage snapshot takes
    /// about 8 s over a 20 Mbit/s Wi-Fi) to go out as one send.</summary>
    public const int StalledMs = 15_000;

    /// <summary>A client with more than this waiting is cut off whatever its progress.</summary>
    public const long MaxQueuedBytes = 64L * 1024 * 1024;

    /// <summary>The stall limit in use (<see cref="StalledMs"/>; shorter in tests).</summary>
    public int StallLimitMs { get; set; } = StalledMs;

    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        /// <summary>A short name for the log: the start of its connection id.</summary>
        public required string Name { get; init; }
        public long ConnectedTicks = Environment.TickCount64;
        /// <summary>When a message last came from it.</summary>
        public long HeardTicks = Environment.TickCount64;
        /// <summary>When a frame to it last finished sending (0: none yet).</summary>
        public long SentTicks;
        public int PageNotes;
        public Channel<byte[]> Out { get; } = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public long QueuedBytes;
        public int Pending;
        /// <summary>When the queue last moved (a send finished, or it went from empty to not).</summary>
        public long ProgressTicks = Environment.TickCount64;
        public Task? Sender;
        /// <summary>Why it was cut off; null while it is not.</summary>
        public string? CutOff;
        /// <summary>Frames broadcast while this client's seed is being built: they go out
        /// after the seed, in order (null once the seed is queued). Otherwise a coverage
        /// delta drained during the build arrives before the seed's snapshot and its reset,
        /// and is lost on this screen; a newer Scene would arrive before the seed's older one.</summary>
        public List<byte[]>? Held = new();
        public readonly object HeldLock = new();
        private long _refusalToldAt = long.MinValue / 2;
        /// <summary>True when this client has not been told of a refused command lately.</summary>
        public bool ShouldTellRefusal()
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _refusalToldAt) < 3000) return false;
            Interlocked.Exchange(ref _refusalToldAt, now);
            return true;
        }
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ControlAuthority _authority;

    public WebSocketHub(ControlAuthority authority)
    {
        _authority = authority;
        authority.DeadmanTripped += (conn, ms) => Log?.Invoke(
            $"[Browser] {NameOf(conn)}: deadman, no presence from it for {Secs(ms)}; {LinkState(conn)}");
    }

    /// <summary>Lines for the app's log: browsers arriving and leaving, and why (set by the host).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>At most this many page notes (diag.page) are logged per connection.</summary>
    private const int MaxPageNotes = 200;

    /// <summary>How each browser draws the map (<c>diag.client</c>), for the bug report dump.</summary>
    public ClientDiagnostics Diagnostics { get; } = new();

    /// <summary>The dump's "Browsers" section (<see cref="ClientDiagnostics.Summary"/>).</summary>
    public string DiagnosticsSummary() => Diagnostics.Summary(_authority.HoldsFresh);

    /// <summary>True for a command id that needs control: it is dropped unless the
    /// sending connection currently holds fresh authority (the host gates all but a few).
    /// Set by the host (the app knows its command ids); null = nothing restricted.</summary>
    public Func<string, bool>? IsRestrictedCommand { get; set; }

    /// <summary>
    /// Frames sent to a client the instant it connects (current Scene + coverage
    /// snapshot). Set by <see cref="MapBroadcaster"/>; returns an empty list until then.
    /// </summary>
    public Func<IReadOnlyList<byte[]>>? SeedProvider { get; set; }

    /// <summary>
    /// Invoked (off the UI thread) with the id + arg of each inbound text message
    /// from a client (arg is "" when the message carries no "|arg" suffix). The host
    /// maps known ids to actions and marshals them to the UI thread; unknown ids are
    /// the host's no-op — that allowlist is the command-safety boundary. Null until
    /// the host wires it.
    /// </summary>
    public Action<string, string>? CommandHandler { get; set; }

    /// <summary>Told each command dropped because its screen has no control: the connection,
    /// the id, and whether to tell that screen (at most once every few seconds, so a slider
    /// dragged on an observer does not bury it in notices). Set by the host.</summary>
    public Action<Guid, string, bool>? CommandRefused { get; set; }

    /// <summary>Send one frame to one client (queued like a broadcast).</summary>
    public void SendTo(Guid conn, byte[] frame)
    {
        if (_clients.TryGetValue(conn, out var c)) Enqueue(c, frame);
    }

    /// <summary>Told the id of every command that passed the authority gate, before
    /// <see cref="CommandHandler"/> runs it (the projector's lists, #604).</summary>
    public Action<string>? CommandAccepted { get; set; }

    public int ClientCount => _clients.Count;

    /// <summary>Owns the socket for its lifetime: seed, then drain until close.</summary>
    /// <param name="from">The browser's address, for the log.</param>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct, string? from = null)
    {
        var id = Guid.NewGuid();
        var client = new Client { Socket = socket, Name = id.ToString("N")[..6] };
        client.Sender = Task.Run(() => SendLoopAsync(client, ct));
        _clients[id] = client;
        string? ending = null;
        try
        {
            // Tell the client its id first (so it can recognise itself as the
            // controller), then claim control. Control is implicit and by connection
            // order: the first client to connect becomes the controller; Acquire is
            // denied for later clients (they observe). No take-over.
            Write(client, WireCodec.EncodeHello(id.ToString(), AgOpenWeb.Models.FeatureSwitches.Enabled));
            bool control = _authority.Acquire(id, "Browser");
            Log?.Invoke($"[Browser] {client.Name} connected{(from is null ? "" : " from " + from)}, "
                + (control ? "has control" : "observer") + $"; {_clients.Count} connected");
            try
            {
                foreach (var frame in SeedProvider?.Invoke() ?? Array.Empty<byte[]>())
                    Write(client, frame);
            }
            finally
            {
                // What was broadcast meanwhile, after the seed.
                lock (client.HeldLock)
                {
                    foreach (var frame in client.Held!) Write(client, frame);
                    client.Held = null;
                }
            }

            var buf = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await socket.ReceiveAsync(buf, ct).ConfigureAwait(false);
                Volatile.Write(ref client.HeardTicks, Environment.TickCount64);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    ending = "the page closed the link" + (res.CloseStatus is { } code
                        ? $" ({(int)code} {code}{(string.IsNullOrEmpty(res.CloseStatusDescription) ? "" : ": " + OneLine(res.CloseStatusDescription))})"
                        : "");
                    break;
                }
                // Commands are short single-frame text messages: "id" or "id|arg".
                if (res.MessageType == WebSocketMessageType.Text && res.Count > 0)
                {
                    var msg = System.Text.Encoding.UTF8.GetString(buf, 0, res.Count);
                    try { Dispatch(id, msg); } catch { /* a bad command must not drop the client */ }
                }
            }
        }
        catch (Exception ex)
        {
            // Client dropped — fall through to cleanup.
            ending = ex is OperationCanceledException && ct.IsCancellationRequested
                ? "the host is stopping"
                : $"the link failed ({ex.GetType().Name}: {ex.Message})";
        }
        finally
        {
            // A cut-off aborts the socket, which ends the receive loop with an error: the
            // cut-off is the reason.
            ending = Volatile.Read(ref client.CutOff) is { } cut ? "cut off: " + cut
                : ending ?? (ct.IsCancellationRequested ? "the host is stopping" : $"its socket is {socket.State}");
            bool held = _authority.IsHolder(id);
            long now = Environment.TickCount64;
            Log?.Invoke($"[Browser] {client.Name} left after {Span(now - client.ConnectedTicks)}: {ending}; "
                + $"last heard from it {Secs(now - Volatile.Read(ref client.HeardTicks))} ago"
                + (held ? "; it had control" : ""));
            _authority.Drop(id); // revoke + failsafe if this connection held control
            _clients.TryRemove(id, out _);
            Diagnostics.Left(id);
            client.Out.Writer.TryComplete();
            // Let the sender write what is queued, briefly; a socket that does not take it
            // is aborted (the close below would otherwise wait on it too).
            if (client.Sender is { } sender && await Task.WhenAny(sender, Task.Delay(1000)).ConfigureAwait(false) != sender)
                socket.Abort();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>The client's sender: its frames in order, one send at a time.</summary>
    private static async Task SendLoopAsync(Client c, CancellationToken ct)
    {
        try
        {
            var reader = c.Out.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (reader.TryRead(out var frame))
                {
                    await c.Socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct)
                        .ConfigureAwait(false);
                    Interlocked.Add(ref c.QueuedBytes, -frame.Length);
                    Interlocked.Decrement(ref c.Pending);
                    Volatile.Write(ref c.ProgressTicks, Environment.TickCount64);
                    Volatile.Write(ref c.SentTicks, Environment.TickCount64);
                }
        }
        catch (Exception ex)
        {
            // The socket failed or was aborted: the receive loop ends and cleans up.
            CutOff(c, $"sending to it failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Queue a frame for one client, after its seed if that is still being built.</summary>
    private void Enqueue(Client c, byte[] frame)
    {
        if (Volatile.Read(ref c.Held) != null)
            lock (c.HeldLock)
                if (c.Held != null) { c.Held.Add(frame); return; }
        Write(c, frame);
    }

    /// <summary>Queue a frame for one client now; cut it off when it has stopped reading.</summary>
    private void Write(Client c, byte[] frame)
    {
        if (Volatile.Read(ref c.CutOff) != null) return;
        long now = Environment.TickCount64;
        if (Interlocked.Increment(ref c.Pending) == 1) Volatile.Write(ref c.ProgressTicks, now);
        long queued = Interlocked.Add(ref c.QueuedBytes, frame.Length);
        if (!c.Out.Writer.TryWrite(frame)) return; // completed: the client is leaving
        long stalled = now - Volatile.Read(ref c.ProgressTicks);
        if (queued > MaxQueuedBytes || stalled > StallLimitMs)
            CutOff(c, $"it took nothing it was sent for {Secs(stalled)}, {queued / 1024} KB waiting");
    }

    /// <summary>Stop a client that does not read: the socket is aborted, its receive loop
    /// ends, and the usual clean-up runs (the failsafe too if it held control).</summary>
    private static void CutOff(Client c, string why)
    {
        if (Interlocked.CompareExchange(ref c.CutOff, why, null) != null) return;
        c.Out.Writer.TryComplete();
        try { c.Socket.Abort(); } catch { /* already gone */ }
    }

    // Route one inbound command from a connection. control.* manage actuation
    // authority; any other id runs through CommandHandler, but a restricted id is
    // dropped unless this connection holds fresh authority — the safety gate.
    private void Dispatch(Guid conn, string msg)
    {
        var bar = msg.IndexOf('|');
        var id = bar < 0 ? msg : msg[..bar];
        var arg = bar < 0 ? "" : msg[(bar + 1)..];

        switch (id)
        {
            case "control.acquire":
            {
                bool had = _authority.IsHolder(conn);
                if (_authority.Acquire(conn, arg) && !had) Log?.Invoke($"[Browser] {NameOf(conn)} took control");
                return;
            }
            case "control.release": _authority.Release(conn); return;
            case "control.presence": _authority.Refresh(conn); return;
            // Link-latency probe: reply on this connection (queued here, on the
            // receive-loop thread — no UI-thread hop) so the client's RTT measures the
            // link and what is queued for it, not host scheduling.
            case "diag.ping":
                if (_clients.TryGetValue(conn, out var pinger))
                    Enqueue(pinger, WireCodec.EncodePong(arg));
                return;
            // How this browser draws the map, for the bug report dump. From observers too: it
            // describes the sender's own device and changes nothing in the app.
            case "diag.client":
                Diagnostics.Report(conn, arg);
                return;
            // What the page saw of itself and its link (transport.js): hidden, frozen, its
            // timers paused, the device offline, a link it gave up. From observers too, so on
            // one line: a line break in it would write lines of its own in the log.
            case "diag.page":
                if (_clients.TryGetValue(conn, out var page) && Interlocked.Increment(ref page.PageNotes) <= MaxPageNotes)
                    Log?.Invoke($"[Browser] {page.Name} page: {OneLine(arg.Length > 300 ? arg[..300] : arg)}");
                return;
        }

        if (IsRestrictedCommand is { } restricted && restricted(id) && !_authority.HoldsFresh(conn))
        {
            // No fresh authority → dropped (an observer changes nothing). Say so to that
            // screen, or an edit typed there looks saved while the host kept its value.
            if (_clients.TryGetValue(conn, out var refused))
                CommandRefused?.Invoke(conn, id, refused.ShouldTellRefusal());
            return;
        }

        CommandAccepted?.Invoke(id);
        CommandHandler?.Invoke(id, arg);
    }

    /// <summary>Text from a browser, on one line in the log (CR and LF written as \r and \n).</summary>
    private static string OneLine(string text) =>
        text.IndexOfAny(new[] { '\r', '\n' }) < 0 ? text : text.Replace("\r", "\\r").Replace("\n", "\\n");

    private string NameOf(Guid conn) =>
        _clients.TryGetValue(conn, out var c) ? c.Name : conn.ToString("N")[..6];

    /// <summary>The state of a browser's link, for the deadman's line in the log.</summary>
    private string LinkState(Guid conn)
    {
        if (!_clients.TryGetValue(conn, out var c)) return "its link is already gone";
        long now = Environment.TickCount64;
        long sent = Volatile.Read(ref c.SentTicks);
        return $"its socket is {c.Socket.State}, last heard from it {Secs(now - Volatile.Read(ref c.HeardTicks))} ago, "
            + (sent == 0 ? "nothing sent to it yet" : $"last frame to it sent {Secs(now - sent)} ago") + ", "
            + $"{Volatile.Read(ref c.Pending)} frames ({Interlocked.Read(ref c.QueuedBytes) / 1024} KB) waiting to go to it";
    }

    private static string Secs(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Span(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s"
            : Secs(ms);
    }

    /// <summary>Queue one frame for every connected client. Returns at once: each client's
    /// own sender writes it, so a slow or silent browser holds up nobody else.</summary>
    public Task BroadcastAsync(byte[] frame, CancellationToken ct = default)
    {
        foreach (var kv in _clients) Enqueue(kv.Value, frame);
        return Task.CompletedTask;
    }
}
