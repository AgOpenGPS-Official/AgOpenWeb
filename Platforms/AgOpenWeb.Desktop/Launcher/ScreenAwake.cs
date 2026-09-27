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
/// Keeps the display from sleeping/blanking while the launcher shows the web UI (App Settings ›
/// Keep Screen On) — a guidance screen that dims mid-pass is unusable. Best effort; each hold also
/// ends on its own when the process (or window) goes away.
/// <list type="bullet">
///   <item>Windows: <c>SetThreadExecutionState</c> on the UI thread (held until that thread exits).</item>
///   <item>macOS: <c>caffeinate -d -i -w &lt;pid&gt;</c> (exits with this process).</item>
///   <item>Linux (X11): <c>xdg-screensaver suspend|resume &lt;xid&gt;</c> (lifts when the window is destroyed).</item>
/// </list>
/// </summary>
internal static class ScreenAwake
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static bool _on;
    private static Process? _caffeinate;

    /// <summary>Call on the UI thread. Idempotent, so a page reload or a repeated setting
    /// write never spawns a second caffeinate / xdg-screensaver.</summary>
    public static void Set(Window window, bool on)
    {
        if (on == _on) return;
        _on = on;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadExecutionState(on ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (on) _caffeinate = Spawn("/usr/bin/caffeinate", $"-d -i -w {Environment.ProcessId}");
                else { _caffeinate?.Kill(); _caffeinate?.Dispose(); _caffeinate = null; }
            }
            else if (OperatingSystem.IsLinux() && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } h)
            {
                Spawn("xdg-screensaver", $"{(on ? "suspend" : "resume")} 0x{h.Handle.ToInt64():x}")?.Dispose();
            }
            else return;
            Console.WriteLine($"[screen] keep-awake {(on ? "on" : "off")}");
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
