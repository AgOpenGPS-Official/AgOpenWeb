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
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AgOpenWeb.iOS.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.iOS;

/// <summary>
/// The iOS head is an all-in-one thin launcher: it boots the platform-agnostic guidance
/// backend (<see cref="AgOpenWeb.RemoteWiring.WebBackend"/>) in-process and fills the screen
/// with a <see cref="NativeWebView"/> pointed at the local web UI. There is no native UI — the
/// WebView (WKWebView) is the only view. The host also binds 0.0.0.0, so other devices on the
/// LAN can connect too.
/// </summary>
public partial class App : Avalonia.Application
{
    /// <summary>The DI provider — read by <see cref="AppDelegate"/> to save config + state
    /// when the app is backgrounded/terminated.</summary>
    public static IServiceProvider? Services { get; private set; }

    private AgOpenWeb.RemoteWiring.WebBackend? _backend;
    private NativeWebView? _web;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Build DI. The platform extension registers a HostLoopDispatcher as the UI-thread
        // stand-in and NullMapService (the browser/CanvasKit client renders the map).
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddAgOpenWebServices();
        Services = serviceCollection.BuildServiceProvider();

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            _web = new NativeWebView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            singleView.MainView = _web;

            // Start the backend off the UI thread; point the WebView at it once it's bound.
            _ = StartBackendThenNavigateAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartBackendThenNavigateAsync()
    {
        try
        {
            var backend = await Task.Run(() =>
                AgOpenWeb.RemoteWiring.WebBackend.StartAsync(Services!, new IosImageryCapture()));
            _backend = backend;
            var port = backend.Server.Port;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_web is not null) _web.Source = new Uri($"http://localhost:{port}/");
            });
            Console.WriteLine($"[App] Backend up; WebView -> http://localhost:{port}/ (all-in-one).");
        }
        catch (Exception ex)
        {
            // Console (not Debug) so it shows in a Release device console (devicectl --console).
            Console.WriteLine($"[App] Backend start FAILED: {ex}");
        }
    }
}
