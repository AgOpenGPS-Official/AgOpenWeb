// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
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
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services;

/// <summary>
/// NTRIP client for receiving RTK correction data from base station
/// Forwards RTCM3 corrections to GPS module via UDP port 2233
/// Based on AgIO NTRIP implementation.
/// The connection itself (request, reply, retry, timeouts) is <see cref="NtripSession"/>;
/// this class frames the stream it delivers and forwards it.
/// </summary>
public class NtripClientService : INtripClientService, IDisposable
{
    public event EventHandler<NtripConnectionEventArgs>? ConnectionStatusChanged;
    public event EventHandler<RtcmDataReceivedEventArgs>? RtcmDataReceived;

    private Socket? _udpSocket;
    private NtripSession? _session;
    private NtripSessionStatus _status = NtripSessionStatus.Idle;
    private (bool Connected, string Message)? _reported;   // the last status event raised, so repeats are not
    private readonly NtripEventLog _events = new();   // for the bug report; kept across sessions
    private CancellationTokenSource? _cancellationTokenSource;
    private NtripConfiguration? _config;
    private bool _isDisposed;
    // The NTRIP version that delivered corrections, per service: a reconnect starts with it.
    private readonly ConcurrentDictionary<string, NtripVersion> _versions = new();

    private IPEndPoint? _rtcmUdpEndpoint;
    private bool _rtcmUnicast;   // _rtcmUdpEndpoint is the GPS module's own address
    private Timer? _healthTimer;
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

    // A 5 s health line so operators can tell caster pauses from app brokenness. A stall
    // itself is NtripSession's to handle (NtripTimeouts.NoData).
    private long _lastRtcmReceivedTimestamp;
    private const double HEALTH_LINE_INTERVAL_MS = 5000.0;

    // Completed by the network watcher when an address changes: the retry waiting out its
    // backoff tries at once instead (#385). The backoff still spaces retries otherwise.
    private TaskCompletionSource _networkChanged = NewWake();
    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IGpsService _gpsService;
    private readonly ILogger<NtripClientService> _logger;

    /// <summary>The caster accepted the request (200); cleared on any disconnect.</summary>
    public bool IsConnected { get; private set; }
    /// <summary>A connection was requested and not disconnected: connected, connecting,
    /// retrying, or stopped after the caster rejected it.</summary>
    public bool IsActive => Volatile.Read(ref _wanted);
    public ulong TotalBytesReceived { get; private set; }
    public NtripSessionStatus Status => _status;
    public NtripServiceInfo? Service { get; private set; }

    public IReadOnlyList<NtripEvent> Events => _events.ToArray();

    private bool _wanted;      // ConnectAsync called, DisconnectAsync not
    private bool _sessionOpen; // a session is running: connecting, retrying or streaming
    private readonly object _sessionLock = new();

    public NtripClientService(IGpsService gpsService, ILogger<NtripClientService> logger,
        INetworkWatcher? networkWatcher = null)
    {
        _gpsService = gpsService;
        _logger = logger;
        networkWatcher?.Subscribe(NetworkStage.Corrections, reason =>
        {
            if (reason == NetworkCheckReason.AddressChanged)
                Interlocked.Exchange(ref _networkChanged, NewWake()).TrySetResult();
        });
    }

    /// <summary>
    /// Starts a session and returns at once: it connects, and keeps retrying with a backoff
    /// after a drop or a failed connect (#385), on its own task. IsConnected stays false until
    /// the caster has accepted; progress is reported through <see cref="ConnectionStatusChanged"/>.
    /// </summary>
    public async Task ConnectAsync(NtripConfiguration config)
    {
        Volatile.Write(ref _wanted, true);
        StartSession(config);
        await Task.CompletedTask;
    }

