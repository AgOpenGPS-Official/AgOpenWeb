// Raw-WebSocket fan-out hub — the SignalR replacement. Holds the set of
// connected clients and broadcasts pre-encoded binary frames (WireCodec) to all
// of them. Each client has its own send gate so the broadcaster loop and the
// coverage event can't issue concurrent SendAsync on one socket (which the
// WebSocket API forbids). Inbound messages are drained but ignored for now —
// the receive loop exists to detect close and is where client→host commands
// will land in a later phase.

using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace AgValoniaGPS.RemoteServer;

public sealed class WebSocketHub
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ControlAuthority _authority;

    public WebSocketHub(ControlAuthority authority) => _authority = authority;

    /// <summary>Classifies a command id as Tier-2 (live actuation). Tier-2 commands
    /// are dropped unless the sending connection currently holds fresh authority.
    /// Set by the host (the app knows its command ids); null = nothing restricted.</summary>
    public Func<string, bool>? IsRestrictedCommand { get; set; }

    /// <summary>
    /// Frames sent to a client the instant it connects (current Scene + coverage
    /// snapshot). Set by <see cref="MapBroadcaster"/>; returns an empty list until then.
    /// </summary>
    public Func<IReadOnlyList<byte[]>>? SeedProvider { get; set; }

    /// <summary>
    /// Invoked (off the UI thread) with the command id of each inbound text
    /// message from a client. The host maps known ids to actions and marshals
    /// them to the UI thread; unknown ids are the host's no-op — that allowlist
    /// is the command-safety boundary. Null until the host wires it.
    /// </summary>
    public Action<string>? CommandHandler { get; set; }

    public int ClientCount => _clients.Count;

    /// <summary>Owns the socket for its lifetime: seed, then drain until close.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client { Socket = socket };
        _clients[id] = client;
        try
        {
            // Tell the client its id first (so it can recognise itself as the
            // controller), then claim control. Control is implicit and by connection
            // order: the first client to connect becomes the controller; Acquire is
            // denied for later clients (they observe). No take-over.
            await SendToAsync(client, WireCodec.EncodeHello(id.ToString()), ct).ConfigureAwait(false);
            _authority.Acquire(id, "Browser");
            foreach (var frame in SeedProvider?.Invoke() ?? Array.Empty<byte[]>())
                await SendToAsync(client, frame, ct).ConfigureAwait(false);

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
            client.Gate.Dispose();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    // Route one inbound command from a connection. control.* manage actuation
    // authority; any other id runs through CommandHandler, but a Tier-2 id is
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
        }

        if (IsRestrictedCommand is { } restricted && restricted(id) && !_authority.HoldsFresh(conn))
            return; // Tier-2 without fresh authority → dropped

        CommandHandler?.Invoke(id);
    }

    /// <summary>Send one frame to every connected client; drop any that fault.</summary>
    public async Task BroadcastAsync(byte[] frame, CancellationToken ct = default)
    {
        if (_clients.IsEmpty) return;
        var sends = _clients.Select(async kv =>
        {
            try { await SendToAsync(kv.Value, frame, ct).ConfigureAwait(false); }
            catch { _clients.TryRemove(kv.Key, out _); }
        });
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private static async Task SendToAsync(Client c, byte[] frame, CancellationToken ct)
    {
        await c.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await c.Socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally { c.Gate.Release(); }
    }
}
