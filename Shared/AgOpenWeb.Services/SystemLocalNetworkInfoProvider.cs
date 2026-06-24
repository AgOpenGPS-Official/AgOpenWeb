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
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Local network discovery for desktop platforms and as the default provider.
/// </summary>
public sealed class SystemLocalNetworkInfoProvider : ILocalNetworkInfoProvider
{
    public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses()
    {
        var addresses = new List<LocalNetworkAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    if (unicast.IPv4Mask is null) continue;

                    var addressText = unicast.Address.ToString();
                    if (!seen.Add(addressText)) continue;

                    addresses.Add(new LocalNetworkAddress(
                        unicast.Address,
                        GetPrefixLength(unicast.IPv4Mask),
                        nic.Name));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to enumerate system network interfaces: {ex.Message}");
        }

        return addresses;
    }

    internal static int GetPrefixLength(IPAddress subnetMask)
    {
        int prefixLength = 0;
        foreach (byte value in subnetMask.GetAddressBytes())
        {
            byte current = value;
            for (int bit = 0; bit < 8; bit++)
            {
                prefixLength += current >> 7;
                current <<= 1;
            }
        }

        return prefixLength;
    }
}
