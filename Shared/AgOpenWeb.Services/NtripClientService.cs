// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// NTRIP client for receiving RTK correction data from base station
/// Forwards RTCM3 corrections to GPS module via UDP port 2233
/// Based on AgIO NTRIP implementation
/// </summary>
public class NtripClientService : INtripClientService, IDisposable
{
    public event EventHandler<NtripConnectionEventArgs>? ConnectionStatusChanged;
    public event EventHandler<RtcmDataReceivedEventArgs>? RtcmDataReceived;

    private Socket? _tcpSocket;
    private Socket? _udpSocket;
    private readonly byte[] _receiveBuffer = new byte[4096];
    private readonly List<byte> _headerBuffer = new List<byte>();
    private bool _headerDumped = false;
    private CancellationTokenSource? _cancellationTokenSource;
    private NtripConfiguration? _config;
    private bool _isDisposed;

    private IPEndPoint? _rtcmUdpEndpoint;
    private string? _rtcmSubnet; // the /24 _rtcmUdpEndpoint was built for
    private Timer? _ggaTimer;
    private Timer? _watchdogTimer;
    // The caster's stream is framed into RTCM messages (_rtcmStats counts them by type) and
    // each whole message is queued for the paced sender: 256-byte datagrams 25 ms apart,
    // AgIO-style, because unpaced back-to-back datagrams of a post-pause TCP backlog stalled
    // the AiO board (#169). On a backlog the queue keeps the newest observation epoch and the
    // newest station data, and only ever drops whole messages. See RtcmQueue and
    // Plans/RTCM_FORWARDING_PLAN.md (Phase 2).
    private readonly RtcmQueue _pacer = new();
    private readonly RtcmStreamStats _rtcmStats = new();
    private ChunkedDecoder? _chunked;   // set when the caster's reply is chunked
    // No RTCM 3 found in the first UnframedAfterBytes of the session: the mount point sends
    // something else (RTCM 2, CMR, a raw receiver format). Forward it byte for byte, as
    // before, rather than nothing.
    private bool _unframed;
    private const int UnframedAfterBytes = 4096;
    private long _healthLineSuperseded;
    private Dictionary<int, long> _healthLineCounts = new(); // per-type counts at the last health line
    private readonly SemaphoreSlim _sendSignal = new(0);

    // #169 diagnostics: a read this large means TCP coalesced a backlog (a 1 Hz epoch is
    // usually smaller). Logged with the gap before it; repeats throttled unless after a pause.
    private const int LARGE_READ_BYTES = 512;
    private const double LARGE_READ_PAUSE_SECONDS = 2.0;
    private const double LARGE_READ_LOG_THROTTLE_SECONDS = 10.0;
    private long _lastLargeReadLogTimestamp;
    private int _largeReadsSuppressed;

    // ── Stall watchdog ────────────────────────────────────────────────────
    // Logs a 5 s health line so operators can distinguish caster pauses from
    // app brokenness. Triggers a reconnect at WATCHDOG_RECONNECT_SECONDS of
    // no data on the wire (catches silent half-open TCP and caster keep-alive
    // timeout cases that the receive loop wouldn't notice on its own).
    private long _lastRtcmReceivedTimestamp;
    private const double WATCHDOG_TIMER_INTERVAL_MS = 5000.0;
    private const double WATCHDOG_RECONNECT_SECONDS = 30.0;

    // ── Reconnect with backoff ────────────────────────────────────────────
    // Triggered by any failure (receive error, send error, watchdog stall).
    // Backoff schedule: 1s, 2s, 4s, 8s, 15s — then hold at 15s indefinitely.
    // Small glitches recover quickly; long outages keep retrying without
    // hammering the caster. Each reconnect is a full TCP teardown + new
    // ConnectAsync (NTRIP is HTTP-style stateless — no session resume).
    private static readonly int[] BackoffScheduleSec = new[] { 1, 2, 4, 8, 15 };
    private int _reconnectInProgress;  // 0/1 flag, atomic via Interlocked
    // Failed attempts since corrections last flowed. The backoff continues from here, so a
    // caster that accepts the TCP connection but keeps turning us away isn't retried every
    // second forever; reset by real RTCM or a new ConnectAsync.
    private int _failureStreak;
    private CancellationTokenSource? _reconnectCts;

