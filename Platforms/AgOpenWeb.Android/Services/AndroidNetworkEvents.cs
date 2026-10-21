// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using Android.Content;
using Android.Net;
using AgOpenWeb.Services.Network;
using ConnectivityManager = Android.Net.ConnectivityManager;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// Android's network changes, reported to the <see cref="INetworkWatcher"/> (#385): .NET's
/// NetworkChange event doesn't fire for apps on Android, which the watcher would otherwise
/// cover only with its 30 s heartbeat. Every network counts, not just the default one, so
/// a USB Ethernet link to the AiO next to Wi-Fi is heard too.
/// </summary>
internal sealed class AndroidNetworkEvents : ConnectivityManager.NetworkCallback
{
    private readonly INetworkWatcher _watcher;

    private AndroidNetworkEvents(INetworkWatcher watcher) => _watcher = watcher;

    public static void Register(Context context, INetworkWatcher watcher)
    {
        if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm) return;
        var request = new NetworkRequest.Builder().Build();
        if (request != null) cm.RegisterNetworkCallback(request, new AndroidNetworkEvents(watcher));
    }

    public override void OnAvailable(Network network) => _watcher.NotifyChanged();
    public override void OnLost(Network network) => _watcher.NotifyChanged();
    public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => _watcher.NotifyChanged();
}
