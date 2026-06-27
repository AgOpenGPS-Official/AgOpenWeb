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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tram;
using AgOpenWeb.ViewModels;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Android.Services;
using AgOpenWeb.Services.Logging;
using AgOpenWeb.Services.Threading;

namespace AgOpenWeb.Android.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgOpenWebServices(this IServiceCollection services)
    {
        // Configure structured logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            builder.AddProvider(new InMemoryLoggerProvider());
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Centralized application state (single source of truth)
        services.AddSingleton<ApplicationState>();           // ephemeral, in-memory only
        services.AddSingleton(_ => AgOpenWeb.Models.Configuration.ConfigurationStore.Instance); // config SoT; same object as .Instance (Views/tests use the static seam)
        services.AddSingleton(_ => PersistentAppState.Instance); // persisted to appstate.json (same object as .Instance)

        // UI-thread dispatcher + timer abstraction. With the native UI gone the VM/pipeline
        // run on a single HostLoopDispatcher (owned by the foreground BackendService's
        // AndroidBackendHost, which re-registers its own instance — last-wins), so guidance
        // stays live while the WebView Activity is backgrounded. See Plans/CONFIG_STATE_AUDIT.md §11.
        services.AddSingleton<HostLoopDispatcher>();
        services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<HostLoopDispatcher>());
        services.AddSingleton<IUiTimerFactory>(sp => sp.GetRequiredService<HostLoopDispatcher>());

        // Register ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConfigurationViewModel>();

        // Register Services
        services.AddSingleton<ILocalNetworkInfoProvider>(_ =>
            new AndroidLocalNetworkInfoProvider(
                global::Android.App.Application.Context
                ?? throw new InvalidOperationException("Android application context is unavailable.")));
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
        services.AddSingleton<BoundaryFileService>();

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

        // Smart WAS calibration (statistical WAS zero analyzer)
        services.AddSingleton<ISmartWasCalibrationService, SmartWasCalibrationService>();

        // Track copying between fields (transforms via source plane → WGS84 → target plane)
        services.AddSingleton<ITrackCopierService, TrackCopierService>();

        // Chart data service (collects rolling time-series for diagnostic charts)
        services.AddSingleton<IChartDataService, ChartDataService>();

        // Audio service (cross-platform sound effects)
        services.AddSingleton<IAudioService, AgOpenWeb.Android.Services.AudioService>();

        // Battery service — subscribes to the sticky ACTION_BATTERY_CHANGED
        // broadcast via Application.Context (no polling needed).
        services.AddSingleton<IBatteryService>(_ =>
            new AgOpenWeb.Android.Services.AndroidBatteryService(
                global::Android.App.Application.Context!));

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
        services.AddSingleton<IGpsPipelineService, GpsPipelineService>();

        // No native map control in the web build — the browser/CanvasKit client renders the map.
        services.AddSingleton<IMapService, NullMapService>();

        return services;
    }

    /// <summary>
    /// Wire up services that need cross-references after the container is built.
    /// Call this after building the service provider.
    /// </summary>
    public static void WireUpServices(this IServiceProvider serviceProvider)
    {
        // Wire AutoSteerService into UdpCommunicationService for zero-copy GPS processing
        var udpService = serviceProvider.GetRequiredService<IUdpCommunicationService>() as UdpCommunicationService;
        var autoSteerService = serviceProvider.GetRequiredService<IAutoSteerService>();

        udpService?.SetAutoSteerService(autoSteerService);
    }
}
