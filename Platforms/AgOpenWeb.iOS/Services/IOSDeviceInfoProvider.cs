// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using UIKit;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.iOS.Services;

/// <summary>
/// The iPad or iPhone for the bug report dump (<c>device_info.txt</c>): its model, iOS, memory,
/// Low Power Mode and thermal state. iOS lets an app list neither USB devices nor how a network
/// is carried; the interfaces .NET finds are in the shared part of the file.
/// </summary>
public sealed class IOSDeviceInfoProvider : IDeviceInfoProvider
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [DllImport("libc")]
    private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    public string Describe()
    {
        var sb = new StringBuilder();
        Part(sb, "Model", () =>
        {
            // "iPad13,4": the model itself; UIDevice only says "iPad".
            nint len = 0;
            sysctlbyname("hw.machine", null, ref len, IntPtr.Zero, 0);
            var buf = new byte[len];
            string machine = len > 0 && sysctlbyname("hw.machine", buf, ref len, IntPtr.Zero, 0) == 0
                ? Encoding.ASCII.GetString(buf).TrimEnd('\0') : "";
            string kind = "";
            UIApplication.SharedApplication.InvokeOnMainThread(() => kind = UIDevice.CurrentDevice.Model);
            return $"Apple {kind} {machine}".TrimEnd();
        });
        Part(sb, "iOS", () => NSProcessInfo.ProcessInfo.OperatingSystemVersionString);
        Part(sb, "Memory", () => string.Create(Inv, $"{NSProcessInfo.ProcessInfo.PhysicalMemory / 1024 / 1024} MB"));
        Part(sb, "Power", () =>
            (NSProcessInfo.ProcessInfo.LowPowerModeEnabled ? "Low Power Mode ON" : "Low Power Mode off")
            + ", thermal state " + NSProcessInfo.ProcessInfo.ThermalState);
        sb.AppendLine("USB devices: iOS doesn't let an app list them");
        return sb.ToString();
    }

    private static void Part(StringBuilder sb, string name, Func<string> read)
    {
        string text;
        try { text = read(); }
        catch (Exception ex) { text = $"not read ({ex.GetType().Name}: {ex.Message})"; }
        sb.AppendLine(Inv, $"{name}: {text}");
    }
}
