// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input.Platform; // ClipboardExtensions.SetTextAsync
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AgValoniaGPS.Desktop.Launcher;

/// <summary>
/// The in-process Windows launcher — a small app-like window that starts/stops the headless
/// guidance backend (<see cref="BackendHost"/>) and opens the web UI in the default browser.
/// The backend runs IN THIS PROCESS on its own host-loop thread; this window is just the
/// supervisor/console for the Windows-centric AgOpen audience, who expect a program with
/// buttons, not a systemd daemon. Naturally cross-platform (Avalonia), but Windows is the
/// reason it exists; Linux/macOS use the systemd unit instead.
/// </summary>
internal sealed class LauncherWindow : Window
{
    private readonly string[] _args;
    private BackendHost? _backend;
    private bool _busy;
    private bool _closing;

    // Controls updated by UpdateUi().
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _urlText = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
    private readonly TextBlock _clientsText = new() { Foreground = new SolidColorBrush(Color.Parse("#9fb3cc")), FontSize = 12 };
    private readonly Button _startStop = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Height = 38 };
    private readonly Button _open = new() { Content = "Open in Browser", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Height = 38 };
    private readonly Button _copy = new() { Content = "Copy", Padding = new Thickness(10, 4), MinWidth = 0 };
    private readonly CheckBox _autoOpen = new() { Content = "Open browser when started", IsChecked = true };
    private readonly CheckBox _runOnLogin = new() { Content = "Start with Windows" };

    public LauncherWindow(string[] args)
    {
        _args = args;

        Title = "AgOpenWeb";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;
        TryLoadIcon();

        _urlText.Text = LanUrl();
        _runOnLogin.IsVisible = OperatingSystem.IsWindows();
        _runOnLogin.IsChecked = RunOnLoginEnabled();

        _startStop.Click += (_, _) => { if (_backend is { IsRunning: true }) _ = StopAsync(); else _ = StartAsync(); };
        _open.Click += (_, _) => OpenUrl(LanUrl());
        _copy.Click += async (_, _) => { var cb = TopLevel.GetTopLevel(this)?.Clipboard; if (cb != null) await cb.SetTextAsync(LanUrl()); };
        _runOnLogin.IsCheckedChanged += (_, _) => SetRunOnLogin(_runOnLogin.IsChecked == true);

        Content = BuildLayout();
        UpdateUi();

        // Poll the live client count / status while running (cheap; the window is tiny).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (_backend is { IsRunning: true }) UpdateUi(); };
        timer.Start();
    }

    private Control BuildLayout()
    {
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = "AgOpenWeb", FontSize = 22, FontWeight = FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = "Guidance host", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9fb3cc")) });

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        statusRow.Children.Add(_dot);
        statusRow.Children.Add(_statusText);

        var urlRow = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var urlBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1b2735")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = _urlText,
        };
        Grid.SetColumn(urlBox, 0);
        Grid.SetColumn(_copy, 1);
        _copy.Margin = new Thickness(8, 0, 0, 0);
        urlRow.Children.Add(urlBox);
        urlRow.Children.Add(_copy);

        _clientsText.Margin = new Thickness(0, 6, 0, 0);

        _startStop.Classes.Add("accent");
        var buttons = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_startStop);
        buttons.Children.Add(_open);

        var options = new StackPanel { Spacing = 6, Margin = new Thickness(0, 16, 0, 0) };
        options.Children.Add(_autoOpen);
        options.Children.Add(_runOnLogin);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(header);
        root.Children.Add(statusRow);
        root.Children.Add(urlRow);
        root.Children.Add(_clientsText);
        root.Children.Add(buttons);
        root.Children.Add(options);
        return root;
    }

    // ---- lifecycle ----

    private async Task StartAsync()
    {
        if (_busy || _backend is { IsRunning: true }) return;
        _busy = true;
        _statusText.Text = "Starting…";
        UpdateUi();
        string? error = null;
        var backend = new BackendHost();
        try { await Task.Run(() => backend.StartAsync(_args)); _backend = backend; }
        catch (Exception ex)
        {
            error = ex.Message;
            // Full stack to a temp log so a Start failure on a user's box is diagnosable
            // (a WinExe has no console to print to).
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agopenweb-launcher-error.log"), ex.ToString()); } catch { }
            try { await backend.StopAsync(); } catch { /* best effort */ }
        }
        _busy = false;
        UpdateUi();
        if (error != null) _statusText.Text = "Failed: " + error;
        else if (_autoOpen.IsChecked == true) OpenUrl(LanUrl());
    }

    private async Task StopAsync()
    {
        if (_busy || _backend is not { IsRunning: true }) return;
        _busy = true;
        _statusText.Text = "Stopping…";
        UpdateUi();
        try { await _backend.StopAsync(); } catch { /* best effort — UI still returns to Stopped */ }
        _backend = null;
        _busy = false;
        UpdateUi();
    }

    // Closing the window must stop the backend first so its ApplicationStopping save runs
    // (config + state). Cancel the close, stop async, then close for real.
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
        try { if (_backend != null) await _backend.StopAsync(); } catch { /* close regardless */ }
        Close();
    }

    private void UpdateUi()
    {
        bool running = _backend is { IsRunning: true };
        if (!_busy)
        {
            _dot.Fill = new SolidColorBrush(running ? Color.Parse("#39FF6A") : Color.Parse("#ff5a5a"));
            _statusText.Text = running ? "Running" : "Stopped";
        }
        else
        {
            _dot.Fill = new SolidColorBrush(Color.Parse("#e0b341"));
        }
        _startStop.Content = running ? "Stop" : "Start";
        _startStop.IsEnabled = !_busy;
        _open.IsEnabled = running;
        _copy.IsEnabled = running;
        _urlText.Text = LanUrl();
        int clients = _backend?.Server?.ClientCount ?? 0;
        _clientsText.Text = running ? $"{clients} browser{(clients == 1 ? "" : "s")} connected" : "";
        _clientsText.IsVisible = running;
    }

    // ---- helpers ----

    private string LanUrl()
    {
        int port = _backend?.Server?.Port ?? 5174;
        return $"http://{LanIp()}:{port}";
    }

    private static string LanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
            }
        }
        catch { /* fall through */ }
        return "localhost";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* no browser available — the URL is shown for manual entry */ }
    }

    // Run-on-login via reg.exe (Windows only) — package-free; the Run key launches the
    // launcher on user sign-in. Guarded so it is never touched off Windows.
    private static readonly string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    private static void SetRunOnLogin(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = on
                ? new ProcessStartInfo("reg", $"add \"{RunKey}\" /v AgOpenWeb /t REG_SZ /d \"\\\"{exe}\\\" --launcher\" /f")
                : new ProcessStartInfo("reg", $"delete \"{RunKey}\" /v AgOpenWeb /f");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { /* a Run-key write failure must not crash the launcher */ }
    }

    private static bool RunOnLoginEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("reg", $"query \"{RunKey}\" /v AgOpenWeb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void TryLoadIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://AgValoniaGPS.Desktop/Assets/avalonia-logo.ico"));
            Icon = new WindowIcon(s);
        }
        catch { /* icon is cosmetic */ }
    }
}
