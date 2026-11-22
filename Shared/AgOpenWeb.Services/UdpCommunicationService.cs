// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgOpenWeb.Models;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services;

/// <summary>
/// UDP communication service for Teensy modules
/// Eliminates unreliable USB serial connections
/// Port 9999 for module communication
///
/// Zero-copy GPS path: When NMEA data arrives, AutoSteerService.ProcessGpsBuffer
/// is called directly with the receive buffer (no copy). This is the critical
/// path for low-latency GPS-to-PGN processing.
/// </summary>
public class UdpCommunicationService : IUdpCommunicationService, IDisposable
{
    public event EventHandler<UdpDataReceivedEventArgs>? DataReceived;
    public event EventHandler<ModuleConnectionEventArgs>? ModuleConnectionChanged;

    private Socket? _udpSocket;
    private readonly ILocalNetworkInfoProvider _localNetworkInfoProvider;

    /// <summary>The module port, and Ace's GPS1 / GPS2 ports a bridge may use for a receiver.</summary>
    public const int ModulePort = 9999;
    public const int Gps1Port = 2211;
    public const int Gps2Port = 2222;

    /// <summary>
    /// AgOpenGPS's own loopback port, where programs beside it send to it: a rate controller
    /// sends its section switches (PGN 234) here. AgIO sends its GPS data here too (and so
    /// does this app, for GPS_Out), so only PGN 234 is taken from it.
    /// </summary>
    public const int AgOpenGpsLoopbackPort = 15555;

    // One receive buffer and endpoint per listening socket; each socket's callbacks are
    // serial, so a listener is touched by one thread at a time.
    private sealed class Listener
    {
        public readonly Socket Socket;
        public readonly GpsSource Source;
        public readonly bool SwitchControlOnly;
        public readonly byte[] Buffer = new byte[1024];
        public EndPoint Remote = new IPEndPoint(IPAddress.Any, 0);
        /// <summary>The socket tells which network link each datagram came in on, and nothing is
        /// used from a module heard on two (null: it can't tell, everything is used, as before).
        /// Set to null if the system turns out not to give the link after all.</summary>
        public TwoLinkDetector? Links;
        public Listener(Socket socket, GpsSource source, bool switchControlOnly = false, bool linkInfo = false)
        {
            Socket = socket; Source = source; SwitchControlOnly = switchControlOnly;
            if (linkInfo) Links = new TwoLinkDetector();
        }
    }
    private readonly List<Listener> _listeners = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isDisposed;

    // AutoSteer service for zero-copy GPS processing
    private IAutoSteerService? _autoSteerService;

    // Auto-discovery: broadcast on all interfaces until a module responds
    private List<IPEndPoint> _discoveryEndpoints = new();
    private IPEndPoint? _lockedEndpoint;
    private DateTime _lastModuleResponse = DateTime.MinValue;
    // Set by the network watcher (an address changed, or its heartbeat), so a link that
    // comes up after the app started (Ethernet to the AiO, Wi-Fi at boot) is broadcast to
    // on the next send (#385).
    private int _refreshRequested;
    private static readonly IPEndPoint _localhostEndpoint = new(IPAddress.Loopback, 8888);
    // AgIO's loopback port: AgOpenGPS sends PGNs to the loopback broadcast so every program
    // bound to 17777 gets a copy. Apple platforms accept a loopback broadcast but never
    // deliver it, so there it goes to 127.0.0.1 (one listener).
    private static readonly IPAddress _loopbackTarget =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            ? IPAddress.Loopback
            : IPAddress.Parse("127.255.255.255");
    private static readonly IPEndPoint _agIoLoopbackEndpoint = new(_loopbackTarget, 17777);
    // Where AgIO sends its parsed GPS data (PGN 214) for AgOpenGPS; GPS_Out listens there too.
    private static readonly IPEndPoint _agIoGpsLoopbackEndpoint = new(_loopbackTarget, 15555);
    private const int ModuleTimeoutSeconds = 5;

    // Module connection tracking - Hello responses (2 second timeout)
    private DateTime _lastHelloFromAutoSteer = DateTime.MinValue;
    private DateTime _lastHelloFromMachine = DateTime.MinValue;
    private DateTime _lastHelloFromIMU = DateTime.MinValue;

    // Module data tracking - Data flow (50ms for 50Hz modules, 250ms for 10Hz GPS/IMU)
    private DateTime _lastDataFromAutoSteer = DateTime.MinValue;
    private DateTime _lastDataFromMachine = DateTime.MinValue;
    private DateTime _lastDataFromIMU = DateTime.MinValue;

    // Per-module remote IP from the most recent inbound packet. Populated from
    // HELLO_FROM_* (and AutoSteer data PGNs, which also identify the module), and
    // authoritatively from a PGN 203 scan reply (which also carries GPS + subnet).
    private string? _autoSteerIp;
    private string? _machineIp;
    private string? _imuIp;
    private string? _gpsIp;
    private string? _moduleSubnet;
    private string? _scanSubnet;

