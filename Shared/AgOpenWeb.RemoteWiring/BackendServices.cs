// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.Tram;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.RemoteWiring;

/// <summary>
/// The services every head registers the same way, registered once here; each head's
/// <c>AddAgOpenWebServices</c> calls it and adds only what is its own (logging, the network
/// watcher and interfaces, the battery, the device report, the VT card). A new service goes
/// here, not in three places. <c>BackendServicesTests</c> builds this container with stand-ins
/// for the platform's and resolves the view model: the real graph, which the view model's unit
/// tests (a builder of stand-ins) never build.
/// </summary>
public static class BackendServices
{
    public static IServiceCollection AddBackendServices(this IServiceCollection services)
    {
        // Centralized application state (single source of truth)
        // The backend's state: the statics point to it while it runs, let go when it stops (BackendStateLease).
        services.AddSingleton<AgOpenWeb.Models.State.BackendStateLease>();
        services.AddSingleton(sp => sp.GetRequiredService<AgOpenWeb.Models.State.BackendStateLease>().State);
        services.AddSingleton(sp => sp.GetRequiredService<AgOpenWeb.Models.State.BackendStateLease>().Store);
        services.AddSingleton(sp => sp.GetRequiredService<AgOpenWeb.Models.State.BackendStateLease>().Persistent);

        // The host loop (dispatcher + timers) the VM and the pipeline run on. Each head's host
        // re-registers its own instance (last wins); these keep the container resolvable on
        // its own. See Plans/Completed/CONFIG_STATE_AUDIT.md §11.
        services.AddSingleton<HostLoopDispatcher>();
        services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<HostLoopDispatcher>());
        services.AddSingleton<IUiTimerFactory>(sp => sp.GetRequiredService<HostLoopDispatcher>());

        services.AddSingleton<MainViewModel>(); // one per backend: it subscribes to the singletons it shows
        services.AddTransient<ConfigurationViewModel>();

        // One watcher for network changes and the local network's interfaces are the
        // platform's (each head registers its own).
        services.AddSingleton<IUdpCommunicationService, UdpCommunicationService>();

        // Core services
        services.AddSingleton<IGpsService, GpsService>();
        services.AddSingleton<IDisplaySettingsService, DisplaySettingsService>();
        services.AddSingleton<IFieldStatisticsService, FieldStatisticsService>();
        services.AddSingleton<IGpsSimulationService, GpsSimulationService>();

        // Other services
        services.AddSingleton<IFieldService, FieldService>();
        services.AddSingleton<INtripClientService, NtripClientService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPersistentStateService, PersistentStateService>();

        // Per-session jobs (#349). Reads FieldsRoot through ISettingsService
        // so changes to the user's Documents path are picked up live.
        services.AddSingleton<IJobService>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new JobService(() => settings.Settings.FieldsDirectory);
        });

        // Field file I/O services
        services.AddSingleton<FieldPlaneFileService>();

        // Boundary recording service
        services.AddSingleton<IBoundaryRecordingService, BoundaryRecordingService>();
        services.AddSingleton<IBoundaryBuilderService, BoundaryBuilderService>();

        // Headland builder services
        services.AddSingleton<IPolygonOffsetService, PolygonOffsetService>();
        services.AddSingleton<IHeadlandBuilderService, HeadlandBuilderService>();
        services.AddSingleton<ITurnAreaService, TurnAreaService>();

        // Guidance algorithm services
        services.AddSingleton<ITrackGuidanceService, TrackGuidanceService>();

        // AutoSteer pipeline service (zero-copy GPS→PGN path)
        services.AddSingleton<IAutoSteerService, AutoSteerService>();

        // ISOBUS task controller link: PGN 240 heartbeat in, PGN 241 section control out
        services.AddSingleton<IIsobusTaskControllerService, AgOpenWeb.Services.Isobus.IsobusTaskControllerService>();
        // The ISOBUS virtual terminal (VT card) is registered by each head, which references it,
        // when switched on (FeatureSwitches).

        // Smart WAS calibration (statistical WAS zero analyzer)
        services.AddSingleton<ISmartWasCalibrationService, SmartWasCalibrationService>();

        // Track copying between fields (transforms via source plane → WGS84 → target plane)
        services.AddSingleton<ITrackCopierService, TrackCopierService>();

        // Chart data service (collects rolling time-series for diagnostic charts)
        services.AddSingleton<IChartDataService, ChartDataService>();

        // Audio service (cross-platform sound effects)
        services.AddSingleton<IAudioService, AgOpenWeb.Services.Audio.WebClientAudioService>();

        // The battery and the device for the bug report dump are the platform's (each head).

        // Module communication service (work switch, steer switch logic)
        services.AddSingleton<IModuleCommunicationService, ModuleCommunicationService>();

        // YouTurn services
        services.AddSingleton<YouTurnCreationService>();
        services.AddSingleton<YouTurnGuidanceService>();
        services.AddSingleton<YouTurnPathingService>();
        services.AddSingleton<YouTurnStateMachine>();

        // Tool position service (for trailing implements)
        services.AddSingleton<IToolPositionService, ToolPositionService>();

        // Worked area calculation service
        services.AddSingleton<IWorkedAreaService, WorkedAreaService>();

        // Coverage map service (tracks worked area as triangle strips)
        services.AddSingleton<ICoverageMapService, CoverageMapService>();

        // Section control service (automatic section on/off based on coverage, boundaries, headlands)
        services.AddSingleton<ISectionControlService, SectionControlService>();

        // Tram line services (controlled traffic farming)
        services.AddSingleton<ITramLineOffsetService, TramLineOffsetService>();
        services.AddSingleton<ITramLineService, TramLineService>();

        // Vehicle profile service
        services.AddSingleton<IVehicleProfileService, VehicleProfileService>();
        services.AddSingleton<IToolProfileService, ToolProfileService>();

        // NTRIP profile service
        services.AddSingleton<INtripProfileService, NtripProfileService>();

        // Configuration service (single source of truth)
        services.AddSingleton<IConfigurationService, ConfigurationService>();

        // Elevation log service (#120)
        services.AddSingleton<IElevationLogService, ElevationLogService>();

        // GPS processing pipeline (background-thread orchestration)
        // Singleton: UI commands and the cycle worker must share one intent instance.
        services.AddSingleton<IPipelineIntents, PipelineIntents>();
        // Fusion service holds fix-to-fix state between cycles; singleton required.
        services.AddSingleton<IGpsHeadingFusionService, GpsHeadingFusionService>();
        // Position estimator: GPS-anchored snapshot bridge between the GPS
        // arrival path (10 Hz) and the host control loop (100 Hz). Singleton —
        // single shared snapshot.
        services.AddSingleton<IPositionEstimator, PositionEstimator>();
        // Host control loop (#313): runs at 100 Hz on its own thread, sends
        // PGN 254 + PGN 239 every tick to match the firmware autosteer cadence.
        services.AddSingleton<ISteerMachineLoopService>(_ => new SteerMachineLoopService(frequencyHz: 100.0));
        // The work of each control tick (WebBackend.WireCrossServices joins it to the loop).
        services.AddSingleton<ControlTick>();
        services.AddSingleton<IGpsPipelineService, GpsPipelineService>();

        // No native map control in the web build — the browser/CanvasKit client renders the map.
        services.AddSingleton<IMapService, NullMapService>();

        return services;
    }
}
