// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenWeb.VehicleSimulator.Modules;

/// <summary>
/// Virtual IMU module. The simulator already embeds heading/roll/pitch/yaw in
/// the $PANDA sentence from <see cref="VirtualGpsReceiver"/>, but the host only
/// lists the IMU under "Module Status" when it sees the IMU hello (PGN 121).
/// This module answers the host hello (PGN 200) and also broadcasts PGN 121
/// once a second so the IMU shows up as a connected module alongside the
/// autosteer and machine modules — matching real AiO firmware where one Teensy
/// reports each subsystem with its own hello PGN.
/// </summary>
public class VirtualImuModule : IDisposable
{
    private readonly UdpClient _udp;
    private readonly UdpTargets _targets;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _helloTask;

    // Counters
    public long ReceivedHelloCount { get; private set; }
    public long SentHelloCount { get; private set; }

    public VirtualImuModule(UdpTargets targets, int listenPort = 8890)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort)) { EnableBroadcast = true };
        _targets = targets;
    }

    /// <summary>Convenience ctor (tests): a single fixed host destination.</summary>
    public VirtualImuModule(int listenPort = 8890, int hostPort = 9999, string hostIp = "127.0.0.1")
        : this(new UdpTargets(new IPEndPoint(IPAddress.Parse(hostIp), hostPort)), listenPort) { }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
        _helloTask = Task.Run(() => HelloLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _receiveTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _helloTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    /// <summary>Taps for the sim's data panes (outgoing / incoming raw frames).</summary>
    public Action<string>? OnSent;
    public Action<string>? OnReceived;

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                ProcessPacket(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
    }

    private void ProcessPacket(byte[] data)
    {
        OnReceived?.Invoke(PgnProtocol.Describe(data, data.Length));
        if (!PgnProtocol.IsValidPacket(data, data.Length))
            return;

        switch (PgnProtocol.GetPgn(data))
        {
            case PgnProtocol.PGN_HELLO_FROM_HOST:
                ReceivedHelloCount++;
                SendHello();
                break;
        }
    }

    private void Emit(byte[] packet)
    {
        OnSent?.Invoke(PgnProtocol.Describe(packet, packet.Length));
        foreach (var ep in _targets.Endpoints)
        {
            try { _udp.Send(packet, packet.Length, ep); } catch { /* one bad dest must not stop the others */ }
        }
    }

    private void SendHello()
    {
        var packet = PgnProtocol.BuildHelloPacket(PgnProtocol.PGN_HELLO_IMU);
        Emit(packet);
        SentHelloCount++;
    }

    private async Task HelloLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SendHello();
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        Stop();
        _udp.Dispose();
        _cts?.Dispose();
    }
}
