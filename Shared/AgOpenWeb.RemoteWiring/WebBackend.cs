// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
        // Slow-work reporting on the host loop (#169) once logging is available.
        if (sp.GetService<IUiDispatcher>() is AgOpenWeb.Services.Threading.HostLoopDispatcher hostLoop)
            hostLoop.Logger = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("HostLoop");
        sp.GetRequiredService<ISettingsService>().Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();

        WireCrossServices(sp);

        // Construct the single MainViewModel (it subscribes), then start the services: on the
        // host loop, where the view model's state lives.
        var vm = await CreateViewModelOnHostLoopAsync(sp).ConfigureAwait(false);

        // Start the embedded browser server, then wire its command handler + projectors.
        var server = new AgOpenWeb.RemoteServer.RemoteServerHost();
        // Browsers arriving and leaving, and why, in the app's log (the bug report dump's logs.txt).
        if (sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("AgOpenWeb.RemoteServer.Browsers") is { } browserLog)
            server.BrowserLog = line => Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(browserLog, "{Line}", line);
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
            sp.GetRequiredService<IPersistentStateService>(),
            sp.GetService<INtripClientService>()).ConfigureAwait(false);

        // Wire on the host loop so the command handler runs serialized with the render-pull /
        // status timers, exactly as the Avalonia UI thread did in the old windowed build.
        sp.GetRequiredService<IUiDispatcher>().Post(() =>
            RemoteServerWiring.Wire(server, vm, sp, configService, imageryCapture));

        return new WebBackend { Server = server, ViewModel = vm };
    }

    /// <summary>
    /// <see cref="CreateViewModel"/> on the host loop (<see cref="IUiDispatcher"/>), from whatever
    /// thread starts the backend. The view model's start begins its module status loop (the
    /// hello, the module and GPS checks, the module-lost and GPS-lost alarms, every 100 ms), which
    /// continues wherever its awaits resume: on the host loop only if it was started there. The
    /// heads start the backend on their start-up thread (iOS from the thread pool), so the loop
    /// ran on the thread pool beside the host loop, writing the view model's state from there.
    /// The harness rigs already built it on the host loop.
    /// </summary>
    public static async Task<MainViewModel> CreateViewModelOnHostLoopAsync(IServiceProvider sp)
    {
        MainViewModel vm = null!;
        await sp.GetRequiredService<IUiDispatcher>().InvokeAsync(() => vm = CreateViewModel(sp)).ConfigureAwait(false);
        return vm;
    }

    /// <summary>
    /// Build the view model, then start the services and the view model, in this order: what
    /// the view model listens to is subscribed before a service sends its first event. The
    /// order of the starts is the one the view model's constructor had. Called on the host loop,
    /// by <see cref="StartAsync"/> (through <see cref="CreateViewModelOnHostLoopAsync"/>) and by
    /// the harness rigs; after <see cref="WireCrossServices"/>.
    /// </summary>
    public static MainViewModel CreateViewModel(IServiceProvider sp)
    {
        var vm = sp.GetRequiredService<MainViewModel>();
        StartServices(sp, vm);
        return vm;
    }

    /// <summary>The services' starts, in the order the view model's constructor had them, then
    /// the view model's own (<see cref="MainViewModel.Start"/>), which waits for the UDP port.
    /// Stopped in reverse by <see cref="StopServices"/>.</summary>
    public static void StartServices(IServiceProvider sp, MainViewModel vm)
    {
        sp.GetRequiredService<IBatteryService>().Start();
        sp.GetRequiredService<IAutoSteerService>().Start(); // Enable zero-copy GPS pipeline
        sp.GetRequiredService<IGpsPipelineService>().Start(); // the background GPS processing pipeline
        sp.GetService<ISteerMachineLoopService>()?.Start(); // the 100 Hz control loop (#313)
        // Chart data collection from the start, so a chart opened mid-session shows recent
        // history (rolling buffers of a few hundred points).
        sp.GetRequiredService<IChartDataService>().Start();
        sp.GetRequiredService<RenderPose>().Start(); // the map's pose between fixes, as the view model's start began
        vm.Start(sp.GetRequiredService<IUdpCommunicationService>().StartAsync());
    }

    /// <summary>
    /// The backend's stop, when its web server stops: the reverse of <see cref="StartServices"/>.
    /// The view model's background loops end first (the module checks), then chart collection, the control loop
    /// (it waits up to 1 s for its last tick), the GPS pipeline, AutoSteer and the battery
    /// reader. The UDP port is closed when the provider is disposed, as before; the control loop
    /// and the battery reader are disposed then too, a second stop that does nothing.
    /// </summary>
    public static void StopServices(IServiceProvider sp, MainViewModel vm)
    {
        vm.StopBackgroundLoops();
        sp.GetRequiredService<IChartDataService>().Stop();
        sp.GetService<ISteerMachineLoopService>()?.Stop();
        sp.GetRequiredService<IGpsPipelineService>().Stop();
        sp.GetRequiredService<IAutoSteerService>().Stop();
        sp.GetRequiredService<IBatteryService>().Stop();
    }

    /// <summary>Wire cross-referencing services after the container is built: the
    /// AutoSteerService into UdpCommunicationService for the zero-copy GPS→steering path, the
    /// services AutoSteer reads, the control tick's work into the control loop and the loop's rate
    /// into section control.
    /// Replaces the per-platform <c>WireUpServices()</c> DI extensions; services are joined
    /// here, where they are built, not by casts in the view model.</summary>
    public static void WireCrossServices(IServiceProvider sp)
    {
        var udp = sp.GetRequiredService<IUdpCommunicationService>() as UdpCommunicationService;
        udp?.SetAutoSteerService(sp.GetRequiredService<IAutoSteerService>());
        (sp.GetRequiredService<IAutoSteerService>() as Services.AutoSteer.AutoSteerService)?.SetTramLineService(sp.GetRequiredService<ITramLineService>());
        (sp.GetRequiredService<IAutoSteerService>() as Services.AutoSteer.AutoSteerService)?.SetToolPositionService(sp.GetRequiredService<IToolPositionService>());
        (sp.GetRequiredService<IAutoSteerService>() as Services.AutoSteer.AutoSteerService)?.SetSmartWasService(sp.GetRequiredService<ISmartWasCalibrationService>());
        if (sp.GetService<ISteerMachineLoopService>() is { } controlLoop)
        {
            // The control tick's work (ControlTick), joined before the loop starts, as the view
            // model's handler was in its constructor.
            controlLoop.Ticked += sp.GetRequiredService<Services.Pipeline.ControlTick>().Run;
            // TickHz is on the concrete SectionControlService, not the
            // interface (it's an internal-tuning concern, not a contract).
            if (sp.GetRequiredService<ISectionControlService>() is Services.Section.SectionControlService scsConcrete)
                scsConcrete.TickHz = controlLoop.FrequencyHz;
        }
    }
}
