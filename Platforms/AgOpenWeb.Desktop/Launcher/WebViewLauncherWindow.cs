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
using Avalonia.Threading;
using NativeWebView = Avalonia.Controls.NativeWebView;

namespace AgOpenWeb.Desktop.Launcher;

/// <summary>
/// The all-in-one launcher (the desktop twin of the iOS thin launcher): a single maximized
/// window that starts the in-process guidance backend (<see cref="BackendHost"/>) and then
/// fills itself with a <see cref="NativeWebView"/> pointed at the local web UI. One double-click
/// = host + UI on this box, no separate browser. The host still binds 0.0.0.0, so other devices
/// on the LAN can connect too.
///
/// Backends: WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux — all embedded in-window
/// and GPU-accelerated. On Linux the WebView is a reparented WebKitGTK child (NativeControlHost);
/// its hardware-GL surface presents on real GPUs but comes up black on virtualized GPUs (e.g. the
/// virtio-gpu in a VM) — the Linux launcher is supported on real hardware only, not in a VM.
/// </summary>
internal sealed class WebViewLauncherWindow : Window
{
    private readonly string[] _args;
    private BackendHost? _backend;
    private bool _closing;

    // The WebView lives in the visual tree from window open (so the native WKWebView/WebView2/
    // WebKitGTK is realized + sized by the maximized window) — we only navigate it once the host
    // is up. A native control swapped in AFTER layout settled tends to come up zero-sized/invisible.
    private readonly NativeWebView _web = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    private readonly Border _splash;

    public WebViewLauncherWindow(string[] args)
    {
        _args = args;
        Title = "AgOpenWeb";
        WindowState = WindowState.Maximized;
        RequestedThemeVariant = ThemeVariant.Dark;
        TryLoadIcon();

        _splash = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0b1020")),
            Child = SplashText("Starting AgOpenWeb…"),
        };
        _web.NavigationStarted += (_, _) =>
        {
            Console.WriteLine("[webview] navigating");
            // The native child window exists by now (navigation needs the adapter) — sync its size.
            SyncNativeWebViewSize();
        };
        _web.NavigationCompleted += (_, e) =>
        {
            Console.WriteLine($"[webview] completed IsSuccess={e.IsSuccess}");
            // Reveal the loaded UI. The WebKitGTK backend can raise this off the UI thread, so
            // marshal the property change — setting IsVisible off-thread silently no-ops on X11.
            if (e.IsSuccess) Dispatcher.UIThread.Post(() => _splash.IsVisible = false);
        };

        // WebView underneath, splash on top until the page loads.
        Content = new Grid { Children = { _web, _splash } };
        Opened += (_, _) => _ = StartAsync();
    }

    /// <summary>
    /// Linux (X11) only: re-sync the embedded WebView's native child window to the control's bounds.
    /// Avalonia's <c>NativeControlHost</c> creates the native window asynchronously (after the
    /// WebKitGTK adapter is built) — which is after the maximized window's initial arrange — and
    /// then never re-arranges it, so the native window stays at its 1×1 creation size while the
    /// Avalonia-side bounds are already full. The page then renders into a 1-pixel corner (the
    /// window looks black). Forcing a re-arrange once the adapter is up snaps the native window to
    /// full size. No-op on Windows/macOS (their backends size correctly), so it's gated to Linux.
    /// </summary>
    private void SyncNativeWebViewSize()
    {
        if (!OperatingSystem.IsLinux()) return;
        void Rearrange()
        {
            _web.InvalidateMeasure();
            _web.InvalidateArrange();
        }
        // Post so the re-arrange runs after the current layout pass; a couple of staggered retries
        // absorb the race between async adapter/native-window creation and the arrange we trigger.
        Dispatcher.UIThread.Post(Rearrange, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(Rearrange, TimeSpan.FromMilliseconds(300));
        DispatcherTimer.RunOnce(Rearrange, TimeSpan.FromMilliseconds(1000));
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
            _splash.Child = SplashText($"AgOpenWeb failed to start.\n\n{ex.Message}\n\nDetails: {log}");
            return;
        }

        var port = _backend.Server?.Port ?? 5174;
        _web.Source = new Uri($"http://localhost:{port}/");
    }

    private static Control SplashText(string message) => new TextBlock
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
