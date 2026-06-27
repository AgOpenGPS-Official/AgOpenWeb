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

        // Load state, build the VM, start + wire the embedded server — the shared,
        // platform-agnostic sequence (identical on the daemon, the WebView launcher, and iOS).
        var backend = await AgOpenWeb.RemoteWiring.WebBackend.StartAsync(provider, new AndroidImageryCapture());
        var remoteServer = backend.Server;
        var configService = provider.GetRequiredService<IConfigurationService>();
        var persistentState = provider.GetRequiredService<IPersistentStateService>();

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
