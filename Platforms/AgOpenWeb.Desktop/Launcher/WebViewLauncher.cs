// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
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
    private BackendHost? _backend;
    private bool _closing;

    public WebViewLauncher(PhotinoWindow window, string[] args)
    {
        _window = window;
        _args = args;
    }

    public void Show()
    {
        _window
            .SetMaximized(true)
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .RegisterWindowClosingHandler((_, _) => LauncherEntry.StopThenClose(_window, _backend, ref _closing))
            .RegisterWindowCreatedHandler((_, _) => _ = StartAsync())
            .LoadRawString(LauncherEntry.SplashHtml("Starting AgOpenWeb…"));
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
            if (fullscreen) _window.SetFullScreen(true);
            _window.Load(new Uri($"http://localhost:{port}/"));
            ScreenAwake.Hold();
        });
        Console.WriteLine($"[launcher] Backend up; window -> http://localhost:{port}/");
    }

    // The page talks to its host through window.external.sendMessage (Photino's channel):
    //   open|<url>   open a link in the system browser (window.open can't leave the web view)
    private void OnWebMessage(object? sender, string message)
    {
        if (message.StartsWith("open|", StringComparison.Ordinal))
        {
            var url = message[5..];
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                LauncherEntry.OpenUrl(url);
        }
    }
}
