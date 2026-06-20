// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Runtime.InteropServices;
using System.Threading;
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
        services2.GetRequiredService<IPersistentStateService>().Load();

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

        // Run until SIGTERM (systemd stop) / SIGINT (Ctrl+C). Then persist config +
        // state and stop the server cleanly — mirrors the windowed desktop.Exit.
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RequestShutdown(PosixSignalContext ctx)
        {
            // Cancel the default disposition (terminate) so we run graceful
            // shutdown — save config + state and stop the server — before exit.
            ctx.Cancel = true;
            shutdown.TrySetResult();
        }

        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);

        await shutdown.Task;

        Console.WriteLine("[headless] shutting down — saving config + state…");
        try { configService.SaveAppSettings(); } catch (Exception ex) { Console.WriteLine($"[headless] save config failed: {ex.Message}"); }
        try { services2.GetRequiredService<IPersistentStateService>().Save(); } catch (Exception ex) { Console.WriteLine($"[headless] save state failed: {ex.Message}"); }
        try { await remoteServer.StopAsync(); } catch (Exception ex) { Console.WriteLine($"[headless] server stop failed: {ex.Message}"); }
        hostLoop.Dispose();
        host.Dispose();
        Console.WriteLine("[headless] stopped.");
    }
}
