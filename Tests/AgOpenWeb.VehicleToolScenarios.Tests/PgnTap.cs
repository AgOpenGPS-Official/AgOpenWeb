// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The real UDP service with a tap on what leaves it: every datagram AgOpenWeb sends to the
/// modules (PGN 254 steer data, 239 machine data, 251/252 steer config and settings, 238
/// machine config, 236 pins, 235 section widths, 247 vehicle geometry) is copied as it goes
/// out. Everything else is the real service, sockets included. A module's reply is sent to
/// AgOpenWeb's port 9999 on the loopback with <see cref="InjectAsync"/>, as the module sends
/// it, so it takes the whole receive path (the module counts as connected).
/// </summary>
public sealed class PgnTap : IUdpCommunicationService, IDisposable
{
    private readonly IUdpCommunicationService _inner;
    private readonly object _sync = new();
    private readonly Dictionary<byte, byte[]> _last = new();
    private readonly Dictionary<byte, int> _count = new();
    private List<(byte Pgn, byte[] Data)>? _log;
    private EventHandler<UdpDataReceivedEventArgs>? _received;
    private UdpClient? _module;
    private (byte[] Data, TaskCompletionSource Arrived)? _awaiting;

    public PgnTap(IUdpCommunicationService inner)
    {
        _inner = inner;
        _inner.DataReceived += OnReceived;
    }

    /// <summary>The newest datagram of a PGN, or null when none went out yet.</summary>
    public byte[]? Last(byte pgn)
    {
        lock (_sync) return _last.TryGetValue(pgn, out var b) ? (byte[])b.Clone() : null;
    }

    /// <summary>How many datagrams of a PGN went out so far.</summary>
    public int Count(byte pgn)
    {
        lock (_sync) return _count.TryGetValue(pgn, out var n) ? n : 0;
    }

    /// <summary>Start keeping every datagram (not only the newest of each PGN).</summary>
    public void StartLog()
    {
        lock (_sync) _log = new List<(byte, byte[])>();
    }

    public List<(byte Pgn, byte[] Data)> StopLog()
    {
        lock (_sync)
        {
            var l = _log ?? new List<(byte, byte[])>();
            _log = null;
            return l;
        }
    }

    private void Tap(byte[] data)
    {
        if (data.Length < 5 || data[0] != 0x80 || data[1] != 0x81) return;
        // PgnBuilder reuses thread-static buffers: keep a copy.
        var copy = (byte[])data.Clone();
        lock (_sync)
        {
            _last[copy[3]] = copy;
            _count[copy[3]] = (_count.TryGetValue(copy[3], out var n) ? n : 0) + 1;
            _log?.Add((copy[3], copy));
        }
    }

    /// <summary>A module's datagram, sent from the loopback to AgOpenWeb's port 9999 as the
    /// module sends it: the UDP service tracks the module's connection and then hands it to
    /// AgOpenWeb's listeners. Returns once every listener has had it; false when it did not
    /// arrive within half a second (the UDP service is not running, or another program on
    /// this machine took the datagram on port 9999).</summary>
    public async Task<bool> InjectAsync(byte[] datagram)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _module ??= new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _awaiting = (datagram, arrived);
        }
        await _module.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, UdpCommunicationService.ModulePort));
        bool ok = await Task.WhenAny(arrived.Task, Task.Delay(500)) == arrived.Task;
        lock (_sync) if (_awaiting?.Arrived == arrived) _awaiting = null;
        return ok;
    }

    // The listeners first, then the datagram counts as arrived: when InjectAsync returns,
    // AgOpenWeb has handled it.
    private void OnReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        _received?.Invoke(sender, e);
        TaskCompletionSource? done = null;
        lock (_sync)
            if (_awaiting is { } w && e.Data.AsSpan().SequenceEqual(w.Data))
            {
                done = w.Arrived;
                _awaiting = null;
            }
        done?.TrySetResult();
    }

    public event EventHandler<UdpDataReceivedEventArgs>? DataReceived
    {
        add { lock (_sync) _received += value; }
        remove { lock (_sync) _received -= value; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _module?.Dispose();
            _module = null;
        }
    }

    public event EventHandler<ModuleConnectionEventArgs>? ModuleConnectionChanged
    {
        add => _inner.ModuleConnectionChanged += value;
        remove => _inner.ModuleConnectionChanged -= value;
    }

    public bool IsConnected => _inner.IsConnected;
    public string? LocalIPAddress => _inner.LocalIPAddress;
    public IReadOnlyList<string> GetLocalIpAddresses() => _inner.GetLocalIpAddresses();
    public Task StartAsync() => _inner.StartAsync();
    public Task StopAsync() => _inner.StopAsync();

    public void SendToModules(byte[] data)
    {
        Tap(data);
        _inner.SendToModules(data);
    }

    public void SendToLoopback(byte[] data) => _inner.SendToLoopback(data);
    public void SendGpsDataToLoopback(byte[] data) => _inner.SendGpsDataToLoopback(data);
    public void SendHelloPacket() => _inner.SendHelloPacket();
    public bool IsModuleHelloOk(ModuleType moduleType) => _inner.IsModuleHelloOk(moduleType);
    public bool IsModuleDataOk(ModuleType moduleType) => _inner.IsModuleDataOk(moduleType);
    public string? GetModuleIpAddress(ModuleType moduleType) => _inner.GetModuleIpAddress(moduleType);
    public string? GetModuleSubnet() => _inner.GetModuleSubnet();
    public string? GetScanReplySubnet() => _inner.GetScanReplySubnet();
    public string? GetActiveModuleSubnet() => _inner.GetActiveModuleSubnet();
    public IPAddress? GetGpsSourceAddress(double maxAgeSeconds = 10) => _inner.GetGpsSourceAddress(maxAgeSeconds);
    public void ScanModules() => _inner.ScanModules();
    public void SetModuleSubnet(byte octet1, byte octet2, byte octet3) => _inner.SetModuleSubnet(octet1, octet2, octet3);
}
