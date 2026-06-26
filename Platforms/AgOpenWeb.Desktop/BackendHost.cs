// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading.Tasks;
using AgOpenWeb.Desktop.DependencyInjection;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgOpenWeb.Desktop;

/// <summary>
/// The display-less guidance backend — DI + control loops + the embedded browser server —
/// factored out of <see cref="HeadlessHost"/> so BOTH the systemd/console daemon
/// (HeadlessHost) and the in-process Windows launcher (<see cref="Launcher.LauncherWindow"/>)
/// drive the same lifecycle. There is exactly one of these alive at a time.
///
/// Reproduces the non-UI half of <see cref="App.OnFrameworkInitializationCompleted"/>: build
/// DI (UI-thread role played by a single <see cref="HostLoopDispatcher"/>), load
/// settings/config/state, construct the single <c>MainViewModel</c> (which starts the 100 Hz
/// control loop + render-pull/status timers on the host loop), start the RemoteServer, and
/// wire its command handler + projectors to the VM via <see cref="App.WireRemoteServer"/>.
///
/// The daemon awaits <see cref="WaitForShutdownAsync"/> (SIGTERM/Ctrl-C via the generic host
/// lifetime); the launcher calls <see cref="StopAsync"/> from a button. Both routes fire the
/// ApplicationStopping save (config + state) BEFORE the provider is disposed.
/// </summary>
internal sealed class BackendHost
{
    private HostLoopDispatcher? _hostLoop;
    private IHost? _host;
    private AgOpenWeb.RemoteServer.RemoteServerHost? _remoteServer;

    /// <summary>True once <see cref="StartAsync"/> has completed and before <see cref="StopAsync"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The embedded server (status readouts: <c>Port</c>, <c>ClientCount</c>). Null until started.</summary>
    public AgOpenWeb.RemoteServer.RemoteServerHost? Server => _remoteServer;

    /// <summary>Build DI, load persisted state, construct the VM, start + wire the RemoteServer,
    /// then start the generic host. Returns once the backend is live and serving.</summary>
    public async Task StartAsync(string[] args)
    {
        if (IsRunning) return;

        // The single host-loop thread that stands in for the Avalonia UI thread.
        var hostLoop = new HostLoopDispatcher();

        var host = Host.CreateDefaultBuilder(args)
            // Under a systemd Type=notify service: SystemdLifetime (SIGTERM + READY=1) +
            // journald log format. No-op off systemd (so the console daemon keeps
            // ConsoleLifetime, and the Windows launcher just gets the default lifetime,
            // which it never waits on — it drives stop from a button).
            .UseSystemd()
            // Windows counterpart: when launched by the Service Control Manager (the
            // deploy/windows install-service.ps1 registers the exe with --headless),
            // WindowsServiceLifetime handles SCM start/stop + logs to the Event Log.
            // No-op when not run as a service (console daemon / launcher / macOS / Linux).
            .UseWindowsService()
            .ConfigureServices(services =>
            {
                services.AddAgOpenWebServices();

                // Headless overrides (last registration wins). The Avalonia-backed
                // dispatcher/timer and SkiaMapControl MapService from AddAgOpenWebServices
                // are never resolved on this path.
                services.AddSingleton(hostLoop);
                services.AddSingleton<IUiDispatcher>(hostLoop);
                services.AddSingleton<IUiTimerFactory>(hostLoop);
                services.AddSingleton<IMapService, NullMapService>();

                // Pets the systemd hardware watchdog (WatchdogSec) via sd_notify; no-op
                // when not under a watchdog-enabled systemd service.
                services.AddHostedService<SystemdWatchdogService>();
            })
            .Build();

        App.Services = host.Services;
        var sp = host.Services;
        sp.WireUpServices();

        // Load persisted settings → ConfigurationStore, then persistent app state.
        var settingsService = sp.GetRequiredService<ISettingsService>();
        settingsService.Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        var persistentState = sp.GetRequiredService<IPersistentStateService>();
        persistentState.Load();

        // Construct the single MainViewModel from DI (starts the control loop + the
        // render-pull / status timers, which fire on the host loop).
        var vm = sp.GetRequiredService<AgOpenWeb.ViewModels.MainViewModel>();

        // Start the embedded browser server, then wire its command handler + projectors.
        var remoteServer = new AgOpenWeb.RemoteServer.RemoteServerHost();
        await remoteServer.StartAsync(
            sp.GetRequiredService<ApplicationState>(),
            sp.GetRequiredService<ICoverageMapService>(),
            sp.GetRequiredService<ISectionControlService>(),
            sp.GetRequiredService<IToolPositionService>(),
            sp.GetRequiredService<ConfigurationStore>(),
            sp.GetRequiredService<IJobService>(),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<IAutoSteerService>(),
            sp.GetRequiredService<ISmartWasCalibrationService>(),
            sp.GetRequiredService<IUdpCommunicationService>(),
            sp.GetRequiredService<INtripProfileService>(),
            sp.GetRequiredService<AgOpenWeb.Services.IFieldService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IVehicleProfileService>(),
            sp.GetRequiredService<IPersistentStateService>());

        // Wire on the host loop so the command handler runs serialized with the render-pull /
        // status timers, exactly as the Avalonia UI thread does in the windowed build.
        hostLoop.Post(() => AgOpenWeb.RemoteWiring.RemoteServerWiring.Wire(
            remoteServer, vm, sp, configService, new DesktopImageryCapture()));

        // Persist config + state and stop the embedded server during ApplicationStopping,
        // which fires WHILE the provider is still alive (a save after the provider is
        // disposed throws). Registered first so it completes before the host tears down.
        // Uses captured locals only.
        var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine("[backend] shutting down — saving config + state…");
            try { configService.SaveAppSettings(); } catch (Exception ex) { Console.WriteLine($"[backend] save config failed: {ex.Message}"); }
            try { persistentState.Save(); } catch (Exception ex) { Console.WriteLine($"[backend] save state failed: {ex.Message}"); }
            try { remoteServer.StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine($"[backend] server stop failed: {ex.Message}"); }
            Console.WriteLine("[backend] stopped.");
        });

        await host.StartAsync();

        _hostLoop = hostLoop;
        _host = host;
        _remoteServer = remoteServer;
        IsRunning = true;
    }

    /// <summary>Blocks until the generic host lifetime signals shutdown (SIGTERM / Ctrl-C /
    /// systemd stop). Used by the console/systemd daemon; the launcher never calls this.</summary>
    public Task WaitForShutdownAsync() =>
        _host?.WaitForShutdownAsync() ?? Task.CompletedTask;

    /// <summary>Stop the host (fires ApplicationStopping → save) and release everything.
    /// Idempotent. After this the backend can be started again with a fresh instance.</summary>
    public async Task StopAsync()
    {
        if (_host is { } host)
        {
            try { await host.StopAsync(); } catch { /* a stop fault must not wedge the caller */ }
            host.Dispose();
        }
        _hostLoop?.Dispose();
        _host = null;
        _hostLoop = null;
        _remoteServer = null;
        IsRunning = false;
    }
}
