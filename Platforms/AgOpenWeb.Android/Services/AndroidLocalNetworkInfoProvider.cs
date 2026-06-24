// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Android.Content;
using Android.Net;
using AgOpenWeb.Services.Interfaces;
using ConnectivityManager = Android.Net.ConnectivityManager;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// Reads local link addresses from Android's ConnectivityManager.
/// System.Net.NetworkInformation can return no interfaces on some Android
/// devices even while Wi-Fi or USB Ethernet is connected.
/// </summary>
public sealed class AndroidLocalNetworkInfoProvider : ILocalNetworkInfoProvider
{
    private readonly ConnectivityManager? _connectivityManager;

    public AndroidLocalNetworkInfoProvider(Context context)
    {
        _connectivityManager =
            context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
    }

    public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses()
    {
        var addresses = new List<LocalNetworkAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var networks = _connectivityManager?.GetAllNetworks();
            if (networks is null) return addresses;

            foreach (var network in networks)
            {
                var capabilities = _connectivityManager?.GetNetworkCapabilities(network);
                if (capabilities is null)
                    continue;

                bool isLan =
                    capabilities.HasTransport(TransportType.Wifi) ||
                    capabilities.HasTransport(TransportType.Ethernet);

                if (!isLan)
                    continue;

                var properties = _connectivityManager?.GetLinkProperties(network);
                if (properties is null)
                    continue;


                foreach (var linkAddress in properties.LinkAddresses)
                {
                    var hostAddress = linkAddress.Address?.HostAddress;
                    if (string.IsNullOrWhiteSpace(hostAddress)) continue;

                    // IPv6 addresses may contain a scope suffix such as "%wlan0".
                    int scopeIndex = hostAddress.IndexOf('%');
                    if (scopeIndex >= 0)
                        hostAddress = hostAddress[..scopeIndex];

                    if (!IPAddress.TryParse(hostAddress, out var address)) continue;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;

                    var addressText = address.ToString();
                    if (!seen.Add(addressText)) continue;

                    addresses.Add(new LocalNetworkAddress(
                        address,
                        linkAddress.PrefixLength,
                        properties.InterfaceName));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to enumerate Android network links: {ex.Message}");
        }

        return addresses;
    }
}
