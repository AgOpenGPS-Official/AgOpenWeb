using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Battery;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.ViewModels;
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
    public IGpsService GpsService { get; } = Substitute.For<IGpsService>();
    public ITrackGuidanceService TrackGuidanceService { get; } = Substitute.For<ITrackGuidanceService>();
    public IAutoSteerService AutoSteerService { get; } = Substitute.For<IAutoSteerService>();
    public IMapService MapService { get; } = Substitute.For<IMapService>();
    public ICoverageMapService CoverageMapService { get; } = Substitute.For<ICoverageMapService>();
    public ISectionControlService SectionControlService { get; } = Substitute.For<ISectionControlService>();
    public IGpsSimulationService SimulatorService { get; } = Substitute.For<IGpsSimulationService>();
    public IGpsPipelineService GpsPipelineService { get; } = Substitute.For<IGpsPipelineService>();
    public AgOpenWeb.Services.Pipeline.PipelineIntents Intents { get; } = new();

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
        return new MainViewModel(
            udpService: Substitute.For<IUdpCommunicationService>(),
            gpsService: GpsService,
            fieldService: Substitute.For<IFieldService>(),
            ntripService: Substitute.For<INtripClientService>(),
            displaySettings: Substitute.For<IDisplaySettingsService>(),
            fieldStatistics: Substitute.For<IFieldStatisticsService>(),
            simulatorService: SimulatorService,
            settingsService: SettingsService,
            mapService: MapService,
            boundaryRecordingService: Substitute.For<IBoundaryRecordingService>(),
            boundaryBuilderService: Substitute.For<IBoundaryBuilderService>(),
            boundaryFileService: new BoundaryFileService(),
            headlandBuilderService: Substitute.For<AgOpenWeb.Services.Headland.IHeadlandBuilderService>(),
            trackGuidanceService: TrackGuidanceService,
            youTurnCreationService: new YouTurnCreationService(
                NullLogger<YouTurnCreationService>.Instance,
                Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(),
                ConfigurationStore.Instance),
            youTurnGuidanceService: new YouTurnGuidanceService(),
            youTurnPathingService: new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance),
            polygonOffsetService: Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(),
            turnAreaService: Substitute.For<ITurnAreaService>(),
            vehicleProfileService: VehicleProfileService,
            configurationService: Substitute.For<IConfigurationService>(),
            autoSteerService: AutoSteerService,
            smartWasService: Substitute.For<ISmartWasCalibrationService>(),
            trackCopierService: Substitute.For<ITrackCopierService>(),
            moduleCommunicationService: Substitute.For<IModuleCommunicationService>(),
            toolPositionService: Substitute.For<IToolPositionService>(),
            coverageMapService: CoverageMapService,
            sectionControlService: SectionControlService,
            ntripProfileService: NtripProfileService,
            chartDataService: Substitute.For<IChartDataService>(),
            audioService: Substitute.For<IAudioService>(),
            elevationLogService: Substitute.For<IElevationLogService>(),
            jobService: Substitute.For<IJobService>(),
            tramLineService: Substitute.For<AgOpenWeb.Services.Interfaces.ITramLineService>(),
            gpsPipelineService: GpsPipelineService,
            intents: Intents,
            logger: NullLogger<MainViewModel>.Instance,
            appState: new ApplicationState(),
            configStore: ConfigurationStore.Instance,
            persistentStateService: Substitute.For<IPersistentStateService>(),
            batteryService: new NullBatteryService(),
            uiDispatcher: new AgOpenWeb.Services.Threading.InlineUiDispatcher(),
            uiTimerFactory: new AgOpenWeb.Services.Threading.ManualUiTimerFactory());
    }
}