    private const int HELLO_TIMEOUT_MS = 3000; // 3 seconds for hello response (tolerates Wi-Fi jitter)
    private const int DATA_TIMEOUT_STEER_MACHINE_MS = 1500; // 50Hz data, allow 1500ms for Wi-Fi latency jitter
    // The machine module's own PGN 237 (five times a second or more)
    // counts in the same window as its hello reply.
    private const int DATA_TIMEOUT_MACHINE_MS = HELLO_TIMEOUT_MS;
    private const int DATA_TIMEOUT_IMU_MS = 1500; // 10Hz data, allow 1500ms for Wi-Fi latency jitter

    // #169 diagnostics: log once when GPS NMEA or steer PGN 253 stops for over 2 s and once
    // when it resumes (with the gap), so a bug-report log shows what the board stopped sending.
    private const string SourceGpsNmea = "GPS NMEA";
    private const string SourceSteerPgn253 = "steer PGN 253";
    private readonly SourceSilenceMonitor _silence = new();
    private readonly ILogger<UdpCommunicationService> _logger;

    public bool IsConnected { get; private set; }
    public string? LocalIPAddress { get; private set; }

    public UdpCommunicationService(
        ILocalNetworkInfoProvider localNetworkInfoProvider,
        ILogger<UdpCommunicationService>? logger = null,
        INetworkWatcher? networkWatcher = null)
    {
        _localNetworkInfoProvider = localNetworkInfoProvider
            ?? throw new ArgumentNullException(nameof(localNetworkInfoProvider));
        _logger = logger ?? NullLogger<UdpCommunicationService>.Instance;
        networkWatcher?.Subscribe(NetworkStage.Modules, _ => RequestDiscoveryRefresh());
    }

    /// <summary>
    /// Register the AutoSteer service for zero-copy GPS processing.
    /// When NMEA data arrives, it will be passed directly to the AutoSteer
    /// service without copying, achieving minimum latency.
    /// </summary>
    public void SetAutoSteerService(IAutoSteerService autoSteerService)
    {
        _autoSteerService = autoSteerService;
    }

