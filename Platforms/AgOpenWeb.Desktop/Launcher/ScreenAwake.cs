// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// Keeps the display from sleeping/blanking while the launcher shows the web UI — a guidance screen
/// that dims mid-pass is unusable. Always on, no setting. Best effort; each hold ends on its own
/// when the process (or window) goes away.
/// <list type="bullet">
///   <item>Windows: <c>SetThreadExecutionState</c> on the UI thread (held until that thread exits).</item>
///   <item>macOS: <c>caffeinate -d -i -w &lt;pid&gt;</c> (exits with this process).</item>
///   <item>Linux (X11): <c>xdg-screensaver suspend &lt;xid&gt;</c> (lifts when the window is destroyed).</item>
/// </list>
/// </summary>
internal static class ScreenAwake
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static bool _held;

    /// <summary>Call on the UI thread. Idempotent, so a page reload never spawns a second
    /// caffeinate / xdg-screensaver.</summary>
    public static void Hold(Window window)
    {
        if (_held) return;
        _held = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Spawn("/usr/bin/caffeinate", $"-d -i -w {Environment.ProcessId}")?.Dispose();
            }
            else if (OperatingSystem.IsLinux() && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } h)
            {
                Spawn("xdg-screensaver", $"suspend 0x{h.Handle.ToInt64():x}")?.Dispose();
            }
            else return;
            Console.WriteLine("[screen] keep-awake on");
        }
        catch (Exception ex)
        {
            // e.g. no xdg-utils installed — the UI still works, the screen may just blank.
            Console.WriteLine($"[screen] keep-awake unavailable: {ex.Message}");
        }
    }

    private static Process? Spawn(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
}
