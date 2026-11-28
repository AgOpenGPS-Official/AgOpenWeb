// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Battery;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Builds a MainViewModel with all services mocked via NSubstitute.
/// Call Build() to get a ready instance for ViewModel-only tests (no Avalonia).
/// Mocked services are exposed as public properties so tests can configure them.
/// </summary>
public class MainViewModelBuilder
{
    public ISettingsService SettingsService { get; } = Substitute.For<ISettingsService>();
    public IVehicleProfileService VehicleProfileService { get; } = Substitute.For<IVehicleProfileService>();
    public INtripProfileService NtripProfileService { get; } = Substitute.For<INtripProfileService>();
    public INtripClientService NtripService { get; } = Substitute.For<INtripClientService>();
    public IGpsService GpsService { get; } = Substitute.For<IGpsService>();
    public ITrackGuidanceService TrackGuidanceService { get; } = Substitute.For<ITrackGuidanceService>();
    public IAutoSteerService AutoSteerService { get; } = Substitute.For<IAutoSteerService>();
    public IUdpCommunicationService UdpService { get; } = Substitute.For<IUdpCommunicationService>();
    public IMapService MapService { get; } = Substitute.For<IMapService>();
    public ICoverageMapService CoverageMapService { get; } = Substitute.For<ICoverageMapService>();
    public ISectionControlService SectionControlService { get; set; } = Substitute.For<ISectionControlService>();
    public IBoundaryRecordingService BoundaryRecordingService { get; set; } = Substitute.For<IBoundaryRecordingService>();
    public IToolPositionService ToolPositionService { get; } = Substitute.For<IToolPositionService>();
    public IGpsSimulationService SimulatorService { get; } = Substitute.For<IGpsSimulationService>();
    public IGpsPipelineService GpsPipelineService { get; } = Substitute.For<IGpsPipelineService>();
    public IModuleCommunicationService ModuleCommunicationService { get; set; } = Substitute.For<IModuleCommunicationService>();
    public IConfigurationService ConfigurationService { get; set; } = Substitute.For<IConfigurationService>();
    public IFieldService FieldService { get; set; } = Substitute.For<IFieldService>();
    public IJobService JobService { get; set; } = Substitute.For<IJobService>();
    public ILogger<MainViewModel> Logger { get; set; } = NullLogger<MainViewModel>.Instance;
    public AgOpenWeb.Services.Interfaces.ITramLineService TramLineService { get; set; } = Substitute.For<AgOpenWeb.Services.Interfaces.ITramLineService>();
    public AgOpenWeb.Services.Pipeline.PipelineIntents Intents { get; } = new();
    public AgOpenWeb.Services.Geometry.IPolygonOffsetService PolygonOffsetService { get; set; } = Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>();
    /// <summary>Wires the map's 30 Hz render pull (none without one, as before).</summary>
    public IAudioService AudioService { get; } = Substitute.For<IAudioService>();
    public IPositionEstimator? PositionEstimator { get; set; }
    /// <summary>Every timer the view model makes; none fires until a test calls <c>Fire</c>.</summary>
    public List<AgOpenWeb.Services.Threading.ManualUiTimer> Timers { get; } = new();

    public MainViewModelBuilder()
    {
        // Sensible defaults so the VM constructor doesn't NullRef
        var settings = new AppSettings { FieldsDirectory = Path.GetTempPath() };
        SettingsService.Settings.Returns(settings);
        SettingsService.GetSettingsFilePath().Returns(Path.Combine(Path.GetTempPath(), "appsettings.json"));

        VehicleProfileService.VehiclesDirectory.Returns(Path.GetTempPath());
        NtripProfileService.ProfilesDirectory.Returns(Path.GetTempPath());
        NtripProfileService.Profiles.Returns(new List<AgOpenWeb.Models.Ntrip.NtripProfile>());
        VehicleProfileService.GetAvailableProfiles().Returns(new List<string>());
    }

    public MainViewModel Build()
    {
        var battery = new NullBatteryService();
        var dispatcher = new AgOpenWeb.Services.Threading.InlineUiDispatcher();
        var timers = new RecordingTimerFactory(Timers);
        var appState = new ApplicationState();
        // Made before the view model, so its timer is the first 30 Hz timer in Timers, as the
        // view model's own was.
        var renderPose = new AgOpenWeb.ViewModels.RenderPose(ToolPositionService, MapService, appState,
            ConfigurationStore.Instance, dispatcher, timers, PositionEstimator);
        var vm = new MainViewModel(
            udpService: UdpService,
            gpsService: GpsService,
            fieldService: FieldService,
            ntripService: NtripService,
            displaySettings: Substitute.For<IDisplaySettingsService>(),
            fieldStatistics: Substitute.For<IFieldStatisticsService>(),
            simulatorService: SimulatorService,
            settingsService: SettingsService,
            mapService: MapService,
            boundaryRecordingService: BoundaryRecordingService,
            boundaryBuilderService: Substitute.For<IBoundaryBuilderService>(),
            headlandBuilderService: Substitute.For<AgOpenWeb.Services.Headland.IHeadlandBuilderService>(),
            trackGuidanceService: TrackGuidanceService,
            youTurnCreationService: new YouTurnCreationService(
                NullLogger<YouTurnCreationService>.Instance,
                Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(),
                ConfigurationStore.Instance),
            youTurnGuidanceService: new YouTurnGuidanceService(),
            youTurnPathingService: new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance),
            polygonOffsetService: PolygonOffsetService,
            turnAreaService: Substitute.For<ITurnAreaService>(),
            vehicleProfileService: VehicleProfileService,
            configurationService: ConfigurationService,
            autoSteerService: AutoSteerService,
            smartWasService: Substitute.For<ISmartWasCalibrationService>(),
            trackCopierService: Substitute.For<ITrackCopierService>(),
            moduleCommunicationService: ModuleCommunicationService,
            toolPositionService: ToolPositionService,
            coverageMapService: CoverageMapService,
            sectionControlService: SectionControlService,
            ntripProfileService: NtripProfileService,
            audioService: AudioService,
            elevationLogService: Substitute.For<IElevationLogService>(),
            jobService: JobService,
            tramLineService: TramLineService,
            gpsPipelineService: GpsPipelineService,
            intents: Intents,
            logger: Logger,
            appState: appState,
            configStore: ConfigurationStore.Instance,
            persistentStateService: Substitute.For<IPersistentStateService>(),
            batteryService: battery,
            uiDispatcher: dispatcher,
            uiTimerFactory: timers);

        // The starts, as WebBackend.StartServices makes them after building the view model (no
        // control loop here).
        battery.Start();
        AutoSteerService.Start();
        GpsPipelineService.Start();
        renderPose.Start();
        vm.Start(UdpService.StartAsync());
        return vm;
    }
}

/// <summary>A <see cref="AgOpenWeb.Services.Threading.ManualUiTimerFactory"/> that keeps the timers it makes.</summary>
internal sealed class RecordingTimerFactory(List<AgOpenWeb.Services.Threading.ManualUiTimer> timers) : IUiTimerFactory
{
    public IUiTimer Create()
    {
        var t = new AgOpenWeb.Services.Threading.ManualUiTimer();
        timers.Add(t);
        return t;
    }
}
