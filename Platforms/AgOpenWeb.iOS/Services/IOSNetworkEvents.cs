// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.Network;
using CoreFoundation;
using Network;

namespace AgOpenWeb.iOS.Services;

/// <summary>
/// iOS network changes, reported to the <see cref="INetworkWatcher"/> (#385): .NET's
/// NetworkChange event isn't available on iOS, which the watcher would otherwise cover only
/// with its 30 s heartbeat. NWPathMonitor reports every change of path (Wi-Fi, cellular,
/// a USB Ethernet adapter).
/// </summary>
internal static class IOSNetworkEvents
{
    private static NWPathMonitor? _monitor; // kept: a collected monitor stops reporting

    public static void Register(INetworkWatcher watcher)
    {
        if (_monitor != null) return;
        var monitor = new NWPathMonitor();
        monitor.SnapshotHandler = _ => watcher.NotifyChanged();
        monitor.SetQueue(DispatchQueue.DefaultGlobalQueue);
        monitor.Start();
        _monitor = monitor;
    }
}
