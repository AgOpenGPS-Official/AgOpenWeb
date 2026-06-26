// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace AgOpenWeb.VehicleSimulator.ViewModels;

/// <summary>
/// One checkable network destination in the "Send to" list: loopback, or a real NIC (whose
/// subnet broadcast we target so the host on that LAN receives the GPS/PGN without us having to
/// know its exact IP). The host binds <c>0.0.0.0:9999</c>, so a directed broadcast reaches it.
/// </summary>
public sealed class NetworkTargetOption : INotifyPropertyChanged
{
    /// <summary>Stable id for persistence (interface name + address, or "loopback").</summary>
    public string Key { get; }
    public string Display { get; }
    public IPEndPoint Endpoint { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Raised when toggled — the ViewModel recomputes the live target set + persists.</summary>
    public Action? SelectionChanged;

    public NetworkTargetOption(string key, string display, IPEndPoint endpoint, bool selected)
    {
        Key = key;
        Display = display;
        Endpoint = endpoint;
        _isSelected = selected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Loopback + every up IPv4 interface, as send options. <paramref name="selectedKeys"/> is the
    /// persisted selection; if empty, loopback defaults to checked (preserves localhost behavior).
    /// </summary>
    public static List<NetworkTargetOption> Enumerate(int port, IReadOnlyCollection<string> selectedKeys)
    {
        var list = new List<NetworkTargetOption>
        {
            new("loopback", "Loopback (127.0.0.1)", new IPEndPoint(IPAddress.Loopback, port),
                selectedKeys.Count == 0 || selectedKeys.Contains("loopback")),
        };

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bcast = DirectedBroadcast(ua.Address, ua.IPv4Mask);
                if (bcast == null) continue;

                string key = nic.Name + "|" + ua.Address;
                string display = $"{nic.Name} — {ua.Address} → {bcast}";
                list.Add(new NetworkTargetOption(
                    key, display, new IPEndPoint(bcast, port), selectedKeys.Contains(key)));
            }
        }
        return list;
    }

    /// <summary>Subnet (directed) broadcast = ip | ~mask.</summary>
    private static IPAddress? DirectedBroadcast(IPAddress ip, IPAddress? mask)
    {
        if (mask == null) return null;
        var ipb = ip.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        if (ipb.Length != 4 || mb.Length != 4) return null;
        var bc = new byte[4];
        for (int i = 0; i < 4; i++) bc[i] = (byte)(ipb[i] | (~mb[i] & 0xFF));
        return new IPAddress(bc);
    }
}
