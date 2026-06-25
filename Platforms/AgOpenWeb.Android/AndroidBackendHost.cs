// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading.Tasks;
using AgOpenWeb.Android.DependencyInjection;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.Android;

/// <summary>
/// The in-process guidance backend for the Android all-in-one launcher — the mobile twin of
/// the Desktop <c>BackendHost</c>. Builds DI, loads persisted state, constructs the single
/// <see cref="MainViewModel"/>, starts the embedded <see cref="AgOpenWeb.RemoteServer.RemoteServerHost"/>
/// and wires its command handler/projectors to the VM. Hosted inside <see cref="BackendService"/>
/// (a foreground service) so it keeps running while the WebView Activity is backgrounded.
///
/// Like the headless daemon — and UNLIKE the iOS all-in-one — the VM's "UI-thread" work
/// (render-pull + status timers + the RemoteServer command handler) runs on a dedicated
/// <see cref="HostLoopDispatcher"/> thread, NOT the Avalonia UI thread. Android pauses the
/// Activity's UI thread when backgrounded; routing this work to the host loop means guidance,
/// the 100 Hz control loop (already its own thread), UDP, and the LAN feed all stay live as
/// long as the foreground-service process is alive. There is exactly one of these at a time.
/// </summary>
internal sealed class AndroidBackendHost
{
    private HostLoopDispatcher? _hostLoop;
    private ServiceProvider? _provider;
    private AgOpenWeb.RemoteServer.RemoteServerHost? _remoteServer;
    private IConfigurationService? _configService;
    private IPersistentStateService? _persistentState;

    public bool IsRunning { get; private set; }

    /// <summary>The bound server port once <see cref="StartAsync"/> completes (default 5174).</summary>
    public int Port => _remoteServer?.Port ?? 5174;

    /// <summary>Build DI on a dedicated host loop, load persisted state, construct the VM, then
    /// start + wire the embedded server. Returns once the backend is live and serving.</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;

        // The single host-loop thread that stands in for the Avalonia UI thread.
        var hostLoop = new HostLoopDispatcher();

        var services = new ServiceCollection();
        services.AddAgOpenWebServices();

        // Headless overrides (last registration wins): the Avalonia-backed dispatcher/timer and
        // the SkiaMapControl-backed MapService from AddAgOpenWebServices are never resolved here.
        services.AddSingleton(hostLoop);
        services.AddSingleton<IUiDispatcher>(hostLoop);
        services.AddSingleton<IUiTimerFactory>(hostLoop);
        services.AddSingleton<IMapService, NullMapService>();

        var provider = services.BuildServiceProvider();
        App.Services = provider;
        provider.WireUpServices();

        // Load persisted settings → ConfigurationStore, then persistent app state.
        var settingsService = provider.GetRequiredService<ISettingsService>();
        settingsService.Load();
        var configService = provider.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        var persistentState = provider.GetRequiredService<IPersistentStateService>();
        persistentState.Load();

        // Construct the single MainViewModel (starts the control loop + the render-pull/status
        // timers, which fire on the host loop). Constructing off the host-loop thread is fine —
        // the timers + dispatch are bound to the host loop regardless of where they were created.
        var vm = provider.GetRequiredService<MainViewModel>();

        // Start the embedded browser server, then wire its command handler + projectors.
        var remoteServer = new AgOpenWeb.RemoteServer.RemoteServerHost();
        await remoteServer.StartAsync(
            provider.GetRequiredService<ApplicationState>(),
            provider.GetRequiredService<ICoverageMapService>(),
            provider.GetRequiredService<ISectionControlService>(),
            provider.GetRequiredService<IToolPositionService>(),
            provider.GetRequiredService<ConfigurationStore>(),
            provider.GetRequiredService<IJobService>(),
            provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<IAutoSteerService>(),
            provider.GetRequiredService<ISmartWasCalibrationService>(),
            provider.GetRequiredService<IUdpCommunicationService>(),
            provider.GetRequiredService<INtripProfileService>(),
            provider.GetRequiredService<AgOpenWeb.Services.IFieldService>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IVehicleProfileService>(),
            provider.GetRequiredService<IPersistentStateService>());

        // Wire on the host loop so the command handler runs serialized with the render-pull /
        // status timers, exactly as the Avalonia UI thread does in the windowed build.
        hostLoop.Post(() => AgOpenWeb.RemoteWiring.RemoteServerWiring.Wire(
            remoteServer, vm, provider, configService, new AndroidImageryCapture()));

        _hostLoop = hostLoop;
        _provider = provider;
        _remoteServer = remoteServer;
        _configService = configService;
        _persistentState = persistentState;
        IsRunning = true;
        Console.WriteLine($"[android-backend] started, serving on :{Port}.");
    }

    /// <summary>Persist config + state, stop the embedded server, and release everything.
    /// Idempotent. Mirrors the daemon's ApplicationStopping save.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        Console.WriteLine("[android-backend] stopping — saving config + state…");
        try { _configService?.SaveAppSettings(); } catch (Exception ex) { Console.WriteLine($"[android-backend] save config failed: {ex.Message}"); }
        try { _persistentState?.Save(); } catch (Exception ex) { Console.WriteLine($"[android-backend] save state failed: {ex.Message}"); }
        if (_remoteServer is { } server)
        {
            try { await server.StopAsync().ConfigureAwait(false); } catch (Exception ex) { Console.WriteLine($"[android-backend] server stop failed: {ex.Message}"); }
        }
        _hostLoop?.Dispose();
        try { _provider?.Dispose(); } catch { /* a dispose fault must not wedge teardown */ }
        _hostLoop = null;
        _provider = null;
        _remoteServer = null;
        _configService = null;
        _persistentState = null;
        IsRunning = false;
        Console.WriteLine("[android-backend] stopped.");
    }
}
