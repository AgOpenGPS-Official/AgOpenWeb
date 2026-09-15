// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.VirtualModules;

/// <summary>
/// Socket construction shared by the listening virtual modules (steer, machine,
/// IMU), so the bind address and broadcast flag are decided in exactly one place
/// rather than being repeated — and drifting — per module.
/// </summary>
internal static class ModuleSocket
{
    /// <summary>
    /// Create a module's listening socket for the given <see cref="ModuleBindMode"/>.
    /// </summary>
    internal static UdpClient NewListener(int listenPort, ModuleBindMode bindMode)
    {
        var bindAddress = bindMode == ModuleBindMode.AllInterfaces
            ? IPAddress.Any
            : IPAddress.Loopback;

        return new UdpClient(new IPEndPoint(bindAddress, listenPort))
        {
            // Broadcast is only meaningful once we're bound off-loopback.
            EnableBroadcast = bindMode == ModuleBindMode.AllInterfaces
        };
    }
}