    // Cap header accumulation to prevent memory-exhaustion DoS from a
    // malicious caster — or a MITM on the path — streaming bytes without
    // the \r\n\r\n terminator. Real caster headers are well under 1 KB;
    // 8 KiB is generous. See issue #286 / threat model finding F2.
    private const int MaxHeaderBytes = 8 * 1024;
    private readonly IGpsService _gpsService;
    private readonly ILogger<NtripClientService> _logger;

    /// <summary>The caster accepted the request (200); cleared on any disconnect.</summary>
    public bool IsConnected { get; private set; }
    /// <summary>A connection was requested and not disconnected: connected, connecting,
    /// retrying, or stopped after the caster rejected it.</summary>
    public bool IsActive => Volatile.Read(ref _wanted);
    public ulong TotalBytesReceived { get; private set; }

    private bool _wanted;      // ConnectAsync called, DisconnectAsync not
    private bool _sessionOpen; // sockets open: connecting, waiting for the reply, or streaming
    private bool _rtcmSeen;    // an RTCM3 frame (0xD3) arrived since the caster accepted
    private readonly object _sessionLock = new();
    private int _generation;   // bumped by every Teardown; a session only tears down its own

    public NtripClientService(IGpsService gpsService, ILogger<NtripClientService> logger)
    {
        _gpsService = gpsService;
        _logger = logger;
    }

    public async Task ConnectAsync(NtripConfiguration config)
    {
        CancelReconnect(); // a new request replaces any retry loop
        Volatile.Write(ref _wanted, true);
        Interlocked.Exchange(ref _failureStreak, 0);
        await ConnectCoreAsync(config);
    }