    public async Task StartAsync()
    {
        if (IsConnected) return;

        try
        {
            // Get local IP address
            LocalIPAddress = GetLocalIPAddress();

            // Create UDP socket on port 9999
            _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // 64 KB receive buffer to absorb Wi-Fi bursts without kernel packet drops
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);

            // Windows: an ICMP Port Unreachable from a peer on a prior Send would
            // otherwise surface as a SocketException (ConnectionReset) on the next
            // ReceiveFrom call, breaking the receive loop. SIO_UDP_CONNRESET
            // disables that surfacing. No-op on non-Windows. (From PR #278.)
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
                _udpSocket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }

            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, ModulePort));
            _listeners.Clear();
            _listeners.Add(new Listener(_udpSocket, GpsSource.ModulePort, linkInfo: TryAskLinkInfo(_udpSocket)));

            // Ace's GPS ports: a bridge that keeps two receivers apart sends GPS1 to 2211 and
            // GPS2 to 2222. Text only; a port another program holds is logged and skipped.
            foreach (var (port, source) in new[] { (Gps1Port, GpsSource.Gps1), (Gps2Port, GpsSource.Gps2) })
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);
                    s.Bind(new IPEndPoint(IPAddress.Any, port));
                    _listeners.Add(new Listener(s, source, linkInfo: TryAskLinkInfo(s)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[UDP] not listening on {Port} ({Source}): {Message}", port, source, ex.Message);
                }
            }

            // A rate controller's section switches (PGN 234), on the loopback as AgOpenGPS
            // listens. Shared, as GPS_Out listens there too; a port another program holds
            // exclusively is logged and skipped.
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Loopback, AgOpenGpsLoopbackPort));
                _listeners.Add(new Listener(s, GpsSource.ModulePort, switchControlOnly: true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[UDP] not listening on {Port} (rate controller switches): {Message}",
                    AgOpenGpsLoopbackPort, ex.Message);
            }

            // Discover broadcast endpoints on all network interfaces
            _discoveryEndpoints = GetBroadcastEndpoints();
            _lockedEndpoint = null;

            IsConnected = true;

            // Start receiving
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoop(_cancellationTokenSource.Token));

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            throw new Exception($"Failed to start UDP communication: {ex.Message}", ex);
        }
    }

    public async Task StopAsync()
    {
        if (!IsConnected) return;

        _cancellationTokenSource?.Cancel();
        foreach (var l in _listeners)
        {
            try { l.Socket.Close(); l.Socket.Dispose(); } catch { /* closing */ }
        }
        _listeners.Clear();
        _udpSocket = null;
        IsConnected = false;
        _silence.Reset();

        await Task.CompletedTask;
    }

    private void RequestDiscoveryRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    /// <summary>Rebuild the broadcast list when the network watcher asked for it. Runs on the
    /// send path, the list's only reader.</summary>
    internal void RefreshDiscoveryIfDue()
    {
        if (Interlocked.Exchange(ref _refreshRequested, 0) == 0) return;
        _discoveryEndpoints = GetBroadcastEndpoints();
        LocalIPAddress = GetLocalIPAddress();
    }

    internal IReadOnlyList<IPEndPoint> DiscoveryEndpoints => _discoveryEndpoints;

    public void SendToModules(byte[] data)
    {
        if (!IsConnected || _udpSocket == null) return;

        // PERF-05 #6 (UDP TX). Cycle = one SendToModules invocation that
        // passed the connected check. Marker .perf_udp shared with RX path;
        // emit line is [UdpTx-PERF].
        bool perf = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfUdp;
        long perfT0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perf ? GC.GetAllocatedBytesForCurrentThread() : 0;

        RefreshDiscoveryIfDue();

        // Check for module timeout -> go back to discovery
        if (_lockedEndpoint != null &&
            (DateTime.UtcNow - _lastModuleResponse).TotalSeconds > ModuleTimeoutSeconds)
        {
            _lockedEndpoint = null;
        }

        if (_lockedEndpoint != null)
        {
            // Connected: send to locked endpoint + localhost only
            SendPacket(data, _lockedEndpoint);
            SendPacket(data, _localhostEndpoint);
        }
        else
        {
            // Discovery: broadcast on all interfaces
            foreach (var ep in _discoveryEndpoints)
                SendPacket(data, ep);
        }

        if (perf)
        {
            _perfTxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfT0;
            _perfTxAllocs += GC.GetAllocatedBytesForCurrentThread() - perfA0;
            _perfTxCount++;
            EmitTxIfWindowElapsed();
        }
    }

    public void SendToLoopback(byte[] data)
    {
        if (!IsConnected || _udpSocket == null) return;
        SendPacket(data, _agIoLoopbackEndpoint);
    }

    public void SendGpsDataToLoopback(byte[] data)
    {
        if (!IsConnected || _udpSocket == null) return;
        SendPacket(data, _agIoGpsLoopbackEndpoint);
    }

    // The task controller's own address, from its heartbeat (it binds <LAN-IP>:8888 only, so
    // the localhost copy of the module PGNs never reaches it), and when it was last heard.
    private IPEndPoint? _taskControllerEndpoint;
    private long _taskControllerHeardAt;   // Environment.TickCount64
    private const int TaskControllerTimeoutMs = 3000;

    public bool IsTaskControllerHeard =>
        Volatile.Read(ref _taskControllerEndpoint) != null
        && Environment.TickCount64 - Volatile.Read(ref _taskControllerHeardAt) < TaskControllerTimeoutMs;

    public void SendToTaskController(byte[] data)
    {
        if (!IsConnected || _udpSocket == null || Volatile.Read(ref _taskControllerEndpoint) is not { } endpoint) return;
        SendPacket(data, endpoint);
    }

    private void NoteTaskController(IPAddress address)
    {
        var known = _taskControllerEndpoint;
        if (known == null || !known.Address.Equals(address))
            Volatile.Write(ref _taskControllerEndpoint, new IPEndPoint(address, 8888));
        Volatile.Write(ref _taskControllerHeardAt, Environment.TickCount64);
    }

    // Each endpoint's serialised address, made once (an endpoint dropped is dropped from here too).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPEndPoint, SocketAddress> Addresses = new();

    internal static SocketAddress Address(IPEndPoint endpoint) =>
        Addresses.TryGetValue(endpoint, out var a) ? a : Addresses.GetValue(endpoint, static ep => ep.Serialize());

    private void SendPacket(byte[] data, IPEndPoint endpoint)
    {
        try
        {
            // Synchronous SendTo is zero-alloc; the legacy BeginSendTo APM
            // pattern allocated an IAsyncResult + overlapped state + the
            // completion-callback closure per call. With ~15 broadcast
            // endpoints in discovery mode and 2 PGNs/cycle at 100 Hz, that
            // adds up to ~5 MB/s of LOH churn — sustained allocation pressure
            // that eventually triggers a Gen2 collection and produces a
            // 1-3 s UI thread freeze. UDP sends to a local socket are
            // microseconds; the kernel queues immediately.
            // Not SendTo(byte[], EndPoint): that serialises the endpoint on every call (64 bytes a
            // send, six a control tick, against the comment above). The endpoint's address is
            // serialised once and kept with it.
            _udpSocket!.SendTo(data, SocketFlags.None, Address(endpoint));
        }
        catch { }
    }

    public void SendHelloPacket()
    {
        SendToModules(PgnBuilder.BuildHelloPacket());
    }

    public bool IsModuleHelloOk(ModuleType moduleType)
    {
        var now = DateTime.Now;
        var timeout = TimeSpan.FromMilliseconds(HELLO_TIMEOUT_MS);

        return moduleType switch
        {
            ModuleType.AutoSteer => (now - _lastHelloFromAutoSteer) < timeout,
            ModuleType.Machine => (now - _lastHelloFromMachine) < timeout,
            ModuleType.IMU => (now - _lastHelloFromIMU) < timeout,
            _ => false
        };
    }

    public bool IsModuleDataOk(ModuleType moduleType)
    {
        var now = DateTime.Now;

        return moduleType switch
        {
            ModuleType.AutoSteer => (now - _lastDataFromAutoSteer).TotalMilliseconds < DATA_TIMEOUT_STEER_MACHINE_MS,
            ModuleType.Machine => (now - _lastDataFromMachine).TotalMilliseconds < DATA_TIMEOUT_MACHINE_MS,
            ModuleType.IMU => (now - _lastDataFromIMU).TotalMilliseconds < DATA_TIMEOUT_IMU_MS,
            _ => false
        };
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        // Start the first receive operation on every listening port
        foreach (var l in _listeners)
            BeginReceive(l);

        // Keep the task alive until cancellation; also the 100 ms tick for silence checks.
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var (name, silentMs, from) in _silence.CheckSilences())
            {
                _logger.LogWarning("[UDP] no {Source} for {Sec:F1}s (last from {From})",
                    name, silentMs / 1000.0, from ?? "?");
            }
        }
    }

    private void BeginReceive(Listener l)
    {
        try
        {
            if (l.Links != null)
            {
                try
                {
                    l.Socket.BeginReceiveMessageFrom(l.Buffer, 0, l.Buffer.Length, SocketFlags.None,
                        ref l.Remote, ReceiveCallback, l);
                    return;
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    // Never lose the modules over it: receive as before, every datagram taken.
                    NoLinkInfo(l, ex);
                }
            }
            l.Socket.BeginReceiveFrom(l.Buffer, 0, l.Buffer.Length, SocketFlags.None,
                ref l.Remote, ReceiveCallback, l);
        }
        catch { }
    }

    private void NoLinkInfo(Listener l, Exception ex)
    {
        l.Links = null;
        _logger.LogWarning("[UDP] port {Port}: the system does not say which network link a datagram came in on "
            + "({Message}); a module heard on two links can't be detected, everything is used", (l.Socket.LocalEndPoint as IPEndPoint)?.Port, ex.Message);
    }

    /// <summary>
    /// Ask the socket to say which network link each datagram came in on (IP_PKTINFO). A socket
    /// bound to every address gets a broadcast once per link on the modules' subnet: a tablet on
    /// cable and on Wi-Fi to the same router got each fix and each PGN 253 twice, the Wi-Fi copy
    /// late (0.3 to 1.7 s), and AutoSteer went off over and over (a tester's Puma,
    /// 20 Nov 2026). False where the system can't: everything is then taken, as before.
    /// </summary>
    private static bool TryAskLinkInfo(Socket s)
    {
        try
        {
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The datagram in <paramref name="l"/>'s buffer comes from a module heard on two network links
    /// (<see cref="TwoLinkDetector"/>): not used. Each detection and its end is logged with both links.
    /// </summary>
    private bool IsFromModuleOnTwoLinks(Listener l, int link)
    {
        if (l.Links == null || link <= 0 || l.Remote is not IPEndPoint remote) return false;
        Span<byte> addr = stackalloc byte[16];
        if (!remote.Address.TryWriteBytes(addr, out int n) || n != 4) return false;
        uint sender = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(addr);

        bool take = l.Links.Take(sender, link, Environment.TickCount64, out var change);
        if (change != TwoLinkDetector.Change.None)
        {
            var (a, b) = l.Links.Links(sender);
            var from = remote.Address;
            // Off the receive path: listing the system's links can take a while.
            _ = Task.Run(() =>
            {
                if (change == TwoLinkDetector.Change.Detected)
                    _logger.LogWarning("[UDP] {Module} is heard on two network links, {A} and {B}: nothing from it is used "
                        + "until one is disconnected (on cable and Wi-Fi to the same router everything comes twice, "
                        + "the Wi-Fi copy late)", from, LinkName(a), LinkName(b));
                else
                    _logger.LogWarning("[UDP] {Module} is heard on one network link again: used", from);
            });
        }
        return !take;
    }

    private readonly List<TwoLinkDetector.ModuleOnTwoLinks> _twoLinks = new();

    /// <inheritdoc />
    public IReadOnlyList<AgOpenWeb.Models.State.SecondLink> GetSecondLinks()
    {
        lock (_twoLinks)
        {
            _twoLinks.Clear();
            long now = Environment.TickCount64;
            foreach (var l in _listeners.ToArray())
                if (l.Links is { } links && links.Any()) links.AddTo(_twoLinks, now);
            if (_twoLinks.Count == 0) return Array.Empty<AgOpenWeb.Models.State.SecondLink>();
            var list = new AgOpenWeb.Models.State.SecondLink[_twoLinks.Count];
            for (int i = 0; i < list.Length; i++)
            {
                var m = _twoLinks[i];
                string ip = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{m.Sender >> 24}.{(m.Sender >> 16) & 255}.{(m.Sender >> 8) & 255}.{m.Sender & 255}");
                list[i] = new AgOpenWeb.Models.State.SecondLink(ip, CachedLinkName(m.LinkA), CachedLinkName(m.LinkB), m.NotUsed, m.Times, m.Now);
            }
            return list;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _linkNames = new();

    // A link's name without waiting: the first time, its index while the name is looked up.
    private string CachedLinkName(int index)
    {
        if (_linkNames.TryGetValue(index, out var name)) return name;
        _linkNames[index] = $"link {index}";
        _ = Task.Run(() => LinkName(index));
        return _linkNames[index];
    }

    /// <summary>A network link's name for the log ("Wi-Fi (Wireless80211)"), or its index.</summary>
    private string LinkName(int index)
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.GetIPProperties().GetIPv4Properties()?.Index == index)
                        return _linkNames[index] = $"{nic.Name} ({nic.NetworkInterfaceType})";
                }
                catch (Exception) { /* no IPv4 on it */ }
            }
        }
        catch (Exception) { /* the system won't list them (Android) */ }
        return $"link {index}";
    }

    private void ReceiveCallback(IAsyncResult ar)
    {
        var l = (Listener)ar.AsyncState!;
        if (_udpSocket == null) return;

        try
        {
            int bytesReceived;
            int link = 0;
            if (l.Links != null)
            {
                SocketFlags flags = SocketFlags.None;
                bytesReceived = l.Socket.EndReceiveMessageFrom(ar, ref flags, ref l.Remote, out IPPacketInformation info);
                link = info.Interface;
            }
            else
            {
                bytesReceived = l.Socket.EndReceiveFrom(ar, ref l.Remote);
            }

            if (bytesReceived > 0 && !IsFromModuleOnTwoLinks(l, link))
            {
                // ZERO-COPY PATH: GPS text (a line, or the rest of a line a bridge cut)
                // goes to the line splitter + parser straight from the receive buffer
                // before any copying. GPS → Parse → Guidance → PGN all happen here.
                // Binary PGNs take the event path below; other binary datagrams (strangers
                // broadcasting to 9999) are ignored as they always were.
                var datagram = l.Buffer.AsSpan(0, bytesReceived);
                if (l.SwitchControlOnly)
                {
                    if (bytesReceived > 3 && datagram[0] == PgnMessage.HEADER1 && datagram[1] == PgnMessage.HEADER2
                        && datagram[3] == PgnNumbers.SWITCH_CONTROL)
                        ProcessReceivedData(datagram.ToArray(), (IPEndPoint)l.Remote);
                }
                else
                {
                    if (_autoSteerService != null && Gps.NmeaLineSplitter.IsTextDatagram(datagram))
                        _autoSteerService.ProcessGpsDatagram(datagram, l.Source);

                    // Now copy for other consumers (events, logging, etc.)
                    byte[] data = new byte[bytesReceived];
                    Array.Copy(l.Buffer, data, bytesReceived);

                    ProcessReceivedData(data, (IPEndPoint)l.Remote);
                }
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Non-fatal on UDP. Paired with SIO_UDP_CONNRESET above, this should
            // be rare — but if SIO setup failed (unusual socket state) the
            // ICMP Port Unreachable can still surface here. From PR #278.
        }
        catch (ObjectDisposedException)
        {
            // Socket closed during shutdown — stop the receive loop. From PR #278.
            return;
        }
        catch (NotSupportedException ex) when (l.Links != null)
        {
            NoLinkInfo(l, ex);
        }
        catch (Exception ex)
        {
            // Unexpected — at least trace instead of silently swallowing.
            System.Diagnostics.Debug.WriteLine($"[UDP] ReceiveCallback unexpected: {ex}");
        }

        // IMMEDIATELY start the next receive operation - this is the key fix!
        if (_udpSocket != null)
            BeginReceive(l);
    }

    private void ProcessReceivedData(byte[] data, IPEndPoint remoteEndPoint)
    {
        // PERF-05 #6 (UDP RX). Cycle = one inbound packet processed.
        // Captures DataReceived subscriber cost (NMEA parse, GpsData alloc,
        // PGN handler dispatch). Marker .perf_udp shared with TX.
        bool perf = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfUdp;
        long perfT0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perf ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            // Check if this is a binary PGN message or text NMEA sentence
            if (data.Length >= 2 && data[0] == PgnMessage.HEADER1 && data[1] == PgnMessage.HEADER2)
            {
                // Binary PGN message
                if (data.Length < 6) return;

                byte pgn = data[3];

                // Track module connections based on hello messages
                UpdateModuleConnection(data, remoteEndPoint);

                // Fire event
                DataReceived?.Invoke(this, new UdpDataReceivedEventArgs
                {
                    Data = data,
                    RemoteEndPoint = remoteEndPoint,
                    PGN = pgn,
                    Timestamp = DateTime.Now
                });
            }
            else if (data.Length > 0 && (data[0] == (byte)'$' || data[0] == (byte)'#' || data[0] == (byte)'%'))
            {
                // A '$' sentence or a Unicore '#'/'%' log: this is where the position comes
                // from, so the RTCM corrections go back to this address (unicast on 2233) and
                // Network IO names it.
                MarkSourceSeen(SourceGpsNmea, remoteEndPoint);

                // Text NMEA sentence (starts with $)
                // Fire event with PGN 0 to indicate NMEA text
                DataReceived?.Invoke(this, new UdpDataReceivedEventArgs
                {
                    Data = data,
                    RemoteEndPoint = remoteEndPoint,
                    PGN = 0, // Special PGN for NMEA text
                    Timestamp = DateTime.Now
                });
            }
        }
        finally
        {
            if (perf)
            {
                _perfRxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfT0;
                _perfRxAllocs += GC.GetAllocatedBytesForCurrentThread() - perfA0;
                _perfRxCount++;
                EmitRxIfWindowElapsed();
            }
        }
    }

    // PERF-05 #6 accumulators (gated by DiagFlags.PerfUdp). RX runs on the
    // socket receive thread; TX runs on whichever thread fires SendToModules
    // (autosteer pipeline / UI). No lock — last writer wins on the counters
    // is acceptable for diagnostic data.
    private long _perfRxTicks, _perfRxAllocs;
    private int _perfRxCount;
    private DateTime _perfRxWindowStart = DateTime.UtcNow;
    private long _perfTxTicks, _perfTxAllocs;
    private int _perfTxCount;
    private DateTime _perfTxWindowStart = DateTime.UtcNow;

    private void EmitRxIfWindowElapsed()
    {
        // #412: Snapshot the accumulators first so a concurrent caller can't
        // reset _perfRxCount to 0 between our guard check and the integer
        // division below (which would throw DivideByZeroException).
        int count = _perfRxCount;
        long ticks = _perfRxTicks;
        long allocs = _perfRxAllocs;
        var elapsed = (DateTime.UtcNow - _perfRxWindowStart).TotalSeconds;
        if (elapsed < 1.0 || count == 0) return;
        double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
        Console.WriteLine(
            $"[UdpRx-PERF] packets={count}"
            + $" us/packet={(ticks / ticksPerUs / count):F1}"
            + $" alloc/packet={(allocs / count)}B"
            + $" total_us={(long)(ticks / ticksPerUs)}"
            + $" total_alloc={allocs}B"
            + $" window={elapsed:F2}s");
        _perfRxTicks = 0;
        _perfRxAllocs = 0;
        _perfRxCount = 0;
        _perfRxWindowStart = DateTime.UtcNow;
    }

    private void EmitTxIfWindowElapsed()
    {
        // #412: Snapshot the accumulators first so a concurrent caller can't
        // reset _perfTxCount to 0 between our guard check and the integer
        // division below (which would throw DivideByZeroException).
        int count = _perfTxCount;
        long ticks = _perfTxTicks;
        long allocs = _perfTxAllocs;
        var elapsed = (DateTime.UtcNow - _perfTxWindowStart).TotalSeconds;
        if (elapsed < 1.0 || count == 0) return;
        double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
        Console.WriteLine(
            $"[UdpTx-PERF] sends={count}"
            + $" us/send={(ticks / ticksPerUs / count):F1}"
            + $" alloc/send={(allocs / count)}B"
            + $" total_us={(long)(ticks / ticksPerUs)}"
            + $" total_alloc={allocs}B"
            + $" window={elapsed:F2}s");
        _perfTxTicks = 0;
        _perfTxAllocs = 0;
        _perfTxCount = 0;
        _perfTxWindowStart = DateTime.UtcNow;
    }

    internal void UpdateModuleConnection(byte[] data, IPEndPoint remoteEndPoint)
    {
        var now = DateTime.Now;
        var remoteIp = remoteEndPoint.Address.ToString();
        byte pgn = data[3];

        // Track ALL PGNs as data - if we're getting any PGN from a module, it's sending data
        switch (pgn)
        {
            // Scan reply: the module self-reports its full IP (and is the only inbound
            // PGN that carries the GPS module's IP), plus the subnet the scan came from.
            case PgnNumbers.SCAN_REPLY: // 203
                if (PgnBuilder.TryParseScanReply(data, out byte moduleId, out string scanIp, out string scanSubnet))
                {
                    _moduleSubnet = scanIp[..scanIp.LastIndexOf('.')];
                    _scanSubnet = scanSubnet;
                    switch (moduleId)
                    {
                        case 126: _autoSteerIp = scanIp; break;
                        case 123: _machineIp = scanIp; break;
                        case 121: _imuIp = scanIp; break;
                        case 120: _gpsIp = scanIp; break;
                    }
                    _lastModuleResponse = DateTime.UtcNow;
                }
                break;

            // AutoSteer PGNs
            case PgnNumbers.HELLO_FROM_AUTOSTEER: // 126
                _lastHelloFromAutoSteer = now;
                _autoSteerIp = remoteIp;
                LockToSubnet(remoteEndPoint.Address);
                System.Diagnostics.Debug.WriteLine($"AutoSteer HELLO received at {now:HH:mm:ss.fff}");
                break;

            case PgnNumbers.SENSOR_DATA:          // 250 - Sensor data from module
            case PgnNumbers.AUTOSTEER_DATA:       // 253 - Regular data
            case PgnNumbers.AUTOSTEER_DATA2:      // 254
            case PgnNumbers.STEER_SETTINGS:       // 252
            case PgnNumbers.STEER_CONFIG:         // 251
                if (pgn == PgnNumbers.AUTOSTEER_DATA)
                    MarkSourceSeen(SourceSteerPgn253, remoteEndPoint);
                _lastDataFromAutoSteer = now;
                _autoSteerIp = remoteIp;
                _lastModuleResponse = DateTime.UtcNow;
                break;

            // Machine module: its hello reply here, its own PGN 237 below.
            case PgnNumbers.HELLO_FROM_MACHINE:  // 123
                _lastHelloFromMachine = now;
                _machineIp = remoteIp;
                LockToSubnet(remoteEndPoint.Address);
                System.Diagnostics.Debug.WriteLine($"Machine HELLO received at {now:HH:mm:ss.fff}");
                break;

            // IMU PGNs (only Hello matters - data only sent when active)
            case PgnNumbers.HELLO_FROM_IMU: // 121
                _lastHelloFromIMU = now;
                _imuIp = remoteIp;
                LockToSubnet(remoteEndPoint.Address);
                System.Diagnostics.Debug.WriteLine($"IMU HELLO received at {now:HH:mm:ss.fff}");
                break;

            case PgnNumbers.IMU_DATA: // 211 - external IMU roll/heading (#353)
                _lastDataFromIMU = now;
                _imuIp = remoteIp;
                break;

            case PgnNumbers.ISOBUS_HEARTBEAT: // 240 - task controller; IsobusTaskControllerService reads it
                if (data.Length > 2 && data[2] == Isobus.IsobusTaskControllerService.TaskControllerSource)
                    NoteTaskController(remoteEndPoint.Address);
                break;

            case PgnNumbers.SWITCH_CONTROL: // 234 - remote section switches; AutoSteerService reads it
                break;

            // The machine module talking on its own: present, as with its hello reply. Over Wi-Fi
            // the hello and its reply are both broadcasts, which nothing resends, while 237 comes
            // five times a second or more; on the hello alone the Modules dot went amber for
            // seconds with the module working.
            case PgnNumbers.FROM_MACHINE: // 237 - what the module's outputs do
                _lastDataFromMachine = now;
                _machineIp = remoteIp;
                _lastModuleResponse = DateTime.UtcNow;
                break;

            default:
                // Log unknown PGNs to help debug
                System.Diagnostics.Debug.WriteLine($"Unknown PGN {pgn} (0x{pgn:X2}) received");
                break;
        }
    }

    private void MarkSourceSeen(string source, IPEndPoint remoteEndPoint)
    {
        if (_silence.MarkSeen(source, remoteEndPoint.Address.ToString()) is { } gapMs)
        {
            _logger.LogWarning("[UDP] {Source} resumed after {Sec:F1}s silence (from {From})",
                source, gapMs / 1000.0, remoteEndPoint.Address);
        }
    }

    /// <summary>
    /// Returns the most-recently-observed remote IP for the given module, or null
    /// if no packet has ever been received from it.
    /// </summary>
    public string? GetModuleIpAddress(ModuleType moduleType) => moduleType switch
    {
        ModuleType.AutoSteer => _autoSteerIp,
        ModuleType.Machine   => _machineIp,
        ModuleType.IMU       => _imuIp,
        // Where the position sentences are coming from; a scan reply's address otherwise.
        ModuleType.GPS       => GetGpsSourceAddress()?.ToString() ?? _gpsIp,
        _                    => null,
    };

    /// <summary>
    /// The /24 subnet (first three octets) of the module that sent the most recent
    /// PGN 203 scan reply (from its IP), or null if no scan reply has been seen.
    /// </summary>
    public string? GetModuleSubnet() => _moduleSubnet;

    /// <inheritdoc />
    public string? GetScanReplySubnet() => _scanSubnet;

    public IPAddress? GetGpsSourceAddress(double maxAgeSeconds = 10)
    {
        if (_silence.LastSeen(SourceGpsNmea) is not { } seen || seen.AgeMs > maxAgeSeconds * 1000.0)
            return null;
        return IPAddress.TryParse(seen.From, out var address) ? address : null;
    }

    public string? GetActiveModuleSubnet()
    {
        var locked = _lockedEndpoint;
        if (locked != null)
        {
            var b = locked.Address.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{b[2]}";
        }
        return _moduleSubnet;
    }

    /// <summary>
    /// Broadcast a scan request (PGN 202). Modules reply with PGN 203 (parsed in
    /// <see cref="UpdateModuleConnection"/> into per-module IP + subnet).
    /// </summary>
    public void ScanModules() => SendModuleConfig(PgnBuilder.BuildScanRequest());

    /// <summary>
    /// Broadcast a set-subnet command (PGN 201, global /24 change), then re-arm
    /// discovery so the next module hellos re-lock the new subnet.
    /// </summary>
    public void SetModuleSubnet(byte octet1, byte octet2, byte octet3)
    {
        SendModuleConfig(PgnBuilder.BuildSubnetChange(octet1, octet2, octet3));
        ResetDiscovery();
    }

    /// <summary>
    /// Send a module-config packet (scan request 202 / set-subnet 201) to the
    /// GLOBAL broadcast 255.255.255.255:8888, once per up IPv4 NIC — matching
    /// AgIO's FormUDP behaviour. Global (not directed subnet.255) broadcast is
    /// deliberate so the packet reaches modules that are currently on a different
    /// or unknown subnet. Also hits localhost for the simulator/ModSim.
    /// </summary>
    private void SendModuleConfig(byte[] data)
    {
        if (!IsConnected) return;

        // Simulator / ModSim listens on loopback.
        try { _udpSocket?.SendTo(data, _localhostEndpoint); } catch { }

        var dest = new IPEndPoint(IPAddress.Broadcast, 8888);
        try
        {
            foreach (var localAddress in _localNetworkInfoProvider.GetIPv4Addresses())
            {
                try
                {
                    // Bind a transient socket to this link so the broadcast
                    // actually egresses it (multi-homed tablets), exactly as
                    // AgIO does. ReuseAddress lets us co-bind :9999 with the
                    // main receive socket; replies still arrive on it.
                    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontRoute, true);
                    s.Bind(new IPEndPoint(localAddress.Address, 9999));
                    s.SendTo(data, dest);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UDP] SendModuleConfig failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drop the locked send endpoint and refresh discovery so the next module
    /// hellos re-lock the (possibly new) subnet. Call right after a subnet change
    /// so the app follows the modules to their new /24 instead of waiting out the
    /// module-timeout.
    /// </summary>
    private void ResetDiscovery()
    {
        _lockedEndpoint = null;
        _discoveryEndpoints = GetBroadcastEndpoints();
    }

    /// <summary>
    /// Lock outgoing packets to the subnet of a responding module.
    /// Assumes /24 subnet (most common for field hardware).
    /// </summary>
    private void LockToSubnet(IPAddress remoteIP)
    {
        _lastModuleResponse = DateTime.UtcNow;

        if (_lockedEndpoint != null || IPAddress.IsLoopback(remoteIP))
            return;

        var ipBytes = remoteIP.GetAddressBytes();
        ipBytes[3] = 255;
        _lockedEndpoint = new IPEndPoint(new IPAddress(ipBytes), 8888);
        System.Diagnostics.Debug.WriteLine($"Auto-discovery: locked to subnet {_lockedEndpoint}");
    }

    /// <summary>
    /// Enumerate broadcast addresses for all active IPv4 network interfaces.
    /// Always includes localhost for simulator support.
    /// </summary>
    private List<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new List<IPEndPoint> { _localhostEndpoint };
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            _localhostEndpoint.ToString()
        };

        try
        {
            foreach (var localAddress in _localNetworkInfoProvider.GetIPv4Addresses())
            {
                var endpoint = new IPEndPoint(CalculateBroadcastAddress(localAddress), 8888);
                if (seen.Add(endpoint.ToString()))
                    endpoints.Add(endpoint);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to enumerate network interfaces: {ex.Message}");
        }

        return endpoints;
    }

    internal static IPAddress CalculateBroadcastAddress(LocalNetworkAddress localAddress)
    {
        if (localAddress.Address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(localAddress));
        if (localAddress.PrefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(
                nameof(localAddress),
                "IPv4 prefix length must be between 0 and 32.");

        uint ip = NetworkToHostUInt32(localAddress.Address.GetAddressBytes());
        uint mask = localAddress.PrefixLength == 0
            ? 0
            : uint.MaxValue << (32 - localAddress.PrefixLength);
        uint broadcast = ip | ~mask;

        return new IPAddress(new[]
        {
            (byte)(broadcast >> 24),
            (byte)(broadcast >> 16),
            (byte)(broadcast >> 8),
            (byte)broadcast
        });
    }

    private static uint NetworkToHostUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24)
        | ((uint)bytes[1] << 16)
        | ((uint)bytes[2] << 8)
        | bytes[3];

    private string? GetLocalIPAddress()
    {
        return _localNetworkInfoProvider.GetIPv4Addresses()
            .Select(address => address.Address.ToString())
            .FirstOrDefault();
    }

    /// <summary>
    /// All non-loopback IPv4 addresses of the host's up network interfaces.
    /// Lets the operator see which subnet the host is on so they can match the
    /// modules' subnet.
    /// </summary>
    public IReadOnlyList<string> GetLocalIpAddresses()
    {
        return _localNetworkInfoProvider.GetIPv4Addresses()
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        StopAsync().Wait();
        _cancellationTokenSource?.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
