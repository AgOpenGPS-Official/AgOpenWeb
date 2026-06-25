// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// Entry for the in-process launcher mode (Windows default; <c>--launcher</c> on any OS).
/// Boots a minimal Avalonia desktop app (<see cref="LauncherApplication"/>) whose only window
/// supervises the headless <see cref="BackendHost"/>. Distinct from <see cref="HeadlessHost"/>
/// (display-less daemon) and the full windowed <see cref="App"/> (legacy native UI).
/// </summary>
internal static class LauncherEntry
{
    /// <summary>The process args, exposed to <see cref="LauncherApplication"/> so the window
    /// passes them through to <see cref="BackendHost.StartAsync"/> (host config binding).</summary>
    public static string[] Args { get; private set; } = Array.Empty<string>();

    public static void Run(string[] args)
    {
        Args = args;
        WarnIfVirtualMachine();
        AppBuilder.Configure<LauncherApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// The Linux launcher embeds WebKitGTK as a reparented X11 child (NativeControlHost). Its
    /// hardware-GL surface presents correctly on real GPUs (Intel/AMD/NVIDIA, ARM SBCs, industrial
    /// x86) but comes up black on a virtualized GPU — the virtio-gpu used by VMs can't present the
    /// reparented child's GL buffer. The launcher is hardware-accelerated and supported on real
    /// hardware only; we don't force software rendering (it would make the map sluggish). If we
    /// detect a VM, log a clear hint so a black window is never a silent mystery.
    /// </summary>
    private static void WarnIfVirtualMachine()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "systemd-detect-virt", "--vm --quiet") { UseShellExecute = false });
            if (p == null) return;
            p.WaitForExit(1500);
            if (p.HasExited && p.ExitCode == 0) // exit 0 == running in a VM
            {
                Console.WriteLine("[launcher] Warning: virtual machine detected. The embedded AgOpenWeb " +
                    "interface needs a real GPU — on a VM's virtio-gpu the WebView renders black. The " +
                    "Linux launcher is supported on real hardware only.");
            }
        }
        catch { /* detection is a best-effort hint */ }
    }
}
