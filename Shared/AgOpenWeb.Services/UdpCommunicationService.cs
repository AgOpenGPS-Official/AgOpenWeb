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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;

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
    private readonly byte[] _receiveBuffer = new byte[1024];
    private EndPoint _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isDisposed;

    // AutoSteer service for zero-copy GPS processing
    private IAutoSteerService? _autoSteerService;

    // Auto-discovery: broadcast on all interfaces until a module responds
    private List<IPEndPoint> _discoveryEndpoints = new();
    private IPEndPoint? _lockedEndpoint;
    private DateTime _lastModuleResponse = DateTime.MinValue;
    private DateTime _lastDiscoveryRefresh = DateTime.MinValue;
    private static readonly IPEndPoint _localhostEndpoint = new(IPAddress.Loopback, 8888);
    private const int ModuleTimeoutSeconds = 5;
    private const int DiscoveryRefreshSeconds = 30;

    // Hello packet: [0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, CRC]
    private readonly byte[] _helloPacket = { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 };

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

    private const int HELLO_TIMEOUT_MS = 2000; // 2 seconds for hello response
    private const int DATA_TIMEOUT_STEER_MACHINE_MS = 100; // 50Hz data = 20ms cycle, allow 100ms
    private const int DATA_TIMEOUT_IMU_MS = 300; // 10Hz data = 100ms cycle, allow 300ms

    public bool IsConnected { get; private set; }
    public string? LocalIPAddress { get; private set; }

    public UdpCommunicationService(ILocalNetworkInfoProvider localNetworkInfoProvider)
    {
        _localNetworkInfoProvider = localNetworkInfoProvider
            ?? throw new ArgumentNullException(nameof(localNetworkInfoProvider));
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

            // Reduce receive buffer to minimize packet buffering/delay
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 8192);

            // Windows: an ICMP Port Unreachable from a peer on a prior Send would
            // otherwise surface as a SocketException (ConnectionReset) on the next
            // ReceiveFrom call, breaking the receive loop. SIO_UDP_CONNRESET
            // disables that surfacing. No-op on non-Windows. (From PR #278.)
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
                _udpSocket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }

            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 9999));

            // Discover broadcast endpoints on all network interfaces
            _discoveryEndpoints = GetBroadcastEndpoints();
            _lockedEndpoint = null;
            _lastDiscoveryRefresh = DateTime.UtcNow;

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
        _udpSocket?.Close();
        _udpSocket?.Dispose();
        _udpSocket = null;
        IsConnected = false;

        await Task.CompletedTask;
    }

    public void SendToModules(byte[] data)
    {
        if (!IsConnected || _udpSocket == null) return;

        // PERF-05 #6 (UDP TX). Cycle = one SendToModules invocation that
        // passed the connected check. Marker .perf_udp shared with RX path;
        // emit line is [UdpTx-PERF].
        bool perf = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfUdp;
        long perfT0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perf ? GC.GetAllocatedBytesForCurrentThread() : 0;

        // Refresh discovery endpoints periodically
        if ((DateTime.UtcNow - _lastDiscoveryRefresh).TotalSeconds > DiscoveryRefreshSeconds)
        {
            _discoveryEndpoints = GetBroadcastEndpoints();
            _lastDiscoveryRefresh = DateTime.UtcNow;
        }

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
            _udpSocket!.SendTo(data, endpoint);
        }
        catch { }
    }

    public void SendHelloPacket()
    {
        SendToModules(_helloPacket);
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
            ModuleType.Machine => (now - _lastDataFromMachine).TotalMilliseconds < DATA_TIMEOUT_STEER_MACHINE_MS,
            ModuleType.IMU => (now - _lastDataFromIMU).TotalMilliseconds < DATA_TIMEOUT_IMU_MS,
            _ => false
        };
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        // Start the first receive operation
        if (_udpSocket != null)
        {
            try
            {
                _udpSocket.BeginReceiveFrom(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None,
                    ref _remoteEndPoint, ReceiveCallback, null);
            }
            catch { }
        }

        // Keep the task alive until cancellation
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private void ReceiveCallback(IAsyncResult ar)
    {
        if (_udpSocket == null) return;

        try
        {
            int bytesReceived = _udpSocket.EndReceiveFrom(ar, ref _remoteEndPoint);

            if (bytesReceived > 0)
            {
                // ZERO-COPY PATH: Check if this is NMEA data for AutoSteer
                // Process directly from receive buffer before any copying
                if (_receiveBuffer[0] == (byte)'$' && _autoSteerService != null)
                {
                    // Direct zero-copy call - this is the critical low-latency path
                    // GPS → Parse → Guidance → PGN all happen here before we continue
                    _autoSteerService.ProcessGpsBuffer(_receiveBuffer, bytesReceived);
                }

                // Now copy for other consumers (events, logging, etc.)
                byte[] data = new byte[bytesReceived];
                Array.Copy(_receiveBuffer, data, bytesReceived);

                ProcessReceivedData(data, (IPEndPoint)_remoteEndPoint);
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
        catch (Exception ex)
        {
            // Unexpected — at least trace instead of silently swallowing.
            System.Diagnostics.Debug.WriteLine($"[UDP] ReceiveCallback unexpected: {ex}");
        }

        // IMMEDIATELY start the next receive operation - this is the key fix!
        if (_udpSocket != null)
        {
            try
            {
                _udpSocket.BeginReceiveFrom(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None,
                    ref _remoteEndPoint, ReceiveCallback, null);
            }
            catch { }
        }
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
            else if (data.Length > 0 && data[0] == (byte)'$')
            {
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

    private void UpdateModuleConnection(byte[] data, IPEndPoint remoteEndPoint)
    {
        var now = DateTime.Now;
        var remoteIp = remoteEndPoint.Address.ToString();
        byte pgn = data[3];

        // Track ALL PGNs as data - if we're getting any PGN from a module, it's sending data
        switch (pgn)
        {
            // Scan reply: the module self-reports its full IP + subnet (and is the
            // only inbound PGN that carries the GPS module's IP).
            case PgnNumbers.SCAN_REPLY: // 203
                if (PgnBuilder.TryParseScanReply(data, out byte moduleId, out string scanIp, out string scanSubnet))
                {
                    _moduleSubnet = scanSubnet;
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
                _lastDataFromAutoSteer = now;
                _autoSteerIp = remoteIp;
                _lastModuleResponse = DateTime.UtcNow;
                break;

            // Machine PGNs (receive-only, only Hello matters)
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

            default:
                // Log unknown PGNs to help debug
                System.Diagnostics.Debug.WriteLine($"Unknown PGN {pgn} (0x{pgn:X2}) received");
                break;
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
        ModuleType.GPS       => _gpsIp,
        _                    => null,
    };

    /// <summary>
    /// The /24 subnet (first three octets) most recently reported by a module in
    /// a PGN 203 scan reply, or null if no scan reply has been seen.
    /// </summary>
    public string? GetModuleSubnet() => _moduleSubnet;

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
        _lastDiscoveryRefresh = DateTime.UtcNow;
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
