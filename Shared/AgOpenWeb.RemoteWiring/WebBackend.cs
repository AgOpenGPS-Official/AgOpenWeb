// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading.Tasks;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.RemoteWiring;

/// <summary>
/// The platform-agnostic guidance backend: load persisted state, construct the single
/// <see cref="MainViewModel"/> (which starts the 100 Hz control loop + render-pull / status
/// timers), start the embedded <see cref="AgOpenWeb.RemoteServer.RemoteServerHost"/>, and wire
/// its command handler + projectors to the VM. The browser/WebView at http://&lt;host&gt;:5174
/// is the UI on every head — there is no native UI.
///
/// Each platform supplies a fully-built <see cref="IServiceProvider"/> (its own
/// AddAgOpenWebServices, with a <c>HostLoopDispatcher</c> registered as
/// <see cref="IUiDispatcher"/>/<see cref="IUiTimerFactory"/> and <c>NullMapService</c> as
/// <see cref="IMapService"/>) plus an <see cref="IBoundaryImageryCapture"/>. The Desktop daemon
/// wraps this in a generic host for systemd/Windows-Service lifetime; the WebView launcher and
/// the mobile heads call it directly and own their own shutdown.
/// </summary>
public sealed class WebBackend
{
    /// <summary>The embedded server (status readouts: Port, ClientCount). Set after <see cref="StartAsync"/>.</summary>
    public AgOpenWeb.RemoteServer.RemoteServerHost Server { get; private set; } = null!;

    /// <summary>The single control-brain VM the server reads + commands. Set after <see cref="StartAsync"/>.</summary>
    public MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// Load settings/config/persistent state, build the VM, start + wire the RemoteServer.
    /// The provider must already be built and have its cross-service refs wired
    /// (see <see cref="WireCrossServices"/>, called here). Returns once the server is bound.
    /// </summary>
    public static async Task<WebBackend> StartAsync(IServiceProvider sp, IBoundaryImageryCapture imageryCapture)
    {
        // Persisted settings → ConfigurationStore, then app config + persistent state.
        sp.GetRequiredService<ISettingsService>().Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();

        WireCrossServices(sp);

        // Construct the single MainViewModel (starts the control loop + render-pull /
        // status timers on the host loop).
        var vm = sp.GetRequiredService<MainViewModel>();

        // Start the embedded browser server, then wire its command handler + projectors.
        var server = new AgOpenWeb.RemoteServer.RemoteServerHost();
        await server.StartAsync(
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
            sp.GetRequiredService<IFieldService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IVehicleProfileService>(),
            sp.GetRequiredService<IPersistentStateService>()).ConfigureAwait(false);

        // Wire on the host loop so the command handler runs serialized with the render-pull /
        // status timers, exactly as the Avalonia UI thread did in the old windowed build.
        sp.GetRequiredService<IUiDispatcher>().Post(() =>
            RemoteServerWiring.Wire(server, vm, sp, configService, imageryCapture));

        return new WebBackend { Server = server, ViewModel = vm };
    }

    /// <summary>Wire cross-referencing services after the container is built: the
    /// AutoSteerService into UdpCommunicationService for the zero-copy GPS→steering path.
    /// Replaces the per-platform <c>WireUpServices()</c> DI extensions.</summary>
    public static void WireCrossServices(IServiceProvider sp)
    {
        var udp = sp.GetRequiredService<IUdpCommunicationService>() as UdpCommunicationService;
        udp?.SetAutoSteerService(sp.GetRequiredService<IAutoSteerService>());
    }
}
