// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgOpenWeb.Services;
using Photino.NET;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// The all-in-one launcher (the desktop twin of the iOS and Android heads): one maximized window
/// that starts the in-process guidance backend and then fills itself with the local web UI. One
/// double-click = host + UI on this box, no separate browser. The host still binds 0.0.0.0, so
/// other devices on the LAN can connect too.
/// </summary>
internal sealed class WebViewLauncher
{
    private readonly PhotinoWindow _window;
    private readonly string[] _args;
    private readonly WindowFullScreen _fullScreen;
    private BackendHost? _backend;
    private bool _closing;

    public WebViewLauncher(PhotinoWindow window, string[] args)
    {
        _window = window;
        _args = args;
        _fullScreen = new WindowFullScreen(window);
    }

    public void Show()
    {
        // The size and place it had when it last closed, or 1280 × 800 centred: what Restore
        // returns to (#571; with none it collapsed to its title bar). The screens aren't known
        // until the window exists, so the saved place is checked against them there.
        _saved = WindowPlacement.Load(AppDataRoot.Documents);
        var start = WindowPlacement.Fit(_saved, Array.Empty<System.Drawing.Rectangle>());
        _restored = start.Bounds;
        _maximized = start.Maximized;
        _window
            .SetMinSize(WindowPlacement.MinWidth, WindowPlacement.MinHeight)
            .SetSize(start.Width, start.Height)
            .SetLocation(new System.Drawing.Point(start.Left, start.Top))
            .SetMaximized(start.Maximized)
            .RegisterSizeChangedHandler((_, size) => { if (IsRestored()) _restored.Size = size; })
            .RegisterLocationChangedHandler((_, at) => { if (IsRestored()) _restored.Location = at; })
            .RegisterMaximizedHandler((_, _) => { if (!_fullScreen.IsOn) _maximized = true; })
            .RegisterRestoredHandler((_, _) => { if (!_fullScreen.IsOn) _maximized = false; })
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .RegisterWindowClosingHandler((_, _) => OnClosing())
            .RegisterWindowCreatedHandler((_, _) => { FitToScreens(); _ = StartAsync(); })
            .LoadRawString(LauncherEntry.SplashHtml("Starting AgOpenWeb…"));
    }

    private WindowPlacement? _saved;
    private System.Drawing.Rectangle _restored;
    private bool _maximized;

    /// <summary>A plain window: its size and place are the ones Restore goes back to.</summary>
    private bool IsRestored()
    {
        try { return !_window.Maximized && !_window.Minimized && !_fullScreen.IsOn; }
        catch { return false; }
    }

    /// <summary>A saved place on a screen that's gone (a monitor unplugged): the default instead.</summary>
    private void FitToScreens()
    {
        if (_saved == null) return;
        try
        {
            var areas = new List<System.Drawing.Rectangle>();
            foreach (var m in _window.Monitors) areas.Add(m.WorkArea);
            var fit = WindowPlacement.Fit(_saved, areas);
            if (fit.Bounds == _saved.Bounds) return;
            _restored = fit.Bounds;
            if (_maximized) return;   // the OS maximizes on a screen it has; Restore then uses _restored next time
            _window.SetSize(fit.Width, fit.Height).SetLocation(new System.Drawing.Point(fit.Left, fit.Top));
        }
        catch (Exception ex) { Console.WriteLine($"[launcher] window placement: {ex.Message}"); }
    }

    private bool OnClosing()
    {
        bool stay = LauncherEntry.StopThenClose(_window, _backend, ref _closing);
        if (!stay)
            new WindowPlacement(_restored.Left, _restored.Top, _restored.Width, _restored.Height, _maximized)
                .Save(AppDataRoot.Documents);
        return stay;
    }

    private async Task StartAsync()
    {
        var backend = new BackendHost();
        try
        {
            // Off the UI thread: StartAsync builds the DI graph + VM pipeline and binds the host.
            await Task.Run(() => backend.StartAsync(_args));
            _backend = backend;
        }
        catch (Exception ex)
        {
            try { await backend.StopAsync(); } catch { /* best effort */ }
            var msg = LauncherEntry.LogStartFailure(ex);
            _window.Invoke(() => _window.LoadRawString(LauncherEntry.SplashHtml($"AgOpenWeb failed to start.\n\n{msg}")));
            return;
        }

        var port = backend.Server?.Port ?? 5174;
        // App Settings › Start Fullscreen (AgOpenGPS isStartFullScreen): the settings are loaded
        // now, so go fullscreen instead of the default maximized window (#110).
        bool fullscreen = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Display.StartFullscreen;
        _window.Invoke(() =>
        {
            if (fullscreen) _fullScreen.Set(true);
            _window.Load(new Uri($"http://localhost:{port}/"));
            ScreenAwake.Hold();
        });
        Console.WriteLine($"[launcher] Backend up; window -> http://localhost:{port}/");
    }

    // The page talks to its host through window.external.sendMessage (Photino's channel):
    //   open|<url>          open a link in the system browser (window.open can't leave the web view)
    //   fullscreen|toggle   the status bar's fullscreen button (#498); fullscreen|state asks.
    //                       Both answer fullscreen|1 or fullscreen|0 for the button's icon.
    private void OnWebMessage(object? sender, string message)
    {
        if (message.StartsWith("open|", StringComparison.Ordinal))
        {
            var url = message[5..];
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                LauncherEntry.OpenUrl(url);
        }
        else if (message is "fullscreen|toggle" or "fullscreen|state")
        {
            // The answer is the state asked for: macOS animates there and says so only after.
            bool on = _fullScreen.IsOn;
            if (message == "fullscreen|toggle") _fullScreen.Set(on = !on);
            try { _window.SendWebMessage(on ? "fullscreen|1" : "fullscreen|0"); } catch { /* window going away */ }
        }
    }
}
