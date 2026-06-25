// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using NativeWebView = Avalonia.Controls.NativeWebView;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// The all-in-one Windows launcher (the desktop twin of the iOS thin launcher): a single
/// maximized window that starts the in-process guidance backend (<see cref="BackendHost"/>)
/// and then fills itself with a <see cref="NativeWebView"/> (WebView2) pointed at the local
/// web UI. One double-click = host + UI on this box, no separate browser. The host still
/// binds 0.0.0.0, so other devices on the LAN can connect too.
/// </summary>
internal sealed class WebViewLauncherWindow : Window
{
    private readonly string[] _args;
    private BackendHost? _backend;
    private bool _closing;

    public WebViewLauncherWindow(string[] args)
    {
        _args = args;
        Title = "AgOpenWeb";
        WindowState = WindowState.Maximized;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = new SolidColorBrush(Color.Parse("#0b1020"));
        TryLoadIcon();

        Content = Splash("Starting AgOpenWeb…");
        // Start once the window exists; the host binds before the WebView navigates to it.
        Opened += (_, _) => _ = StartAsync();
    }

    private async Task StartAsync()
    {
        var backend = new BackendHost();
        try
        {
            // Off the UI thread: StartAsync builds the DI graph + VM pipeline and binds the
            // host (blocking-ish). The await means the host is fully up before we navigate.
            await Task.Run(() => backend.StartAsync(_args));
            _backend = backend;
        }
        catch (Exception ex)
        {
            // A WinExe has no console — drop the stack to a temp log so a start failure on a
            // user's box is diagnosable, and show the gist in-window.
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agopenweb-launcher-error.log");
            try { System.IO.File.WriteAllText(log, ex.ToString()); } catch { }
            try { await backend.StopAsync(); } catch { /* best effort */ }
            Content = Splash($"AgOpenWeb failed to start.\n\n{ex.Message}\n\nDetails: {log}");
            return;
        }

        var port = _backend.Server?.Port ?? 5174;
        Content = new NativeWebView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Source = new Uri($"http://localhost:{port}/"),
        };
    }

    private static Control Splash(string message) => new TextBlock
    {
        Text = message,
        Foreground = Brushes.White,
        FontSize = 16,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 560,
    };

    // Closing must stop the backend first so its ApplicationStopping save runs (config + state).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_backend is { IsRunning: true } && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            _ = StopThenCloseAsync();
        }
        base.OnClosing(e);
    }

    private async Task StopThenCloseAsync()
    {
        var backend = _backend;
        // Off the UI thread — StopAsync does blocking save/dispose work that would deadlock
        // against Avalonia's sync context if awaited on the UI thread.
        try { if (backend != null) await Task.Run(() => backend.StopAsync()); } catch { /* close regardless */ }
        Close();
    }

    private void TryLoadIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://AgOpenWeb.Desktop/Assets/avalonia-logo.ico"));
            Icon = new WindowIcon(s);
        }
        catch { /* icon is cosmetic */ }
    }
}
