// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// The dump's <c>device_info.txt</c>: the device and what is connected to it. A report of a slow
/// screen or of lost sentences needs to say whether the modules are on Wi-Fi or on a USB
/// Ethernet adapter, how much memory the tablet has and what it is (#922: the dump said
/// "Unix 36.0.0.0, localhost, 8 processors").
/// Memory, the data folder's disk and the network interfaces are the same code everywhere; the
/// desktop's make, model and USB devices are read here too (files and the system's own tools,
/// no platform API); a tablet's come from its head (<see cref="IDeviceInfoProvider"/>).
/// Every part is on its own: one that fails says so in a line and the rest is written.
/// No hardware addresses and no serial numbers.
/// </summary>
public static class DeviceReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Build the report. Starts the system's tools on a desktop: call it off the host loop.</summary>
    public static string Build(IDeviceInfoProvider? platform, string? dataFolder = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Inv, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture})");
        // A head gives the device's memory itself; .NET's own figure is the device's on a desktop
        // and a limit of the runtime's on Android (170 MB on a 4 GB tablet).
        Part(sb, "Memory", () => Memory(deviceTotal: platform == null));
        Part(sb, "Data folder", () => Disk(dataFolder ?? AppDataRoot.Documents));

        if (platform != null)
        {
            sb.AppendLine();
            sb.AppendLine("Device:");
            Part(sb, "Device", () => Indent(platform.Describe()), heading: false);
        }
        else if (Desktop() is { } desktop)
        {
            sb.AppendLine();
            sb.AppendLine("Device:");
            Part(sb, "Device", desktop, heading: false);
        }

        sb.AppendLine();
        sb.AppendLine("Network interfaces (.NET):");
        Part(sb, "Network interfaces", NetworkInterfaces, heading: false);
        return sb.ToString();
    }

    private static void Part(StringBuilder sb, string name, Func<string> read, bool heading = true)
    {
        string text;
        try { text = read(); }
        catch (Exception ex) { text = heading ? "not read (" + ex.GetType().Name + ": " + ex.Message + ")" : "  " + name + ": not read (" + ex.GetType().Name + ": " + ex.Message + ")"; }
        if (heading) sb.AppendLine(Inv, $"{name}: {text}");
        else sb.AppendLine(text.TrimEnd('\r', '\n'));
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Select(l => "  " + l));

    private static string Mb(long bytes) => (bytes / 1024 / 1024).ToString(Inv) + " MB";

    private static string Memory(bool deviceTotal)
    {
        // iOS gives no working set (0): the managed heap is what can be said there.
        long ws = Environment.WorkingSet;
        string mine = ws > 0 ? "this process " + Mb(ws) : "this process's managed heap " + Mb(GC.GetTotalMemory(false));
        return deviceTotal ? Mb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) + ", " + mine : mine;
    }

    private static string Disk(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && Path.GetFullPath(folder).StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault()
            ?? new DriveInfo(root ?? "/");
        return string.Create(Inv, $"{Mb(drive.AvailableFreeSpace)} free of {Mb(drive.TotalSize)} ({drive.DriveFormat})");
    }

    /// <summary>Every interface with an address, but the loopback: its kind, state, speed and addresses.</summary>
    internal static string NetworkInterfaces()
    {
        var lines = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1).ThenBy(n => n.Name, StringComparer.Ordinal))
        {
            var addrs = new List<string>();
            try
            {
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        addrs.Add(string.Create(Inv, $"{a.Address}/{PrefixLength(a)}"));
                    else if (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                        addrs.Add("an IPv6 address");
                }
            }
            catch { /* some systems don't give the addresses of an interface that is down */ }
            // An interface with no address (the dozens of tunnels and bridges a system keeps)
            // carries nothing for the app.
            if (addrs.Count == 0) continue;
            string speed = "";
            try { if (ni.Speed > 0) speed = string.Create(Inv, $", {ni.Speed / 1_000_000} Mbit/s"); } catch { }
            string desc = string.IsNullOrWhiteSpace(ni.Description) || ni.Description == ni.Name ? "" : " (" + ni.Description + ")";
            lines.Add(string.Create(Inv, $"  {ni.Name}{desc}: {ni.NetworkInterfaceType}, {ni.OperationalStatus}{speed}, {string.Join(", ", addrs)}"));
        }
        return lines.Count == 0 ? "  none with an address" : string.Join('\n', lines);
    }

    private static int PrefixLength(UnicastIPAddressInformation a)
    {
        try { return a.PrefixLength; } catch { return 0; }
    }

    // ── Desktop: make, model and USB devices, by the system's own files and tools ──

    private static Func<string>? Desktop()
    {
        if (OperatingSystem.IsLinux()) return Linux;
        if (OperatingSystem.IsMacOS()) return Mac;
        if (OperatingSystem.IsWindows()) return Windows;
        return null;
    }

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim('\0', ' ', '\r', '\n', '\t') : null; }
        catch { return null; }
    }

    private static string Linux()
    {
        var sb = new StringBuilder();
        // A PC says who made it in DMI, a single-board computer in the device tree.
        string? model = ReadFile("/proc/device-tree/model");
        if (string.IsNullOrEmpty(model))
        {
            model = string.Join(' ', new[] { ReadFile("/sys/class/dmi/id/sys_vendor"), ReadFile("/sys/class/dmi/id/product_name") }
                .Where(s => !string.IsNullOrEmpty(s)));
        }
        sb.AppendLine(Inv, $"  Model: {(string.IsNullOrEmpty(model) ? "not given by the system" : model)}");
        sb.AppendLine("  USB devices:");
        int n = 0;
        if (Directory.Exists("/sys/bus/usb/devices"))
        {
            foreach (var dir in Directory.GetDirectories("/sys/bus/usb/devices").OrderBy(d => d, StringComparer.Ordinal))
            {
                string? vid = ReadFile(Path.Combine(dir, "idVendor")), pid = ReadFile(Path.Combine(dir, "idProduct"));
                if (vid == null || pid == null) continue; // an interface of a device, not a device
                string name = string.Join(' ', new[] { ReadFile(Path.Combine(dir, "manufacturer")), ReadFile(Path.Combine(dir, "product")) }
                    .Where(s => !string.IsNullOrEmpty(s)));
                string speed = ReadFile(Path.Combine(dir, "speed")) is { Length: > 0 } s ? ", " + s + " Mbit/s" : "";
                sb.AppendLine(Inv, $"    {vid}:{pid} {(name.Length == 0 ? "(no name)" : name)}{speed}");
                n++;
            }
        }
        if (n == 0) sb.AppendLine("    none found");
        return sb.ToString();
    }

    private static string Mac()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Inv, $"  Model: {Run("/usr/sbin/sysctl", "-n hw.model") ?? "not given by the system"}");
        sb.AppendLine("  USB devices:");
        // The registry's USB plane, one device a line: "+-o Name@location  <class ...>".
        var names = (Run("/usr/sbin/ioreg", "-p IOUSB -w0") ?? "").Split('\n')
            .Select(l => l.IndexOf("+-o ", StringComparison.Ordinal) is var i and >= 0 ? l[(i + 4)..] : "")
            .Select(l => l.IndexOf("  <", StringComparison.Ordinal) is var j and >= 0 ? l[..j] : l)
            .Select(l => l.IndexOf('@') is var k and > 0 ? l[..k] : l)
            .Where(l => l.Length > 0 && l != "Root").ToList();
        if (names.Count == 0) sb.AppendLine("    none found");
        foreach (var name in names) sb.AppendLine(Inv, $"    {name}");
        return sb.ToString();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Windows()
    {
        using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string model = string.Join(' ', new[] { bios?.GetValue("SystemManufacturer") as string, bios?.GetValue("SystemProductName") as string }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.Create(Inv, $"  Model: {(model.Length == 0 ? "not given by the system" : model)}\n  USB devices: not listed on Windows");
    }

    /// <summary>A system tool's output, or null when it isn't there, fails or takes over 3 s.</summary>
    private static string? Run(string file, string args)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            string text = read.Wait(1000) ? read.Result.Trim() : "";
            return p.ExitCode == 0 && text.Length > 0 ? text : null;
        }
        catch { return null; }
    }
}
