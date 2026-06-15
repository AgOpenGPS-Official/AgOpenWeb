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
            foreach (var frame in SeedProvider?.Invoke() ?? Array.Empty<byte[]>())
                await SendToAsync(client, frame, ct).ConfigureAwait(false);

            var buf = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await socket.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) break;
                // Commands are short single-frame text messages (a command id).
                if (res.MessageType == WebSocketMessageType.Text && res.Count > 0 && CommandHandler is { } h)
                {
                    var cmdId = System.Text.Encoding.UTF8.GetString(buf, 0, res.Count);
                    try { h(cmdId); } catch { /* a bad command must not drop the client */ }
                }
            }
        }
        catch { /* client dropped — fall through to cleanup */ }
        finally
        {
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