    private void StartSession(NtripConfiguration config)
    {
        lock (_sessionLock)
        {
            Teardown(null);
            _config = config;
            Service = new NtripServiceInfo(config.CasterAddress, config.CasterPort, config.MountPoint,
                !string.IsNullOrEmpty(config.Username), !string.IsNullOrEmpty(config.Password));

            // UDP socket for forwarding RTCM data to the GPS module (subnet.255:2233)
            Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _rtcmUdpEndpoint = new IPEndPoint(IPAddress.Parse($"{config.SubnetAddress}.255"), config.UdpForwardPort);
            }
            catch
            {
                udp.Dispose();
                throw;
            }
            _udpSocket = udp;
            _rtcmUnicast = false;
            _lastRtcmReceivedTimestamp = Clock.Current.GetTimestamp();
            _status = NtripSessionStatus.Idle;
            _sessionOpen = true;
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            // A session that was stopped can still deliver once more; only the current one is heard.
            NtripSession? session = null;
            session = new NtripSession(config,
                data => { if (ReferenceEquals(session, _session)) ForwardRtcmData(data); },
                () => _rtcmStats.Messages > 0 || _unframed, () => NextGgaSentence(config))
            {
                StreamStarted = chunked => { if (ReferenceEquals(session, _session)) OnStreamStarted(chunked); },
                NetworkChanged = () => Volatile.Read(ref _networkChanged).Task,
                VersionMemory = _versions,
            };
            session.StatusChanged += status => OnSessionStatus(session, status);
            _session = session;

            _ = Task.Run(() => session.RunAsync(token));
            _ = Task.Run(() => SendLoop(token));

            // Drives the periodic [NTRIP] health log line.
            _healthTimer = new Timer(HealthLineCallback, null,
                TimeSpan.FromMilliseconds(HEALTH_LINE_INTERVAL_MS), TimeSpan.FromMilliseconds(HEALTH_LINE_INTERVAL_MS));
        }
    }

    public async Task DisconnectAsync()
    {
        bool wasActive = Volatile.Read(ref _wanted) || _sessionOpen;
        Volatile.Write(ref _wanted, false);
        Teardown(wasActive ? "Disconnected" : null);
        await Task.CompletedTask;
    }

    /// <summary>Stop the session (its connection closes as it ends) and the forwarding. Raises a
    /// not-connected status with <paramref name="message"/> when one is given.</summary>
    private void Teardown(string? message)
    {
        lock (_sessionLock)
        {
            _healthTimer?.Dispose();
            _healthTimer = null;

            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _pacer.Clear(); // a new session must not inherit the old one's corrections
            _session = null;   // what the session that is ending still reports is ignored
            NtripVersion version = _status.Version;
            _status = NtripSessionStatus.Idle;

            _udpSocket?.Close();
            _udpSocket?.Dispose();
            _udpSocket = null;

            _sessionOpen = false;
            IsConnected = false;
            _reported = null;
            if (message != null)
                _events.Add(new NtripEvent(Clock.Current.Now, Service?.ToString() ?? "", NtripState.Stopped, null, message, "", version, 0));
        }

        if (message != null) RaiseStatus(false, message);
    }

    // A caster should never repeat the login, but one that does must not put it in the report or the log.
    // Each secret is replaced as typed, URL-escaped and (the pair) as the Basic value, longest first. A
    // secret under three characters is left alone: it would mangle ordinary words.
    private string Redact(string text, bool userName)
    {
        if (_config is not { } config || text.Length == 0) return text;
        var secrets = new List<string>();
        if (NtripCredentials.BasicAuthorization(config.Username, config.Password) is { } basic) secrets.Add(basic["Basic ".Length..]);
        AddSecret(secrets, config.Password);
        if (userName) AddSecret(secrets, config.Username);
        foreach (string secret in secrets.OrderByDescending(s => s.Length))
            text = text.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void AddSecret(List<string> secrets, string secret)
    {
        if (secret.Length < 3) return;
        secrets.Add(secret);
        secrets.Add(Uri.EscapeDataString(secret));
    }

    private void RaiseStatus(bool connected, string message) =>
        ConnectionStatusChanged?.Invoke(this, new NtripConnectionEventArgs { IsConnected = connected, Message = message });

    private void OnSessionStatus(NtripSession source, NtripSessionStatus status)
    {
        lock (_sessionLock)
        {
            if (!ReferenceEquals(source, _session)) return;
            // What the caster said loses the user name too; the text made here keeps it, because a mount point can be the same word.
            string firstLine = Redact(status.FirstLine, userName: true);
            string message = status.FirstLine.Length > 0 ? status.Message.Replace(status.FirstLine, firstLine, StringComparison.Ordinal) : status.Message;
            status = status with { Message = Redact(message, userName: false), FirstLine = firstLine };
            _status = status;
            IsConnected = status.IsOpen;
            if (status.State == NtripState.GaveUp) _sessionOpen = false;   // over until the user acts; IsActive stays
            _events.Add(new NtripEvent(Clock.Current.Now, Service?.ToString() ?? "", status.State, status.LastError,
                status.Message, status.FirstLine, status.Version, status.Attempts));
            if (_reported == (status.IsOpen, status.Message)) return;
            _reported = (status.IsOpen, status.Message);
        }

        if (status.State == NtripState.Connected)
            _logger.LogInformation("[NTRIP] connected and authorized with NTRIP {Version}, receiving RTCM data", status.Version);
        else if (status.State is NtripState.Waiting or NtripState.GaveUp)
            _logger.LogWarning("[NTRIP] {Category}: {Message} (caster said: {FirstLine})", status.LastError, status.Message, status.FirstLine);
        RaiseStatus(status.IsOpen, status.Message);
    }

    /// <summary>A stream begins: forget the previous one's counts and queue, and decode it if the reply was chunked.</summary>
    private void OnStreamStarted(bool chunked)
    {
        _pacer.Clear();
        _rtcmStats.Reset(chunked);
        // An NTRIP 2 caster may send the stream chunked: decode it, or the chunk-size lines
        // land inside RTCM messages and break them.
        _chunked = chunked ? new ChunkedDecoder() : null;
        _unframed = false;
        _healthLineCounts = new();
        TotalBytesReceived = 0;
        if (chunked)
            _logger.LogInformation("[NTRIP] the caster replied with Transfer-Encoding: chunked; decoding it");
    }

    /// <summary>The /24 to forward RTCM to: the provider's live subnet, else the configured one.</summary>
    internal static string ResolveRtcmSubnet(NtripConfiguration config)
    {
        string? live = null;
        try { live = config.SubnetProvider?.Invoke(); } catch { /* fall back to the setting */ }
        return string.IsNullOrEmpty(live) ? config.SubnetAddress : live;
    }

    /// <summary>
    /// Where RTCM goes: the GPS module's own address when it is known and broadcasting is
    /// not forced (<see cref="NtripConfiguration.GpsModuleAddressProvider"/>,
    /// <see cref="NtripConfiguration.BroadcastOnly"/>), else subnet.255 for the modules'
    /// current /24, as AgIO sends to its subnet setting. Null if neither can be formed.
    ///
    /// Unicast because a Wi-Fi access point repeats every broadcast to its wireless clients
    /// unacknowledged and at its lowest rate: that costs airtime the steering traffic needs,
    /// and a tablet's broadcast can be lost on the way (Plans/RTCM_FORWARDING_PLAN.md, Phase 4).
    /// </summary>
    internal static IPEndPoint? ResolveRtcmDestination(NtripConfiguration config, out bool unicast)
    {
        unicast = false;
        bool broadcastOnly = false;
        try { broadcastOnly = config.BroadcastOnly?.Invoke() ?? false; } catch { /* default */ }
        if (!broadcastOnly)
        {
            IPAddress? module = null;
            try { module = config.GpsModuleAddressProvider?.Invoke(); } catch { /* broadcast */ }
            if (module != null)
            {
                unicast = true;
                return new IPEndPoint(module, config.UdpForwardPort);
            }
        }
        return IPAddress.TryParse($"{ResolveRtcmSubnet(config)}.255", out var broadcast)
            ? new IPEndPoint(broadcast, config.UdpForwardPort)
            : null;
    }

    /// <summary>The destination for the next datagram; logs when it changes.</summary>
    private IPEndPoint? CurrentRtcmEndpoint()
    {
        var config = _config;
        var endpoint = _rtcmUdpEndpoint;
        if (config == null || endpoint == null) return endpoint;

        var target = ResolveRtcmDestination(config, out bool unicast);
        if (target == null || (target.Equals(endpoint) && unicast == _rtcmUnicast)) return endpoint;

        _logger.LogInformation("[NTRIP] forwarding RTCM to {Endpoint} ({How})", target,
            unicast ? "the GPS module" : "broadcast");
        _rtcmUnicast = unicast;
        _rtcmUdpEndpoint = target;
        return target;
    }

    /// <inheritdoc />
    public (string Address, bool Unicast) RtcmDestination =>
        _sessionOpen && _rtcmUdpEndpoint is { } e ? (e.Address.ToString(), _rtcmUnicast) : ("", false);

    private void ForwardRtcmData(ReadOnlySpan<byte> rtcmData)
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
    /// (down to RtcmQueue.MinGapMs). A timer cannot keep those, so a shortened wait sleeps on
    /// the timer for all but its last few milliseconds and spins those out (RTCM plan, Phase 3).
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
                    if (wait >= RtcmQueue.IntervalMs - 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait)), token);
                        continue;
                    }
                    // A catch-up gap. Sleep the bulk of it; if the timer overshoots, the
                    // queue counts that as lateness too.
                    if (wait > SpinTailMs + 2)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Floor(wait - SpinTailMs)), token);
                    SpinFor(_pacer.MsUntilDue(), token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The end of a shortened wait that is spun, not slept: about the lateness of a
    /// timer on a good day.</summary>
    private const double SpinTailMs = 6.0;

    /// <summary>Wait a few milliseconds precisely, without a timer.</summary>
    private static void SpinFor(double milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0) return;
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

    /// <summary>
    /// The position a VRS caster builds its corrections for: the receiver's fix; after losing
    /// it, the last one; before the first, the configured fallback (the open field's origin).
    /// Null with none of these: a made-up position (this sent Kansas) gets corrections for the
    /// wrong place or the connection dropped (#443), so no GGA goes out until one is known.
    /// Any fix counts, single and DGPS too: IsValid means good enough to steer by (RTK by
    /// default), and a receiver in single that waited for it would never get the corrections
    /// to reach RTK. AgIO sends the receiver's position whatever its quality.
    /// </summary>
    internal string? NextGgaSentence(NtripConfiguration config)
    {
        if (config.UseManualPosition)
            return GenerateGgaSentence(config.ManualLatitude, config.ManualLongitude, 0, 4, 12);

        var gpsData = _gpsService.CurrentData;
        if (gpsData != null && HasFix(gpsData))
        {
            var p = gpsData.CurrentPosition;
            _lastGgaFix = (p.Latitude, p.Longitude, p.Altitude);
            return GenerateGgaSentence(p.Latitude, p.Longitude, p.Altitude, gpsData.FixQuality, gpsData.SatellitesInUse);
        }
        if (_lastGgaFix is { } last)
            return GenerateGgaSentence(last.Latitude, last.Longitude, last.Altitude, 1, 8);
        if (config.FallbackPositionProvider?.Invoke() is { } fallback)
            return GenerateGgaSentence(fallback.Latitude, fallback.Longitude, 0, 1, 8);
        return null;
    }

    private (double Latitude, double Longitude, double Altitude)? _lastGgaFix;

    /// <summary>The receiver reports a fix of any quality, at a position (not 0,0).</summary>
    private static bool HasFix(GpsData gpsData) =>
        gpsData.FixQuality > 0
        && (gpsData.CurrentPosition.Latitude != 0 || gpsData.CurrentPosition.Longitude != 0);

    private void HealthLineCallback(object? state)
    {
        if (!_sessionOpen) return;

        long now = Clock.Current.GetTimestamp();
        long last = Volatile.Read(ref _lastRtcmReceivedTimestamp);
        double secondsSinceData = Clock.Current.ElapsedMs(last, now) / 1000.0;

        // Once per HEALTH_LINE_INTERVAL_MS regardless of state. Operators read this to tell
        // "caster paused" from "AgOpenWeb broke" without needing the debug log.
        var gps = _gpsService.CurrentData;
        var snap = _rtcmStats.Snapshot();
        long superseded = _pacer.SupersededObservations + _pacer.SupersededOther + _pacer.MemoryGuardDrops;
        long newlySuperseded = superseded - _healthLineSuperseded;
        _healthLineSuperseded = superseded;
        var (datagrams, catchUp, maxLateMs) = _pacer.TakePacingStats();
        _logger.LogInformation(
            "[NTRIP] last RTCM {Sec:F1}s ago, total {Bytes} bytes; messages since last line: {Messages}; not sent (replaced by newer) {Superseded}; sent {Datagrams} datagrams, {CatchUp} at a catch-up gap, sender late up to {Late:F0} ms; session checksum failures {Crc}, skipped {Skipped} bytes; receiver fix {Fix}, differential age {Age:F1}s",
            secondsSinceData, TotalBytesReceived, DescribeNewMessages(snap), newlySuperseded,
            datagrams, catchUp, maxLateMs,
            snap.ChecksumFailures, snap.BytesSkipped, gps?.FixQuality ?? 0, gps?.DifferentialAge ?? 0);
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

    public async Task SendGgaSentenceAsync(string ggaSentence)
    {
        if (_session is { } session)
            await session.SendAsync(ggaSentence + "\r\n");
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
