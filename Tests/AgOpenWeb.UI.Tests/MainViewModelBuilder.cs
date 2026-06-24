using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Battery;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.UI.Tests;

/// <summary>
/// Builds a MainViewModel with all services mocked via NSubstitute.
/// Call Build() to get a ready instance for headless UI tests.
/// </summary>
public class MainViewModelBuilder
{
    public ISettingsService SettingsService { get; } = Substitute.For<ISettingsService>();
    public IVehicleProfileService VehicleProfileService { get; } = Substitute.For<IVehicleProfileService>();
    public INtripProfileService NtripProfileService { get; } = Substitute.For<INtripProfileService>();
    public IAutoSteerService AutoSteerService { get; } = Substitute.For<IAutoSteerService>();
    public ICoverageMapService CoverageMapService { get; } = Substitute.For<ICoverageMapService>();
    public IJobService JobService { get; } = Substitute.For<IJobService>();
    public IMapService MapService { get; } = Substitute.For<IMapService>();
    public IFieldService FieldService { get; } = Substitute.For<IFieldService>();
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
        // ConfigurationViewModel (lazily built by config-backed dialogs such as
        // App Settings) reads IConfigurationService.Store in its ctor, so the
        // mock must return the real singleton store.
        var configurationService = Substitute.For<IConfigurationService>();
        configurationService.Store.Returns(AgOpenWeb.Models.Configuration.ConfigurationStore.Instance);

        return new MainViewModel(
            udpService: Substitute.For<IUdpCommunicationService>(),
            gpsService: Substitute.For<AgOpenWeb.Services.Interfaces.IGpsService>(),
            fieldService: FieldService,
            ntripService: Substitute.For<INtripClientService>(),
            displaySettings: Substitute.For<AgOpenWeb.Services.Interfaces.IDisplaySettingsService>(),
            fieldStatistics: Substitute.For<AgOpenWeb.Services.Interfaces.IFieldStatisticsService>(),
            simulatorService: Substitute.For<AgOpenWeb.Services.Interfaces.IGpsSimulationService>(),
            settingsService: SettingsService,
            mapService: MapService,
            boundaryRecordingService: Substitute.For<IBoundaryRecordingService>(),
            boundaryBuilderService: Substitute.For<IBoundaryBuilderService>(),
            boundaryFileService: new BoundaryFileService(),
            headlandBuilderService: Substitute.For<AgOpenWeb.Services.Headland.IHeadlandBuilderService>(),
            trackGuidanceService: Substitute.For<ITrackGuidanceService>(),
            youTurnCreationService: new YouTurnCreationService(
                NullLogger<YouTurnCreationService>.Instance,
                Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(),
                AgOpenWeb.Models.Configuration.ConfigurationStore.Instance),
            youTurnGuidanceService: new YouTurnGuidanceService(),
            youTurnPathingService: new YouTurnPathingService(
                NullLogger<YouTurnPathingService>.Instance,
                AgOpenWeb.Models.Configuration.ConfigurationStore.Instance),
            polygonOffsetService: Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(),
            turnAreaService: Substitute.For<AgOpenWeb.Services.Interfaces.ITurnAreaService>(),
            vehicleProfileService: VehicleProfileService,
            configurationService: configurationService,
            autoSteerService: AutoSteerService,
            smartWasService: Substitute.For<ISmartWasCalibrationService>(),
            trackCopierService: Substitute.For<ITrackCopierService>(),
            moduleCommunicationService: Substitute.For<IModuleCommunicationService>(),
            toolPositionService: Substitute.For<IToolPositionService>(),
            coverageMapService: CoverageMapService,
            sectionControlService: Substitute.For<ISectionControlService>(),
            ntripProfileService: NtripProfileService,
            chartDataService: Substitute.For<IChartDataService>(),
            audioService: Substitute.For<IAudioService>(),
            elevationLogService: Substitute.For<IElevationLogService>(),
            jobService: JobService,
            tramLineService: Substitute.For<ITramLineService>(),
            gpsPipelineService: Substitute.For<IGpsPipelineService>(),
            intents: Intents,
            logger: NullLogger<MainViewModel>.Instance,
            appState: new ApplicationState(),
            configStore: AgOpenWeb.Models.Configuration.ConfigurationStore.Instance,
            persistentStateService: Substitute.For<IPersistentStateService>(),
            batteryService: new NullBatteryService(),
            uiDispatcher: new AgOpenWeb.Services.Threading.InlineUiDispatcher(),
            uiTimerFactory: new AgOpenWeb.Services.Threading.ManualUiTimerFactory());
    }
}
