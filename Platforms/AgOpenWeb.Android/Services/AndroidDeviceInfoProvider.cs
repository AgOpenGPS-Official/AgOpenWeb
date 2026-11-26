// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.Net;
using Android.OS;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// The tablet for the bug report dump (<c>device_info.txt</c>): make and model, Android, the web
/// view that draws the UI, memory, power saving, how each network is carried (Wi-Fi, Ethernet:
/// a USB adapter is Ethernet) and the USB devices plugged in. Every part is on its own, so one
/// that a device refuses leaves the rest. No serial numbers (they need a permission anyway).
/// </summary>
public sealed class AndroidDeviceInfoProvider : IDeviceInfoProvider
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Context _context;

    public AndroidDeviceInfoProvider(Context context) => _context = context;

    public string Describe()
    {
        var sb = new StringBuilder();
        Part(sb, "Model", () =>
        {
            string soc = OperatingSystem.IsAndroidVersionAtLeast(31) ? $"{Build.SocManufacturer} {Build.SocModel}" : Build.Hardware ?? "";
            return $"{Build.Manufacturer} {Build.Model} ({Build.Device}), chipset {soc}";
        });
        Part(sb, "Android", () => string.Create(Inv, $"{Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})"));
        Part(sb, "Web view", () =>
        {
            var pkg = OperatingSystem.IsAndroidVersionAtLeast(26) ? global::Android.Webkit.WebView.CurrentWebViewPackage : null;
            return pkg == null ? "not given by the system" : $"{pkg.PackageName} {pkg.VersionName}";
        });
        Part(sb, "Memory", () =>
        {
            var am = (ActivityManager?)_context.GetSystemService(Context.ActivityService);
            var mi = new ActivityManager.MemoryInfo();
            am!.GetMemoryInfo(mi);
            return string.Create(Inv, $"{mi.TotalMem / 1024 / 1024} MB, {mi.AvailMem / 1024 / 1024} MB available{(mi.LowMemory ? ", the system says memory is LOW" : "")}");
        });
        Part(sb, "Power", () =>
        {
            var pm = (PowerManager?)_context.GetSystemService(Context.PowerService);
            var parts = new List<string>
            {
                pm!.IsPowerSaveMode ? "power saving ON" : "power saving off",
                pm.IsIgnoringBatteryOptimizations(_context.PackageName) ? "the app is exempt from battery optimisation" : "the app is under battery optimisation",
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) parts.Add("thermal status " + pm.CurrentThermalStatus);
            return string.Join(", ", parts);
        });
        Section(sb, "Networks:", Networks);
        Section(sb, "USB devices:", UsbDevices);
        return sb.ToString();
    }

    private static void Part(StringBuilder sb, string name, Func<string> read)
    {
        string text;
        try { text = read(); }
        catch (Exception ex) { text = $"not read ({ex.GetType().Name}: {ex.Message})"; }
        sb.AppendLine(Inv, $"{name}: {text}");
    }

    private static void Section(StringBuilder sb, string heading, Func<List<string>> read)
    {
        sb.AppendLine(heading);
        List<string> lines;
        try { lines = read(); }
        catch (Exception ex) { lines = new List<string> { $"not read ({ex.GetType().Name}: {ex.Message})" }; }
        if (lines.Count == 0) lines.Add("none found");
        foreach (var l in lines) sb.AppendLine("  " + l);
    }

    private List<string> Networks()
    {
        var lines = new List<string>();
        var cm = (ConnectivityManager?)_context.GetSystemService(Context.ConnectivityService);
        var active = cm!.ActiveNetwork;
#pragma warning disable CA1422 // as AndroidLocalNetworkInfoProvider: the list of all networks, not only the default one
        var networks = cm.GetAllNetworks() ?? Array.Empty<Network>();
#pragma warning restore CA1422
        foreach (var network in networks)
        {
            var caps = cm.GetNetworkCapabilities(network);
            var props = cm.GetLinkProperties(network);
            if (caps == null || props == null) continue;
            var kinds = new (TransportType T, string Name)[]
            {
                (TransportType.Wifi, "Wi-Fi"), (TransportType.Ethernet, "Ethernet"), (TransportType.Cellular, "cellular"),
                (TransportType.Bluetooth, "Bluetooth"), (TransportType.Vpn, "VPN"),
            }.Where(k => caps.HasTransport(k.T)).Select(k => k.Name).ToList();
            var addrs = props.LinkAddresses
                .Where(a => a.Address is Java.Net.Inet4Address)
                .Select(a => string.Create(Inv, $"{a.Address!.HostAddress}/{a.PrefixLength}")).ToList();
            lines.Add(string.Create(Inv,
                $"{props.InterfaceName}: {(kinds.Count == 0 ? "unknown kind" : string.Join("+", kinds))}, {(addrs.Count == 0 ? "no IPv4 address" : string.Join(", ", addrs))}, down {caps.LinkDownstreamBandwidthKbps / 1000} Mbit/s, up {caps.LinkUpstreamBandwidthKbps / 1000} Mbit/s{(caps.HasCapability(NetCapability.Internet) ? ", internet" : ", no internet")}{(network.Equals(active) ? ", the default network" : "")}"));
        }
        return lines;
    }

    private List<string> UsbDevices()
    {
        var lines = new List<string>();
        var usb = (UsbManager?)_context.GetSystemService(Context.UsbService);
        foreach (var d in usb?.DeviceList?.Values ?? Enumerable.Empty<UsbDevice>())
        {
            string name = string.Join(' ', new[] { d.ManufacturerName, d.ProductName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            // The class of a composite device is on its interfaces (an Ethernet adapter: Comm, CdcData).
            var classes = new List<string> { d.DeviceClass.ToString() };
            for (int i = 0; i < d.InterfaceCount; i++) classes.Add(d.GetInterface(i).InterfaceClass.ToString());
            lines.Add(string.Create(Inv,
                $"{d.VendorId:x4}:{d.ProductId:x4} {(name.Length == 0 ? "(no name)" : name)}, class {string.Join("/", classes.Distinct().Where(c => c != "PerInterface"))}"));
        }
        return lines;
    }
}