    /// <summary>
    /// Open a session: resolve, connect, send the request and start the receive loop.
    /// IsConnected stays false until the caster's reply is checked (ReceiveLoop).
    /// </summary>
    private async Task ConnectCoreAsync(NtripConfiguration config)
    {
        Teardown(null);
        int gen = Volatile.Read(ref _generation);
        _config = config;

        Socket? tcp = null, udp = null;
        try
        {
            // UDP socket for forwarding RTCM data to the GPS module (subnet.255:2233)
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var rtcmEndpoint = new IPEndPoint(
                IPAddress.Parse($"{config.SubnetAddress}.255"),
                config.UdpForwardPort);

            // Resolved on every (re)connect, so a caster that moves (dynamic DNS) is followed.
            IPAddress casterIP = await ResolveCasterAsync(config.CasterAddress);
            tcp = new Socket(casterIP.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            RaiseStatus(false, $"Connecting to {config.CasterAddress}:{config.CasterPort}/{config.MountPoint}…");
            await tcp.ConnectAsync(new IPEndPoint(casterIP, config.CasterPort));

            CancellationToken token;
            Socket sock = tcp;
            lock (_sessionLock)
            {
                // A newer Connect/Disconnect ran meanwhile: this attempt is stale.
                if (gen != _generation) throw new OperationCanceledException("superseded");
                _tcpSocket = tcp;
                _udpSocket = udp;
                _rtcmUdpEndpoint = rtcmEndpoint;
                _rtcmSubnet = config.SubnetAddress;
                tcp = udp = null; // owned by the session now; Teardown closes them
                _headerBuffer.Clear();
                _headerDumped = false;
                _rtcmSeen = false;
                _rtcmStats.Reset();
                _chunked = null;
                _unframed = false;
                _healthLineCounts = new();
                TotalBytesReceived = 0;
                _sessionOpen = true;
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            await SendNtripRequestAsync();

            // The loop gets its own socket, so a stale loop can never read a newer session's.
            _ = Task.Run(() => ReceiveLoop(sock, gen, token));
            _ = Task.Run(() => SendLoop(token));

            // GGA only goes out once the caster has accepted (the callback checks IsConnected).
            if (config.GgaIntervalSeconds > 0)
            {
                _ggaTimer = new Timer(
                    GgaTimerCallback,
                    null,
                    TimeSpan.FromSeconds(5), // First GGA after 5 seconds
                    TimeSpan.FromSeconds(config.GgaIntervalSeconds));
            }

            // Stall watchdog — drives the periodic [NTRIP] health log line and reconnects
            // if nothing has arrived for WATCHDOG_RECONNECT_SECONDS, including a caster
            // that never answers the request (catches silent half-open TCP too).
            _lastRtcmReceivedTimestamp = Clock.Current.GetTimestamp();
            _watchdogTimer = new Timer(
                WatchdogTimerCallback,
                null,
                TimeSpan.FromMilliseconds(WATCHDOG_TIMER_INTERVAL_MS),
                TimeSpan.FromMilliseconds(WATCHDOG_TIMER_INTERVAL_MS));
        }
        catch (Exception ex)
        {
            // Don't leak the sockets of a failed attempt.
            tcp?.Dispose();
            udp?.Dispose();
            if (Teardown(null, gen)) RaiseStatus(false, $"Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Caster IP: a literal address, or the host's IPv4 address when it has one
    /// (else its first address; the socket is created for that address family).</summary>
    private static async Task<IPAddress> ResolveCasterAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new Exception($"Could not resolve caster host '{host}'");
    }

    public async Task DisconnectAsync()
    {
        bool wasActive = Volatile.Read(ref _wanted) || _sessionOpen;
        Volatile.Write(ref _wanted, false);
        CancelReconnect(); // otherwise the retry loop reconnects after the user stopped it
        Teardown(wasActive ? "Disconnected" : null);
        await Task.CompletedTask;
    }

    private void CancelReconnect()
    {
        try { _reconnectCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Close the current session (timers, receive loop, sockets). Raises a
    /// not-connected status with <paramref name="message"/> when one is given. With
    /// <paramref name="onlyGeneration"/>, does nothing (returns false) if a newer
    /// session has replaced that one.</summary>
    private bool Teardown(string? message, int? onlyGeneration = null)
    {
        lock (_sessionLock)
        {
            if (onlyGeneration is { } g && g != _generation) return false;
            _generation++;

            _ggaTimer?.Dispose();
            _ggaTimer = null;

            _watchdogTimer?.Dispose();
            _watchdogTimer = null;

            _cancellationTokenSource?.Cancel();
            _pacer.Clear(); // a new session must not inherit the old one's corrections

            _tcpSocket?.Close();
            _tcpSocket?.Dispose();
            _tcpSocket = null;

            _udpSocket?.Close();
            _udpSocket?.Dispose();
            _udpSocket = null;

            _sessionOpen = false;
            IsConnected = false;
        }

        if (message != null) RaiseStatus(false, message);
        return true;
    }

    private void RaiseStatus(bool connected, string message) =>
        ConnectionStatusChanged?.Invoke(this, new NtripConnectionEventArgs { IsConnected = connected, Message = message });

    private async Task SendNtripRequestAsync()
    {
        if (_tcpSocket == null || _config == null) return;

        // Build NTRIP request (HTTP GET with Basic Auth)
        // Use NTRIP 1.0 compatible format (simpler, more widely supported)
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_config.Username}:{_config.Password}"));

        // Build request string manually with explicit \r\n to ensure correct formatting.
        // NTRIP protocol is invariant — interpolations must not pick up locale formatting.
        var inv = CultureInfo.InvariantCulture;
        var request = new StringBuilder();
        request.Append(inv, $"GET /{_config.MountPoint} HTTP/1.1\r\n");
        request.Append(inv, $"Host: {_config.CasterAddress}\r\n");
        request.Append("User-Agent: NTRIP AgOpenWeb/1.0\r\n");
        request.Append(inv, $"Authorization: Basic {credentials}\r\n");
        request.Append("Accept: */*\r\n");
        request.Append("Connection: keep-alive\r\n");
        request.Append("\r\n");

        string requestStr = request.ToString();
        byte[] requestBytes = Encoding.ASCII.GetBytes(requestStr);
        await _tcpSocket.SendAsync(requestBytes, SocketFlags.None);
    }

    private async Task ReceiveLoop(Socket socket, int gen, CancellationToken cancellationToken)
    {
        bool headerReceived = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int bytesReceived = await socket.ReceiveAsync(
                    new ArraySegment<byte>(_receiveBuffer),
                    SocketFlags.None,
                    cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                if (bytesReceived > 0)
                {
                    // First response is HTTP header - check for success
                    if (!headerReceived)
                    {
                        // Bail if the header has grown past the cap without a
                        // terminator. Without this an unbounded caster could
                        // OOM the tablet by streaming bytes forever.
                        if (_headerBuffer.Count + bytesReceived > MaxHeaderBytes)
                        {
                            _logger.LogWarning(
                                "NTRIP header exceeded {Max} bytes without \\r\\n\\r\\n terminator; disconnecting",
                                MaxHeaderBytes);
                            Teardown("Caster sent an invalid reply", gen);
                            return;
                        }

                        // Accumulate header bytes
                        for (int i = 0; i < bytesReceived; i++)
                        {
                            _headerBuffer.Add(_receiveBuffer[i]);
                        }

                        // Dump header bytes once for debugging
                        if (!_headerDumped && _headerBuffer.Count >= 10)
                        {
                            _headerDumped = true;
                            int dumpSize = Math.Min(100, _headerBuffer.Count);
                            string headerPreview = Encoding.ASCII.GetString(_headerBuffer.Take(dumpSize).ToArray());
                            _logger.LogDebug("Response header: {Header}", headerPreview.Replace("\r\n", " "));
                        }

                        // Find header/body boundary
                        // ICY protocol uses single \r\n, HTTP uses \r\n\r\n
                        int headerEnd = -1;
                        int dataStart = -1;

                        // First check for ICY single line response (just \r\n)
                        for (int i = 0; i < _headerBuffer.Count - 1; i++)
                        {
                            if (_headerBuffer[i] == '\r' && _headerBuffer[i + 1] == '\n')
                            {
                                // Check if this looks like ICY response
                                if (i < 50)
                                {
                                    string testHeader = Encoding.ASCII.GetString(_headerBuffer.ToArray(), 0, i);
                                    if (testHeader.StartsWith("ICY 200"))
                                    {
                                        headerEnd = i;
                                        dataStart = i + 2; // After \r\n
                                        break;
                                    }
                                }

                                // Check for HTTP \r\n\r\n
                                if (i + 3 < _headerBuffer.Count &&
                                    _headerBuffer[i + 2] == '\r' && _headerBuffer[i + 3] == '\n')
                                {
                                    headerEnd = i;
                                    dataStart = i + 4; // After \r\n\r\n
                                    break;
                                }
                            }
                        }

                        if (headerEnd >= 0)
                        {
                            // Parse header as ASCII string
                            string response = Encoding.ASCII.GetString(_headerBuffer.ToArray(), 0, headerEnd);
                            var (reply, reason) = NtripResponse.Classify(response);

                            if (reply == NtripReply.Accepted)
                            {
                                headerReceived = true;
                                IsConnected = true;
                                _logger.LogInformation("Connected and authorized, receiving RTCM data");
                                // An NTRIP 2 caster may send the stream chunked: decode it, or the
                                // chunk-size lines land inside RTCM messages and break them.
                                bool chunked = NtripResponse.IsChunked(response);
                                _rtcmStats.Reset(chunked);
                                _chunked = chunked ? new ChunkedDecoder() : null;
                                if (chunked)
                                    _logger.LogInformation("[NTRIP] the caster replied with Transfer-Encoding: chunked; decoding it");
                                var c = _config;
                                RaiseStatus(true, c == null ? "Connected"
                                    : $"Connected to {c.CasterAddress}:{c.CasterPort}/{c.MountPoint}");

                                // Forward any RTCM data after header
                                if (dataStart < _headerBuffer.Count)
                                {
                                    int rtcmBytes = _headerBuffer.Count - dataStart;
                                    byte[] rtcmData = new byte[rtcmBytes];
                                    _headerBuffer.CopyTo(dataStart, rtcmData, 0, rtcmBytes);
                                    ForwardRtcmData(rtcmData);
                                }

                                // Clear header buffer
                                _headerBuffer.Clear();
                            }
                            else
                            {
                                _logger.LogWarning("NTRIP caster rejected the request: {Response}", response);
                                if (reply == NtripReply.RejectedRetry) TriggerReconnect(reason);
                                else Teardown("Rejected: " + reason, gen); // wrong mount/login won't fix itself
                                return;
                            }
                        }
                        // If no complete header yet, accumulate more data
                    }
                    else
                    {
                        // All subsequent data is RTCM3 corrections - forward as raw bytes
                        byte[] rtcmData = new byte[bytesReceived];
                        Array.Copy(_receiveBuffer, rtcmData, bytesReceived);
                        ForwardRtcmData(rtcmData);
                    }
                }
                else
                {
                    // Connection closed by server (FIN). NTRIP has no resume,
                    // so kick the backoff reconnect loop. (#334) A close before any
                    // RTCM means the caster turned us away (AgOpenGPS #1219): some
                    // reply 200 and then close for a bad mount point or account.
                    string reason;
                    if (!headerReceived)
                        reason = _headerBuffer.Count > 0
                            ? NtripResponse.Classify(Encoding.ASCII.GetString(_headerBuffer.ToArray())).Reason
                            : "Caster closed the connection without answering";
                    else if (!_rtcmSeen)
                        reason = "Caster closed the connection before sending corrections (mount point or account rejected?)";
                    else
                        reason = "caster sent FIN";
                    _logger.LogInformation("Connection closed by caster: {Reason}", reason);
                    TriggerReconnect(reason);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A socket closed by Disconnect/Teardown lands here too — not a failure.
                if (cancellationToken.IsCancellationRequested) break;
                _logger.LogError(ex, "Receive error");
                TriggerReconnect($"receive error: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>The /24 to forward RTCM to: the provider's live subnet, else the configured one.</summary>
    internal static string ResolveRtcmSubnet(NtripConfiguration config)
    {
        string? live = null;
        try { live = config.SubnetProvider?.Invoke(); } catch { /* fall back to the setting */ }
        return string.IsNullOrEmpty(live) ? config.SubnetAddress : live;
    }

    /// <summary>
    /// subnet.255:port for the modules' current /24 (<see cref="NtripConfiguration.SubnetProvider"/>,
    /// else <see cref="NtripConfiguration.SubnetAddress"/>), as AgIO sends to its subnet
    /// setting. Rebuilt only when the subnet changes.
    /// </summary>
    private IPEndPoint? CurrentRtcmEndpoint()
    {
        var config = _config;
        var endpoint = _rtcmUdpEndpoint;
        if (config == null || endpoint == null) return endpoint;

        string subnet = ResolveRtcmSubnet(config);
        if (subnet == _rtcmSubnet) return endpoint;

        if (!IPAddress.TryParse($"{subnet}.255", out var broadcast)) return endpoint;
        endpoint = new IPEndPoint(broadcast, config.UdpForwardPort);
        _logger.LogInformation("[NTRIP] forwarding RTCM to {Endpoint}", endpoint);
        _rtcmSubnet = subnet;
        _rtcmUdpEndpoint = endpoint;
        return endpoint;
    }

    private void ForwardRtcmData(byte[] rtcmData)
    {
        if (rtcmData.Length == 0)
            return;

        long now = Clock.Current.GetTimestamp();
        double secondsSincePrevious =
            Clock.Current.ElapsedMs(Volatile.Read(ref _lastRtcmReceivedTimestamp), now) / 1000.0;
        if (rtcmData.Length > LARGE_READ_BYTES)
            LogLargeRead(rtcmData.Length, secondsSincePrevious, now);

        // Frame the stream and queue each whole message for the paced sender (SendLoop).
        var decoder = _chunked;
        if (decoder != null)
        {
            bool wasBroken = decoder.IsBroken;
            decoder.Feed(rtcmData, QueueStreamBytes);
            if (decoder.IsBroken && !wasBroken)
                _logger.LogWarning("[NTRIP] the caster announced chunked transfer encoding but the stream is not chunked; forwarding it as it comes");
        }
        else
        {
            QueueStreamBytes(rtcmData);
        }
        _sendSignal.Release();

        if (!_rtcmSeen && Array.IndexOf(rtcmData, (byte)0xD3) >= 0)
        {
            _rtcmSeen = true;
            Interlocked.Exchange(ref _failureStreak, 0); // corrections flow: next outage starts at 1 s
        }
        TotalBytesReceived += (ulong)rtcmData.Length;
        Volatile.Write(ref _lastRtcmReceivedTimestamp, now);
        RtcmDataReceived?.Invoke(this, new RtcmDataReceivedEventArgs { BytesReceived = rtcmData.Length });
    }

    /// <summary>Stream bytes (chunking removed) → whole RTCM messages → the send queue.</summary>
    private void QueueStreamBytes(ReadOnlySpan<byte> data)
    {
        if (_unframed)
        {
            _pacer.Enqueue(RtcmQueue.Opaque, data);
            return;
        }

        _rtcmStats.Feed(data, _pacer.Enqueue);

        if (_rtcmStats.Messages == 0 && _rtcmStats.BytesSkipped >= UnframedAfterBytes)
        {
            _unframed = true;
            _logger.LogWarning(
                "[NTRIP] no RTCM 3 messages in the first {Bytes} bytes from the caster; forwarding the stream unframed, byte for byte",
                _rtcmStats.BytesSkipped);
        }
    }

    /// <summary>Log a read over LARGE_READ_BYTES. One that follows a pause is always logged
    /// (it is the #169 resume burst); others at most once per throttle window, since a
    /// heavy MSM7 stream can exceed the threshold every epoch.</summary>
    private void LogLargeRead(int bytes, double secondsSincePrevious, long now)
    {
        bool afterPause = secondsSincePrevious >= LARGE_READ_PAUSE_SECONDS;
        long lastLog = _lastLargeReadLogTimestamp;
        if (!afterPause && lastLog != 0 &&
            Clock.Current.ElapsedMs(lastLog, now) / 1000.0 < LARGE_READ_LOG_THROTTLE_SECONDS)
        {
            _largeReadsSuppressed++;
            return;
        }
        _lastLargeReadLogTimestamp = now;
        int suppressed = _largeReadsSuppressed;
        _largeReadsSuppressed = 0;
        _logger.LogInformation(
            "[NTRIP] large RTCM read: {Bytes} bytes, {Gap:F1}s since previous RTCM, {Queued} bytes queued ({Suppressed} similar reads not logged)",
            bytes, secondsSincePrevious, _pacer.Count, suppressed);
    }

    /// <summary>
    /// Sends queued RTCM to the AiO, one datagram of at most RtcmQueue.ChunkSize bytes per
    /// RtcmQueue.IntervalMs on average. Sleeps on _sendSignal while the queue is empty, so the
    /// first datagram of an epoch goes out as soon as it arrives. Runs per session (its token).
    ///
    /// Task.Delay is often late (15 ms timer steps on Windows, 40–50 ms wake-ups on Android),
    /// which used to cost throughput. The queue now turns that lateness into shorter gaps
    /// (down to RtcmQueue.MinGapMs); those short waits are spun out here, because a timer
    /// cannot keep them (RTCM plan, Phase 3).
    /// </summary>
    private async Task SendLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _sendSignal.WaitAsync(token);
                while (_pacer.Count > 0 && !token.IsCancellationRequested)
                {
                    if (_pacer.TryDequeue(out byte[] chunk))
                        SendRtcmDatagram(chunk);

                    double wait = _pacer.MsUntilDue();
                    if (_pacer.Count == 0 || wait <= 0) continue;
                    if (wait < RtcmQueue.IntervalMs - 1)
                        SpinFor(wait, token);   // a catch-up gap: at most MaxCreditMs of these in a row
                    else
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait)), token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Wait a few milliseconds precisely, without a timer.</summary>
    private static void SpinFor(double milliseconds, CancellationToken token)
    {
        long until = System.Diagnostics.Stopwatch.GetTimestamp()
                     + (long)(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < until && !token.IsCancellationRequested)
            Thread.SpinWait(64);
    }

    private void SendRtcmDatagram(byte[] chunk)
    {
        var udpSocket = _udpSocket;
        var endpoint = CurrentRtcmEndpoint();
        if (udpSocket == null || endpoint == null) return;
        try
        {
            udpSocket.SendTo(chunk, endpoint);
        }
        catch (ObjectDisposedException)
        {
            // Session torn down mid-send.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward RTCM data");
        }
    }

    private void GgaTimerCallback(object? state)
    {
        if (!IsConnected || _config == null) return;

        try
        {
            string ggaSentence;

            if (_config.UseManualPosition)
            {
                // Use manual position
                ggaSentence = GenerateGgaSentence(
                    _config.ManualLatitude,
                    _config.ManualLongitude,
                    0, // altitude
                    4, // fix quality (RTK fixed)
                    12); // satellites
            }
            else
            {
                // Use GPS position from GpsService
                var gpsData = _gpsService.CurrentData;
                if (gpsData != null && gpsData.IsValid)
                {
                    ggaSentence = GenerateGgaSentence(
                        gpsData.CurrentPosition.Latitude,
                        gpsData.CurrentPosition.Longitude,
                        gpsData.CurrentPosition.Altitude,
                        gpsData.FixQuality,
                        gpsData.SatellitesInUse);
                }
                else
                {
                    // No GPS data available yet - send default position (center of US)
                    // This allows caster to start sending corrections
                    ggaSentence = GenerateGgaSentence(
                        39.8283, // Latitude (Kansas, US)
                        -98.5795, // Longitude
                        0, // altitude
                        1, // fix quality (GPS fix)
                        8); // satellites
                }
            }

            _ = SendGgaSentenceAsync(ggaSentence);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NTRIP: GGA timer error: {ex.Message}");
        }
    }

    private void WatchdogTimerCallback(object? state)
    {
        if (!_sessionOpen) return; // also while waiting for the caster's reply

        long now = Clock.Current.GetTimestamp();
        long last = Volatile.Read(ref _lastRtcmReceivedTimestamp);
        double secondsSinceData = Clock.Current.ElapsedMs(last, now) / 1000.0;

        // Periodic health line — once per WATCHDOG_TIMER_INTERVAL_MS regardless
        // of state. Operators read this to tell "caster paused" from
        // "AgOpenWeb broke" without needing the debug log.
        var gps = _gpsService.CurrentData;
        var snap = _rtcmStats.Snapshot();
        long superseded = _pacer.SupersededObservations + _pacer.SupersededOther + _pacer.MemoryGuardDrops;
        long newlySuperseded = superseded - _healthLineSuperseded;
        _healthLineSuperseded = superseded;
        _logger.LogInformation(
            "[NTRIP] last RTCM {Sec:F1}s ago, total {Bytes} bytes; messages since last line: {Messages}; not sent (replaced by newer) {Superseded}; session checksum failures {Crc}, skipped {Skipped} bytes; receiver fix {Fix}, differential age {Age:F1}s",
            secondsSinceData, TotalBytesReceived, DescribeNewMessages(snap), newlySuperseded,
            snap.ChecksumFailures, snap.BytesSkipped, gps?.FixQuality ?? 0, gps?.DifferentialAge ?? 0);

        if (secondsSinceData >= WATCHDOG_RECONNECT_SECONDS)
        {
            TriggerReconnect(IsConnected
                ? $"no RTCM for {secondsSinceData:F1}s"
                : $"no reply from the caster for {secondsSinceData:F0}s");
        }
    }

    /// <summary>"1005x1 1077x5 …": the messages that arrived since the previous health line.</summary>
    private string DescribeNewMessages(RtcmStreamSnapshot snap)
    {
        var previous = _healthLineCounts;
        var current = new Dictionary<int, long>(snap.Types.Count);
        var sb = new StringBuilder();
        foreach (var t in snap.Types)
        {
            current[t.Type] = t.Count;
            long added = t.Count - (previous.TryGetValue(t.Type, out long before) ? before : 0);
            if (added <= 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(CultureInfo.InvariantCulture, $"{t.Type}x{added}");
        }
        _healthLineCounts = current;
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    /// <inheritdoc />
    public RtcmStreamSnapshot GetRtcmStreamSnapshot() => _rtcmStats.Snapshot() with
    {
        SupersededObservations = _pacer.SupersededObservations,
        SupersededOther = _pacer.SupersededOther,
        MemoryGuardDrops = _pacer.MemoryGuardDrops,
        Unframed = _unframed,
    };

    /// <summary>
    /// Kick off the backoff reconnect loop. Idempotent — overlapping
    /// triggers (e.g. watchdog stall + receive error firing in the same
    /// window) are coalesced via the Interlocked guard. No-op once the user
    /// has disconnected.
    /// </summary>
    private void TriggerReconnect(string reason)
    {
        if (!Volatile.Read(ref _wanted)) return;
        if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
            return;
        _logger.LogWarning("[NTRIP] reconnect triggered: {Reason}", reason);
        _ = ReconnectWithBackoffAsync(reason);
    }

    private async Task ReconnectWithBackoffAsync(string reason)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var token = cts.Token;
        bool ownsFlag = true;
        try
        {
            // Always start with a clean teardown so the next connect opens fresh
            // sockets and re-authenticates from scratch — NTRIP is HTTP-style
            // stateless, the caster has dropped our mountpoint subscription anyway.
            Teardown($"{reason} — reconnecting…");

            while (!token.IsCancellationRequested)
            {
                int attempt = Interlocked.Increment(ref _failureStreak) - 1;
                int backoffSec = BackoffScheduleSec[Math.Min(attempt, BackoffScheduleSec.Length - 1)];
                _logger.LogInformation(
                    "[NTRIP] reconnect attempt {Attempt} after {Sec}s backoff",
                    attempt + 1, backoffSec);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSec), token);
                }
                catch (OperationCanceledException) { return; }

                var config = _config;
                if (config == null || !Volatile.Read(ref _wanted)) return;

                // Release the guard first: the new session's reply is checked on the
                // receive loop, and a rejection there must be able to start a new loop.
                Interlocked.Exchange(ref _reconnectInProgress, 0);
                ownsFlag = false;
                try
                {
                    await ConnectCoreAsync(config);
                    _logger.LogInformation(
                        "[NTRIP] reconnect attempt {Attempt}: request sent",
                        attempt + 1);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "[NTRIP] reconnect attempt {Attempt} failed: {Msg}",
                        attempt + 1, ex.Message);
                    // Carry on retrying unless something else already started a loop.
                    if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0) return;
                    ownsFlag = true;
                }
            }
        }
        finally
        {
            if (ownsFlag) Interlocked.Exchange(ref _reconnectInProgress, 0);
            if (ReferenceEquals(_reconnectCts, cts))
                _reconnectCts = null;
            cts.Dispose();
        }
    }

    public async Task SendGgaSentenceAsync(string ggaSentence)
    {
        var socket = _tcpSocket;
        if (!IsConnected || socket == null) return;

        try
        {
            byte[] ggaBytes = Encoding.ASCII.GetBytes(ggaSentence + "\r\n");
            await socket.SendAsync(ggaBytes, SocketFlags.None);
        }
        catch (Exception ex)
        {
            // Send failures usually mean the TCP path is broken even if the
            // receive loop hasn't noticed yet. Kick the reconnect loop. (#334)
            _logger.LogError(ex, "Failed to send GGA");
            TriggerReconnect($"GGA send failed: {ex.Message}");
        }
    }

    private string GenerateGgaSentence(double lat, double lon, double alt, int fixQuality, int sats)
    {
        // NMEA is invariant: in a ','-decimal locale (de, el, nl, fr…) "5230,0000" split the
        // latitude field and VRS casters got a broken position.
        var inv = CultureInfo.InvariantCulture;
        // Convert decimal degrees to NMEA format (DDMM.MMMM)
        double latDeg = Math.Abs(lat);
        int latDegrees = (int)latDeg;
        double latMinutes = (latDeg - latDegrees) * 60.0;
        string latStr = string.Create(inv, $"{latDegrees:00}{latMinutes:00.0000}");
        string latDir = lat >= 0 ? "N" : "S";

        double lonDeg = Math.Abs(lon);
        int lonDegrees = (int)lonDeg;
        double lonMinutes = (lonDeg - lonDegrees) * 60.0;
        string lonStr = string.Create(inv, $"{lonDegrees:000}{lonMinutes:00.0000}");
        string lonDir = lon >= 0 ? "E" : "W";

        // Get UTC time
        DateTime utc = DateTime.UtcNow;
        string timeStr = utc.ToString("HHmmss.ff", CultureInfo.InvariantCulture);

        // Build GGA sentence (without checksum yet)
        string gga = string.Create(inv, $"GPGGA,{timeStr},{latStr},{latDir},{lonStr},{lonDir},{fixQuality},{sats:00},1.0,{alt:F1},M,0.0,M,,");

        // Calculate checksum (XOR of all characters between $ and *)
        byte checksum = 0;
        foreach (char c in gga)
        {
            checksum ^= (byte)c;
        }

        return $"${gga}*{checksum:X2}";
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        DisconnectAsync().Wait();
        _cancellationTokenSource?.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}