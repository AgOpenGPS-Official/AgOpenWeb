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
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using AgOpenWeb.Android.DependencyInjection;
using AgOpenWeb.Android.Views;
using AgOpenWeb.Android.Services;
using AgOpenWeb.ViewModels;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Android;

public partial class App : Avalonia.Application
{
    private IServiceProvider? _serviceProvider;

    // internal set: in all-in-one launcher mode the foreground BackendService's AndroidBackendHost
    // owns the DI provider and publishes it here so incidental App.Services lookups still resolve.
    public static IServiceProvider? Services { get; internal set; }
    public static MainView? MainView { get; private set; }

    public override void Initialize()
    {
        Console.WriteLine("[App] Initializing...");
        AvaloniaXamlLoader.Load(this);
        Console.WriteLine("[App] XAML loaded.");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("[App] Framework initialization starting...");

        AgOpenWeb.Views.Diagnostics.DiagFlagsBootstrap.ApplyAtStartup(this);

        if (AgOpenWeb.Models.Diagnostics.DiagFlags.WebViewLauncher)
        {
            // ALL-IN-ONE LAUNCHER: no native UI and NO app-side DI here. The foreground
            // BackendService (started by MainActivity) owns the in-process host on its own
            // host-loop thread; this Activity is just a full-screen WebView showing the local
            // web app once the host is bound. Marker: Documents/AgOpenWeb/.use_webview_launcher.
            Console.WriteLine("[App] WebView launcher mode (no native UI).");
            if (ApplicationLifetime is IActivityApplicationLifetime launcherActivity)
                launcherActivity.MainViewFactory = BuildWebViewLauncherView;
            else if (ApplicationLifetime is ISingleViewApplicationLifetime launcherSingleView)
                launcherSingleView.MainView = BuildWebViewLauncherView();

            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Set up dependency injection
        var services = new ServiceCollection();
        services.AddAgOpenWebServices();
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Wire up services that need cross-references
        _serviceProvider.WireUpServices();

        Console.WriteLine("[App] Services configured.");

        // Load settings and sync to ConfigurationStore
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();
        try
        {
            var configService = Services.GetRequiredService<IConfigurationService>();
            configService.LoadAppSettings();
            Services.GetRequiredService<IPersistentStateService>().Load();
            Console.WriteLine("[App] Settings loaded and synced to ConfigurationStore.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Error syncing settings: {ex.Message}");
        }

        // Extract sound files from Avalonia resources
        ExtractSoundFiles(Services);

        // Apply saved language (#40)
        var savedLang = settingsService.Settings.Language;
        if (!string.IsNullOrEmpty(savedLang) && savedLang != "en")
        {
            try
            {
                AgOpenWeb.Views.Localization.TranslationSource.Instance.CurrentCulture =
                    new System.Globalization.CultureInfo(savedLang);
            }
            catch { /* fall back to English */ }
        }

        // Create ViewModel on the UI thread before the factory lambda
        Console.WriteLine("[App] Getting MainViewModel...");
        var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        Console.WriteLine("[App] Getting MapService...");
        var mapService = (MapService)_serviceProvider.GetRequiredService<IMapService>();
        Console.WriteLine("[App] Getting CoverageMapService...");
        var coverageService = _serviceProvider.GetRequiredService<ICoverageMapService>();

        // Wire language change to TranslationSource (#40)
        viewModel.LanguageChanged += code =>
        {
            try
            {
                AgOpenWeb.Views.Localization.TranslationSource.Instance.CurrentCulture =
                    new System.Globalization.CultureInfo(code);
            }
            catch { }
        };

        // Provide DI to chart panels for auto-configuration
        AgOpenWeb.Views.Controls.Panels.SteerChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.HeadingChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.XTEChartPanel.ServiceProvider = Services;

        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            Console.WriteLine("[App] Using IActivityApplicationLifetime with MainViewFactory...");
            activityLifetime.MainViewFactory = () =>
            {
                Console.WriteLine("[App] MainViewFactory creating MainView...");
                var mainView = new MainView(viewModel, mapService, coverageService);
                MainView = mainView;
                Console.WriteLine("[App] MainView created and assigned.");
                return mainView;
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewLifetime)
        {
            Console.WriteLine("[App] Fallback: Using ISingleViewApplicationLifetime...");
            var mainView = new MainView(viewModel, mapService, coverageService);
            singleViewLifetime.MainView = mainView;
            MainView = mainView;
            Console.WriteLine("[App] MainView created and assigned.");
        }

        base.OnFrameworkInitializationCompleted();
        Console.WriteLine("[App] Framework initialization completed.");
    }

    // The launcher view: a full-screen NativeWebView with a splash on top until the page loads.
    // The in-process host binds :5174 from the foreground BackendService, which may still be
    // starting (cold start) or restarting (the Activity can outlive a stopped host, leaving the
    // static HostReady signal stale). So we navigate as soon as the host SIGNALS ready (bounded
    // wait, so a stale/pending signal can't block us), then RETRY on any load failure until the
    // page actually comes up — that bridges the startup gap instead of leaving a dead
    // "Webpage not available" on the first miss.
    private const int LauncherPort = 5174;

    private static Control BuildWebViewLauncherView()
    {
        var web = new Avalonia.Controls.NativeWebView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var splash = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0b1020")),
            Child = new TextBlock
            {
                Text = "Starting AgOpenWeb…",
                Foreground = Brushes.White,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var uri = new Uri($"http://localhost:{LauncherPort}/");
        int attempts = 0;
        const int maxAttempts = 60; // ~45 s of retries at 750 ms — covers a slow cold start

        void TryNavigate()
        {
            attempts++;
            Console.WriteLine($"[App] webview navigate attempt {attempts} → {uri}");
            try { web.Navigate(uri); } catch (Exception ex) { Console.WriteLine($"[App] navigate threw: {ex.Message}"); }
        }

        web.NavigationCompleted += (_, e) =>
        {
            Console.WriteLine($"[App] webview completed IsSuccess={e.IsSuccess} attempt={attempts}");
            if (e.IsSuccess) { splash.IsVisible = false; return; }
            // Failed (host not listening yet / transient). Retry until it loads.
            if (attempts < maxAttempts)
                DelayThenPost(750, TryNavigate);
        };

        _ = StartNavigationAsync(TryNavigate);
        return new Grid { Children = { web, splash } };
    }

    // Navigate as soon as the host signals ready, but wait no longer than a few seconds for that
    // signal — the retry-on-failure loop covers any remaining gap (and the case where HostReady is
    // a stale leftover from a previous run in the same process).
    private static async Task StartNavigationAsync(Action tryNavigate)
    {
        try { await Task.WhenAny(BackendService.HostReady.Task, Task.Delay(8000)).ConfigureAwait(false); }
        catch { /* host start may have faulted; try anyway — the server can still come up */ }
        Dispatcher.UIThread.Post(tryNavigate);
    }

    private static void DelayThenPost(int milliseconds, Action action) =>
        _ = Task.Delay(milliseconds).ContinueWith(
            _ => Dispatcher.UIThread.Post(action), TaskScheduler.Default);

    private static void ExtractSoundFiles(IServiceProvider services)
    {
        try
        {
            var audioService = services.GetService<IAudioService>() as AgOpenWeb.Services.Audio.AudioServiceBase;
            if (audioService == null) return;

            var cacheDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sounds");
            System.IO.Directory.CreateDirectory(cacheDir);

            var soundFiles = AgOpenWeb.Services.Audio.AudioServiceBase.GetSoundFileNames();

            foreach (var fileName in soundFiles)
            {
                var destPath = System.IO.Path.Combine(cacheDir, fileName);
                if (System.IO.File.Exists(destPath)) continue;

                try
                {
                    var uri = new Uri($"avares://AgOpenWeb.Views/Assets/Sounds/{fileName}");
                    using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                    using var fileStream = System.IO.File.Create(destPath);
                    stream.CopyTo(fileStream);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Audio] Failed to extract {fileName}: {ex.Message}");
                }
            }

            audioService.SetSoundDirectory(cacheDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Sound extraction failed: {ex.Message}");
        }
    }
}
