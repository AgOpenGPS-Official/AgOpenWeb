// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>
/// The steer module on the same tractor, as far as the machine module cares: it sends PGN 253
/// ten times a second to port 9999 (wheel straight, no IMU, no switches wired). AgOpenWeb
/// sends its settings (251/252/238/236/247) when a steer module first talks or comes back after
/// 2 s of silence, and the machine module hears those too, so a tractor with and without a
/// steer module behave differently for the machine module.
/// </summary>
public sealed class SteerModuleStub : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _host = new(IPAddress.Loopback, 9999);
    private readonly Timer _timer;
    public volatile bool On;

    public SteerModuleStub()
    {
        _timer = new Timer(_ => Send(), null, 100, 100);
    }

    private void Send()
    {
        if (!On) return;
        // 0x7E = the steer module; angle 0, heading 9999 (none), roll 8888 (none), switches
        // 0x01 (work switch input high = off, steer switch off), PWM 0.
        var m = new byte[] { 0x80, 0x81, 0x7E, 253, 8, 0, 0, 0x0F, 0x27, 0xB8, 0x22, 0x01, 0, 0 };
        int sum = 0;
        for (int i = 2; i < m.Length - 1; i++) sum += m[i];
        m[^1] = (byte)sum;
        try { _udp.Send(m, m.Length, _host); } catch { /* nobody listening yet */ }
    }

    public void Dispose()
    {
        On = false;
        _timer.Dispose();
        _udp.Dispose();
    }
}
