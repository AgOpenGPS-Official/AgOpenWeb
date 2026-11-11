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

using System.Collections.Concurrent;
using System.Diagnostics;
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
        public Channel<byte[]> Out { get; } = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public long QueuedBytes;
        public int Pending;
        /// <summary>When the queue last moved (a send finished, or it went from empty to not).</summary>
        public long ProgressTicks = Environment.TickCount64;
        public Task? Sender;
        public int CutOff;
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

    public WebSocketHub(ControlAuthority authority) => _authority = authority;

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
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client { Socket = socket };
        client.Sender = Task.Run(() => SendLoopAsync(client, ct));
        _clients[id] = client;
        try
        {
            // Tell the client its id first (so it can recognise itself as the
            // controller), then claim control. Control is implicit and by connection
            // order: the first client to connect becomes the controller; Acquire is
            // denied for later clients (they observe). No take-over.
            Write(client, WireCodec.EncodeHello(id.ToString()));
            _authority.Acquire(id, "Browser");
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
                if (res.MessageType == WebSocketMessageType.Close) break;
                // Commands are short single-frame text messages: "id" or "id|arg".
                if (res.MessageType == WebSocketMessageType.Text && res.Count > 0)
                {
                    var msg = System.Text.Encoding.UTF8.GetString(buf, 0, res.Count);
                    try { Dispatch(id, msg); } catch { /* a bad command must not drop the client */ }
                }
            }
        }
        catch { /* client dropped — fall through to cleanup */ }
        finally
        {
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
                }
        }
        catch
        {
            // The socket failed or was aborted: the receive loop ends and cleans up.
            CutOff(c);
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
        if (Volatile.Read(ref c.CutOff) != 0) return;
        long now = Environment.TickCount64;
        if (Interlocked.Increment(ref c.Pending) == 1) Volatile.Write(ref c.ProgressTicks, now);
        long queued = Interlocked.Add(ref c.QueuedBytes, frame.Length);
        if (!c.Out.Writer.TryWrite(frame)) return; // completed: the client is leaving
        if (queued > MaxQueuedBytes || now - Volatile.Read(ref c.ProgressTicks) > StallLimitMs)
        {
            Debug.WriteLine($"[ws] client cut off: {queued / 1024} KB queued, no progress for {now - Volatile.Read(ref c.ProgressTicks)} ms");
            CutOff(c);
        }
    }

    /// <summary>Stop a client that does not read: the socket is aborted, its receive loop
    /// ends, and the usual clean-up runs (the failsafe too if it held control).</summary>
    private static void CutOff(Client c)
    {
        if (Interlocked.Exchange(ref c.CutOff, 1) != 0) return;
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
            case "control.acquire": _authority.Acquire(conn, arg); return;
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

    /// <summary>Queue one frame for every connected client. Returns at once: each client's
    /// own sender writes it, so a slow or silent browser holds up nobody else.</summary>
    public Task BroadcastAsync(byte[] frame, CancellationToken ct = default)
    {
        foreach (var kv in _clients) Enqueue(kv.Value, frame);
        return Task.CompletedTask;
    }
}
