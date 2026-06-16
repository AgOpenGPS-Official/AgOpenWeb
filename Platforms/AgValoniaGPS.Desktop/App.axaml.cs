// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AgValoniaGPS.Desktop.Views;
using AgValoniaGPS.Desktop.DependencyInjection;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgValoniaGPS.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private AgValoniaGPS.RemoteServer.RemoteServerHost? _remoteServer;

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
        AgValoniaGPS.Views.Diagnostics.DiagFlagsBootstrap.ApplyAtStartup(this);

        // Build DI container
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddAgValoniaServices();
                ConfigureTestServices?.Invoke(services);
            })
            .Build();

        Services = _host.Services;

        // Provide DI to chart panels for auto-configuration
        AgValoniaGPS.Views.Controls.Panels.SteerChartPanel.ServiceProvider = Services;
        AgValoniaGPS.Views.Controls.Panels.HeadingChartPanel.ServiceProvider = Services;
        AgValoniaGPS.Views.Controls.Panels.XTEChartPanel.ServiceProvider = Services;

        // Wire up cross-referencing services (AutoSteer → UDP)
        Services.WireUpServices();

        // Phase 1 web UI (Plans/REMOTE_WEB_UI_SPLIT.md): embedded SignalR map
        // server running ALONGSIDE the app, fed by the live ApplicationState.
        // Browse to http://localhost:5174 (or the LAN IP) to see the live map.
        _remoteServer = new AgValoniaGPS.RemoteServer.RemoteServerHost();
        _ = _remoteServer.StartAsync(
            Services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>(),
            Services.GetRequiredService<ICoverageMapService>(),
            Services.GetRequiredService<ISectionControlService>(),
            Services.GetRequiredService<IToolPositionService>(),
            Services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>(),
            Services.GetRequiredService<IJobService>());

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
                AgValoniaGPS.Views.Localization.TranslationSource.Instance.CurrentCulture =
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
            if (mainWindow.DataContext is AgValoniaGPS.ViewModels.MainViewModel windowVm)
            {
                windowVm.LanguageChanged += code =>
                {
                    try
                    {
                        AgValoniaGPS.Views.Localization.TranslationSource.Instance.CurrentCulture =
                            new System.Globalization.CultureInfo(code);
                    }
                    catch { }
                };

                // Remote/web UI client→host commands (REMOTE_WEB_UI_SPLIT §5). The
                // hub calls this off-thread; marshal to the UI thread, map known ids,
                // ignore the rest. Tier-2 (live actuation) ids only arrive when the
                // sending client holds fresh control authority (the hub gates them).
                if (_remoteServer is not null)
                {
                    _remoteServer.CommandHandler = (cmd, arg) =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            var num = System.Globalization.NumberStyles.Float;
                            // Sim ids that set a property or carry an arg (all Tier-1,
                            // hardware-safe) are handled directly; the rest map to a VM
                            // command below.
                            switch (cmd)
                            {
                                case "sim.toggleEnable":
                                    windowVm.IsSimulatorEnabled = !windowVm.IsSimulatorEnabled; return;
                                case "sim.toggle10x":
                                    windowVm.IsSimulatorSpeed10x = !windowVm.IsSimulatorSpeed10x; return;
                                case "sim.setSteer":
                                    if (double.TryParse(arg, num, inv, out var deg))
                                        windowVm.SimulatorSteerAngle = deg;
                                    return;
                                case "sim.setCoords":
                                    var parts = arg.Split(',');
                                    if (parts.Length == 2
                                        && double.TryParse(parts[0], num, inv, out var lat)
                                        && double.TryParse(parts[1], num, inv, out var lon))
                                        windowVm.SetSimulatorCoordinates(lat, lon);
                                    return;
                            }

                            System.Windows.Input.ICommand? c = cmd switch
                            {
                                "sim.steerLeft" => windowVm.SimulatorSteerLeftCommand,
                                "sim.steerRight" => windowVm.SimulatorSteerRightCommand,
                                "sim.speedUp" => windowVm.SimulatorSpeedUpCommand,
                                "sim.speedDown" => windowVm.SimulatorSpeedDownCommand,
                                "sim.stop" => windowVm.SimulatorStopCommand,
                                "sim.reset" => windowVm.ResetSimulatorCommand,
                                "sim.steerReset" => windowVm.ResetSteerAngleCommand,
                                "sim.reverseDir" => windowVm.SimulatorReverseDirectionCommand,
                                // Right-nav operational toolbar (Tier-2).
                                "contour.toggle" => windowVm.ToggleContourModeCommand,
                                "section.master" => windowVm.ToggleSectionMasterCommand,
                                "section.manual" => windowVm.ToggleManualModeCommand,
                                "youturn.toggle" => windowVm.ToggleYouTurnCommand,
                                "youturn.direction" => windowVm.ToggleUTurnDirectionCommand,
                                "youturn.manualLeft" => windowVm.ManualYouTurnLeftCommand,
                                "youturn.manualRight" => windowVm.ManualYouTurnRightCommand,
                                "autosteer.toggle" => windowVm.ToggleAutoSteerCommand,
                                _ => null, // unknown id → ignored (safety boundary)
                            };
                            if (c?.CanExecute(null) == true) c.Execute(null);
                        });

                    // Tier-2 (live actuation) ids — honored only while a client holds
                    // fresh control authority (Phase 2 safety gate).
                    _remoteServer.IsRestrictedCommand = id =>
                        id.StartsWith("section.") || id.StartsWith("autosteer.")
                        || id.StartsWith("youturn.") || id.StartsWith("contour.");

                    // One operator, via the browser. When the control session ends —
                    // release, disconnect, or deadman — the machine must not keep
                    // actuating with no interface: disengage autosteer and turn sections
                    // off. (Headless target → control state lives in the browser.)
                    _remoteServer.AuthorityChangedHandler = (held, name) =>
                    {
                        if (held)
                        {
                            System.Diagnostics.Debug.WriteLine($"[remote] control taken by {name}");
                            return;
                        }
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (windowVm.IsAutoSteerEngaged
                                && windowVm.ToggleAutoSteerCommand?.CanExecute(null) == true)
                                windowVm.ToggleAutoSteerCommand.Execute(null);
                            if (windowVm.IsManualSectionMode
                                && windowVm.ToggleManualModeCommand?.CanExecute(null) == true)
                                windowVm.ToggleManualModeCommand.Execute(null);
                            if (windowVm.IsSectionMasterOn
                                && windowVm.ToggleSectionMasterCommand?.CanExecute(null) == true)
                                windowVm.ToggleSectionMasterCommand.Execute(null);
                        });
                    };

                    // Involuntary loss (disconnect / deadman) — log the reason; the
                    // disengage runs in AuthorityChangedHandler (covers release too).
                    _remoteServer.FailsafeHandler = reason =>
                        System.Diagnostics.Debug.WriteLine($"[remote] actuation failsafe: {reason}");
                }
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
            var audioService = services.GetService<IAudioService>() as AgValoniaGPS.Services.Audio.AudioServiceBase;
            if (audioService == null) return;

            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgValoniaGPS", "Sounds");
            System.IO.Directory.CreateDirectory(tempDir);

            var soundFiles = AgValoniaGPS.Services.Audio.AudioServiceBase.GetSoundFileNames();

            foreach (var fileName in soundFiles)
            {
                var destPath = System.IO.Path.Combine(tempDir, fileName);
                if (System.IO.File.Exists(destPath)) continue;

                try
                {
                    var uri = new Uri($"avares://AgValoniaGPS.Views/Assets/Sounds/{fileName}");
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