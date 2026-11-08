// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// A browser on the cab's Wi-Fi, as far as the host can tell: a WebSocket to <c>/ws</c> that
/// reads every frame the host sends, and, for the operator's tablet, the presence heartbeat
/// the web client sends every 500 ms while it holds control (<c>js/control.js</c>). The
/// harness's own commands go to the host's command handler directly, as in the other
/// harnesses; the browsers are here for what the host sends them and for what happens to the
/// host when a browser goes away the ways a phone or a tablet does:
/// <list type="bullet">
/// <item><see cref="CloseAsync"/>: the page is closed (a clean WebSocket close).</item>
/// <item><see cref="Abort"/>: the browser is killed or the device reboots (the socket is reset).</item>
/// <item><see cref="Stall"/>: the device stops reading but the connection stays open, as a
/// phone does when its screen goes off in a pocket, or a tablet whose Wi-Fi drops without
/// a word: the host's sends pile up in the socket until its buffers are full.</item>
/// </list>
/// The socket's receive buffer is small (as a phone's is when it stops reading: the window
/// closes after a few kilobytes), so a stall is felt by the host within seconds, not minutes.
/// </summary>
public sealed class WebClient : IAsyncDisposable
{
    public string Name { get; }
    private readonly int _port;
    private readonly bool _operator;
    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private Task? _recv, _presence;
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    /// <summary>False while the device is stalled: no frame is read, no presence is sent.</summary>
    public bool Reading => Volatile.Read(ref _reading);
    private bool _reading = true;
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Connected => _ws?.State == WebSocketState.Open;

    /// <summary>How fast the device's Wi-Fi delivers, bytes a second (0: as fast as the host
    /// sends). A phone at the edge of the cab's Wi-Fi gets a few megabits.</summary>
    public double RateBytesPerSecond;
    /// <summary>The current connection is still loading its seed.</summary>
    public bool Loading { get { lock (_lock) return _seed is { } sd && (sd.AwaitSnapshot || WallMs - _connectedAtMs <= SeedWindowMs); } }
    public int Connects { get; private set; }

    // Totals over the day.
    public long Bytes => Interlocked.Read(ref _bytes);
    private long _bytes;
    public readonly long[] BytesByType = new long[64];
    public readonly long[] FramesByType = new long[64];

    /// <summary>Wall-clock ms of the last Tick frame (WireCodec type 2); -1 before the first.</summary>
    private double _lastTickMs = -1;
    /// <summary>The longest wait between two Tick frames since <see cref="TakeWindow"/>, ms,
    /// counting the wait still running.</summary>
    private double _maxGapMs;
    private int _ticksInWindow;
    private long _tickBytesInWindow;
    private double _windowStartMs;

    /// <summary>What the host sent a new connection in its first three seconds besides Ticks:
    /// the seed (scene, status, config, coverage snapshot...), and the ms until the first Tick
    /// (NaN until one comes).</summary>
    public sealed class Seed
    {
        /// <summary>The ms from connecting to the last frame of the seed (the coverage snapshot
        /// when there is one): how long the page took to load.</summary>
        public double LoadedMs = double.NaN;
        /// <summary>A CoverageInit came: the seed lasts until the snapshot after it.</summary>
        public bool AwaitSnapshot;
        public long Bytes, CoverageBytes;
        public readonly long[] ByType = new long[64];
        public int Frames;
        public double FirstTickMs = double.NaN;
        /// <summary>Why this connection is judged elsewhere (it connected while the phone was in
        /// the pocket: <c>MAP-FROZEN-BY-A-PHONE</c>), or null.</summary>
        public string? Note;
    }
    public readonly List<Seed> Seeds = new();
    /// <summary>Connections that have had their first Tick.</summary>
    public int Ticked { get { lock (_lock) return Seeds.Count(s => !double.IsNaN(s.FirstTickMs)); } }
    private Seed? _seed;
    private double _connectedAtMs;
    public const double SeedWindowMs = 3000;

    public WebClient(string name, int port, bool isOperator)
    {
        Name = name;
        _port = port;
        _operator = isOperator;
    }

    public double WallMs => _wall.Elapsed.TotalMilliseconds;

