// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// The network between AgOpenWeb and the steer module, in place of the UDP service: what the
/// host sends to the modules goes to the <see cref="EmulatedSteerModule"/> and its PGN 253 comes
/// back through <see cref="DataReceived"/>, each direction on its own thread as a socket would
/// deliver it. Only the network is replaced; every service above it is the real one.
/// </summary>
public sealed class ModuleWire : IUdpCommunicationService, IDisposable
{
    private readonly EmulatedSteerModule _module;
    private readonly BlockingCollection<byte[]> _toModule = new();
    private readonly BlockingCollection<byte[]> _toHost = new();
    private readonly CancellationTokenSource _cts = new();
    private long _lastFromModule;
    private static readonly IPEndPoint ModuleEp = new(IPAddress.Parse("192.168.5.126"), 8888);

    public ModuleWire(EmulatedSteerModule module)
    {
        _module = module;
        module.Send = p => { if (!_toHost.IsAddingCompleted) _toHost.Add(p); };
        new Thread(() => Pump(_toModule, p => _module.Receive(p))) { IsBackground = true, Name = "wire→module" }.Start();
        new Thread(() => Pump(_toHost, Deliver)) { IsBackground = true, Name = "wire→host" }.Start();
    }

    /// <summary>Packets the host sent, by PGN (for checks such as "status 0 after the test").</summary>
    public ConcurrentQueue<(long Ms, byte[] Packet)> Sent { get; } = new();

    private void Pump(BlockingCollection<byte[]> q, Action<byte[]> to)
    {
        try
        {
            foreach (var p in q.GetConsumingEnumerable(_cts.Token)) to(p);
        }
        catch (OperationCanceledException) { }
    }

    private void Deliver(byte[] p)
    {
        Interlocked.Exchange(ref _lastFromModule, Environment.TickCount64);
        DataReceived?.Invoke(this, new UdpDataReceivedEventArgs
        {
            Data = p, RemoteEndPoint = ModuleEp, PGN = p[3], Timestamp = DateTime.Now,
        });
    }

    public event EventHandler<UdpDataReceivedEventArgs>? DataReceived;
    public event EventHandler<ModuleConnectionEventArgs>? ModuleConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public string? LocalIPAddress => "192.168.5.10";
    public IReadOnlyList<string> GetLocalIpAddresses() => new[] { "192.168.5.10" };
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public void SendToModules(byte[] data)
    {
        var copy = (byte[])data.Clone(); // the builders reuse thread-local buffers
        if (copy.Length > 3 && copy[3] != 254) Sent.Enqueue((_module.NowMs, copy));
        if (!_toModule.IsAddingCompleted) _toModule.Add(copy);
    }

    public void SendToLoopback(byte[] data) { }
    public void SendGpsDataToLoopback(byte[] data) { }
    public void SendHelloPacket() { }
    public bool IsModuleHelloOk(ModuleType moduleType) => moduleType == ModuleType.AutoSteer && Fresh();
    public bool IsModuleDataOk(ModuleType moduleType) => moduleType == ModuleType.AutoSteer && Fresh();
    private bool Fresh() => Environment.TickCount64 - Interlocked.Read(ref _lastFromModule) < 2000;
    public string? GetModuleIpAddress(ModuleType moduleType) => moduleType == ModuleType.AutoSteer ? "192.168.5.126" : null;
    public string? GetModuleSubnet() => "192.168.5";
    public string? GetScanReplySubnet() => null;
    public string? GetActiveModuleSubnet() => "192.168.5";
    public IPAddress? GetGpsSourceAddress(double maxAgeSeconds = 10) => null;
    public void ScanModules() { }
    public void SetModuleSubnet(byte octet1, byte octet2, byte octet3) { }

    public void Dispose()
    {
        _cts.Cancel();
        _toModule.CompleteAdding();
        _toHost.CompleteAdding();
    }
}
