// Minimal embedded HTTP/1.1 + WebSocket server on a raw TcpListener — the Kestrel
// replacement. ASP.NET Core has no iOS runtime pack (publish fails with NETSDK1082),
// so the embedded host cannot use Kestrel on mobile. This engine is pure BCL
// (System.Net.Sockets + System.Net.WebSockets), so it runs anywhere .NET runs,
// including under full iOS AOT — proven on a physical iPad by the AOT spike.
//
// It speaks just enough HTTP/1.1 to serve the embedded client assets and to upgrade
// GET /ws to a WebSocket via WebSocket.CreateFromStream — the exact type WebSocketHub
// already consumes. So the hub, WireCodec, and all projectors are reused unchanged;
// only the transport shell differs from the old Kestrel host.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace AgOpenWeb.RemoteServer;

public sealed class SimpleWebServer
{
    /// <summary>An HTTP response: status + content-type + body bytes + optional extra headers.</summary>
    public sealed record Response(
        int Status, string ContentType, byte[] Body,
        IReadOnlyList<(string Name, string Value)>? Headers = null)
    {
        public static Response Text(string body, string contentType,
            IReadOnlyList<(string, string)>? headers = null) =>
            new(200, contentType, Encoding.UTF8.GetBytes(body), headers);
        public static Response Bytes(byte[] body, string contentType,
            IReadOnlyList<(string, string)>? headers = null) =>
            new(200, contentType, body, headers);
        public static readonly Response NotFound = new(404, "text/plain", "not found"u8.ToArray());
        public static readonly Response MethodNotAllowed = new(405, "text/plain", "method not allowed"u8.ToArray());
    }

    private readonly int _port;
    private readonly Dictionary<string, Func<Task<Response>>> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Func<string, Task<Response>> Handler)> _prefix = new();
    private string? _wsPath;
    private Func<WebSocket, CancellationToken, Task>? _wsHandler;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxConcurrentConnections = 64;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _connGate = new(MaxConcurrentConnections, MaxConcurrentConnections);

    public int Port => _port;

    public SimpleWebServer(int port) => _port = port;

    // ---- route registration (called before StartAsync) ----

    public void MapGet(string path, Func<Task<Response>> handler) => _exact[path] = handler;
    public void MapGet(string path, Func<Response> handler) => _exact[path] = () => Task.FromResult(handler());
    public void MapGetPrefix(string prefix, Func<string, Task<Response>> handler) => _prefix.Add((prefix, handler));
    public void MapGetPrefix(string prefix, Func<string, Response> handler) =>
        _prefix.Add((prefix, s => Task.FromResult(handler(s))));
    public void MapWebSocket(string path, Func<WebSocket, CancellationToken, Task> handler)
    {
        _wsPath = path;
        _wsHandler = handler;
    }

    // ---- lifecycle ----

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port); // 0.0.0.0 — loopback + LAN
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; } // a transient accept fault must not kill the loop
            await _connGate.WaitAsync(ct).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try { await ServeConnectionAsync(client, ct).ConfigureAwait(false); }
                catch { /* one connection's fault must not affect others */ }
                finally { _connGate.Release(); }
            }, ct);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            // HTTP/1.1 keep-alive: serve successive requests on one socket until the
            // peer closes, asks to close, idles out, or upgrades to a WebSocket (which
            // takes the socket over for its lifetime).
            while (!ct.IsCancellationRequested)
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(IdleTimeout);
                var req = await ReadRequestHeadAsync(stream, reqCts.Token).ConfigureAwait(false);
                if (req is null) return; // closed, idle-timed-out, or malformed

                if (req.Method != "GET")
                {
                    await WriteResponseAsync(stream, Response.MethodNotAllowed, keepAlive: false, ct).ConfigureAwait(false);
                    return;
                }

                if (_wsPath is not null && req.Path == _wsPath
                    && req.Headers.TryGetValue("upgrade", out var up)
                    && up.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await UpgradeWebSocketAsync(stream, req, ct).ConfigureAwait(false);
                    return; // socket is now owned by the WebSocket handler
                }

                var resp = await RouteAsync(req).ConfigureAwait(false);
                var keepAlive = !req.Headers.TryGetValue("connection", out var conn)
                    || !conn.Contains("close", StringComparison.OrdinalIgnoreCase);
                if (!await WriteResponseAsync(stream, resp, keepAlive, ct).ConfigureAwait(false)) return;
                if (!keepAlive) return;
            }
        }
    }

    private async Task<Response> RouteAsync(Request req)
    {
        if (_exact.TryGetValue(req.Path, out var handler))
            return await handler().ConfigureAwait(false);
        foreach (var (prefix, h) in _prefix)
            if (req.Path.StartsWith(prefix, StringComparison.Ordinal))
                return await h(Uri.UnescapeDataString(req.Path[prefix.Length..])).ConfigureAwait(false);
        return Response.NotFound;
    }

    // ---- HTTP request parsing ----

    private sealed class Request
    {
        public required string Method;
        public required string Path;   // path only (query string stripped)
        public required Dictionary<string, string> Headers; // case-insensitive keys
    }

    // Reads the request head one byte at a time up to the blank line. Byte-by-byte is
    // deliberate (not just simple): it stops exactly at "\r\n\r\n" and leaves any
    // following bytes (e.g. the first WebSocket frames) untouched in the socket buffer,
    // which a BufferedStream would swallow.
    private static async Task<Request?> ReadRequestHeadAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        int nlRun = 0;
        while (sb.Length < MaxHeaderBytes)
        {
            int n;
            try { n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false); }
            catch { return null; }
            if (n == 0) return null;
            char c = (char)one[0];
            sb.Append(c);
            if (c == '\n') { if (++nlRun == 2) break; }
            else if (c != '\r') nlRun = 0;
        }

        var lines = sb.ToString().Split("\r\n");
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var target = first[1];
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) break;
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }
        return new Request { Method = first[0], Path = path, Headers = headers };
    }

    private static async Task<bool> WriteResponseAsync(Stream stream, Response r, bool keepAlive, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(StatusText(r.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        if (r.Headers is not null)
            foreach (var (name, value) in r.Headers)
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
            if (r.Body.Length > 0) await stream.WriteAsync(r.Body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; } // peer went away mid-write — drop the connection
    }

    private static string StatusText(int s) => s switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        431 => "Request Header Fields Too Large",
        _ => "OK",
    };

    // ---- WebSocket upgrade (RFC 6455) ----

    private async Task UpgradeWebSocketAsync(NetworkStream stream, Request req, CancellationToken ct)
    {
        if (_wsHandler is null) return;
        if (!req.Headers.TryGetValue("sec-websocket-key", out var key))
        {
            await WriteResponseAsync(stream, new Response(400, "text/plain", "missing key"u8.ToArray()),
                keepAlive: false, ct).ConfigureAwait(false);
            return;
        }

        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + magic)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { return; }

        // Wrap the upgraded stream as a server WebSocket — the same type the production
        // WebSocketHub.HandleAsync already owns (seed, drain, close).
        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); // server shutdown cancels clients
        try { await _wsHandler(socket, linked.Token).ConfigureAwait(false); }
        catch { /* the hub owns the socket lifetime; a fault just ends this client */ }
        finally { try { socket.Dispose(); } catch { } }
    }
}
