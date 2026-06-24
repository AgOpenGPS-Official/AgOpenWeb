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
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AgOpenWeb.Desktop.Views;
using AgOpenWeb.Desktop.DependencyInjection;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgOpenWeb.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private AgOpenWeb.RemoteServer.RemoteServerHost? _remoteServer;


    public static IServiceProvider? Services { get; set; }

    /// <summary>
    /// Hook for integration tests to replace services before DI build.
    /// </summary>
    public static Action<IServiceCollection>? ConfigureTestServices { get; set; }

    /// <summary>
    /// Hook for integration tests to run scenarios after MainWindow is shown.
    /// </summary>
    public static Func<IClassicDesktopStyleApplicationLifetime, Task>? OnAppReady { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AgOpenWeb.Views.Diagnostics.DiagFlagsBootstrap.ApplyAtStartup(this);

        // Build DI container
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddAgOpenWebServices();
                ConfigureTestServices?.Invoke(services);
            })
            .Build();

        Services = _host.Services;

        // Provide DI to chart panels for auto-configuration
        AgOpenWeb.Views.Controls.Panels.SteerChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.HeadingChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.XTEChartPanel.ServiceProvider = Services;

        // Wire up cross-referencing services (AutoSteer → UDP)
        Services.WireUpServices();

        // Phase 1 web UI (Plans/REMOTE_WEB_UI_SPLIT.md): embedded SignalR map
        // server running ALONGSIDE the app, fed by the live ApplicationState.
        // Browse to http://localhost:5174 (or the LAN IP) to see the live map.
        _remoteServer = new AgOpenWeb.RemoteServer.RemoteServerHost();
        _ = _remoteServer.StartAsync(
            Services.GetRequiredService<AgOpenWeb.Models.State.ApplicationState>(),
            Services.GetRequiredService<ICoverageMapService>(),
            Services.GetRequiredService<ISectionControlService>(),
            Services.GetRequiredService<IToolPositionService>(),
            Services.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>(),
            Services.GetRequiredService<IJobService>(),
            Services.GetRequiredService<IConfigurationService>(),
            Services.GetRequiredService<IAutoSteerService>(),
            Services.GetRequiredService<ISmartWasCalibrationService>(),
            Services.GetRequiredService<IUdpCommunicationService>(),
            Services.GetRequiredService<INtripProfileService>(),
            Services.GetRequiredService<AgOpenWeb.Services.IFieldService>(),
            Services.GetRequiredService<ISettingsService>(),
            Services.GetRequiredService<IVehicleProfileService>(),
            Services.GetRequiredService<IPersistentStateService>());

        // Extract sound files from Avalonia resources for cross-platform audio
        ExtractSoundFiles(Services);

        // Load settings and sync to ConfigurationStore
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();
        var configService = Services.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();

        // Load persistent application state (window/last-view/last-field/sim
        // position). Migrates from legacy appsettings.json on first run of this
        // build. Tier-2 store, separate from config.
        Services.GetRequiredService<IPersistentStateService>().Load();

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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Wire language change to TranslationSource (#40)
            // Must use MainWindow's ViewModel (not DI - MainViewModel is Transient)
            if (mainWindow.DataContext is AgOpenWeb.ViewModels.MainViewModel windowVm)
            {
                windowVm.LanguageChanged += code =>
                {
                    try
                    {
                        AgOpenWeb.Views.Localization.TranslationSource.Instance.CurrentCulture =
                            new System.Globalization.CultureInfo(code);
                    }
                    catch { }
                };

                // Remote/web UI client→host commands (REMOTE_WEB_UI_SPLIT §5). The
                // hub calls this off-thread; marshal to the UI thread, map known ids,
                // ignore the rest. Tier-2 (live actuation) ids only arrive when the
                // sending client holds fresh control authority (the hub gates them).
                if (_remoteServer is not null)
                    AgOpenWeb.RemoteWiring.RemoteServerWiring.Wire(
                        _remoteServer, windowVm, Services!, configService, new DesktopImageryCapture());
            }

            desktop.Exit += (sender, args) =>
            {
                // Persist config (store→DTO→disk) and application state on exit.
                // Use SaveAppSettings (not raw settingsService.Save) so the
                // ConfigurationStore is the source of truth for the written file.
                configService.SaveAppSettings();
                Services.GetRequiredService<IPersistentStateService>().Save();
                _ = _remoteServer?.StopAsync();
                _host?.Dispose();
            };

            // Integration test hook: run scenarios after window is ready
            if (OnAppReady != null)
            {
                var callback = OnAppReady;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await Task.Delay(100); // Let window render initial frame
                        await callback(desktop);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IntTest] SCENARIO FAILED: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                    finally
                    {
                        desktop.Shutdown();
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }


    private static void ExtractSoundFiles(IServiceProvider services)
    {
        try
        {
            var audioService = services.GetService<IAudioService>() as AgOpenWeb.Services.Audio.AudioServiceBase;
            if (audioService == null) return;

            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgOpenWeb", "Sounds");
            System.IO.Directory.CreateDirectory(tempDir);

            var soundFiles = AgOpenWeb.Services.Audio.AudioServiceBase.GetSoundFileNames();

            foreach (var fileName in soundFiles)
            {
                var destPath = System.IO.Path.Combine(tempDir, fileName);
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

            audioService.SetSoundDirectory(tempDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Sound extraction failed: {ex.Message}");
        }
    }

}