    /// <summary>Wall-clock ms since this browser last connected (its page loading), or infinity.</summary>
    public double SinceConnectMs => Connects == 0 ? double.PositiveInfinity : WallMs - _connectedAtMs;

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    // A phone's or a tablet's window once its browser stops reading.
                    ReceiveBufferSize = 8 * 1024,
                };
                await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        };
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        lock (_lock)
        {
            _seed = new Seed();
            Seeds.Add(_seed);
        }
        _connectedAtMs = WallMs;
        Volatile.Write(ref _reading, true);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), new HttpMessageInvoker(handler), _cts.Token);
        _ws = ws;
        Connects++;
        _lastTickMs = -1;
        var ct2 = _cts.Token;
        _recv = Task.Run(() => ReceiveLoop(ws, ct2));
        if (_operator) _presence = Task.Run(() => PresenceLoop(ws, ct2));
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        int type = -1; long frameLen = 0;
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (!Volatile.Read(ref _reading))
                {
                    await _resume.Task.WaitAsync(ct);
                    continue;
                }
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (RateBytesPerSecond > 0)
                {
                    // The Wi-Fi's pace: this many bytes take this long to arrive.
                    _rateDueMs = Math.Max(_rateDueMs, WallMs) + r.Count * 1000.0 / RateBytesPerSecond;
                    double wait = _rateDueMs - WallMs;
                    if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                }
                if (type < 0 && r.Count > 0) { type = buf[0]; frameLen = 0; }
                frameLen += r.Count;
                if (!r.EndOfMessage) continue;
                OnFrame(type, frameLen);
                type = -1;
            }
        }
        catch { /* closed, reset or cancelled */ }
    }

    private readonly object _lock = new();
    private double _rateDueMs;

    private void OnFrame(int type, long len)
    {
        double now = WallMs;
        lock (_lock)
        {
            Interlocked.Add(ref _bytes, len);
            if (type is >= 0 and < 64) { BytesByType[type] += len; FramesByType[type]++; }
            if (_seed is { } sd)
            {
                double t = now - _connectedAtMs;
                if (type == 2 && double.IsNaN(sd.FirstTickMs)) sd.FirstTickMs = t;
                // The seed: what comes in the first three seconds besides Ticks, and, when a
                // coverage init came, until the snapshot after it (a minute at most).
                if (type != 2 && (t <= SeedWindowMs || (sd.AwaitSnapshot && t <= 60_000)))
                {
                    sd.Bytes += len; sd.Frames++;
                    if (type is >= 0 and < 64) sd.ByType[type] += len;
                    if (type is 3 or 4) sd.CoverageBytes += len;
                    if (type == 3 && sd.Frames < 64) sd.AwaitSnapshot = true;
                    else if (type == 4) sd.AwaitSnapshot = false;
                    sd.LoadedMs = t;
                }
            }
            if (type == 2)
            {
                if (_lastTickMs >= 0) _maxGapMs = Math.Max(_maxGapMs, now - _lastTickMs);
                _lastTickMs = now;
                _ticksInWindow++;
                _tickBytesInWindow += len;
            }
        }
    }

    private async Task PresenceLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var msg = System.Text.Encoding.UTF8.GetBytes("control.presence");
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!Volatile.Read(ref _reading)) continue;   // a stalled device sends nothing either
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            }
        }
        catch { /* closed */ }
    }

    /// <summary>The device stops reading (and, for the tablet, sending): the connection stays open.</summary>
    public void Stall()
    {
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _reading, false);
    }

    /// <summary>The device reads again (and drains what piled up).</summary>
    public void Resume()
    {
        Volatile.Write(ref _reading, true);
        _resume.TrySetResult();
    }

    /// <summary>A Tick-frame summary since the last call: ticks per wall second, the longest
    /// gap between two ticks (including the one still open) and the mean tick frame size.</summary>
    public (double TicksPerSecond, double MaxGapMs, double MeanTickBytes) TakeWindow()
    {
        lock (_lock)
        {
            double now = WallMs;
            double span = Math.Max(1, now - _windowStartMs);
            double gap = _maxGapMs;
            if (_lastTickMs >= 0) gap = Math.Max(gap, now - Math.Max(_lastTickMs, _windowStartMs));
            var r = (_ticksInWindow * 1000.0 / span, gap, _ticksInWindow > 0 ? (double)_tickBytesInWindow / _ticksInWindow : 0);
            _windowStartMs = now; _maxGapMs = 0; _ticksInWindow = 0; _tickBytesInWindow = 0;
            return r;
        }
    }

    /// <summary>How long this browser, reading, has been without a Tick right now: ms since the
    /// last one, or since it connected; NaN while it is not connected or not reading.</summary>
    public double SinceLastTickMs
    {
        get
        {
            lock (_lock)
                return !Connected || !Reading ? double.NaN : WallMs - Math.Max(_lastTickMs, _connectedAtMs);
        }
    }

    /// <summary>The page is closed: a clean WebSocket close.</summary>
    public async Task CloseAsync()
    {
        var ws = _ws;
        if (ws == null) return;
        Resume();
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "page closed", t.Token);
        }
        catch { /* the host went first */ }
        _cts.Cancel();
        await WaitTasks();
        ws.Dispose();
        _ws = null;
    }

    /// <summary>The browser is killed: the socket is reset, no close frame.</summary>
    public async Task Abort()
    {
        var ws = _ws;
        if (ws == null) return;
        ws.Abort();
        _cts.Cancel();
        Resume();
        await WaitTasks();
        ws.Dispose();
        _ws = null;
    }

    private async Task WaitTasks()
    {
        foreach (var t in new[] { _recv, _presence })
            if (t != null) { try { await t.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        _recv = _presence = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
