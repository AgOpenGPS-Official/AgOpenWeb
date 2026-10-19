// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Photino.NET;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// Entry for the in-process launcher mode (Windows and macOS default; <c>--launcher</c> on any
/// OS). The window is a Photino.NET <see cref="PhotinoWindow"/>: WebView2 on Windows, WKWebView on
/// macOS, WebKitGTK on Linux. The default window is the all-in-one app — the guidance backend
/// (<see cref="BackendHost"/>) in this process and the web UI filling the window. <c>--console</c>
/// instead shows the small supervisor page (<see cref="ConsolePage"/>) for a box that only serves
/// other devices. Distinct from <see cref="HeadlessHost"/> (display-less daemon).
/// </summary>
internal static class LauncherEntry
{
    public static void Run(string[] args)
    {
        bool console = Array.IndexOf(args, "--console") >= 0;
        var window = new PhotinoWindow()
            .SetTitle("AgOpenWeb")
            .SetUseOsDefaultLocation(false)
            .SetUseOsDefaultSize(false)
            .SetLogVerbosity(0);
        var icon = IconPath();
        if (icon != null) window.SetIconFile(icon);

        if (console) new ConsolePage(window, args).Show();
        else new WebViewLauncher(window, args).Show();
        window.WaitForClose();
    }

    /// <summary>The window icon file, next to the executable (Windows and Linux; the macOS
    /// bundle carries its own).</summary>
    private static string? IconPath()
    {
        if (OperatingSystem.IsMacOS()) return null;
        var p = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "agopenweb.ico");
        return System.IO.File.Exists(p) ? p : null;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* no browser available */ }
    }

    /// <summary>A start failure is written to a temp log (a WinExe has no console) and returned
    /// as a one-line message for the window.</summary>
    public static string LogStartFailure(Exception ex)
    {
        var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agopenweb-launcher-error.log");
        try { System.IO.File.WriteAllText(log, ex.ToString()); } catch { }
        return $"{ex.Message} (details: {log})";
    }

    /// <summary>Closing the window must stop the backend first so its save runs. Photino asks
    /// before closing: confirm (a stray click on X mid-job, #348), cancel, stop off the UI
    /// thread, then close for real.</summary>
    public static bool StopThenClose(PhotinoWindow window, BackendHost? backend, ref bool closing)
    {
        if (backend is not { IsRunning: true } || closing) return false; // let it close
        // Same wording as the Android head's Back prompt
        if (window.ShowMessage("Exit AgOpenWeb?", "Guidance stops, and other devices lose the web view.",
                PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Question) != PhotinoDialogResult.Yes)
            return true; // stay open
        closing = true;
        _ = Task.Run(async () =>
        {
            try { await backend.StopAsync(); } catch { /* close regardless */ }
            window.Invoke(window.Close);
        });
        return true; // cancel this close
    }

    private const string SplashCss = "html,body{margin:0;height:100%;background:#0b1020;color:#fff;font:16px system-ui,sans-serif}" +
        "body{display:flex;align-items:center;justify-content:center;text-align:center}p{max-width:560px;white-space:pre-line;line-height:1.5}";

    public static string SplashHtml(string text) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><style>{SplashCss}</style></head><body><p>{System.Net.WebUtility.HtmlEncode(text)}</p></body></html>";
}
