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
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AgOpenWeb.iOS.Views;
using AgOpenWeb.iOS.DependencyInjection;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.iOS;

public partial class App : Avalonia.Application
{
    private IServiceProvider? _services;

    public static IServiceProvider? Services { get; private set; }
    public static MainView? MainView { get; private set; }

    public override void Initialize()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[App] Initialize starting...");
            AvaloniaXamlLoader.Load(this);
            System.Diagnostics.Debug.WriteLine("[App] Initialize completed.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Initialize FAILED: {ex}");
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[App] OnFrameworkInitializationCompleted starting...");

            AgOpenWeb.Views.Diagnostics.DiagFlagsBootstrap.ApplyAtStartup(this);

            // Build DI container
            System.Diagnostics.Debug.WriteLine("[App] Building DI container...");
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddAgOpenWebServices();
            _services = serviceCollection.BuildServiceProvider();
            Services = _services;
            System.Diagnostics.Debug.WriteLine("[App] DI container built.");

            // Wire up cross-referencing services (AutoSteer → UDP)
            Services.WireUpServices();
            System.Diagnostics.Debug.WriteLine("[App] Services wired up.");

            // Load settings and sync to ConfigurationStore
            System.Diagnostics.Debug.WriteLine("[App] Loading settings...");
            var settingsService = Services.GetRequiredService<ISettingsService>();
            settingsService.Load();
            try
            {
                var configService = Services.GetRequiredService<IConfigurationService>();
                configService.LoadAppSettings();
                Services.GetRequiredService<IPersistentStateService>().Load();
                System.Diagnostics.Debug.WriteLine("[App] Settings loaded and synced to ConfigurationStore.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Error syncing settings: {ex.Message}");
                Console.WriteLine($"[App] Error syncing settings: {ex}");
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

            if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
            {
                System.Diagnostics.Debug.WriteLine("[App] Creating MainView with ViewModel...");

                // Get MainViewModel and services from DI and create view with them
                var viewModel = Services.GetRequiredService<MainViewModel>();
                System.Diagnostics.Debug.WriteLine("[App] MainViewModel created from DI.");

                var mapService = Services.GetRequiredService<AgOpenWeb.Services.Interfaces.IMapService>();
                var concreteMapService = mapService as AgOpenWeb.iOS.Services.MapService;
                System.Diagnostics.Debug.WriteLine($"[App] MapService retrieved from DI: {concreteMapService != null}");

                var coverageService = Services.GetRequiredService<AgOpenWeb.Services.Interfaces.ICoverageMapService>();
                System.Diagnostics.Debug.WriteLine("[App] CoverageMapService retrieved from DI.");

                var mainView = new MainView(viewModel, concreteMapService!, coverageService);
                singleViewPlatform.MainView = mainView;
                MainView = mainView;
                System.Diagnostics.Debug.WriteLine("[App] MainView created and assigned.");

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
            }

            base.OnFrameworkInitializationCompleted();
            System.Diagnostics.Debug.WriteLine("[App] OnFrameworkInitializationCompleted finished.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] OnFrameworkInitializationCompleted FAILED: {ex}");
            throw;
        }
    }

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
