// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading.Tasks;
using AgValoniaGPS.Desktop.DependencyInjection;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgValoniaGPS.Desktop;

/// <summary>
/// Phase 10 headless host. Boots the app as a plain .NET process — NO Avalonia
/// Application, window, or windowing platform — so it can run on a display-less
/// server/daemon (systemd / Windows Service) with the browser as the only UI.
///
/// It reproduces the non-UI half of <see cref="App.OnFrameworkInitializationCompleted"/>:
/// build DI, load settings/config/persistent state, construct the single
/// <c>MainViewModel</c> (which starts the 100 Hz control loop + the render-pull /
/// status timers), start the RemoteServer, and wire its command handler + projectors
/// to the VM via the shared <see cref="App.WireRemoteServer"/>. The UI-thread role is
/// played by a single-thread <see cref="HostLoopDispatcher"/> (registered as both
/// <see cref="IUiDispatcher"/> and <see cref="IUiTimerFactory"/>), so render-pull,
/// status, and remote commands serialize on one thread exactly as they do on the
/// Avalonia UI thread in the windowed build.
///
/// Selected by the absence of <c>--windowed</c> (see Program.Main).
/// See Plans/WEBUI_MIGRATION_PLAN.md Phase 10 and Plans/WEBUI_SESSION_HANDOFF.md.
/// </summary>
internal static class HeadlessHost
{
    public static async Task RunAsync(string[] args)
    {
        Console.WriteLine("[headless] AgOpenWeb host starting (no window; browser is the UI)…");

        // The single host-loop thread that stands in for the Avalonia UI thread.
        var hostLoop = new HostLoopDispatcher();

        var host = Host.CreateDefaultBuilder(args)
            // When launched as a systemd Type=notify service: swap in SystemdLifetime
            // (handles SIGTERM + sends READY=1 once started), and format logs for
            // journald. A no-op when not run under systemd, so dotnet run still uses
            // ConsoleLifetime (Ctrl-C). See Plans/DEPLOYMENT_PATTERNS.md.
            .UseSystemd()
            .ConfigureServices(services =>
            {
                services.AddAgValoniaServices();

                // Headless overrides (last registration wins on resolve). The
                // Avalonia-backed dispatcher/timer and the SkiaMapControl-backed
                // MapService registered by AddAgValoniaServices are never resolved.
                services.AddSingleton(hostLoop);
                services.AddSingleton<IUiDispatcher>(hostLoop);
                services.AddSingleton<IUiTimerFactory>(hostLoop);
                services.AddSingleton<IMapService, NullMapService>();

                // Pets the systemd hardware watchdog (WatchdogSec) via sd_notify;
                // no-op when not under a watchdog-enabled systemd service.
                services.AddHostedService<SystemdWatchdogService>();
            })
            .Build();

        App.Services = host.Services;
        var services2 = host.Services;
        services2.WireUpServices();

        // Load persisted settings → ConfigurationStore, then persistent app state.
        // (Mirrors App.OnFrameworkInitializationCompleted; no sound extraction /
        // font / language-to-Avalonia steps — those are UI-only.)
        var settingsService = services2.GetRequiredService<ISettingsService>();
        settingsService.Load();
        var configService = services2.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        // Resolve into a local now so the shutdown save below never calls
        // GetRequiredService on a torn-down provider.
        var persistentState = services2.GetRequiredService<IPersistentStateService>();
        persistentState.Load();

        // Construct the single MainViewModel from DI. This starts the control loop
        // and the render-pull / status timers (the timers fire on the host loop).
        var vm = services2.GetRequiredService<AgValoniaGPS.ViewModels.MainViewModel>();

        // Start the embedded browser server, then wire its command handler +
        // projectors to the live VM (shared with the windowed path).
        var remoteServer = new AgValoniaGPS.RemoteServer.RemoteServerHost();
        await remoteServer.StartAsync(
            services2.GetRequiredService<ApplicationState>(),
            services2.GetRequiredService<ICoverageMapService>(),
            services2.GetRequiredService<ISectionControlService>(),
            services2.GetRequiredService<IToolPositionService>(),
            services2.GetRequiredService<ConfigurationStore>(),
            services2.GetRequiredService<IJobService>(),
            services2.GetRequiredService<IConfigurationService>(),
            services2.GetRequiredService<IAutoSteerService>(),
            services2.GetRequiredService<ISmartWasCalibrationService>(),
            services2.GetRequiredService<IUdpCommunicationService>(),
            services2.GetRequiredService<INtripProfileService>(),
            services2.GetRequiredService<AgValoniaGPS.Services.IFieldService>(),
            services2.GetRequiredService<ISettingsService>(),
            services2.GetRequiredService<IVehicleProfileService>(),
            services2.GetRequiredService<IPersistentStateService>());

        // Wire on the host loop so the command handler runs serialized with the
        // render-pull / status timers, exactly like the Avalonia UI thread does.
        hostLoop.Post(() => App.WireRemoteServer(remoteServer, vm, services2, configService));

        Console.WriteLine("[headless] ready — browse to http://localhost:5174 (or the LAN IP).");

        // Persist config + state and stop the embedded server during the host's
        // ApplicationStopping phase, which fires WHILE the service provider is still
        // alive. host.RunAsync() disposes the provider in its finally, so doing this
        // AFTER RunAsync returns would hit a disposed IServiceProvider (the state save
        // failed that way). ApplicationStopping callbacks run synchronously when a
        // signal triggers StopApplication, before any disposal; this one is registered
        // first, so it completes before the host tears down. Uses captured locals only.
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine("[headless] shutting down — saving config + state…");
            try { configService.SaveAppSettings(); } catch (Exception ex) { Console.WriteLine($"[headless] save config failed: {ex.Message}"); }
            try { persistentState.Save(); } catch (Exception ex) { Console.WriteLine($"[headless] save state failed: {ex.Message}"); }
            try { remoteServer.StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine($"[headless] server stop failed: {ex.Message}"); }
            Console.WriteLine("[headless] stopped.");
        });

        // Block until SIGINT (Ctrl-C) / SIGTERM (systemd stop) / SIGQUIT, handled by
        // the generic host's ConsoleLifetime — Microsoft's tested cross-platform impl.
        await host.RunAsync();
        hostLoop.Dispose();
    }
}
