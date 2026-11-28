// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Fields;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Communication;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Models.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IUdpCommunicationService _udpService;
    private readonly AgOpenWeb.Services.Interfaces.IGpsService _gpsService;
    private readonly IFieldService _fieldService;
    private readonly INtripClientService _ntripService;
    private readonly IUiDispatcher _dispatcher;
    private readonly AgOpenWeb.Services.Interfaces.IDisplaySettingsService _displaySettings;
    private readonly AgOpenWeb.Services.Interfaces.IFieldStatisticsService _fieldStatistics;
    private readonly AgOpenWeb.Services.Interfaces.IGpsSimulationService _simulatorService;
    private readonly ISettingsService _settingsService;
    private readonly IPersistentStateService _persistentStateService;
    private readonly IBatteryService _batteryService;
    private readonly IMapService _mapService;
    private readonly IBoundaryRecordingService _boundaryRecordingService;
    private readonly IBoundaryBuilderService _boundaryBuilderService;
    private readonly Services.Headland.IHeadlandBuilderService _headlandBuilderService;
    private readonly IConfigurationService _configurationService;
    private readonly IAutoSteerService _autoSteerService;
    private readonly ISmartWasCalibrationService _smartWasService;
    private readonly ITrackCopierService _trackCopierService;
    private readonly IModuleCommunicationService _moduleCommunicationService;
    private readonly IToolPositionService _toolPositionService;
    private readonly ICoverageMapService _coverageMapService;
    private readonly ISectionControlService _sectionControlService;
    private readonly INtripProfileService _ntripProfileService;
    private readonly IAudioService _audioService;
    private readonly IElevationLogService _elevationLogService;
    private readonly IJobService _jobService;
    // What the open field session owns: replaced, not cleared, when a session starts.
    private FieldSessionData _sessionData;
    private readonly ITramLineService _tramLineService;
    private bool _hasTramSystemsEverUsed;
    private readonly Dictionary<string, (int start, int count, bool isBoundary)> _tramSystemLineRanges = new();
    private readonly IGpsPipelineService _gpsPipelineService;
    private readonly IIsobusTaskControllerService? _isobus;
    private readonly IPipelineIntents _intents;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ApplicationState _appState;
    private readonly ConfigurationStore _configStore;
    private readonly IUiTimerFactory _timerFactory;
    private readonly IUiTimer _simulatorTimer;
    // PERF-05 Phase 2c #2: unified 5 Hz status-display tick, decoupled from
    // every data source (GPS 10 Hz, control loop 100 Hz, AutoSteer 100 Hz).
    // Drives every MainViewModel property bound to the top status bar.
    private IUiTimer? _statusTickTimer;

    /// <summary>
    /// Centralized application state - single source of truth for all runtime state.
    /// Use this for new code. Existing properties will gradually migrate to use State.
    /// </summary>
    public ApplicationState State => _appState;
    public DisplayConfig Display => _configStore.Display;

    /// <summary>
    /// Persistent application state — window/last-view/last-field/sim position
    /// and other "where the app was" values that survive restart via
    /// appstate.json. Distinct from <see cref="State"/> (ephemeral) and
    /// configuration. Same object as <see cref="PersistentAppState.Instance"/>.
    /// </summary>
    public PersistentAppState PersistentState => PersistentAppState.Instance;

    // Convenience accessors for the injected ConfigurationStore (§11.2 — no
    // ambient _configStore access in the VM/services).
    private ConfigurationStore ConfigStore => _configStore;
    private ToolConfig Tool => _configStore.Tool;

    /// <summary>
    /// Sets the field origin in centralized FieldState — the single home (§12.1) —
    /// so all consumers (VM, map control, services) read State.Field.Origin*.
    /// </summary>
    private void SetFieldOrigin(double latitude, double longitude)
    {
        _simulatorLocalPlane = null;

        State.Field.OriginLatitude = latitude;
        State.Field.OriginLongitude = longitude;
        State.Field.LocalPlane = new LocalPlane(
            new Wgs84(latitude, longitude),
            new SharedFieldProperties());
    }

    // Track-on-boundary detection: skip boundary disengage on first pass

    // Track guidance state is now in MainViewModel.Guidance.cs
    // YouTurn state is now in MainViewModel.YouTurn.cs

    private string _statusMessage = "Starting...";
    private string _networkStatus = "Disconnected";
    private double _gpsToPgnLatencyMs;

    // Guidance/Steering status
    private double _crossTrackError;
    private bool _isAutoSteerActive;

    // Hello status (connection health)
    private bool _isAutoSteerHelloOk;
    private bool _isMachineHelloOk;
    private bool _isImuHelloOk;

    // Data status (data flow)
    private bool _isAutoSteerDataOk;
    private bool _isMachineDataOk;
    private bool _isImuDataOk;
    private bool _isGpsDataOk;

    // Tool position (for rendering)
    private double _toolEasting;
    private double _toolNorthing;
    private double _toolHeading;
    private double _toolWidth;
    private double _hitchEasting;
    private double _hitchNorthing;

    // Field properties
    private string _fieldsRootDirectory = string.Empty;

    public MainViewModel(
        IUdpCommunicationService udpService,
        AgOpenWeb.Services.Interfaces.IGpsService gpsService,
        IFieldService fieldService,
        INtripClientService ntripService,
        AgOpenWeb.Services.Interfaces.IDisplaySettingsService displaySettings,
        AgOpenWeb.Services.Interfaces.IFieldStatisticsService fieldStatistics,
        AgOpenWeb.Services.Interfaces.IGpsSimulationService simulatorService,
        ISettingsService settingsService,
        IMapService mapService,
        IBoundaryRecordingService boundaryRecordingService,
        IBoundaryBuilderService boundaryBuilderService,
        Services.Headland.IHeadlandBuilderService headlandBuilderService,
        ITrackGuidanceService trackGuidanceService,
        YouTurnCreationService youTurnCreationService,
        YouTurnGuidanceService youTurnGuidanceService,
        YouTurnPathingService youTurnPathingService,
        Services.Geometry.IPolygonOffsetService polygonOffsetService,
        Services.Interfaces.ITurnAreaService turnAreaService,
        IVehicleProfileService vehicleProfileService,
        IConfigurationService configurationService,
        IAutoSteerService autoSteerService,
        ISmartWasCalibrationService smartWasService,
        ITrackCopierService trackCopierService,
        IModuleCommunicationService moduleCommunicationService,
        IToolPositionService toolPositionService,
        ICoverageMapService coverageMapService,
        ISectionControlService sectionControlService,
        INtripProfileService ntripProfileService,
        IAudioService audioService,
        IElevationLogService elevationLogService,
        IJobService jobService,
        ITramLineService tramLineService,
        IGpsPipelineService gpsPipelineService,
        IPipelineIntents intents,
        ILogger<MainViewModel> logger,
        ApplicationState appState,
        ConfigurationStore configStore,
        IPersistentStateService persistentStateService,
        IBatteryService batteryService,
        IUiDispatcher uiDispatcher,
        IUiTimerFactory uiTimerFactory,
        IIsobusTaskControllerService? isobus = null)
    {
        _isobus = isobus;
        _logger = logger;
        _configStore = configStore;
        _persistentStateService = persistentStateService;
        _batteryService = batteryService;
        _dispatcher = uiDispatcher;
        _timerFactory = uiTimerFactory;
        _tramLineService = tramLineService;
        _sessionData = new FieldSessionData(uiDispatcher);

        // Battery icon in the strip — start the per-platform reader, prime with
        // its initial reading, and refresh on each StatusChanged notification.
        _batteryService.StatusChanged += (_, status) =>
        {
            _batteryStatus = status;
            OnPropertyChanged(nameof(BatteryStatus));
            OnPropertyChanged(nameof(BatteryLevel));
            OnPropertyChanged(nameof(IsBatteryCharging));
            OnPropertyChanged(nameof(IsBatteryAvailable));
        };

        // Status-strip rotator: cycles the bottom of the two-line text stack
        // through field name / stats / AB-line every 5 s, with a pause button.
        InitializeStatusStripRotation();

        // Sync GuidanceConfig.TramDisplay -> TramConfig.DisplayMode and regenerate
        ConfigStore.Guidance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Models.Configuration.GuidanceConfig.TramDisplay))
            {
                ConfigStore.Tram.DisplayMode = ConfigStore.Guidance.TramDisplay
                    ? Models.Configuration.TramDisplayMode.All
                    : Models.Configuration.TramDisplayMode.Off;
                UpdateTramLines(SelectedTrack);
            }
            else if (e.PropertyName == nameof(Models.Configuration.GuidanceConfig.TramPasses))
            {
                ConfigStore.Tram.Passes = ConfigStore.Guidance.TramPasses;
                UpdateTramLines(SelectedTrack);
            }
            else if (e.PropertyName == nameof(Models.Configuration.GuidanceConfig.TramLine))
            {
                // "First tram pass" (1-based) = AgOpenGPS start pass (0-based); the tram
                // generator already honours Tram.StartPass (#110).
                ConfigStore.Tram.StartPass = ConfigStore.Guidance.TramLine - 1;
                UpdateTramLines(SelectedTrack);
            }
        };

        // The Screen & Alerts "On-Screen Buttons" toggles gate the on-map U-Turn
        // and Lateral overlays. Refresh those computed visibilities when the user
        // flips a toggle so the overlays appear/disappear live.
        ConfigStore.Display.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Models.Configuration.DisplayConfig.UTurnButtonVisible))
            {
                OnPropertyChanged(nameof(IsUTurnOverlayVisible));
            }
            else if (e.PropertyName == nameof(Models.Configuration.DisplayConfig.LateralButtonVisible))
            {
                OnPropertyChanged(nameof(IsLateralOverlayVisible));
            }
            else if (e.PropertyName == nameof(Models.Configuration.DisplayConfig.GridVisible))
            {
                // The renderer reads ConfigStore.Display.GridVisible directly; keep the
                // on-screen grid button's active-state binding in sync when the flag is
                // flipped from the Settings/Screen-&-Alerts toggle instead of the button.
                OnPropertyChanged(nameof(IsGridOn));
            }
            else if (e.PropertyName == nameof(Models.Configuration.DisplayConfig.AutoTrack))
            {
                // Auto-track is persisted in ConfigStore.Display; mirror to FieldTools (the
                // SoT the web projector reads) whenever it loads/changes.
                State.FieldTools.IsAutoTrackEnabled = ConfigStore.Display.AutoTrack;
                OnPropertyChanged(nameof(IsAutoTrackEnabled));
            }
        };

        // Aggregate module-status indicator (replaces the four-letter G/I/A/M
        // cluster). Refresh when either the configured set or the live data-ok
        // flags change. State.Connections is wired in the post-_appState block.
        ConfigStore.Connections.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Models.Configuration.ConnectionConfig.IsGpsConfigured):
                case nameof(Models.Configuration.ConnectionConfig.IsImuConfigured):
                case nameof(Models.Configuration.ConnectionConfig.IsAutoSteerConfigured):
                case nameof(Models.Configuration.ConnectionConfig.IsMachineConfigured):
                    RaiseModuleStatusKindChanged();
                    // Module-present checkboxes (Network IO panel) are persistent
                    // config. Save on user change only — never during the initial
                    // settings load (guarded by _configReady).
                    if (_configReady) _configurationService.SaveAppSettings();
                    break;
            }
        };
        _udpService = udpService;
        _gpsService = gpsService;
        _fieldService = fieldService;
        _ntripService = ntripService;
        _displaySettings = displaySettings;
        _fieldStatistics = fieldStatistics;
        _simulatorService = simulatorService;
        _settingsService = settingsService;
        _mapService = mapService;
        _boundaryRecordingService = boundaryRecordingService;
        _boundaryBuilderService = boundaryBuilderService;
        _headlandBuilderService = headlandBuilderService;
        _configurationService = configurationService;
        // Refresh status-bar bindings whenever the active profile changes
        // (load / save / picker dialog) so labels like CurrentProfileName
        // re-render. The store updates correctly on its own, but bindings
        // through computed properties on this VM need an explicit notify.
        _configurationService.ProfileLoaded += (_, _) => { RaiseProfileNameChanged(); RecordJobRig(); };
        _configurationService.ProfileSaved += (_, _) => { RaiseProfileNameChanged(); RecordJobRig(); };
        _autoSteerService = autoSteerService;
        _smartWasService = smartWasService;
        _trackCopierService = trackCopierService;
        _moduleCommunicationService = moduleCommunicationService;
        _toolPositionService = toolPositionService;
        _coverageMapService = coverageMapService;
        _sectionControlService = sectionControlService;
        _ntripProfileService = ntripProfileService;
        _audioService = audioService;
        _elevationLogService = elevationLogService;
        _jobService = jobService;
        // Refresh the field/job pill + status strip whenever the active job
        // changes (created, resumed, suspended).
        _jobService.ActiveJobChanged += (_, job) =>
        {
            if (job != null) RecordJobRig();
            OnPropertyChanged(nameof(CurrentJobTaskName));
            OnPropertyChanged(nameof(CurrentFieldAndJobLabel));
            RaiseStatusStripChanged();
        };
        _gpsPipelineService = gpsPipelineService;
        _intents = intents;
        _appState = appState;

        // State.Field.Tracks is the SoT (projected to the web + read by guidance); it
        // mirrors SavedTracks, the working collection EVERY creation/management path
        // mutates. Subscribing to each session's list keeps them in lockstep for ALL paths — not just
        // load/delete, which used to update both by hand (so a freshly-drawn track never
        // reached ApplicationState and was invisible to the web + the Tracks manager).
        SavedTracks.CollectionChanged += MirrorTracksToState;

        // Live half of the aggregate module-status indicator (see the
        // ConfigStore.Connections subscription above for the configured-set
        // half). Only the four data-ok flags participate; IP and engaged flags
        // do not affect the colour.
        _appState.Connections.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Models.State.ConnectionState.IsGpsDataOk):
                case nameof(Models.State.ConnectionState.IsImuDataOk):
                case nameof(Models.State.ConnectionState.IsAutoSteerDataOk):
                case nameof(Models.State.ConnectionState.IsMachineDataOk):
                    RaiseModuleStatusKindChanged();
                    break;
            }
        };

        // Subscribe to events
        _gpsService.GpsDataUpdated += OnGpsDataUpdated;
        _autoSteerService.StateUpdated += OnAutoSteerStateUpdated;
        _autoSteerService.GuidanceLost += OnGuidanceLost;
        _autoSteerService.SensorKickout += OnSensorKickout;

        // The background GPS processing pipeline (started by the backend)
        _gpsPipelineService.CycleCompleted += OnGpsCycleCompleted;

        // PERF-05 Phase 2c #2. Unified 5 Hz status-display tick — the single
        // cadence for every top-status-bar bound MainViewModel property,
        // decoupled from every upstream source rate (GPS 10 Hz, control loop
        // 100 Hz, AutoSteer 100 Hz). 5 Hz (200 ms) is below the human
        // perception threshold for numeric text on a status bar and cuts the
        // PropertyChanged → Avalonia binding → TextLayout cascade for every
        // status value to the same predictable rate.
        //
        // Replaces Phase 2c #1's 10 Hz "display tick" — same architecture,
        // half the rate, and now also includes diagnostics like
        // GpsToPgnLatencyMs that AutoSteer was writing at 100 Hz.
        // See Plans/Ideas/perf_data/2026-05-20/ANALYSIS.md.
        _statusTickTimer = _timerFactory.Create();
        _statusTickTimer.Interval = TimeSpan.FromMilliseconds(200);
        _statusTickTimer.Tick += OnStatusTick;
        _udpService.ModuleConnectionChanged += OnModuleConnectionChanged;
        _ntripService.ConnectionStatusChanged += OnNtripConnectionChanged;
        _ntripService.RtcmDataReceived += OnRtcmDataReceived;
        _ntripService.Notice += OnNtripNotice;
        ConfigStore.Connections.PropertyChanged += OnNtripEnabledChanged;
        _fieldService.ActiveFieldChanged += OnActiveFieldChanged;
        FieldFullyLoaded += OnFieldFullyLoaded;
        _simulatorService.GpsDataUpdated += OnSimulatorGpsDataUpdated;
        _boundaryRecordingService.PointAdded += OnBoundaryPointAdded;
        _boundaryRecordingService.StateChanged += OnBoundaryStateChanged;
        _moduleCommunicationService.AutoSteerToggleRequested += OnAutoSteerToggleRequested;
        _moduleCommunicationService.SectionMasterToggleRequested += OnSectionMasterToggleRequested;
        _autoSteerService.RemoteSwitchesReceived += OnRemoteSwitchesReceived;
        _sectionControlService.SectionStateChanged += OnSectionStateChanged;
        _coverageMapService.BoundsExpanded += OnCoverageBoundsExpanded;

        // Build the initial section bar (rows + colors) from the seeded
        // NumSections; subsequent NumSections changes rebuild it.
        InitializeSectionBar();

        // Sync drift compensation to AutoSteerService when edited via TextBox
        State.Field.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(State.Field.DriftEasting) or nameof(State.Field.DriftNorthing))
            {
                _autoSteerService.SetDriftCompensation(State.Field.DriftEasting, State.Field.DriftNorthing);
            }
            else if (e.PropertyName == nameof(State.Field.FieldName))
            {
                // CurrentFieldName is a pass-through over State.Field.ActiveField.Name;
                // re-raise it (and the field/job label) when the active field changes.
                OnPropertyChanged(nameof(CurrentFieldName));
                OnPropertyChanged(nameof(CurrentFieldAndJobLabel));
            }
            else if (e.PropertyName == nameof(State.Field.LocalPlane))
            {
                // The cycle converts against the committed plane and runs the origin guard on
                // it; every writer (field open, the cycle's own commits above, field close)
                // lands here, so this is the one push.
                _gpsPipelineService.SetLocalPlane(State.Field.LocalPlane);
            }
            else if (e.PropertyName is nameof(State.Field.CurrentBoundary) or nameof(State.Field.HeadlandLine)
                     or nameof(State.Field.ActiveField))
            {
                SyncFieldContextToSectionControl();
            }
        };

        // Wire YouTurn state -> IsUTurnDistanceVisible computed property
        // so the right-panel distance widget shows during approach, not just mid-turn.
        WireYouTurnDistanceVisibility();

        // AutoSteer goes off when what it steers by changes under it (vehicle, implement,
        // GPS and roll settings, a profile load).
        WatchSettingsUnderAutoSteer();

        // Subscribe to ConfigurationStore changes to update NumSections
        _numSections = _configStore.NumSections;
        _configStore.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Models.Configuration.ConfigurationStore.NumSections))
            {
                NumSections = _configStore.NumSections;
            }
            else if (e.PropertyName == nameof(Models.Configuration.ConfigurationStore.IsMetric))
            {
                // Refresh all unit-dependent displays
                OnPropertyChanged(nameof(WorkedAreaDisplay));
                OnPropertyChanged(nameof(BoundaryAreaDisplay));
                OnPropertyChanged(nameof(WorkRateDisplay));
                OnPropertyChanged(nameof(SimulatorSpeedDisplay));
                OnPropertyChanged(nameof(SpeedLargeValue));
                RaiseStatusStripChanged();
            }
        };

        // Note: FPS subscription is set up in platform code (MainWindow.axaml.cs / MainView.axaml.cs)
        // since ViewModels cannot reference Views directly

        // Note: NOT subscribing to DisplaySettings events - using direct property access instead
        // to avoid threading issues

        // Note: Simulator coordinates are restored in RestoreSettings() from saved app settings
        // Default values only used if no settings exist (first run)

        _simulatorTimer = _timerFactory.Create();
        _simulatorTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30Hz — pipeline back-pressure skips if processing is slow
        _simulatorTimer.Tick += OnSimulatorTick;

        // Initialize commands (split into partial class files for organization)
        InitializeNavigationCommands();
        InitializeSimulatorCommands();
        InitializeConfigurationCommands();
        InitializeFieldCommands();
        InitializeBoundaryCommands();
        InitializeTrackCommands();
        InitializeTrackManagementCommands();
        InitializeRecordedPathCommands();
        InitializeNetworkIoCommands();
        InitializeWizardCommands();
        InitializeSettingsCommands();
        InitializeCoverageGuardCommands();

        // Load display settings first, then restore our app settings on top
        // This ensures AppSettings takes precedence over DisplaySettings
        // IMPORTANT: Run synchronously to ensure settings are loaded before any save can occur
        _displaySettings.LoadSettings();
        RestoreSettings();

        // Seed the web-UI field-tools mirror from the current VM values (some default
        // non-false, e.g. IsAutoTrackEnabled=true, and a few are restored above) — the
        // setters only push on change, so the projector would otherwise read stale zeros.
        State.FieldTools.IsHeadlandOn = _isHeadlandOn;
        _gpsPipelineService.SetHeadlandOn(_isHeadlandOn);
        _gpsPipelineService.SetSimulatorEnabled(_isSimulatorEnabled);
        SyncFieldContextToSectionControl();
        State.FieldTools.IsAutoTrackEnabled = ConfigStore.Display.AutoTrack; // persisted
        State.FieldTools.UTurnSkipRows = _uTurnSkipRows;
        State.FieldTools.IsUTurnSkipRowsEnabled = _isUTurnSkipRowsEnabled;

        // Settings are now loaded; subsequent ConnectionConfig changes are
        // user-driven and should persist (see the ConfigStore.Connections
        // subscription above).
        _configReady = true;
        _constructed = true;
    }

    /// <summary>
    /// The view model's own start, once the backend has built it (the constructor only
    /// subscribes) and started the services (<c>WebBackend.StartServices</c>): the battery
    /// reading, the map's pose pull and the status tick, the clock and day/night, and the module
    /// checks once the UDP port the backend is opening (<paramref name="udpStarted"/>) is open.
    /// Called once, on the thread that built it.
    /// </summary>
    public void Start(Task udpStarted)
    {
        _batteryStatus = _batteryService.CurrentStatus;
        _statusTickTimer.Start();

        // Initialize clock and auto day/night timer
        InitializeClock();
        InitializeAutoDayNight();

        // Start UDP communication (fire-and-forget but explicit)
        _ = InitializeAsync(udpStarted);

        // Diagnostic auto-resume field — lets the FPS test harness run field-open
        // scenarios across force-stop restarts without manual taps.
        if (DiagFlags.AutoResumeField)
        {
            _dispatcher.Post(() =>
            {
                if (ResumeFieldCommand?.CanExecute(null) == true)
                {
                    _logger.LogInformation("[DiagFlags] auto_resume_field: invoking ResumeFieldCommand");
                    ResumeFieldCommand.Execute(null);
                }
            }, UiDispatcherPriority.Background);
        }
    }

    private void RestoreSettings()
    {
        var settings = _settingsService.Settings;

        // Restore vehicle profile settings
        LoadDefaultVehicleProfile();

        // Load NTRIP profiles, then auto-connect the default caster at startup so
        // GPS gets RTCM corrections immediately (no wait when opening a field).
        _ = LoadProfilesThenAutoConnectAsync();

        // Restore legacy NTRIP settings (used if no profiles exist)
        NtripCasterAddress = settings.NtripCasterIp;
        NtripCasterPort = settings.NtripCasterPort;
        NtripMountPoint = settings.NtripMountPoint;
        NtripUsername = settings.NtripUsername;
        NtripPassword = settings.NtripPassword;

        // Auto-connect to NTRIP if configured (legacy behavior, profiles will override when field loads)
        if (settings.NtripAutoConnect && !string.IsNullOrEmpty(settings.NtripCasterIp))
        {
            _logger.LogInformation("NTRIP auto-connecting at startup (legacy settings)");
            _ = ConnectToNtripAsync(); // Fire and forget - don't await in RestoreSettings
        }

        // Restore UI state (through _displaySettings service)
        _displaySettings.IsGridOn = settings.GridVisible;

        // IMPORTANT: Notify bindings that IsGridOn changed
        // (setting _displaySettings directly doesn't trigger property change notification)
        OnPropertyChanged(nameof(IsGridOn));

        // Restore last camera follow mode (Map / NorthUp / HeadingUp) — state.
        if (PersistentState.CameraMode != CameraMode.Free)
            CameraMode = PersistentState.CameraMode;

        // Restore simulator position (state). Always restore coords regardless
        // of enabled state so map dialogs work at startup.
        _simulatorService.Initialize(new AgOpenWeb.Models.Wgs84(
            PersistentState.SimulatorLatitude,
            PersistentState.SimulatorLongitude));
        _simulatorService.StepDistance = PersistentState.SimulatorSpeed;

        Latitude = PersistentState.SimulatorLatitude;
        Longitude = PersistentState.SimulatorLongitude;

        _logger.LogDebug("Restored simulator: {Lat},{Lon}", PersistentState.SimulatorLatitude, PersistentState.SimulatorLongitude);

        // Restore simulator enabled state and panel visibility.
        // hide_all_panels diagnostic flag suppresses the auto-open so baseline
        // perf measurements aren't contaminated by the sim panel.
        IsSimulatorEnabled = settings.SimulatorEnabled;
        IsSimulatorPanelVisible = settings.SimulatorEnabled && !DiagFlags.HideAllPanels;

        // Initialize tool width from config so implement renders before GPS data flows
        var config = _configStore;
        double initialToolWidth = 0;
        for (int i = 0; i < config.NumSections && i < 16; i++)
            initialToolWidth += config.Tool.GetSectionWidth(i) / 100.0;
        if (initialToolWidth > 0.1)
            ToolWidth = initialToolWidth;

        // Surface any crash-recovery that happened while loading settings or
        // the active profile pair. Deferred to the dispatcher so the dialog
        // host is ready (RestoreSettings runs during construction).
        _dispatcher.Post(
            CheckStartupRecovery,
            UiDispatcherPriority.Background);
    }

    private void LoadDefaultVehicleProfile()
    {
        try
        {
            // One-time #346 migration: split any pre-v2 combined profiles
            // into paired v2 vehicle + tool files before the load below
            // tries to find them.
            if (_configurationService.MigrateV1ProfilesIfNeeded())
            {
                _logger.LogInformation("Migrated v1 vehicle profiles to v2 (split vehicle/tool)");
            }

            var imported = _configurationService.ImportAgOpenGpsProfiles();
            if (imported.Any)
            {
                _logger.LogInformation("Imported AgOpenGPS profiles: vehicles {Vehicles}, tools {Tools}",
                    string.Join(", ", imported.Vehicles), string.Join(", ", imported.Tools));
            }
            if (imported.Skipped.Count > 0)
            {
                _logger.LogWarning("AgOpenGPS profiles not imported (a profile of that name exists, or the file is unreadable): {Files}",
                    string.Join(", ", imported.Skipped));
            }

            var profiles = _configurationService.GetAvailableProfiles();
            if (profiles.Count == 0)
            {
                _logger.LogWarning("No vehicle profiles found in Vehicles directory");
                return;
            }

            // Try to load the last used profile first
            var lastUsedVehicle = _settingsService.Settings.LastUsedVehicleProfile;
            var lastUsedTool = _settingsService.Settings.LastUsedToolProfile;
            string vehicleToLoad;

            if (!string.IsNullOrEmpty(lastUsedVehicle) && profiles.Contains(lastUsedVehicle))
            {
                vehicleToLoad = lastUsedVehicle;
                _logger.LogDebug("Loading last used vehicle profile: {ProfileName}", vehicleToLoad);
            }
            else
            {
                // Fall back to first available profile
                vehicleToLoad = profiles[0];
                _logger.LogDebug("Loading first available vehicle profile: {ProfileName}", vehicleToLoad);
            }

            // If no tool was previously paired (fresh install / pre-#346
            // settings file), fall back to a same-named tool — that's the
            // post-migration default and matches what the picker will show
            // until the operator picks a different combo.
            var availableTools = _configurationService.GetAvailableToolProfiles();
            string toolToLoad;
            if (!string.IsNullOrEmpty(lastUsedTool) && availableTools.Contains(lastUsedTool))
                toolToLoad = lastUsedTool;
            else if (availableTools.Contains(vehicleToLoad))
                toolToLoad = vehicleToLoad;
            else if (availableTools.Count > 0)
                toolToLoad = availableTools[0];
            else
                toolToLoad = vehicleToLoad; // best-effort; LoadProfiles tolerates a missing tool file

            // LoadProfiles persists the chosen pair back to AppSettings, so
            // a same-name fallback here will overwrite an empty
            // LastUsedToolProfile with a real name on the next save.
            if (_configurationService.LoadProfiles(vehicleToLoad, toolToLoad))
            {
                var store = _configurationService.Store;
                _logger.LogInformation(
                    "Loaded vehicle profile: {Vehicle} / tool: {Tool}",
                    store.ActiveVehicleProfileName,
                    store.ActiveToolProfileName);
                _logger.LogDebug("  Tool width: {ToolWidth}m (from {NumSections} sections)", store.ActualToolWidth, store.NumSections);
                _logger.LogDebug("  YouTurn radius: {Radius}m", store.Guidance.UTurnRadius);
                _logger.LogDebug("  Wheelbase: {Wheelbase}m", store.Vehicle.Wheelbase);
                _logger.LogDebug("  Sections: {NumSections}", store.NumSections);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading vehicle profile");
        }
    }

    // NTRIP methods (ConnectToNtripAsync, DisconnectFromNtripAsync, HandleNtripProfileForFieldAsync) are in MainViewModel.Ntrip.cs

    private async Task InitializeAsync(Task udpStarted)
    {
        try
        {
            await udpStarted;
            NetworkStatus = $"UDP Connected: {_udpService.LocalIPAddress}";
            StatusMessage = "Ready - Waiting for modules...";

            // Start sending hello packets (fire-and-forget but explicit)
            _ = StartHelloTimerAsync();
        }
        catch (Exception ex)
        {
            NetworkStatus = $"UDP Error: {ex.Message}";
            ReportFailure("Network error");
        }
    }

    // Cancelled when the backend stops (StopBackgroundLoops): the loops below end at once
    // rather than keep the stopped backend alive from a pending delay.
    private readonly System.Threading.CancellationTokenSource _backendStopping = new();

    /// <summary>The backend is stopping: end the background loops (the module hello loop). The
    /// process may outlive the backend (Android), and a loop still waiting kept the whole
    /// backend alive (the working day harness's BACKEND-NOT-FREED-AFTER-EXIT).</summary>
    public void StopBackgroundLoops() => _backendStopping.Cancel();

    private async Task StartHelloTimerAsync()
    {
        try
        {
            int helloCounter = 0;
            while (_udpService.IsConnected && !_backendStopping.IsCancellationRequested)
            {
                // Send hello packet every second (every 10 ticks * 100ms)
                if (helloCounter++ % 10 == 0)
                {
                    _udpService.SendHelloPacket();
                }

                // Check module status using appropriate method for each:
                // - AutoSteer: Data flow (sends PGN 250/253 regularly)
                // - Machine: Hello reply, or its own PGN 237
                // - IMU: Hello only (only sends when active)
                // - GPS: Data flow (sends NMEA regularly)

                var steerOk = _udpService.IsModuleDataOk(ModuleType.AutoSteer);
                var machineOk = _udpService.IsModuleHelloOk(ModuleType.Machine)
                    || _udpService.IsModuleDataOk(ModuleType.Machine);
                var imuOk = _udpService.IsModuleHelloOk(ModuleType.IMU);
                var gpsOk = _gpsService.IsGpsDataOk();

                DropBenchChoiceWhenSteerModuleGoesSilent(steerOk);
                UpdateMachineModuleLost(machineOk);
                if (_isobus != null)
                {
                    var isobus = _isobus.Status;
                    UpdateIsobusLost(isobus);
                    UpdateIsobusSectionCount(isobus);
                }
                UpdateCorrectionsUnused();
                UpdateDataQuality();

                // Update centralized state (single source of truth)
                State.Connections.IsAutoSteerDataOk = steerOk;
                State.Connections.IsMachineDataOk = machineOk;
                State.Connections.IsImuDataOk = imuOk;
                State.Connections.IsGpsDataOk = gpsOk;
                State.Connections.IsGpsConnected = gpsOk;
                State.Connections.AutoSteerIpAddress = _udpService.GetModuleIpAddress(ModuleType.AutoSteer);
                State.Connections.MachineIpAddress = _udpService.GetModuleIpAddress(ModuleType.Machine);
                State.Connections.ImuIpAddress = _udpService.GetModuleIpAddress(ModuleType.IMU);
                // GPS IP: the sender of the position sentences (else a PGN 203 scan reply).
                // The subnet is only known after a scan reply.
                State.Connections.GpsIpAddress = _udpService.GetModuleIpAddress(ModuleType.GPS);
                State.Connections.ModuleSubnet = _udpService.GetModuleSubnet();
                State.Connections.ScanReplySubnet = _udpService.GetScanReplySubnet();

                // Legacy property updates (for existing bindings - will be removed in Phase 5)
                IsAutoSteerDataOk = steerOk;
                IsMachineDataOk = machineOk;
                IsImuDataOk = imuOk;
                IsGpsDataOk = gpsOk;

                UpdateGpsLost(gpsOk, Clock.Current.Now);

                if (!gpsOk)
                {
                    StatusMessage = "GPS Timeout";
                    FixQuality = "No Fix";
                }
                else
                {
                    UpdateStatusMessage();
                }

                await System.Threading.Tasks.Task.Delay(100, _backendStopping.Token); // Check every 100ms for fast response
            }
        }
        catch (OperationCanceledException) { /* the backend stopped */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HelloTimer error");
            ReportFailure("Module check error");
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// GPS-to-PGN pipeline latency in milliseconds.
    /// This is the critical latency from GPS receipt to steering PGN send.
    /// </summary>
    public double GpsToPgnLatencyMs
    {
        get => _gpsToPgnLatencyMs;
        set => SetProperty(ref _gpsToPgnLatencyMs, value);
    }

    public string NetworkStatus
    {
        get => _networkStatus;
        set => SetProperty(ref _networkStatus, value);
    }

    // Guidance/Steering properties
    public double CrossTrackError
    {
        get => _crossTrackError;
        set => SetProperty(ref _crossTrackError, value);
    }

    public bool IsAutoSteerActive
    {
        get => _isAutoSteerActive;
        set => SetProperty(ref _isAutoSteerActive, value);
    }

    // AutoSteer Hello and Data properties
    public bool IsAutoSteerHelloOk
    {
        get => _isAutoSteerHelloOk;
        set => SetProperty(ref _isAutoSteerHelloOk, value);
    }

    public bool IsAutoSteerDataOk
    {
        get => _isAutoSteerDataOk;
        set => SetProperty(ref _isAutoSteerDataOk, value);
    }

    // Right Navigation Panel Properties
    private bool _isContourModeOn;
    // IsManualSectionMode and IsSectionMasterOn are now computed from _sectionControlService.MasterState
    private bool _isAutoSteerAvailable;
    private bool _isAutoSteerEngaged;

    public bool IsContourModeOn
    {
        get => _isContourModeOn;
        set
        {
            if (SetProperty(ref _isContourModeOn, value))
            {
                State.Operation.IsContourOn = value; // mirror for web-UI projection
                _gpsPipelineService.SetContourMode(value); // #110
                // AutoSteer can engage on a contour line with no track (AgOpenGPS).
                IsAutoSteerAvailable = SelectedTrack != null || value;
            }
        }
    }

    private bool _showRecordedPaths;
    public bool ShowRecordedPaths
    {
        get => _showRecordedPaths;
        set
        {
            SetProperty(ref _showRecordedPaths, value);
            State.FieldTools.ShowRecordedPaths = value; // web map (#110)
            UpdateRecordedPathsOnMap();
        }
    }

    private bool _isRecordingContour;
    public bool IsRecordingContour
    {
        get => _isRecordingContour;
        set => SetProperty(ref _isRecordingContour, value);
    }

    private bool _isRecordingPath;
    public bool IsRecordingPath
    {
        get => _isRecordingPath;
        set => SetProperty(ref _isRecordingPath, value);
    }

    public ObservableCollection<Track> ContourStrips { get; } = new();
    public ObservableCollection<Track> RecordedPathTracks { get; } = new();

    // Button state tracking - these just track what the convenience buttons last did
    private bool _isManualAllOn;
    private bool _isAutoAllOn;

    public bool IsManualSectionMode
    {
        get => _isManualAllOn;
        set
        {
            if (SetProperty(ref _isManualAllOn, value))
            {
                State.Operation.IsSectionManualAll = value; // mirror for web-UI projection
                OnPropertyChanged(nameof(IsSectionBarVisible));
            }
        }
    }

    public bool IsSectionMasterOn
    {
        get => _isAutoAllOn;
        set
        {
            if (SetProperty(ref _isAutoAllOn, value))
            {
                State.Operation.IsSectionAutoMaster = value; // mirror for web-UI projection
                OnPropertyChanged(nameof(IsSectionBarVisible));
            }
        }
    }

    public bool IsAutoSteerAvailable
    {
        get => _isAutoSteerAvailable;
        set
        {
            SetProperty(ref _isAutoSteerAvailable, value);
            State.Operation.IsAutoSteerAvailable = value; // mirror for web-UI projection
            RaiseUTurnButtonVisibleChanged();
        }
    }

    public bool IsAutoSteerEngaged
    {
        get => _isAutoSteerEngaged;
        set
        {
            if (SetProperty(ref _isAutoSteerEngaged, value))
            {
                State.Operation.IsAutoSteerEngaged = value; // mirror for web-UI projection
                OnPropertyChanged(nameof(IsManualUTurnVisible));
            }
        }
    }

    // IsYouTurnEnabled is now in MainViewModel.YouTurn.cs

    // Machine Hello and Data properties
    public bool IsMachineHelloOk
    {
        get => _isMachineHelloOk;
        set => SetProperty(ref _isMachineHelloOk, value);
    }

    public bool IsMachineDataOk
    {
        get => _isMachineDataOk;
        set => SetProperty(ref _isMachineDataOk, value);
    }

    // IMU Hello and Data properties
    public bool IsImuHelloOk
    {
        get => _isImuHelloOk;
        set => SetProperty(ref _isImuHelloOk, value);
    }

    public bool IsImuDataOk
    {
        get => _isImuDataOk;
        set => SetProperty(ref _isImuDataOk, value);
    }

    // GPS Hello and Data properties (GPS doesn't have hello, just data from NMEA)
    public bool IsGpsDataOk
    {
        get => _isGpsDataOk;
        set => SetProperty(ref _isGpsDataOk, value);
    }

    // NTRIP properties are in MainViewModel.Ntrip.cs

    // Tool position properties (for map rendering)
    public double ToolEasting
    {
        get => _toolEasting;
        set => SetProperty(ref _toolEasting, value);
    }

    public double ToolNorthing
    {
        get => _toolNorthing;
        set => SetProperty(ref _toolNorthing, value);
    }

    public double ToolHeadingRadians
    {
        get => _toolHeading;
        set => SetProperty(ref _toolHeading, value);
    }

    public double ToolWidth
    {
        get => _toolWidth;
        set => SetProperty(ref _toolWidth, value);
    }

    public double HitchEasting
    {
        get => _hitchEasting;
        set => SetProperty(ref _hitchEasting, value);
    }

    public double HitchNorthing
    {
        get => _hitchNorthing;
        set => SetProperty(ref _hitchNorthing, value);
    }

    public bool IsToolPositionReady => _toolPositionService.IsToolPositionReady;

    // OnAutoSteerStateUpdated is now in MainViewModel.Guidance.cs

    // AutoSteer guidance state and event handlers
    // are now in MainViewModel.Guidance.cs

    // YouTurn methods (ProcessYouTurn, CreateYouTurnPath, CompleteYouTurn, etc.)
    // are now in MainViewModel.YouTurn.cs

    private void OnCoverageBoundsExpanded(object? sender, BoundsExpandedEventArgs e)
    {
        // Reinitialize display bitmap with new expanded bounds
        _dispatcher.Post(() =>
        {
            _mapService.InitializeCoverageBitmapWithBounds(e.MinE, e.MaxE, e.MinN, e.MaxN);
            _logger.LogDebug($"[Coverage] Display bitmap reinitialized for expanded bounds: E[{e.MinE:F0},{e.MaxE:F0}] N[{e.MinN:F0},{e.MaxN:F0}]");
        });
    }

    private void OnAutoSteerToggleRequested(object? sender, AutoSteerToggleEventArgs e)
    {
        void Toggle()
        {
            // The module arming for a steer-wizard Free Drive test isn't a request to steer:
            // ignore it quietly (ToggleAutoSteerCommand would refuse it anyway, #154).
            if (IsSteerWizardOpen && !IsAutoSteerEngaged)
                return;

            // Toggle autosteer when requested by module communication service
            // (e.g., from work switch or steer switch)
            ToggleAutoSteerCommand?.Execute(IsAutoSteerEngaged
                ? ModuleStoppedSteeringReason()
                : "steer switch or button on the steer module");
        }

        // UpdateModuleSwitches runs on the host loop, so toggle right away and report the
        // result: the steer switch → sections check that follows needs it.
        if (_dispatcher.CheckAccess())
        {
            Toggle();
            e.IsAutoSteerOnAfter = IsAutoSteerEngaged;
        }
        else
        {
            _dispatcher.Post(Toggle);
        }
    }

    private void OnSectionMasterToggleRequested(object? sender, SectionMasterToggleEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            // Toggle the section button the module communication service asked for
            // (replaces WinForms PerformClick on btnSectionMasterAuto / Manual).
            if (e.Button == SectionMasterToggleEventArgs.SectionMasterButton.Manual)
                ToggleManualModeCommand?.Execute(null);
            else
                ToggleSectionMasterCommand?.Execute(null);
        });
    }

    /// <summary>
    /// Module switches (#106): feed PGN 253's switch bits to the module communication service
    /// each GPS cycle, as AgOpenGPS does each frame (CModuleComm.CheckWorkAndSteerSwitch).
    /// <list type="bullet">
    /// <item>Work / steer switch → section buttons (Tool config switches).</item>
    /// <item>Steer switch / button engages and disengages AutoSteer when AutoSteer config →
    /// External enable is Switch or Button (default None). The module reports its steering
    /// state in the steer-switch bit — set on tablet engage, cleared by the physical switch,
    /// the button, or a sensor kickout — so the UI follows it both ways.</item>
    /// </list>
    /// Nothing called this before, so all of it was inert.
    /// </summary>
    private void UpdateModuleSwitches()
    {
        AssertHostLoop();
        UpdateModuleSteeringState();
        var tool = ConfigStore.Tool;
        var mc = _moduleCommunicationService;
        mc.IsRemoteWorkSystemOn = tool.IsWorkSwitchEnabled || tool.IsSteerSwitchEnabled;
        bool moduleEngage = ConfigStore.AutoSteer.ExternalEnable != 0;
        if (!mc.IsRemoteWorkSystemOn && !moduleEngage) return;

        var sd = _autoSteerService.LastSteerData;
        // Raw PGN 253 byte 11, as AgOpenGPS reads it: bit 0 work switch, bit 1 steer switch.
        // (The parser stores bit 0 inverted as WorkSwitchActive.)
        mc.WorkSwitchHigh = !sd.WorkSwitchActive;
        mc.SteerSwitchHigh = sd.SteerSwitchActive;
        mc.CheckSwitches(new ModuleSwitchState
        {
            IsAutoSteerOn = IsAutoSteerEngaged,
            IsAutoSteerAuto = moduleEngage,
            AutoButtonState = IsSectionMasterOn ? ButtonStates.Auto : ButtonStates.Off,
            ManualButtonState = IsManualSectionMode ? ButtonStates.On : ButtonStates.Off,
        });
    }

    // True once the module has reported steering since this engage, so the alert is for
    // a real stop (kickout, switch, button), not the moment between engage and arming.
    private bool _moduleSteeredSinceEngage;

    /// <summary>
    /// Module steering state for the web (#126). The steer module reports in PGN 253
    /// byte 11 bit 1 whether it is steering; AgOpenGPS paints the steer circle red when
    /// it isn't, whatever the switch type. Here: engaged + module connected + bit high =
    /// "module not steering". With a Switch/Button configured the bit also disengages
    /// (UpdateModuleSwitches, #124); with None it only shows, as in AgOpenGPS.
    /// Not on the simulator, unless the operator let it move the steering: the module is not
    /// asked to steer then (VehicleState.SimulatorHoldsSteering), so "not steering" is what it
    /// should say, and no alert.
    /// </summary>
    internal void UpdateModuleSteeringState()
    {
        AssertHostLoop();
        bool held = _isSimulatorEnabled && !_isSimulatorDrivingSteering;
        bool connected = State.Connections.IsAutoSteerDataOk && !held;
        bool notSteering = IsAutoSteerEngaged && connected && _autoSteerService.LastSteerData.SteerSwitchActive;
        State.Connections.IsModuleNotSteering = notSteering;

        if (!IsAutoSteerEngaged || !connected) { _moduleSteeredSinceEngage = false; return; }
        if (!notSteering) { _moduleSteeredSinceEngage = true; return; }
        if (_moduleSteeredSinceEngage)
        {
            _moduleSteeredSinceEngage = false;
            _audioService.Play(Services.Interfaces.SoundEffect.AutoSteerOff);
            // With a Switch or Button set, UpdateModuleSwitches turns AutoSteer off next and
            // logs why; with None it stays on, so say so here.
            if (ConfigStore.AutoSteer.ExternalEnable == 0)
                _logger.LogInformation("[AutoSteer] {Reason}; AutoSteer stays on (steer switch / button: None)",
                    ModuleStoppedSteeringReason());
            ReportFailure("Steer module stopped steering (kickout, switch or button)");
        }
    }

    private void OnModuleConnectionChanged(object? sender, ModuleConnectionEventArgs e)
    {
        // This event is no longer used - status is polled every 100ms
    }

    // NTRIP event handlers are in MainViewModel.Ntrip.cs

    private void UpdateStatusMessage()
    {
        int connectedCount = 0;
        if (IsAutoSteerDataOk) connectedCount++;
        if (IsMachineDataOk) connectedCount++;
        if (IsImuDataOk) connectedCount++;

        StatusMessage = connectedCount > 0
            ? $"{connectedCount} module(s) active"
            : "Waiting for modules...";
    }

    // Field management properties
    // Active field — single home is State.Field.ActiveField (§12.1). Pass-through.
    public Field? ActiveField
    {
        get => State.Field.ActiveField;
        set
        {
            if (!ReferenceEquals(State.Field.ActiveField, value))
            {
                State.Field.ActiveField = value;
                OnPropertyChanged();
            }
        }
    }

    public string FieldsRootDirectory
    {
        get => _fieldsRootDirectory;
        set => SetProperty(ref _fieldsRootDirectory, value);
    }

    public string? ActiveFieldName => ActiveField?.Name;
    public bool HasActiveField => ActiveField != null;

    // Services exposed for UI/control access
    public AgOpenWeb.Services.Interfaces.IFieldStatisticsService FieldStatistics => _fieldStatistics;

    // Field statistics properties for UI binding
    public string WorkedAreaDisplay => FormatArea(_coverageMapService.TotalWorkedArea);

    public string BoundaryAreaDisplay
    {
        get
        {
            double areaSqM = FieldBoundaryAreaSqM;
            if (areaSqM > 0)
            {
                return FormatArea(areaSqM);
            }
            return ConfigStore.IsMetric ? "0.00 ha" : "0.00 ac";
        }
    }

    public double RemainingPercent
    {
        get
        {
            double fieldArea = FieldBoundaryAreaSqM;
            if (fieldArea > 0)
            {
                // AgOpenGPS CFieldData.WorkedAreaRemainPercentage: the field less its inner boundaries,
                // not the headland (the worked area counts the headland passes too). Never under 0, as
                // the field data card's Remaining (ours: AgOpenGPS goes below 0 with overlap).
                double workedArea = _coverageMapService.TotalWorkedArea;
                return Math.Max(0, (fieldArea - workedArea) * 100 / fieldArea);
            }
            return 100;
        }
    }

    // Instantaneous work rate based on current speed and tool width
    public string WorkRateDisplay
    {
        get
        {
            // Rate = Speed (m/h) × Tool Width (m) = m²/h
            double speedMetersPerHour = Speed * 3600; // m/s to m/h
            double squareMetersPerHour = speedMetersPerHour * ToolWidth;
            double hectaresPerHour = squareMetersPerHour / 10000.0;

            if (ConfigStore.IsMetric)
                return $"{hectaresPerHour:F1} ha/hr";
            else
                return $"{hectaresPerHour * 2.47105:F1} ac/hr";
        }
    }

    /// <summary>Overlap percent from the field-statistics service.</summary>
    public double OverlapPercent => _fieldStatistics.OverlapPercent;

    // Helper method to format area with metric/imperial support
    private string FormatArea(double squareMeters)
    {
        double hectares = squareMeters * 0.0001;
        if (ConfigStore.IsMetric)
            return $"{hectares:F2} ha";
        else
            return $"{hectares * 2.47105:F2} ac";
    }

    /// <summary>
    /// Called when coverage is updated to refresh UI statistics
    /// </summary>
    public void RefreshCoverageStatistics()
    {
        OnPropertyChanged(nameof(WorkedAreaDisplay));
        OnPropertyChanged(nameof(RemainingPercent));
        OnPropertyChanged(nameof(WorkRateDisplay));
        OnPropertyChanged(nameof(OverlapPercent));
        RaiseStatusStripChanged();
    }

    /// <summary>
    /// Called after a field is fully loaded. Centers camera and refreshes dependent panels.
    /// </summary>
    private void OnFieldFullyLoaded(Field? field)
    {
        // Center camera on field
        if (CameraMode == Models.CameraMode.Free)
            CameraMode = _previousCameraMode;

        if (field?.Boundary != null)
            CenterMapOnBoundary(field.Boundary);
        else
            _mapService.PanTo(State.Vehicle.Easting, State.Vehicle.Northing);

        _mapService.SetVehiclePosition(
            State.Vehicle.Easting, State.Vehicle.Northing,
            State.Vehicle.Heading * Math.PI / 180.0);
    }

    private void OnActiveFieldChanged(object? sender, Field? field)
    {
        // This event is now only used for state synchronization, not for save/load
        // Save/load is handled by OpenFieldAsync and CloseFieldAsync
        State.Field.ActiveField = field;
        ActiveField = field;
        OnPropertyChanged(nameof(ActiveFieldName));
        OnPropertyChanged(nameof(HasActiveField));
        RaiseStatusStripChanged();
    }

    // Pending intents consumed by the next OpenFieldAsync. Set by the
    // OpenField*Async overloads below; cleared on consumption. See the
    // JobService block inside OpenFieldAsync for why this exists
    // (race with CloseFieldAsync's coverage save).
    private (string FieldName, string WorkType, string Notes, string? TaskName)? _pendingNewJob;
    private (string FieldName, string TaskName)? _pendingResumeJob;
    private bool _pendingFieldOnly;

    /// <summary>
    /// Open a field and create a brand-new job inside it. Coverage from
    /// the previous active job is correctly saved to that previous job's
    /// folder before the new job is created.
    /// </summary>
    public Task OpenFieldStartingNewJobAsync(
        string fieldPath, string fieldName, string workType, string notes, string? taskName = null)
    {
        _pendingNewJob = (fieldName, workType, notes, taskName);
        _pendingResumeJob = null;
        _pendingFieldOnly = false;
        return OpenFieldAsync(fieldPath, fieldName);
    }

    /// <summary>
    /// Open a field and resume an existing job inside it.
    /// </summary>
    public Task OpenFieldResumingJobAsync(string fieldPath, string fieldName, string taskName)
    {
        _pendingResumeJob = (fieldName, taskName);
        _pendingNewJob = null;
        _pendingFieldOnly = false;
        return OpenFieldAsync(fieldPath, fieldName);
    }

    /// <summary>
    /// Open a field's geometry without activating any job (Decision #2).
    /// Coverage is not loaded; section paint is silently dropped at close
    /// because <see cref="IJobService.ActiveJob"/> stays null.
    /// </summary>
    public Task OpenFieldOnlyAsync(string fieldPath, string fieldName)
    {
        _pendingFieldOnly = true;
        _pendingNewJob = null;
        _pendingResumeJob = null;
        return OpenFieldAsync(fieldPath, fieldName);
    }

    /// <summary>
    /// Opens a field from the specified path. This is the single entry point for all field opening.
    /// Handles: closing previous field, loading boundary, background, coverage, tracks, headland.
    /// Leaving a field worked with no job asks first, as Close Field does: opening another field
    /// (from the list, Drive In, New, From Existing, KML, ISO-XML) used to drop that coverage
    /// without a word (checklist 7.9). The open runs from the prompt's Save or Discard.
    /// </summary>
    public async Task OpenFieldAsync(string fieldPath, string fieldName)
    {
        if (TryShowUnsavedCoverageGuard(() => _ = OpenFieldCoreAsync(fieldPath, fieldName)))
            return;
        await OpenFieldCoreAsync(fieldPath, fieldName);
    }

    private async Task OpenFieldCoreAsync(string fieldPath, string fieldName)
    {
        _logger.LogDebug($"[Field] OpenFieldAsync: {fieldName} at {fieldPath}");

        // Close current field first (saves coverage, clears state)
        await CloseFieldAsync();

        // Wrap any pre-#349 coverage into a synthetic imported job. Idempotent;
        // a no-op if the field already has a jobs/ folder.
        try
        {
            if (LegacyFieldMigrationService.MigrateIfNeeded(fieldPath))
                _logger.LogDebug("[Job] Migrated legacy coverage for '{FieldName}'", fieldName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Job] Legacy migration failed for '{FieldName}'", fieldName);
        }

        // Show busy overlay for loading
        State.UI.BusyMessage = "Loading field...";
        State.UI.IsBusy = true;
        _logger.LogDebug("[Busy] OpenFieldAsync: Loading field '{FieldName}'", fieldName);

        try
        {
            var opening = new FieldOpening(fieldPath, fieldName);
            foreach (var step in OpenSteps)
                await step.Run(opening);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Field] Error opening field: {fieldName}");
            ReportFailure($"Failed to open field: {ex.Message}");
        }
        finally
        {
            State.UI.IsBusy = false;
            _logger.LogDebug("[Busy] OpenFieldAsync: Complete");
        }
    }

    /// <summary>The field's own files, as a close writes them: tram systems and settings, the
    /// elevation log, the tracks and field.geojson. Coverage is the caller's (it needs the job).</summary>
    private void SaveFieldFiles(Field field)
    {
        if (ConfigStore.Tram.Systems.Count > 0)
        {
            Services.Tram.TramSystemFileService.Save(field.DirectoryPath, ConfigStore.Tram.Systems);
            _logger.LogDebug($"[Tram] Saved {ConfigStore.Tram.Systems.Count} tram systems");
        }
        Services.Tram.TramConfigFileService.Save(field.DirectoryPath, ConfigStore.Tram);
        _elevationLogService.Flush(field.DirectoryPath);
        SaveTracksToFile();
        _fieldService.SaveField(field);
    }

    /// <summary>
    /// Save the vehicle and tool profiles in use, then the open field as a close would, without
    /// closing it: when the app stops or goes to the background. Settings take effect as they
    /// are typed and reach the profile only with Save Profile or a switch to another profile, so
    /// closing the app lost them; AgOpenGPS saves its vehicle and tool settings when it closes
    /// (FormGPS_FormClosing). The active job's coverage goes to its job folder. Coverage painted with
    /// no job goes to a new job: nobody is there to answer the unsaved-coverage question, and
    /// dropping it loses the work. Callable from any thread: it runs on the host loop and waits
    /// up to <paramref name="timeout"/>. Returns false if it didn't finish in time or failed.
    /// </summary>
    public bool SaveOpenFieldForExit(TimeSpan timeout)
    {
        try
        {
            if (_dispatcher.CheckAccess()) { SaveOpenFieldNow(); return true; }
            return _dispatcher.InvokeAsync(SaveOpenFieldNow).Wait(timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Field] Saving the open field at exit failed");
            return false;
        }
    }

    private void SaveOpenFieldNow()
    {
        SaveActiveProfilesForExit();
        var field = ActiveField;
        if (field == null || string.IsNullOrEmpty(field.DirectoryPath)) return;
        if (_jobService.ActiveJob == null && _coverageMapService.PatchCount > 0)
        {
            var job = _jobService.CreateJob(field.Name, workType: string.Empty, notes: string.Empty);
            _logger.LogInformation("[Field] Exit: saved coverage worked with no job to a new job '{TaskName}'", job.TaskName);
        }
        if (_jobService.ActiveJob is { } active)
            _coverageMapService.SaveToFile(field.DirectoryPath, active.TaskName);
        SaveFieldFiles(field);
        _logger.LogDebug("[Field] Saved '{FieldName}' at exit", field.Name);
    }

    private void SaveActiveProfilesForExit()
    {
        var vehicle = ConfigStore.ActiveVehicleProfileName;
        var tool = ConfigStore.ActiveToolProfileName;
        if (string.IsNullOrEmpty(vehicle) || string.IsNullOrEmpty(tool)) return; // none loaded
        try
        {
            _configurationService.SaveProfiles(vehicle, tool);
            _logger.LogDebug("[Profiles] Saved '{Vehicle}' / '{Tool}' at exit", vehicle, tool);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Profiles] Saving the profiles in use at exit failed");
        }
    }

    /// <summary>
    /// Closes the current field. This is the single entry point for all field closing.
    /// Handles: saving coverage, saving tracks, clearing all field state.
    /// </summary>
    public async Task CloseFieldAsync()
    {
        // Stop the autosave timer first so a tick can't fire mid-close
        // and race the explicit close-save below. ClearFieldState also
        // stops it, but stopping here covers both branches without
        // depending on call order.
        StopCoverageAutosave();

        if (ActiveField == null || string.IsNullOrEmpty(ActiveField.DirectoryPath))
        {
            // No field to close, just clear state
            ClearFieldState();
            return;
        }

        _logger.LogDebug($"[Field] CloseFieldAsync: {ActiveField.Name}");

        // Show busy overlay for saving
        State.UI.BusyMessage = "Saving field...";
        State.UI.IsBusy = true;
        _logger.LogDebug("[Busy] CloseFieldAsync: Saving field '{FieldName}'", ActiveField.Name);

        try
        {
            // Force UI to render busy overlay
            await _dispatcher.InvokeAsync(() => { }, UiDispatcherPriority.Render);
            await Task.Delay(50);

            // Save coverage on background thread (RLE compression can take seconds).
            // Coverage is keyed by the active job's task name; if no job is
            // active (field-only open) the save is skipped.
            var savePath = ActiveField.DirectoryPath;
            var activeJob = _jobService.ActiveJob;
            if (activeJob != null)
            {
                var taskName = activeJob.TaskName;
                await Task.Run(() => _coverageMapService.SaveToFile(savePath, taskName));
                _logger.LogDebug($"[Coverage] Saved coverage to {savePath} job={taskName}");
            }
            else
            {
                _logger.LogDebug("[Coverage] No active job; skipping coverage save (field-only open)");
            }

            SaveFieldFiles(ActiveField);
            _elevationLogService.Clear();

            // Suspend rather than close. M2 has no explicit "Close Job"
            // operator action — closing a field should leave the job
            // in-progress so the next open of the same field resumes it.
            // The dialog UI in M3+ will surface explicit close/done.
            _jobService.SuspendCurrentJob();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Field] Error saving field: {ActiveField.Name}");
        }
        finally
        {
            State.UI.IsBusy = false;
            _logger.LogDebug("[Busy] CloseFieldAsync: Complete");
        }

        // Clear all field state
        ClearFieldState();
    }

    /// <summary>
    /// Clears all field-related state without saving.
    /// </summary>
    private void ClearFieldState()
    {
        foreach (var step in CloseSteps)
            step.Run();
    }

    /// <summary>
    /// Load headland line from field directory
    /// </summary>
    private void LoadHeadlandFromField(Field? field)
    {
        if (field == null || string.IsNullOrEmpty(field.DirectoryPath))
        {
            // Clear headland if no field - update centralized state.
            // All in-memory headland state must be wiped here, otherwise it
            // leaks across field-switch boundaries (e.g. close field A,
            // create field B → field A's headland reappears on B because
            // segments / preview were never cleared).
            State.Field.HeadlandLine = null;
            State.Field.HeadlandDistance = 0;
            HeadlandDistance = DefaultHeadlandDistance; // the builder starts afresh, not from the last field's (#965)

            State.Field.HeadlandLine = null;
            _mapService.SetHeadlandLine(null);
            _mapService.SetHeadlandVisible(false);
            // The segments were the session's and went with it.
            HeadlandPreviewLine = null; // setter pushes null to map service

            HasHeadland = false;
            IsHeadlandOn = false;
            OnPropertyChanged(nameof(CurrentHeadlandLine));
            OnPropertyChanged(nameof(CurrentHeadlandLineForPreview));
            return;
        }

        try
        {
            var headlandLine = Services.GeoJson.GeoJsonFieldService.LoadHeadlandLine(field.DirectoryPath);

            if (headlandLine.Tracks.Count > 0 && headlandLine.Tracks[0].TrackPoints.Count > 0)
            {
                // Update centralized state
                State.Field.HeadlandLine = headlandLine.Tracks[0].TrackPoints;
                State.Field.HeadlandDistance = headlandLine.Tracks[0].MoveDistance;

                // Use direct field assignment to avoid triggering save
                State.Field.HeadlandLine = headlandLine.Tracks[0].TrackPoints;
                _mapService.SetHeadlandLine(State.Field.HeadlandLine);
                OnPropertyChanged(nameof(CurrentHeadlandLine));

                HasHeadland = true;
                IsHeadlandOn = true;
                HeadlandDistance = headlandLine.Tracks[0].MoveDistance;

                _logger.LogDebug($"[Headland] Loaded headland from {field.DirectoryPath} ({State.Field.HeadlandLine.Count} points)");
            }
            else
            {
                State.Field.HeadlandLine = null;
                State.Field.HeadlandDistance = 0;
                HeadlandDistance = DefaultHeadlandDistance;

                State.Field.HeadlandLine = null;
                _mapService.SetHeadlandLine(null);
                HasHeadland = false;
                IsHeadlandOn = false;
                _logger.LogDebug($"[Headland] No headland found in {field.DirectoryPath}");
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[Headland] Failed to load headland: {ex.Message}");
            State.Field.HeadlandLine = null;
            State.Field.HeadlandDistance = 0;
            HeadlandDistance = DefaultHeadlandDistance; // the builder starts afresh, not from the last field's (#965)

            State.Field.HeadlandLine = null;
            _mapService.SetHeadlandLine(null);
            HasHeadland = false;
            IsHeadlandOn = false;
        }

        // Load headland segments
        try
        {
            var segments = Services.Headland.HeadlandSegmentFileService.Load(field.DirectoryPath);
            _sessionData.Headland.Clear();
            foreach (var seg in segments)
            {
                // Recompute offsets with current algorithm (may differ from saved)
                ComputeSegmentOffset(seg);
                _sessionData.Headland.Add(seg);
            }
            if (HeadlandSegments.Count > 0)
                BuildHeadlandFromSegments();
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[Headland] Failed to load headland segments: {ex.Message}");
        }
    }

    // Field Selection Dialog properties (visibility managed by State.UI)
    public ObservableCollection<FieldSelectionItem> AvailableFields { get; } = new();

    // AB Line Creation Mode state (dialog visibility managed by State.UI)
    private ABCreationMode _currentABCreationMode = ABCreationMode.None;
    public ABCreationMode CurrentABCreationMode
    {
        get => _currentABCreationMode;
        set
        {
            SetProperty(ref _currentABCreationMode, value);
            OnPropertyChanged(nameof(EnableABClickSelection));
            OnPropertyChanged(nameof(ABCreationInstructions));
        }
    }

    private ABPointStep _currentABPointStep = ABPointStep.None;
    public ABPointStep CurrentABPointStep
    {
        get => _currentABPointStep;
        set
        {
            SetProperty(ref _currentABPointStep, value);
            OnPropertyChanged(nameof(ABCreationInstructions));
        }
    }

    // Temporary storage for Point A during AB creation
    private Position? _pendingPointA;
    public Position? PendingPointA
    {
        get => _pendingPointA;
        set => SetProperty(ref _pendingPointA, value);
    }

    // Curve recording state (drive mode)
    private List<Vec3> _recordedCurvePoints = new();
    private Vec3? _lastCurvePoint;
    private const double CurveMinPointSpacing = 1.0; // a point a metre while driving a curve (AgOpenGPS: every fix)

    /// <summary>
    /// Whether curve recording is currently active
    /// </summary>
    public bool IsRecordingCurve => CurrentABCreationMode == ABCreationMode.Curve;

    /// <summary>
    /// Number of points recorded in current curve
    /// </summary>
    public int RecordedCurvePointCount => _recordedCurvePoints.Count;

    // Curve drawing state (tap mode)
    private List<Vec3> _drawnCurvePoints = new();

    /// <summary>
    /// Whether curve drawing is currently active
    /// </summary>
    public bool IsDrawingCurve => CurrentABCreationMode == ABCreationMode.DrawCurve;

    /// <summary>
    /// Number of points drawn in current curve
    /// </summary>
    public int DrawnCurvePointCount => _drawnCurvePoints.Count;

    public bool EnableABClickSelection => CurrentABCreationMode == ABCreationMode.DrawAB ||
                                          CurrentABCreationMode == ABCreationMode.DriveAB ||
                                          CurrentABCreationMode == ABCreationMode.Curve ||
                                          CurrentABCreationMode == ABCreationMode.DrawCurve ||
                                          _isPlaceFlagOnClickMode;

    private bool _isPlaceFlagOnClickMode;

    /// <summary>
    /// Place a flag at the given world coordinates (from map click).
    /// </summary>
    public void PlaceFlagAtWorldPosition(double easting, double northing, FlagColor? color = null)
    {
        if (!FieldIsOpen()) return;
        var flag = _sessionData.Flags.Add(easting, northing, color);
        var actualColor = flag.FlagColor;
        UpdateFlagsOnMap();
        StatusMessage = $"{actualColor} flag '{flag.Name}' at E:{easting:F1} N:{northing:F1}";

    }

    public string ABCreationInstructions
    {
        get
        {
            return (CurrentABCreationMode, CurrentABPointStep) switch
            {
                (ABCreationMode.DriveAB, ABPointStep.SettingPointA) => "Tap screen to set Point A at current position",
                (ABCreationMode.DriveAB, ABPointStep.SettingPointB) => "Drive to B, then tap screen to set Point B",
                (ABCreationMode.DrawAB, ABPointStep.SettingPointA) => "Tap on map to place Point A",
                (ABCreationMode.DrawAB, ABPointStep.SettingPointB) => "Tap on map to place Point B",
                (ABCreationMode.Curve, _) => $"RECORDING: Drive along curve ({RecordedCurvePointCount} pts) - Tap screen when done",
                (ABCreationMode.DrawCurve, _) => $"DRAWING: Tap to add points ({DrawnCurvePointCount} pts) - Tap Finish when done",
                _ => string.Empty
            };
        }
    }

    // Tracks Dialog data properties
    // The field's tracks are the session's.
    public ObservableCollection<Track> SavedTracks => _sessionData.Tracks;

    /// <summary>State.Field.Tracks follows the session's track list (subscribed to each new one).</summary>
    private void MirrorTracksToState(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var dst = _appState.Field.Tracks;
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                for (int i = 0; i < e.NewItems!.Count; i++)
                    dst.Insert(e.NewStartingIndex + i, (Track)e.NewItems[i]!);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                foreach (var t in e.OldItems!) dst.Remove((Track)t!);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                foreach (var t in e.OldItems!) dst.Remove((Track)t!);
                for (int i = 0; i < e.NewItems!.Count; i++)
                    dst.Insert(e.NewStartingIndex + i, (Track)e.NewItems[i]!);
                break;
            default: // Reset (Clear) / Move → rebuild from scratch
                dst.Clear();
                foreach (var t in SavedTracks) dst.Add(t);
                break;
        }
    }

    private Track? _selectedTrack;
    public Track? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            var oldValue = _selectedTrack;
            SetProperty(ref _selectedTrack, value);
            if (!ReferenceEquals(oldValue, value))
            {
                // Sync IsActive state with selection
                if (oldValue != null)
                {
                    DiscardPendingRefNudge(oldValue); // an unsaved Ref Nudge move doesn't follow to the next line (#489)
                    DiscardPendingSmooth(oldValue);   // nor a curve's unsaved smoothing (#929)
                    oldValue.IsActive = false;
                }
                if (value != null)
                {
                    value.IsActive = true;
                    State.Field.ActiveTrack = value;
                    // Show the track on the map when activated
                    _mapService.SetActiveTrack(value);

                    // Generate tram lines from the selected track. A field open restores the
                    // track before its tram systems are loaded and builds the lines once after.
                    if (!_restoringTrack) UpdateTramLines(value);

                    // Phase D D6: compute the initial pass number + nudge offset
                    // locally from the track's persisted NudgeDistance (inverse of
                    // NudgeDistance = widthMinusOverlap * pathsAway + nudgeOffset).
                    // The cycle is the sole writer of State.Guidance post-D3, so
                    // we don't touch it here — the values flow into _guidanceWorking
                    // via SyncGuidanceStateToPipeline's SetActiveTrack below, and
                    // the next cycle's snapshot mirrors them back to State.Guidance.
                    double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
                    if (widthMinusOverlap > 0.1)
                    {
                        _pendingInitialPathsAway = (int)Math.Round(value.NudgeDistance / widthMinusOverlap);
                        _pendingInitialNudgeOffset = value.NudgeDistance - (_pendingInitialPathsAway.Value * widthMinusOverlap);
                        _logger.LogDebug($"[NUDGE] SelectedTrack setter: '{value.Name}' NudgeDistance={value.NudgeDistance:F2}m -> pathsAway={_pendingInitialPathsAway}, nudgeOffset={_pendingInitialNudgeOffset:F3}m");
                    }
                    else
                    {
                        _pendingInitialPathsAway = 0;
                        _pendingInitialNudgeOffset = 0;
                        _logger.LogDebug("[NUDGE] SelectedTrack setter: '{TrackName}' widthMinusOverlap too small, pathsAway=0", value.Name);
                    }

                    // For curved tracks, calculate and display max inward passes
                    if (value.Points.Count > 2)
                    {
                        // widthMinusOverlap already calculated above
                        double minRadius = CurveProcessing.CalculateMinRadiusOfCurvature(value.Points);
                        int maxPasses = CurveProcessing.CalculateMaxInwardPasses(value.Points, widthMinusOverlap);

                        if (maxPasses < 50) // Only show warning for reasonably tight curves
                        {
                            StatusMessage = $"Curve selected: min radius {minRadius:F1}m, max ~{maxPasses} inward passes";
                            _logger.LogInformation("Curve '{Name}' selected: min radius {Radius:F1}m, max inward passes ~{Max}",
                                value.Name, minRadius, maxPasses);
                        }
                    }
                }
                else
                {
                    State.Field.ActiveTrack = null;
                    // Clear the track and guidance from the map when deactivated
                    _mapService.SetActiveTrack(null);
                    _mapService.SetBaseTrack(null);
                    // The pipeline sync below clears its track, so the next cycle's snapshot
                    // carries a null DisplayTrack and the mirror clears State.Guidance.DisplayLine
                    // (the web's magenta offset line) — its cache is left alone so that change is seen.
                    _lastMirroredBaseTrack = null;
                    _mapService.SetGuidancePoints(0, 0, false);
                    // Clear any U-turn state associated with the deactivated track
                    ClearYouTurnState();
                }

                // Update guidance availability
                HasActiveTrack = value != null;
                IsAutoSteerAvailable = value != null || IsContourModeOn;
                SaveLastUsedTrack(); // reopen the field on the same track (#148)

                // No U-turns on a closed/polygon track — turn the toggle off and
                // refresh the dependent UI (button visibility) (#421).
                if (IsActiveTrackClosed && IsYouTurnEnabled)
                    IsYouTurnEnabled = false;
                OnPropertyChanged(nameof(IsActiveTrackClosed));
                OnPropertyChanged(nameof(IsManualUTurnVisible));
                RaiseUTurnButtonVisibleChanged();
                RaiseStatusStripChanged();

                // Sync to pipeline so guidance computes on background thread
                SyncGuidanceStateToPipeline();

                _logger.LogDebug($"[SelectedTrack] Changed to: {value?.Name ?? "None"}");
            }
        }
    }

    /// <summary>
    /// Generate tram lines from a track and update the map.
    /// </summary>
    private void UpdateTramLines(Track? track)
    {
        var config = _configStore.Tram;

        // Set boundary fence for clipping tram lines
        if (State.Field.CurrentBoundary?.OuterBoundary?.Points != null && State.Field.CurrentBoundary.OuterBoundary.Points.Count >= 3)
        {
            var fencePts = State.Field.CurrentBoundary.OuterBoundary.Points
                .Select(p => new Models.Base.Vec3(p.Easting, p.Northing, p.Heading)).ToList();
            _tramLineService.SetBoundaryFence(fencePts);
        }
        else
        {
            _tramLineService.SetBoundaryFence(null); // a deleted boundary no longer clips the lines
        }

        double fieldWidth = 500;
        if (State.Field.CurrentBoundary?.OuterBoundary?.Points != null && State.Field.CurrentBoundary.OuterBoundary.Points.Count > 0)
        {
            var pts = State.Field.CurrentBoundary.OuterBoundary.Points;
            double maxE = pts.Max(p => p.Easting), minE = pts.Min(p => p.Easting);
            double maxN = pts.Max(p => p.Northing), minN = pts.Min(p => p.Northing);
            fieldWidth = Math.Max(maxE - minE, maxN - minN) * 1.2;
        }

        _tramLineService.Clear();

        // Track if systems have ever been used (disables legacy fallback)
        if (config.Systems.Count > 0)
            _hasTramSystemsEverUsed = true;

        // If TramSystems exist, generate per-system; otherwise use legacy single-track mode
        _tramSystemLineRanges.Clear();
        if (config.Systems.Count > 0)
        {
            foreach (var sys in config.Systems)
            {
                if (!sys.IsEnabled) continue;

                // Boundary reference system: generate boundary tram tracks from field boundary
                if (sys.ReferenceBoundaryIndex >= 0)
                {
                    int passes = sys.PassCount > 0 ? sys.PassCount : 1;
                    int bndStartIdx = _tramLineService.ParallelTramLines.Count;
                    if (State.Field.CurrentBoundary?.OuterBoundary?.Points != null &&
                        State.Field.CurrentBoundary.OuterBoundary.Points.Count >= 3)
                    {
                        var bndPts = State.Field.CurrentBoundary.OuterBoundary.Points
                            .Select(p => new Models.Base.Vec3(p.Easting, p.Northing, p.Heading)).ToList();
                        _tramLineService.GenerateBoundaryTramTracks(bndPts, passes, sys.Mode, sys.TramWidth);
                    }
                    int bndLineCount = _tramLineService.ParallelTramLines.Count - bndStartIdx;
                    _tramSystemLineRanges[sys.Name] = (bndStartIdx, bndLineCount, true);
                    continue;
                }

                // Track reference system: resolve by name only, skip if missing
                if (string.IsNullOrEmpty(sys.ReferenceTrackName)) continue;
                var refTrack = SavedTracks.FirstOrDefault(t => t.Name == sys.ReferenceTrackName);
                if (refTrack == null || refTrack.Points.Count < 2) continue;

                int startIdx = _tramLineService.ParallelTramLines.Count;
                var lines = _tramLineService.GenerateForSystem(sys, refTrack, fieldWidth);
                foreach (var line in lines)
                    _tramLineService.AddTramLine(line);
                _tramSystemLineRanges[sys.Name] = (startIdx, lines.Count, false);
            }
        }
        else if (!_hasTramSystemsEverUsed && track is { IsABLine: true })
        {
            // An AB line: tram lines parallel to it, clipped to the fence, plus the
            // boundary tram track, as AgOpenGPS's CABLine.BuildTram. A straight line's
            // offsets can't fold, so this doesn't bring back the curved-field "web".
            _tramLineService.GenerateParallelTramLines(track, fieldWidth);
            if (State.Field.CurrentBoundary?.OuterBoundary?.Points is { Count: >= 3 } fence)
                _tramLineService.GenerateBoundaryTramTracks(
                    fence.Select(p => new Models.Base.Vec3(p.Easting, p.Northing, p.Heading)).ToList());
        }
        else if (!_hasTramSystemsEverUsed)
        {
            // A curve, or no track: controlled-traffic lanes parallel to the field boundary,
            // spaced at the tram (sprayer/CTF) width. Clean concentric Clipper offsets —
            // replaces the legacy per-point lateral offset of the guidance curve, which
            // folded into a self-intersecting "web" on irregular/curved fields.
            _tramLineService.GenerateConcentricTramLanes();
        }

        // Snapshot collections for thread-safe rendering
        var outerSnap = _tramLineService.OuterBoundaryTrack.ToList();
        var innerSnap = _tramLineService.InnerBoundaryTrack.ToList();
        var parallelSnap = _tramLineService.ParallelTramLines
            .Select(l => (IReadOnlyList<Models.Base.Vec2>)l.ToList()).ToList();
        var bndExtraSnap = _tramLineService.BoundaryExtraLines
            .Select(l => (IReadOnlyList<Models.Base.Vec2>)l.ToList()).ToList();

        _mapService.SetTramLines(outerSnap, innerSnap, parallelSnap, bndExtraSnap);

        OnPropertyChanged(nameof(TramLineCountDisplay));
    }

    // Flag markers: the list and its numbering are the session's (FieldFlags); showing and saving them is here.
    public ObservableCollection<Flag> Flags => _sessionData.Flags.Items;

    /// <summary>
    /// Tracks, flags and tram systems belong to a field: made with none open they stayed in
    /// the lists and were saved into the next field opened (AgOpenGPS shows these buttons only
    /// with a field open). Says so and returns false when no field is open.
    /// </summary>
    private bool FieldIsOpen()
    {
        if (IsFieldOpen) return true;
        ReportFailure("Open a field first");
        return false;
    }

    /// <summary>
    /// A position to act on. E 0, N 0 alone isn't "no GPS": it is the field's origin, where the
    /// tractor stands when the field was just created there. Only with no GPS data coming in
    /// is 0, 0 the empty position the app starts with.
    /// </summary>
    private bool HasGpsPosition(double easting, double northing) =>
        easting != 0 || northing != 0 || _gpsService.IsGpsDataOk();

    private void PlaceFlag(FlagColor? color)
    {
        if (!FieldIsOpen()) return;
        if (!HasGpsPosition(Easting, Northing))
        {
            ReportFailure("No GPS position - cannot place flag");
            return;
        }

        var flag = _sessionData.Flags.Add(Easting, Northing, color);
        UpdateFlagsOnMap();
        StatusMessage = $"{flag.FlagColor} flag '{flag.Name}' at E:{Easting:F1} N:{Northing:F1}";
    }

    public void UpdateFlagsOnMap()
    {
        var flags = Flags.Select(f =>
            (f.Easting, f.Northing, f.FlagColor.ToString(), f.Name)).ToList();
        _mapService.SetFlags(flags);
        // Mirror a render snapshot into FieldState (the SoT the web-UI projector reads).
        State.Field.Flags = Flags
            .Select(f => new Models.State.FlagMarker(f.Easting, f.Northing, Flag.ColorToHex(f.FlagColor), f.Name))
            .ToList();
        SaveFlagsToField();
    }

    // Set while flags are being (re)loaded or cleared with the field, so UpdateFlagsOnMap
    // doesn't write a half-loaded list — or an empty one over the file on close.
    private bool _suppressFlagSave;

    /// <summary>
    /// Persist the open field's flags to Flags.txt (#107: flags were never saved, so they
    /// vanished on restart). Every flag change goes through UpdateFlagsOnMap, which calls this.
    /// </summary>
    private void SaveFlagsToField()
    {
        if (_suppressFlagSave || !IsFieldOpen) return;
        var dir = _fieldService.ActiveField?.DirectoryPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Services.GeoJson.GeoJsonFieldService.SaveFlags(dir, Flags.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Flags] Failed to save flags");
        }
    }

    /// <summary>
    /// Export the open field as ISOXML v4 to &lt;field&gt;/zISOXML/v4, like AgOpenGPS does on field
    /// close (ExportFieldAs_ISOXMLv4): boundary + holes, headland, AB/curve tracks, plus a
    /// device description for the vehicle and tool with their coupling (connector) types (#110).
    /// The data is captured here; the file is written in the background.
    /// </summary>
    private void ExportFieldIsoXml()
    {
        var field = _fieldService.ActiveField;
        var plane = State.Field.LocalPlane;
        if (!IsFieldOpen || field == null || string.IsNullOrEmpty(field.DirectoryPath) || plane == null) return;

        var bnd = State.Field.CurrentBoundary;
        var boundaries = new List<Models.IsoXml.IsoXmlBoundary>();
        var headlands = new List<List<Vec3>>();
        if (bnd?.OuterBoundary is { IsValid: true } outer)
        {
            boundaries.Add(new Models.IsoXml.IsoXmlBoundary { FenceLine = outer.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList() });
            headlands.Add(State.Field.HeadlandLine?.ToList() ?? new List<Vec3>());
            foreach (var hole in bnd.InnerBoundaries.Where(h => h.IsValid))
                boundaries.Add(new Models.IsoXml.IsoXmlBoundary { FenceLine = hole.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList() });
        }

        var tracks = new List<Models.IsoXml.IsoXmlTrack>();
        foreach (var t in SavedTracks.ToList())
        {
            if (t.Points.Count < 2) continue;
            if (t.Type == TrackType.ABLine && t.Points.Count == 2)
                tracks.Add(new Models.IsoXml.IsoXmlTrack
                {
                    Name = t.Name, Mode = Models.IsoXml.IsoXmlTrackMode.AB,
                    PtA = new Vec2(t.Points[0].Easting, t.Points[0].Northing),
                    PtB = new Vec2(t.Points[1].Easting, t.Points[1].Northing),
                    Heading = Math.Atan2(t.Points[1].Easting - t.Points[0].Easting, t.Points[1].Northing - t.Points[0].Northing),
                });
            else if (t.Type == TrackType.Curve)
                tracks.Add(new Models.IsoXml.IsoXmlTrack { Name = t.Name, Mode = Models.IsoXml.IsoXmlTrackMode.Curve, CurvePoints = t.Points.ToList() });
        }

        var devices = new List<Models.IsoXml.IsoXmlDevice>
        {
            new() { Designator = ConfigStore.Vehicle.Name, ConnectorType = ConfigStore.Vehicle.HitchType },
            new() { Designator = ConfigStore.ActiveToolProfileName, ConnectorType = ConfigStore.Tool.HitchType },
        };
        string dir = System.IO.Path.Combine(field.DirectoryPath, "zISOXML", "v4");
        string name = field.Name ?? System.IO.Path.GetFileName(field.DirectoryPath);
        int area = (int)(bnd?.OuterBoundary?.AreaSquareMeters ?? 0);
        string version = typeof(MainViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "AgOpenWeb";

        _ = Task.Run(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                Services.IsoXml.IsoXmlExporter.Export(dir, name, area, boundaries, headlands, tracks, plane,
                    Services.IsoXml.IsoXmlExporter.IsoXmlVersion.V4, version, devices);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[IsoXml] Field export failed"); }
        });
    }

    // The field remembers which track was last used, so reopening it comes back to that
    // track instead of no guidance (#148). One line, in the field's folder.
    private const string LastTrackFileName = "ActiveTrack.txt";
    private bool _restoringTrack;

    private void RestoreLastUsedTrack(string fieldPath)
    {
        try
        {
            var file = System.IO.Path.Combine(fieldPath, LastTrackFileName);
            if (!System.IO.File.Exists(file)) return;
            var name = System.IO.File.ReadAllText(file).Trim();
            if (name.Length == 0) return;
            var track = SavedTracks.FirstOrDefault(t => t.IsVisible && t.Name == name);
            if (track == null) return;
            _restoringTrack = true;
            try { SelectedTrack = track; }
            finally { _restoringTrack = false; }
            _logger.LogDebug("[TrackFiles] Restored last used track '{Name}'", name);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[TrackFiles] Failed to restore the last used track"); }
    }

    /// <summary>Remember (or forget) the field's active track for the next time it opens (#148).</summary>
    private void SaveLastUsedTrack()
    {
        if (_restoringTrack || !IsFieldOpen) return;
        var dir = _fieldService.ActiveField?.DirectoryPath;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
        try
        {
            var file = System.IO.Path.Combine(dir, LastTrackFileName);
            if (SelectedTrack != null) System.IO.File.WriteAllText(file, SelectedTrack.Name);
            else if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[TrackFiles] Failed to save the last used track"); }
    }

    /// <summary>
    /// Tracks manager / Field Builder: the operator tapped a track row. It becomes the
    /// active track (#148) and Auto Track goes off so the choice sticks (#146).
    /// </summary>
    public void SelectTrackAt(int index)
    {
        if (index < 0 || index >= SavedTracks.Count) return;
        var track = SavedTracks[index];
        if (!track.IsVisible) // a hidden track can't be the active one (AgOpenGPS)
        {
            ReportFailure($"Track hidden: {track.Name}"); // was silent (#695)
            return;
        }
        IsAutoTrackEnabled = false;
        SelectedTrack = track;
        StatusMessage = $"Active track: {track.Name}";
    }

    /// <summary>Replace the contour strips with the field's (none when null) (#110).</summary>
    private void LoadContoursFromField(string? fieldPath)
    {
        var strips = new List<List<Vec3>>();
        if (!string.IsNullOrEmpty(fieldPath))
        {
            try { strips = Services.GeoJson.GeoJsonFieldService.LoadContours(fieldPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Contour] Failed to load contours"); }
        }
        _gpsPipelineService.LoadContours(strips);
    }

    /// <summary>Append contour strips finished since the last save to the field's contours (#110).</summary>
    private void SaveContoursToField()
    {
        var strips = _gpsPipelineService.TakeContoursToSave();
        var dir = _fieldService.ActiveField?.DirectoryPath;
        if (strips is not { Count: > 0 } || !IsFieldOpen || string.IsNullOrEmpty(dir)) return;
        try { Services.GeoJson.GeoJsonFieldService.AppendContours(dir, strips); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Contour] Failed to save contours"); }
    }

    /// <summary>Replace the flags with the field's (empty when <paramref name="fieldPath"/> is null).</summary>
    private void LoadFlagsFromField(string? fieldPath)
    {
        _suppressFlagSave = true;
        try
        {
            var loaded = new List<Flag>();
            if (!string.IsNullOrEmpty(fieldPath))
            {
                try
                {
                    loaded = Services.GeoJson.GeoJsonFieldService.LoadFlags(fieldPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Flags] Failed to load flags");
                }
            }
            _sessionData.Flags.Replace(loaded);
            UpdateFlagsOnMap();
        }
        finally { _suppressFlagSave = false; }
    }

    // ---- Remote/web flag-list operations (index into Flags, matching the projected list) ----
    public void RenameFlagAt(int index, string name)
    {
        if (!_sessionData.Flags.Rename(index, name)) return;
        UpdateFlagsOnMap();
    }

    public void SetFlagColorAt(int index, FlagColor color)
    {
        if (!_sessionData.Flags.SetColor(index, color)) return;
        UpdateFlagsOnMap();
    }

    public void DeleteFlagAt(int index)
    {
        if (_sessionData.Flags.RemoveAt(index) is not { } f) return;
        UpdateFlagsOnMap();
        StatusMessage = $"Deleted flag '{f.Name}'";
    }

    public void DeleteAllFlagsRemote()
    {
        int count = Flags.Count;
        _sessionData.Flags.Replace(Array.Empty<Flag>());
        UpdateFlagsOnMap();
        StatusMessage = $"Deleted {count} flags";
    }

    /// <summary>Delete every saved track without asking (the web client has already
    /// confirmed). Also used as the native confirm's action.</summary>
    public void DeleteAllTracksConfirmed()
    {
        if (SavedTracks.Count == 0)
        {
            ReportFailure("No tracks to delete");
            return;
        }
        var names = SavedTracks.Select(t => t.Name).ToList();
        SavedTracks.Clear();
        SelectedTrack = null;
        RebuildRecordedPathsAndContours(); // clear rec-path/contour display
        SaveTracksToFile();
        // Also remove the recorded path in use, else it reloads on next open.
        if (_fieldService.ActiveField is { } f)
            Services.GeoJson.GeoJsonFieldService.DeleteCurrentRecordedPath(f.DirectoryPath);
        var dropped = DropTramSystemsFollowing(names);
        StatusMessage = dropped.Count == 0
            ? "All tracks deleted"
            : $"All tracks deleted, and the {dropped.Count} tram system(s) that followed them";
    }

    /// <summary>
    /// Every way of adding a track comes here: its name is made unique first ("A+ 21:00",
    /// "A+ 21:00 (2)"). Tram systems and the last used track find a track by name, so two
    /// tracks with one name put tram lines on the wrong one, reopened a field on the wrong
    /// line, and a delete or rename of one left its tram system on the other. A+ and drawn
    /// tracks are named after the time or the heading, so two in one minute shared a name.
    /// </summary>
    private void AddSavedTrack(Track track)
    {
        track.Name = UniqueTrackName(track.Name);
        SavedTracks.Add(track);
    }

    private string UniqueTrackName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Track" : name.Trim();
        if (!SavedTracks.Any(t => t.Name == name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} ({i})");
            if (!SavedTracks.Any(t => t.Name == candidate)) return candidate;
        }
    }

    /// <summary>Rename a saved track by index (remote/web Field Builder). Persists.</summary>
    public void RenameTrackAt(int index, string name)
    {
        if (index < 0 || index >= SavedTracks.Count) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            ReportFailure("Type a name for the track");
            return;
        }
        var track = SavedTracks[index];
        if (SavedTracks.Any(t => !ReferenceEquals(t, track) && t.Name == name.Trim()))
        {
            ReportFailure($"There is already a track called '{name.Trim()}'");
            return;
        }
        var oldName = track.Name;
        bool wasActive = ReferenceEquals(track, SelectedTrack);
        track.Name = name.Trim();
        SaveTracksToFile();
        if (wasActive) SaveLastUsedTrack(); // the remembered name follows the rename (#148)

        // Tram systems follow their track by name: carry them along, or their lines vanish
        // with the old name and the systems point at nothing.
        var following = TramSystemsFollowing(oldName);
        if (following.Count > 0 && !SavedTracks.Any(t => !ReferenceEquals(t, track) && t.Name == oldName))
        {
            foreach (var s in following)
            {
                s.ReferenceTrackName = track.Name;
                s.Name = $"Tram {TramSystems.IndexOf(s) + 1} ({track.Name})";
            }
            RebuildAndSaveTram();
            StatusMessage = $"Renamed '{oldName}' to '{track.Name}'; {following.Count} tram system(s) follow it";
        }
    }

    public ICommand? SwapABPointsCommand { get; private set; }

    // NTRIP Profiles Dialog properties
    public ObservableCollection<NtripProfile> NtripProfiles { get; } = new();


    private string _ntripTestStatus = string.Empty;
    public string NtripTestStatus
    {
        get => _ntripTestStatus;
        set => SetProperty(ref _ntripTestStatus, value);
    }

    private bool _isTestingNtripConnection;
    public bool IsTestingNtripConnection
    {
        get => _isTestingNtripConnection;
        set => SetProperty(ref _isTestingNtripConnection, value);
    }

    // New Field Dialog properties (visibility managed by State.UI)
    private string _newFieldName = string.Empty;
    public string NewFieldName
    {
        get => _newFieldName;
        set => SetProperty(ref _newFieldName, value);
    }

    private double _newFieldLatitude;
    public double NewFieldLatitude
    {
        get => _newFieldLatitude;
        set => SetProperty(ref _newFieldLatitude, value);
    }

    private double _newFieldLongitude;
    public double NewFieldLongitude
    {
        get => _newFieldLongitude;
        set => SetProperty(ref _newFieldLongitude, value);
    }

    public ICommand? ConfirmNewFieldDialogCommand { get; private set; }

    // From Existing Field Dialog properties (visibility managed by State.UI)
    private string _fromExistingFieldName = string.Empty;
    public string FromExistingFieldName
    {
        get => _fromExistingFieldName;
        set => SetProperty(ref _fromExistingFieldName, value);
    }

    private FieldSelectionItem? _fromExistingSelectedField;
    public FieldSelectionItem? FromExistingSelectedField
    {
        get => _fromExistingSelectedField;
        set
        {
            SetProperty(ref _fromExistingSelectedField, value);
            if (value != null)
            {
                // Auto-populate field name when selection changes
                FromExistingFieldName = value.Name;
            }
        }
    }

    // Copy options for FromExistingField
    private bool _copyFlags = true;
    public bool CopyFlags
    {
        get => _copyFlags;
        set => SetProperty(ref _copyFlags, value);
    }

    private bool _copyMapping = true;
    public bool CopyMapping
    {
        get => _copyMapping;
        set => SetProperty(ref _copyMapping, value);
    }

    private bool _copyHeadland = true;
    public bool CopyHeadland
    {
        get => _copyHeadland;
        set => SetProperty(ref _copyHeadland, value);
    }

    private bool _copyLines = true;
    public bool CopyLines
    {
        get => _copyLines;
        set => SetProperty(ref _copyLines, value);
    }

    public ICommand? ConfirmFromExistingFieldDialogCommand { get; private set; }

    // KML Import Dialog properties (visibility managed by State.UI)
    public ObservableCollection<KmlFileItem> AvailableKmlFiles { get; } = new();

    private KmlFileItem? _selectedKmlFile;
    public KmlFileItem? SelectedKmlFile
    {
        get => _selectedKmlFile;
        set
        {
            SetProperty(ref _selectedKmlFile, value);
            if (value != null)
            {
                KmlImportFieldName = Path.GetFileNameWithoutExtension(value.Name);
                ParseKmlFile(value.FullPath);
            }
        }
    }

    private string _kmlImportFieldName = string.Empty;
    public string KmlImportFieldName
    {
        get => _kmlImportFieldName;
        set => SetProperty(ref _kmlImportFieldName, value);
    }

    private int _kmlBoundaryPointCount;
    public int KmlBoundaryPointCount
    {
        get => _kmlBoundaryPointCount;
        set => SetProperty(ref _kmlBoundaryPointCount, value);
    }

    private double _kmlCenterLatitude;
    public double KmlCenterLatitude
    {
        get => _kmlCenterLatitude;
        set => SetProperty(ref _kmlCenterLatitude, value);
    }

    private double _kmlCenterLongitude;
    public double KmlCenterLongitude
    {
        get => _kmlCenterLongitude;
        set => SetProperty(ref _kmlCenterLongitude, value);
    }

    private List<(double Latitude, double Longitude)> _kmlBoundaryPoints = new();
    private List<List<(double Latitude, double Longitude)>> _kmlParsedPolygons = new();

    /// <summary>
    /// When true, KML import adds boundaries to the current open field.
    /// When false (default), KML import creates a new field.
    /// </summary>
    private bool _kmlImportToExistingField;

    public ICommand? ConfirmKmlImportDialogCommand { get; private set; }

    // ISO-XML Import Dialog properties (visibility managed by State.UI)
    public ObservableCollection<IsoXmlFileItem> AvailableIsoXmlFiles { get; } = new();

    private IsoXmlFileItem? _selectedIsoXmlFile;
    public IsoXmlFileItem? SelectedIsoXmlFile
    {
        get => _selectedIsoXmlFile;
        set
        {
            SetProperty(ref _selectedIsoXmlFile, value);
            if (value != null)
            {
                IsoXmlImportFieldName = value.Name;
            }
        }
    }

    private string _isoXmlImportFieldName = string.Empty;
    public string IsoXmlImportFieldName
    {
        get => _isoXmlImportFieldName;
        set => SetProperty(ref _isoXmlImportFieldName, value);
    }

    public ICommand? ConfirmIsoXmlImportDialogCommand { get; private set; }

    // Boundary Map Dialog properties (visibility managed by State.UI)
    private double _boundaryMapCenterLatitude;
    public double BoundaryMapCenterLatitude
    {
        get => _boundaryMapCenterLatitude;
        set => SetProperty(ref _boundaryMapCenterLatitude, value);
    }

    private double _boundaryMapCenterLongitude;
    public double BoundaryMapCenterLongitude
    {
        get => _boundaryMapCenterLongitude;
        set => SetProperty(ref _boundaryMapCenterLongitude, value);
    }

    // Existing boundary polygons for reference display in the map dialog (WGS84 coordinates)
    public List<List<(double Latitude, double Longitude)>> BoundaryMapExistingPolygons { get; } = new();


    public ICommand? ShowBoundaryMapDialogCommand { get; private set; }

    // Confirmation Dialog properties (visibility managed by State.UI)
    private string _confirmationDialogTitle = string.Empty;
    public string ConfirmationDialogTitle
    {
        get => _confirmationDialogTitle;
        set => SetProperty(ref _confirmationDialogTitle, value);
    }

    private string _confirmationDialogMessage = string.Empty;
    public string ConfirmationDialogMessage
    {
        get => _confirmationDialogMessage;
        set => SetProperty(ref _confirmationDialogMessage, value);
    }

    // Optional checkbox shown above the buttons. Hidden when label is null/empty.
    private string? _confirmationDialogCheckboxLabel;
    public string? ConfirmationDialogCheckboxLabel
    {
        get => _confirmationDialogCheckboxLabel;
        set => SetProperty(ref _confirmationDialogCheckboxLabel, value);
    }

    private bool _confirmationDialogCheckboxChecked;
    public bool ConfirmationDialogCheckboxChecked
    {
        get => _confirmationDialogCheckboxChecked;
        set => SetProperty(ref _confirmationDialogCheckboxChecked, value);
    }

    // Optional explicit button labels. When both are set, the dialog shows
    // buttons captioned with the actual choices (e.g. "Use Restored" /
    // "New From Defaults") instead of the generic localized Yes/No, so a
    // two-action prompt is unambiguous. Null/empty falls back to Yes/No.
    private string? _confirmationDialogConfirmLabel;
    public string? ConfirmationDialogConfirmLabel
    {
        get => _confirmationDialogConfirmLabel;
        set
        {
            if (SetProperty(ref _confirmationDialogConfirmLabel, value))
                OnPropertyChanged(nameof(HasCustomConfirmationButtons));
        }
    }

    private string? _confirmationDialogCancelLabel;
    public string? ConfirmationDialogCancelLabel
    {
        get => _confirmationDialogCancelLabel;
        set
        {
            if (SetProperty(ref _confirmationDialogCancelLabel, value))
                OnPropertyChanged(nameof(HasCustomConfirmationButtons));
        }
    }

    public bool HasCustomConfirmationButtons =>
        !string.IsNullOrEmpty(_confirmationDialogConfirmLabel) &&
        !string.IsNullOrEmpty(_confirmationDialogCancelLabel);

    // Callbacks. Only one is set per ShowConfirmationDialog call. The
    // checkbox variant receives the checkbox state at the time the user
    // clicked Confirm.
    private Action? _confirmationDialogCallback;
    private Action<bool>? _confirmationDialogCheckboxCallback;
    private Models.State.DialogType _previousDialogBeforeConfirmation;

    public ICommand? CancelConfirmationDialogCommand { get; private set; }
    public ICommand? ConfirmConfirmationDialogCommand { get; private set; }

    /// <summary>
    /// Shows a confirmation dialog with the specified title and message.
    /// When the user confirms, the callback is executed.
    /// Restores the previous dialog on cancel.
    /// </summary>
    public void ShowConfirmationDialog(string title, string message, Action onConfirm)
    {
        ConfirmationDialogTitle = title;
        ConfirmationDialogMessage = message;
        ConfirmationDialogCheckboxLabel = null;
        ConfirmationDialogCheckboxChecked = false;
        ConfirmationDialogConfirmLabel = null;
        ConfirmationDialogCancelLabel = null;
        _confirmationDialogCallback = onConfirm;
        _confirmationDialogCheckboxCallback = null;
        _previousDialogBeforeConfirmation = State.UI.ActiveDialog;
        PromptSeq++;
        State.UI.ShowDialog(Models.State.DialogType.Confirmation);
    }

    /// <summary>
    /// Confirmation dialog whose buttons are captioned with the actual choices
    /// (e.g. "Use Restored" / "New From Defaults") so a two-action decision is
    /// unambiguous. <paramref name="confirmLabel"/> is the affirmative
    /// (right-hand) button; <paramref name="cancelLabel"/> is the dismiss
    /// (left-hand) button and also what a backdrop click selects.
    /// </summary>
    public void ShowConfirmationDialog(
        string title,
        string message,
        string confirmLabel,
        string cancelLabel,
        Action onConfirm)
    {
        ConfirmationDialogTitle = title;
        ConfirmationDialogMessage = message;
        ConfirmationDialogCheckboxLabel = null;
        ConfirmationDialogCheckboxChecked = false;
        ConfirmationDialogConfirmLabel = confirmLabel;
        ConfirmationDialogCancelLabel = cancelLabel;
        _confirmationDialogCallback = onConfirm;
        _confirmationDialogCheckboxCallback = null;
        _previousDialogBeforeConfirmation = State.UI.ActiveDialog;
        PromptSeq++;
        State.UI.ShowDialog(Models.State.DialogType.Confirmation);
    }

    /// <summary>
    /// Confirmation dialog with an extra checkbox above the buttons.
    /// The callback receives the checkbox state at confirm time so the
    /// caller can branch on it (e.g. "also delete N jobs").
    /// </summary>
    public void ShowConfirmationDialog(
        string title,
        string message,
        string checkboxLabel,
        bool defaultChecked,
        Action<bool> onConfirm)
    {
        ConfirmationDialogTitle = title;
        ConfirmationDialogMessage = message;
        ConfirmationDialogCheckboxLabel = checkboxLabel;
        ConfirmationDialogCheckboxChecked = defaultChecked;
        ConfirmationDialogConfirmLabel = null;
        ConfirmationDialogCancelLabel = null;
        _confirmationDialogCallback = null;
        _confirmationDialogCheckboxCallback = onConfirm;
        _previousDialogBeforeConfirmation = State.UI.ActiveDialog;
        PromptSeq++;
        State.UI.ShowDialog(Models.State.DialogType.Confirmation);
    }

    // Error Dialog properties (visibility managed by State.UI)
    private string _errorDialogTitle = string.Empty;
    public string ErrorDialogTitle
    {
        get => _errorDialogTitle;
        set => SetProperty(ref _errorDialogTitle, value);
    }

    private string _errorDialogMessage = string.Empty;
    public string ErrorDialogMessage
    {
        get => _errorDialogMessage;
        set => SetProperty(ref _errorDialogMessage, value);
    }

    public ICommand? DismissErrorDialogCommand { get; private set; }

    /// <summary>
    /// Shows an error dialog with the specified title and message.
    /// </summary>
    public void ShowErrorDialog(string title, string message)
    {
        ErrorDialogTitle = title;
        ErrorDialogMessage = message;
        PromptSeq++;
        State.UI.ShowDialog(Models.State.DialogType.Error);
    }

    // iOS Modal Sheet Visibility Properties
    private bool _isFileMenuVisible;
    public bool IsFileMenuVisible
    {
        get => _isFileMenuVisible;
        set
        {
            if (SetProperty(ref _isFileMenuVisible, value) && value)
            {
                // Close other sheets when opening this one
                IsFieldToolsVisible = false;
                IsSettingsVisible = false;
            }
        }
    }

    private bool _isFieldToolsVisible;
    public bool IsFieldToolsVisible
    {
        get => _isFieldToolsVisible;
        set
        {
            if (SetProperty(ref _isFieldToolsVisible, value) && value)
            {
                // Close other sheets when opening this one
                IsFileMenuVisible = false;
                IsSettingsVisible = false;
            }
        }
    }

    private bool _isSettingsVisible;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (SetProperty(ref _isSettingsVisible, value) && value)
            {
                // Close other sheets when opening this one
                IsFileMenuVisible = false;
                IsFieldToolsVisible = false;
            }
        }
    }

    private bool _isBoundaryPanelVisible;
    public bool IsBoundaryPanelVisible
    {
        get => _isBoundaryPanelVisible;
        set
        {
            if (SetProperty(ref _isBoundaryPanelVisible, value) && value)
            {
                RefreshBoundaryList();
            }
        }
    }

    // Boundary mode: tracks whether next boundary operation targets inner or outer
    private BoundaryType _pendingBoundaryType = BoundaryType.Outer;
    public BoundaryType PendingBoundaryType
    {
        get => _pendingBoundaryType;
        set
        {
            if (SetProperty(ref _pendingBoundaryType, value))
            {
                OnPropertyChanged(nameof(BoundaryRecordingHeaderText));
                OnPropertyChanged(nameof(IsInnerBoundaryMode));
            }
        }
    }

    public bool IsInnerBoundaryMode => PendingBoundaryType == BoundaryType.Inner;

    /// <summary>The drive-around recording under way (recording or paused) is an obstacle, an inner
    /// boundary, not the field's outer boundary. The recorder window's title says which from this
    /// (the Boundary frame): both opened as "Stop Record Pause Boundary", and on a tester's field
    /// (Couto da Ribeira, 20 Nov 2026) three obstacles driven round with the field's button were
    /// each saved as the outer boundary.</summary>
    public bool IsRecordingObstacle =>
        _boundaryRecordingService.State != BoundaryRecordingState.Idle
        && _boundaryRecordingService.CurrentBoundaryType == BoundaryType.Inner;

    public string BoundaryRecordingHeaderText
    {
        get
        {
            if (_boundaryRecordingService.IsRecording || _boundaryRecordingService.State == BoundaryRecordingState.Paused)
            {
                return _boundaryRecordingService.CurrentBoundaryType == BoundaryType.Inner
                    ? "Recording Inner Boundary"
                    : "Recording Outer Boundary";
            }
            return "Start or Delete Boundary";
        }
    }

    // Boundary list for the boundary panel. Replaced whole on each refresh, never cleared and
    // filled again: the web projector reads it on the broadcaster thread, and a tick that fell
    // between the clear and the refill (the field file is read in between) sent the panel an
    // empty or half list, which threw the panel's scroll back to the top.
    private volatile IReadOnlyList<BoundaryListItem> _boundaryItems = Array.Empty<BoundaryListItem>();
    public IReadOnlyList<BoundaryListItem> BoundaryItems => _boundaryItems;

    private int _selectedBoundaryIndex = -1;
    public int SelectedBoundaryIndex
    {
        get => _selectedBoundaryIndex;
        set => SetProperty(ref _selectedBoundaryIndex, value);
    }

    private bool _isBoundaryPlayerPanelVisible;
    public bool IsBoundaryPlayerPanelVisible
    {
        get => _isBoundaryPlayerPanelVisible;
        set => SetProperty(ref _isBoundaryPlayerPanelVisible, value);
    }

    // Boundary Player settings
    private bool _isBoundarySectionControlOn;
    public bool IsBoundarySectionControlOn
    {
        get => _isBoundarySectionControlOn;
        set
        {
            SetProperty(ref _isBoundarySectionControlOn, value);
            StatusMessage = value ? "Boundary records when section is on" : "Boundary section control off";
        }
    }

    // Boundary-recording setup is persistent STATE (last-used setup, restored
    // for continuity). These VM properties delegate to PersistentAppState and
    // raise change notifications for the bound UI.
    public bool IsDrawRightSide
    {
        get => PersistentState.BoundaryDrawRightSide;
        set
        {
            if (PersistentState.BoundaryDrawRightSide == value) return;
            PersistentState.BoundaryDrawRightSide = value;
            OnPropertyChanged();
            StatusMessage = value ? "Boundary on right side" : "Boundary on left side";
            UpdateBoundaryOffsetIndicator();
        }
    }

    public bool IsDrawAtPivot
    {
        get => PersistentState.BoundaryDrawAtPivot;
        set
        {
            if (PersistentState.BoundaryDrawAtPivot == value) return;
            PersistentState.BoundaryDrawAtPivot = value;
            OnPropertyChanged();
            StatusMessage = value ? "Recording at pivot point" : "Recording at tool";
        }
    }

    public double BoundaryOffset
    {
        get => PersistentState.BoundaryOffset;
        set
        {
            var oldValue = PersistentState.BoundaryOffset;
            if (Math.Abs(oldValue - value) < 0.0001) return;
            PersistentState.BoundaryOffset = value;
            OnPropertyChanged();
            UpdateBoundaryOffsetIndicator();
        }
    }

    private void UpdateBoundaryOffsetIndicator()
    {
        // Apply direction: right side = positive offset, left side = negative offset
        double signedOffsetMeters = PersistentState.BoundaryOffset / 100.0;
        if (!PersistentState.BoundaryDrawRightSide)
        {
            signedOffsetMeters = -signedOffsetMeters;
        }
        _mapService.SetBoundaryOffsetIndicator(true, signedOffsetMeters);
    }

    /// <summary>
    /// Calculate offset position perpendicular to heading.
    /// Returns (easting, northing) with offset applied.
    /// </summary>
    private (double easting, double northing) CalculateOffsetPosition(double easting, double northing, double headingRadians)
    {
        if (PersistentState.BoundaryOffset == 0)
            return (easting, northing);

        // Offset in meters (input is cm)
        double offsetMeters = PersistentState.BoundaryOffset / 100.0;

        // If drawing on left side, negate the offset
        if (!PersistentState.BoundaryDrawRightSide)
            offsetMeters = -offsetMeters;

        // Calculate perpendicular offset (90 degrees to the right of heading)
        double perpAngle = headingRadians + Math.PI / 2.0;

        double offsetEasting = easting + offsetMeters * Math.Sin(perpAngle);
        double offsetNorthing = northing + offsetMeters * Math.Cos(perpAngle);

        return (offsetEasting, offsetNorthing);
    }

    // Configuration Dialog properties
    // Configuration Dialog (visibility managed by State.UI)
    private ConfigurationViewModel? _configurationViewModel;
    public ConfigurationViewModel? ConfigurationViewModel
    {
        get => _configurationViewModel;
        set => SetProperty(ref _configurationViewModel, value);
    }

    // AutoSteer Configuration Panel
    private AutoSteerConfigViewModel? _autoSteerConfigViewModel;
    public AutoSteerConfigViewModel? AutoSteerConfigViewModel
    {
        get => _autoSteerConfigViewModel;
        set => SetProperty(ref _autoSteerConfigViewModel, value);
    }

    // Smart WAS calibration dialog
    private SmartWasViewModel? _smartWasViewModel;
    public SmartWasViewModel? SmartWasViewModel
    {
        get => _smartWasViewModel;
        set => SetProperty(ref _smartWasViewModel, value);
    }

    // Start Work Session dialog (#349 M3)
    private StartWorkSessionDialogViewModel? _startWorkSessionDialogVm;
    public StartWorkSessionDialogViewModel? StartWorkSessionDialogVm
    {
        get => _startWorkSessionDialogVm;
        set => SetProperty(ref _startWorkSessionDialogVm, value);
    }


    public ICommand? ShowVehicleConfigDialogCommand { get; private set; }
    public ICommand? ShowToolConfigDialogCommand { get; private set; }
    public ICommand? ResumeLastJobCommand { get; private set; }
    public ICommand? ShowSmartWasCommand { get; private set; }

    public string CurrentProfileName => _configurationService.Store.ActiveVehicleProfileName;

    /// <summary>
    /// Notifies bindings tied to the active vehicle/tool profile names.
    /// Wired to <see cref="IConfigurationService.ProfileLoaded"/> /
    /// <see cref="IConfigurationService.ProfileSaved"/> so labels like the
    /// status pill on ConfigurationPanel refresh after the picker dialog
    /// or any other profile change.
    /// </summary>
    /// <summary>
    /// The active job keeps the machine it is worked with (#547): the vehicle and tool loaded now,
    /// when the job starts or resumes and whenever a profile is loaded or saved (closing a settings
    /// panel saves it). The job's file is written only when the machine changed.
    /// </summary>
    private void RecordJobRig()
    {
        if (_jobService.ActiveJob == null) return;
        try { _jobService.RecordRig(AgOpenWeb.Models.Job.JobRig.From(_configStore)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Job] Could not save the machine to job.json");
        }
    }

    private void RaiseProfileNameChanged()
    {
        OnPropertyChanged(nameof(CurrentProfileName));
    }

    // Headland Builder properties (visibility managed by State.UI)
    private bool _isHeadlandOn;
    public bool IsHeadlandOn
    {
        get => _isHeadlandOn;
        set
        {
            if (SetProperty(ref _isHeadlandOn, value))
            {
                State.FieldTools.IsHeadlandOn = value; // mirror for the web-UI projector
                _gpsPipelineService.SetHeadlandOn(value); // distance HUD + hydraulic lift (#106)
                SyncFieldContextToSectionControl();        // headland section control (#106)
                StatusMessage = value ? "Headland ON" : "Headland OFF";
                _mapService.SetHeadlandVisible(value);
            }
        }
    }

    /// <summary>
    /// Section control operates inside the headland (sections auto-off there). The
    /// single source of truth is <see cref="ConfigStore"/>.Tool.IsHeadlandSectionControl —
    /// the flag <c>SectionControlService.IsPointInHeadland</c> reads live. This property
    /// is a thin pass-through so the bottom-nav button + icon drive the REAL flag (the
    /// former VM-local backing field and the SectionState copy were dead duplicates).
    /// </summary>
    public bool IsSectionControlInHeadland
    {
        get => ConfigStore.Tool.IsHeadlandSectionControl;
        set
        {
            if (ConfigStore.Tool.IsHeadlandSectionControl == value) return;
            ConfigStore.Tool.IsHeadlandSectionControl = value;
            OnPropertyChanged();
        }
    }

    // UTurnSkipRows and IsUTurnSkipRowsEnabled are now in MainViewModel.YouTurn.cs

    private const double DefaultHeadlandDistance = 12.0;
    private double _headlandDistance = DefaultHeadlandDistance;
    public double HeadlandDistance
    {
        get => _headlandDistance;
        set => SetProperty(ref _headlandDistance, Math.Max(1.0, Math.Min(100.0, value)));
    }

    // Headland — single home is State.Field.HeadlandLine (§12.1). Pass-through.
    public List<Models.Base.Vec3>? CurrentHeadlandLine
    {
        get => State.Field.HeadlandLine;
        set
        {
            State.Field.HeadlandLine = value;
            OnPropertyChanged();
            _mapService.SetHeadlandLine(value);
            SaveHeadlandToFile(value);
            DropHeadlandPolygon();

            // Set HasHeadland based on whether we have a valid headland line
            HasHeadland = value != null && value.Count >= 3;

            // Sync to FieldState for section control headland detection
            State.Field.HeadlandLine = value;
        }
    }

    private List<Models.Base.Vec2>? _headlandPreviewLine;
    public List<Models.Base.Vec2>? HeadlandPreviewLine
    {
        get => _headlandPreviewLine;
        set
        {
            SetProperty(ref _headlandPreviewLine, value);
            _mapService.SetHeadlandPreview(value);
        }
    }

    private bool _hasHeadland;
    public bool HasHeadland
    {
        get => _hasHeadland;
        set => SetProperty(ref _hasHeadland, value);
    }

    // Bottom strip state properties (matching AgOpenGPS conditional button visibility)
    private bool _hasActiveTrack;
    /// <summary>
    /// True when an AB line or track is active for guidance (equivalent to AgOpenGPS trk.idx > -1)
    /// </summary>
    public bool HasActiveTrack
    {
        get => _hasActiveTrack;
        set => SetProperty(ref _hasActiveTrack, value);
    }

    private bool _hasBoundary;
    /// <summary>
    /// True when a field boundary exists (equivalent to AgOpenGPS isBnd)
    /// </summary>
    public bool HasBoundary
    {
        get => _hasBoundary;
        set => SetProperty(ref _hasBoundary, value);
    }

    /// <summary>
    /// Gets the current field's boundary for use in the headland editor.
    /// </summary>
    // Boundary — single home is State.Field.CurrentBoundary (§12.1). This VM
    // property is a thin pass-through for binding; no local copy.
    public Boundary? CurrentBoundary
    {
        get => State.Field.CurrentBoundary;
        private set
        {
            if (!ReferenceEquals(State.Field.CurrentBoundary, value))
            {
                State.Field.CurrentBoundary = value;
                OnPropertyChanged();
            }
        }
    }

    // Headland Dialog properties (visibility managed by State.UI)
    private bool _isHeadlandCurveMode = true;
    public bool IsHeadlandCurveMode
    {
        get => _isHeadlandCurveMode;
        set
        {
            var oldValue = _isHeadlandCurveMode;
            if (SetProperty(ref _isHeadlandCurveMode, value))
            {
                // Update preview when track type changes
                if (State.UI.IsFieldBuilderDialogVisible)
                {
                    UpdateHeadlandPreview();
                }
            }
        }
    }

    private int _headlandToolWidthMultiplier = 1;

    public double HeadlandCalculatedWidth => ConfigStore.ActualToolWidth * _headlandToolWidthMultiplier;

    // Headland point selection (for clipping headland via boundary points)
    // Each point is stored as (segmentIndex, t parameter 0-1, world position)
    private int _headlandPoint1Index = -1;

    private int _headlandPoint2Index = -1;


    public bool HeadlandPointsSelected => _headlandPoint1Index >= 0 && _headlandPoint2Index >= 0;



    // Field management properties
    private bool _isFieldOpen;
    public bool IsFieldOpen
    {
        get => _isFieldOpen;
        set
        {
            if (SetProperty(ref _isFieldOpen, value))
            {
                // Commands gated on having a field open need to refresh
                // CanExecute when the field is opened or closed.
                DeleteAppliedAreaCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsSectionBarVisible));
                if (value) RestartTripDistance(); // a field's trip distance starts at its opening (#321)
            }
        }
    }

    /// <summary>
    /// Open field's name (empty when no field is open). Pass-through over the SoT
    /// (<see cref="FieldState.ActiveField"/>); no VM-local copy. Change notifications
    /// come from the State.Field subscription in the constructor (config/state audit §12.1).
    /// </summary>
    public string CurrentFieldName => State.Field.ActiveField?.Name ?? string.Empty;

    /// <summary>
    /// Active job's task name, or empty when no job is active.
    /// </summary>
    public string CurrentJobTaskName => _jobService?.ActiveJob?.TaskName ?? string.Empty;

    /// <summary>
    /// Combined "field / task" label for the JobMenu pill and the
    /// upper-right status strip. Returns just the field name when there's
    /// no active job (e.g. field-only opens once that path lands).
    /// </summary>
    public string CurrentFieldAndJobLabel
    {
        get
        {
            var fieldName = CurrentFieldName;
            var task = CurrentJobTaskName;
            if (string.IsNullOrEmpty(fieldName)) return string.Empty;
            return string.IsNullOrEmpty(task) ? fieldName : $"{fieldName} / {task}";
        }
    }

    public ICommand? ToggleAutoTrackCommand { get; private set; }
    public ICommand? ToggleGridCommand { get; private set; }
    public ICommand? ToggleDayNightCommand { get; private set; }
    public ICommand? ToggleNorthUpCommand { get; private set; }
    public ICommand? CycleDisplayResolutionCommand { get; private set; }

    // Simulator Commands
    public ICommand? ToggleSimulatorPanelCommand { get; private set; }
    public ICommand? ResetSimulatorCommand { get; private set; }
    public ICommand? ResetSteerAngleCommand { get; private set; }
    public ICommand? SimulatorStopCommand { get; private set; }
    public ICommand? SimulatorReverseDirectionCommand { get; private set; }
    public ICommand? SimulatorSteerLeftCommand { get; private set; }
    public ICommand? SimulatorSteerRightCommand { get; private set; }
    public ICommand? SimulatorSpeedDownCommand { get; private set; }
    public ICommand? SimulatorSpeedUpCommand { get; private set; }

    public ICommand? ShowFieldSelectionDialogCommand { get; private set; }

    // Field Commands
    public ICommand? CloseFieldCommand { get; private set; }
    public ICommand? DriveInCommand { get; private set; }
    public ICommand? ResumeFieldCommand { get; private set; }

    // Map Commands
    public ICommand? Toggle3DModeCommand { get; private set; }
    public ICommand? ZoomInCommand { get; private set; }
    public ICommand? ZoomOutCommand { get; private set; }

    public event Action<string>? LanguageChanged;

    /// <summary>
    /// Fired after a field has been fully loaded (boundary, tracks, coverage, recpath).
    /// Subscribe to react to field changes without coupling to OpenFieldAsync internals.
    /// </summary>
    public event Action<Field?>? FieldFullyLoaded;


    public ICommand? StartBoundaryRecordingCommand { get; private set; }
    public ICommand? StopBoundaryRecordingCommand { get; private set; }
    public ICommand? UndoBoundaryPointCommand { get; private set; }
    public ICommand? ClearBoundaryCommand { get; private set; }
    public ICommand? AddBoundaryPointCommand { get; private set; }
    public ICommand? DeleteBoundaryCommand { get; private set; }
    public ICommand? ImportKmlBoundaryCommand { get; private set; }
    public ICommand? BuildFromTracksCommand { get; private set; }
    public ICommand? DriveAroundFieldCommand { get; private set; }
    public ICommand? RecordInnerBoundaryCommand { get; private set; }
    public ICommand? DriveAroundInnerBoundaryCommand { get; private set; }
    public ICommand? DrawMapInnerBoundaryCommand { get; private set; }
    public ICommand? ToggleDriveThroughCommand { get; private set; }
    public ICommand? ToggleHardCommand { get; private set; }
    public ICommand? ToggleRecordingCommand { get; private set; }
    public ICommand? ToggleBoundaryLeftRightCommand { get; private set; }
    public ICommand? ToggleBoundaryAntennaToolCommand { get; private set; }

    // Headland commands
    public ICommand? ShowHeadlandBuilderCommand { get; private set; }
    public ICommand? ToggleHeadlandCommand { get; private set; }
    public ICommand? ToggleSectionInHeadlandCommand { get; private set; }
    public ICommand? ResetToolHeadingCommand { get; private set; }

    // AB Line Guidance Commands - Bottom Bar (always visible)
    public ICommand? SnapLeftCommand { get; private set; }
    public ICommand? StartPivotRecordingCommand { get; private set; }
    public ICommand? SnapRightCommand { get; private set; }
    public ICommand? UTurnCommand { get; private set; }

    public ICommand? StartAPlusLineCommand { get; private set; }
    public ICommand? StartDriveABCommand { get; private set; }
    public ICommand? StartCurveRecordingCommand { get; private set; }
    public ICommand? FinishCurveRecordingCommand { get; private set; }
    public ICommand? CycleABLinesCommand { get; private set; }
    public ICommand? CycleABLinesBackCommand { get; private set; }
    public ICommand? NudgeLeftCommand { get; private set; }
    public ICommand? NudgeRightCommand { get; private set; }
    public ICommand? FineNudgeLeftCommand { get; private set; }
    public ICommand? FineNudgeRightCommand { get; private set; }
    public ICommand? HalfToolNudgeLeftCommand { get; private set; }
    public ICommand? HalfToolNudgeRightCommand { get; private set; }
    public ICommand? ResetNudgeCommand { get; private set; }
    public ICommand? ExtendTrackACommand { get; private set; }
    public ICommand? ExtendTrackBCommand { get; private set; }
    public ICommand? ShrinkTrackACommand { get; private set; }
    public ICommand? ShrinkTrackBCommand { get; private set; }
    public ICommand? StartDrawABModeCommand { get; private set; }
    public ICommand? StartDrawCurveModeCommand { get; private set; }
    public ICommand? FinishDrawCurveCommand { get; private set; }
    public ICommand? UndoLastDrawnPointCommand { get; private set; }
    public ICommand? SetABPointCommand { get; private set; }
    public ICommand? CancelABCreationCommand { get; private set; }

    // Bottom Strip Commands (matching AgOpenGPS panelBottom)
    public ICommand? ChangeMappingColorCommand { get; private set; }
    public ICommand? SnapToPivotCommand { get; private set; }
    public ICommand? ToggleUTurnSkipRowsCommand { get; private set; }
    public ICommand? CycleUTurnSkipRowsCommand { get; private set; }

    // Flags Commands
    public ICommand? PlaceRedFlagCommand { get; private set; }
    public ICommand? PlaceFlagHereCommand { get; private set; }

    // Right Navigation Panel Commands
    public ICommand? ToggleContourModeCommand { get; private set; }
    public ICommand? DeleteContoursCommand { get; private set; }
    // IRelayCommand (not ICommand) so the IsFieldOpen setter can re-evaluate CanExecute.
    public IRelayCommand? DeleteAppliedAreaCommand { get; private set; }
    public ICommand? ToggleTramDisplayCommand { get; private set; }
    public ICommand? BuildTramLinesCommand { get; private set; }
    public ICommand? CreateTrackFromBoundaryCommand { get; private set; }
    public ICommand? CreateCurveFromBoundaryCommand { get; private set; }
    public ICommand? CreateTracksFromAllEdgesCommand { get; private set; }

    public System.Collections.Generic.IReadOnlyList<Models.Base.Vec3>? CurrentHeadlandLineForPreview => State.Field.HeadlandLine;

    public string HeadlandStatusText
    {
        get
        {
            if (!HasHeadland || State.Field.HeadlandLine == null || State.Field.HeadlandLine.Count < 3)
                return HeadlandSegments.Count > 0 ? $"{HeadlandSegments.Count} lines (no intersections)" : "No headland lines";

            double area = System.Math.Abs(HeadlandGeometry.CalculateSignedArea(State.Field.HeadlandLine)) / 10000.0; // m2 -> hectares
            return $"{area:F2} ha ({HeadlandSegments.Count} lines)";
        }
    }

    /// <summary>
    /// List of headland segments that form the headland polygon.
    /// </summary>
    // The field's headland segments are the session's (FieldHeadland).
    public ObservableCollection<Models.Headland.HeadlandSegment> HeadlandSegments => _sessionData.Headland.Segments;

    private Models.Headland.HeadlandSegment? _selectedHeadlandSegment;
    public Models.Headland.HeadlandSegment? SelectedHeadlandSegment
    {
        get => _selectedHeadlandSegment;
        set => SetProperty(ref _selectedHeadlandSegment, value);
    }

    public int TramPasses => ConfigStore.Tram.Passes;
    public double TramWidth => ConfigStore.Tram.TramWidth;
    public System.Collections.ObjectModel.ObservableCollection<Models.Tram.TramSystem> TramSystems => ConfigStore.Tram.Systems;
    public string TramLineCountDisplay => $"{_tramLineService.ParallelTramLines.Count}";
    public ICommand? ToggleTramLeftManualCommand { get; private set; }
    public ICommand? ToggleTramRightManualCommand { get; private set; }
    /// <summary>Runtime tram detection byte: bit 0=right wheel, bit 1=left wheel.</summary>
    public byte TramControlByte { get; set; }

    public string TramDisplayLabel => ConfigStore.Tram.DisplayMode switch
    {
        Models.Configuration.TramDisplayMode.All => "All",
        Models.Configuration.TramDisplayMode.LinesOnly => "Lines",
        Models.Configuration.TramDisplayMode.OuterOnly => "Outer",
        _ => "Off"
    };

    public ICommand? ToggleRecordedPathsCommand { get; private set; }
    public ICommand? ToggleContourLockCommand { get; private set; }
    public ICommand? StartRecordedPathCommand { get; private set; }
    public ICommand? StopRecordedPathCommand { get; private set; }
    public ICommand? DeleteContourTrackCommand { get; private set; }
    public ICommand? ImportTracksFromFieldCommand { get; private set; }
    public ICommand? ToggleManualModeCommand { get; private set; }
    public ICommand? ToggleSectionMasterCommand { get; private set; }
    public ICommand? ToggleSectionCommand { get; private set; }
    public ICommand? ToggleYouTurnCommand { get; private set; }
    public ICommand? ManualYouTurnLeftCommand { get; private set; }
    public ICommand? ManualYouTurnRightCommand { get; private set; }
    public ICommand? ToggleUTurnDirectionCommand { get; private set; }
    public ICommand? ToggleAutoSteerCommand { get; private set; }

    private void CenterMapOnBoundary(Boundary boundary)
    {
        if (boundary.OuterBoundary?.Points == null || boundary.OuterBoundary.Points.Count == 0)
            return;

        double sumE = 0, sumN = 0;
        foreach (var point in boundary.OuterBoundary.Points)
        {
            sumE += point.Easting;
            sumN += point.Northing;
        }
        double centerE = sumE / boundary.OuterBoundary.Points.Count;
        double centerN = sumN / boundary.OuterBoundary.Points.Count;
        _mapService.PanTo(centerE, centerN);
    }

    /// <summary>
    /// Save background image and geo-reference file to field directory, then load it.
    /// </summary>
    private void SaveBackgroundImage(string sourcePath, string fieldPath, double nwLat, double nwLon, double seLat, double seLon,
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY)
    {
        // Copy the image into the field folder; its placement goes in field.geojson.
        File.Copy(sourcePath, Path.Combine(fieldPath, FieldBackground.DefaultImageFile), overwrite: true);
        Services.GeoJson.GeoJsonFieldService.SaveBackground(fieldPath, new FieldBackground(
            FieldBackground.DefaultImageFile, nwLat, nwLon, seLat, seLon,
            new MercatorBounds(mercMinX, mercMaxX, mercMinY, mercMaxY)));

        // Load through single method (applies Mapsui offset correction)
        LoadBackgroundImage(fieldPath, null);
    }

    private void LoadBackgroundImage(string fieldPath, Boundary? boundary)
    {
        // Clear any prior field's imagery up front; set again below on success.
        State.Field.Imagery = null;
        try
        {
            // Placement from field.geojson (opening the field imported an AgOpenGPS BackPic).
            if (Services.GeoJson.GeoJsonFieldService.LoadBackground(fieldPath) is not { } background)
                return;
            var backPicPath = Path.Combine(fieldPath, background.ImageFile);
            if (!File.Exists(backPicPath))
                return;
            double nwLat = background.NwLatitude, nwLon = background.NwLongitude;
            double seLat = background.SeLatitude, seLon = background.SeLongitude;
            bool hasMercator = background.Mercator is not null;
            var (mercMinX, mercMaxX, mercMinY, mercMaxY) = background.Mercator is { } m
                ? (m.MinX, m.MaxX, m.MinY, m.MaxY) : (0.0, 0.0, 0.0, 0.0);

            // Use field origin for LocalPlane (same origin used for boundary coordinates)
            // This ensures the background image aligns with the boundary
            var origin = new Wgs84(State.Field.OriginLatitude, State.Field.OriginLongitude);
            var sharedProps = new SharedFieldProperties();
            var localPlane = new LocalPlane(origin, sharedProps);

            _logger.LogDebug($"[LoadBG] Field origin from ViewModel: ({State.Field.OriginLatitude:F8}, {State.Field.OriginLongitude:F8})");
            _logger.LogDebug($"[LoadBG] LocalPlane origin: ({localPlane.Origin.Latitude:F8}, {localPlane.Origin.Longitude:F8})");
            _logger.LogDebug($"[LoadBG] WGS84 bounds: NW=({nwLat:F8}, {nwLon:F8}), SE=({seLat:F8}, {seLon:F8})");

            // Convert WGS84 to local coordinates
            var nwWgs = new Wgs84(nwLat, nwLon);
            var seWgs = new Wgs84(seLat, seLon);
            var nwLocal = localPlane.ConvertWgs84ToGeoCoord(nwWgs);
            var seLocal = localPlane.ConvertWgs84ToGeoCoord(seWgs);

            _logger.LogDebug($"[LoadBG] Local bounds: NW=({nwLocal.Easting:F2}, {nwLocal.Northing:F2}), SE=({seLocal.Easting:F2}, {seLocal.Northing:F2})");

            // Verify field origin converts to (0,0) in local coords
            var originWgs = new Wgs84(State.Field.OriginLatitude, State.Field.OriginLongitude);
            var originLocal = localPlane.ConvertWgs84ToGeoCoord(originWgs);
            _logger.LogDebug($"[LoadBG] Field origin in local coords (should be ~0,0): ({originLocal.Easting:F2}, {originLocal.Northing:F2})");

            // Use Mercator-aware method if bounds available, otherwise fall back to linear
            if (hasMercator)
            {
                _mapService.SetBackgroundImageWithMercator(backPicPath,
                    nwLocal.Easting, nwLocal.Northing, seLocal.Easting, seLocal.Northing,
                    mercMinX, mercMaxX, mercMinY, mercMaxY,
                    State.Field.OriginLatitude, State.Field.OriginLongitude);
            }
            else
            {
                _mapService.SetBackgroundImage(backPicPath, nwLocal.Easting, nwLocal.Northing, seLocal.Easting, seLocal.Northing);
            }

            // Mirror the placement into state for view-independent consumers
            // (remote/web map). Linear local corners; min/max so corner order
            // doesn't matter.
            State.Field.Imagery = new Models.State.FieldImagery(backPicPath,
                System.Math.Min(nwLocal.Easting, seLocal.Easting),
                System.Math.Min(nwLocal.Northing, seLocal.Northing),
                System.Math.Max(nwLocal.Easting, seLocal.Easting),
                System.Math.Max(nwLocal.Northing, seLocal.Northing));
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[LoadBackgroundImage] Error loading background image: {ex.Message}");
        }
    }

    /// <summary>
    /// Refresh the boundary list from the current field's boundary file. The selection is
    /// cleared, unless <paramref name="keepSelection"/>: a flag toggled on the selected row
    /// leaves the rows as they were, so that row stays selected.
    /// </summary>
    public void RefreshBoundaryList(bool keepSelection = false)
    {
        if (!keepSelection) SelectedBoundaryIndex = -1;
        var items = ReadBoundaryListItems();
        _boundaryItems = items;
        if (SelectedBoundaryIndex >= items.Count) SelectedBoundaryIndex = -1;
    }

    // The list's rows: the valid outer boundary first, then the valid inner ones in order.
    private List<BoundaryListItem> ReadBoundaryListItems()
    {
        var items = new List<BoundaryListItem>();
        if (string.IsNullOrEmpty(CurrentFieldName)) return items;

        var fieldPath = Path.Combine(_settingsService.Settings.FieldsDirectory, CurrentFieldName);
        var boundary = LoadFieldBoundary(fieldPath);

        if (boundary == null) return items;

        int index = 0;

        // Add outer boundary if exists
        if (boundary.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            items.Add(new BoundaryListItem
            {
                Index = index++,
                BoundaryType = "Outer",
                AreaHectares = boundary.OuterBoundary.AreaHectares,
                IsDriveThrough = boundary.OuterBoundary.IsDriveThrough,
                IsHard = boundary.OuterBoundary.IsHard
            });
        }

        // Add inner boundaries
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (inner.IsValid)
            {
                items.Add(new BoundaryListItem
                {
                    Index = index++,
                    BoundaryType = $"Inner {i + 1}",
                    AreaHectares = inner.AreaHectares,
                    IsDriveThrough = inner.IsDriveThrough,
                    IsHard = inner.IsHard
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Delete the selected boundary from the field
    /// </summary>
    private void DeleteSelectedBoundary()
    {
        if (SelectedBoundaryIndex < 0)
        {
            ReportFailure("Select a boundary to delete");
            return;
        }

        if (string.IsNullOrEmpty(CurrentFieldName))
        {
            ReportFailure("No field open");
            return;
        }

        var fieldPath = Path.Combine(_settingsService.Settings.FieldsDirectory, CurrentFieldName);
        var boundary = LoadFieldBoundary(fieldPath);

        if (boundary == null) return;

        int currentIndex = 0;
        bool deleted = false;
        bool outerDeleted = false;

        // Check if outer boundary is selected
        if (boundary.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            if (currentIndex == SelectedBoundaryIndex)
            {
                // AgOpenGPS (FormBoundary): the outer boundary can be deleted only when it is the
                // last one. Islands left without a field around them would be no field at all.
                if (boundary.InnerBoundaries.Any(b => b.IsValid))
                {
                    ReportFailure("Delete the inner boundaries first");
                    return;
                }
                boundary.OuterBoundary = null;
                deleted = true;
                outerDeleted = true;
            }
            currentIndex++;
        }

        // Check inner boundaries
        if (!deleted)
        {
            for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
            {
                if (boundary.InnerBoundaries[i].IsValid)
                {
                    if (currentIndex == SelectedBoundaryIndex)
                    {
                        boundary.InnerBoundaries.RemoveAt(i);
                        deleted = true;
                        break;
                    }
                    currentIndex++;
                }
            }
        }

        if (deleted)
        {
            SaveFieldBoundary(boundary, fieldPath);
            RefreshBoundaryList();
            SetCurrentBoundary(boundary);
            string done = outerDeleted ? OuterBoundaryChanged() : "";

            // If that was the last boundary, drop the field-background image
            // too — it's georeferenced against the boundary, so leaving
            // it on disk would float in space the next time the field opens.
            bool hasOuter = boundary.OuterBoundary != null && boundary.OuterBoundary.IsValid;
            bool hasInner = boundary.InnerBoundaries.Any(b => b.IsValid);
            if (!hasOuter && !hasInner)
            {
                DeleteBackgroundImage(fieldPath);
                StatusMessage = "Boundary deleted; background image removed" + (done.Length > 0 ? $", {done}" : "");
            }
            else
            {
                StatusMessage = "Boundary deleted" + (done.Length > 0 ? $"; {done}" : "");
            }
        }
    }

    private void DeleteBackgroundImage(string fieldPath)
    {
        try
        {
            if (Services.GeoJson.GeoJsonFieldService.LoadBackground(fieldPath) is { } background)
            {
                var imagePath = Path.Combine(fieldPath, background.ImageFile);
                if (File.Exists(imagePath)) File.Delete(imagePath);
                Services.GeoJson.GeoJsonFieldService.SaveBackground(fieldPath, null);
            }
            _mapService.ClearBackground();
            State.Field.Imagery = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[DeleteBackgroundImage] {ex.Message}");
        }
    }

    /// <summary>
    /// Normalize a loaded boundary's outer and inner polygons to a
    /// resolution-independent point set (Douglas-Peucker + max-gap densify). Dense
    /// GPS-captured or imported boundaries (e.g. ~3431 points at ~1 m for a 440-acre
    /// field) otherwise propagate that density into tram/headland generation,
    /// inside-tests, and rendering. Idempotent: an already-sparse boundary is left
    /// essentially unchanged. See Plans/Completed/BOUNDARY_RESOLUTION_NORMALIZATION.md.
    /// </summary>
    private void NormalizeBoundaryInPlace(Boundary boundary)
    {
        void NormalizePolygon(BoundaryPolygon? poly)
        {
            if (poly?.Points == null || poly.Points.Count < 4) return;
            int before = poly.Points.Count;
            poly.Points = Models.Base.BoundaryResolution.Normalize(poly.Points);
            poly.UpdateBounds();
            if (poly.Points.Count != before)
                _logger.LogDebug("[Boundary] Normalized polygon {Before} -> {After} points", before, poly.Points.Count);
        }

        NormalizePolygon(boundary.OuterBoundary);
        if (boundary.InnerBoundaries != null)
            foreach (var inner in boundary.InnerBoundaries)
                NormalizePolygon(inner);
    }

    /// <summary>
    /// Sets the boundary on both the map service and the ViewModel's CurrentBoundary property.
    /// Also populates HeadlandLine from HeadlandPolygon for section control.
    /// </summary>
    // A field's boundary lives in its field.geojson, written through FieldService with the rest
    // of the field, never as a file of its own.
    private Boundary? LoadFieldBoundary(string fieldPath)
    {
        try
        {
            return _fieldService.LoadField(fieldPath).Boundary;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void SaveFieldBoundary(Boundary boundary, string fieldPath)
    {
        var field = _fieldService.LoadField(fieldPath);
        field.Boundary = boundary;
        _fieldService.SaveField(field);
    }

    private void SetCurrentBoundary(Boundary? boundary)
    {
        _mapService.SetBoundary(boundary);
        CurrentBoundary = boundary;

        // Keep the in-memory Field model's boundary in sync. Without this,
        // ActiveField.Boundary remains whatever LoadField returned at open
        // time (usually the empty placeholder for a freshly-created field),
        // and CloseFieldAsync → FieldService.SaveField → SaveBoundary
        // overwrites the user's drawing on disk with "$Boundary\n".
        if (ActiveField != null)
            ActiveField.Boundary = boundary;

        // Set HasBoundary based on whether we have a valid outer boundary
        HasBoundary = boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid;

        // Set fixed field bounds for coverage bitmap coordinate system
        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid && boundary.OuterBoundary.Points.Count > 0)
        {
            // Calculate bounding box with padding for stable bitmap coordinates
            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (var point in boundary.OuterBoundary.Points)
            {
                minE = Math.Min(minE, point.Easting);
                maxE = Math.Max(maxE, point.Easting);
                minN = Math.Min(minN, point.Northing);
                maxN = Math.Max(maxN, point.Northing);
            }
            // Add 50m padding to handle coverage near edges
            const double padding = 50.0;
            double boundsMinE = minE - padding;
            double boundsMaxE = maxE + padding;
            double boundsMinN = minN - padding;
            double boundsMaxN = maxN + padding;

            _coverageMapService.SetFieldBounds(boundsMinE, boundsMaxE, boundsMinN, boundsMaxN);

            // Initialize the coverage bitmap eagerly on field load
            // Background will be composited into it when SetBackgroundImage is called
            _mapService.InitializeCoverageBitmapWithBounds(boundsMinE, boundsMaxE, boundsMinN, boundsMaxN);
        }
        else if (boundary == null || _coverageMapService.PatchCount == 0)
        {
            _coverageMapService.ClearFieldBounds();
            _autoCoverageBoundsInitialized = false; // Allow auto-init from GPS position
        }
        // else: the field stays open with worked area on the map and no outer boundary (its
        // last boundary deleted with a job open). The grid stays as it is: clearing its bounds
        // empties it, and the next save would delete the job's tiles.

        // Sync to FieldState for section control boundary/headland detection
        State.Field.CurrentBoundary = boundary;

        // Update area display
        OnPropertyChanged(nameof(BoundaryAreaDisplay));
        if (boundary != null && boundary.IsValid)
        {
            var boundaryAreas = new System.Collections.Generic.List<double> { boundary.AreaHectares * 10000 };
            _fieldStatistics.UpdateBoundaryAreas(boundaryAreas);
        }

        // Populate HeadlandLine from HeadlandPolygon for section control IsPointInHeadland check
        _logger.LogDebug($"[Headland] SetCurrentBoundary: HeadlandPolygon={boundary?.HeadlandPolygon != null}, IsValid={boundary?.HeadlandPolygon?.IsValid}, PointCount={boundary?.HeadlandPolygon?.Points?.Count ?? 0}");
        if (boundary?.HeadlandPolygon != null && boundary.HeadlandPolygon.IsValid)
        {
            var headlandPoints = new List<Vec3>();
            foreach (var point in boundary.HeadlandPolygon.Points)
            {
                headlandPoints.Add(new Vec3(point.Easting, point.Northing, point.Heading));
            }
            State.Field.HeadlandLine = headlandPoints;
            State.Field.HeadlandLine = headlandPoints;
            _mapService.SetHeadlandLine(headlandPoints);
            HasHeadland = true;
            IsHeadlandOn = true;
            _logger.LogDebug($"[Headland] Loaded {headlandPoints.Count} points from HeadlandPolygon for YouTurn");
        }
        else
        {
            // Boundary didn't carry a HeadlandPolygon (field.geojson has no headland role).
            // DO NOT clobber State.Field.HeadlandLine here — the legacy Headlines.txt
            // loader (LoadHeadland) runs separately on field open and is the authoritative
            // source for the legacy-format case. Nulling it here races with that loader
            // and silently disables U-turn headland detection (#289 F3). Field close and
            // explicit headland removal handle the reset case elsewhere.
            _logger.LogDebug($"[Headland] Boundary has no HeadlandPolygon — deferring to LoadHeadland / existing state");
        }

        // Sync boundary + headland to pipeline for guidance computations
        SyncGuidanceStateToPipeline();
    }

    /// <summary>
    /// Populates the AvailableFields collection from the specified directory.
    /// </summary>
    private void PopulateAvailableFields(string fieldsDirectory)
    {
        AvailableFields.Clear();

        if (!Directory.Exists(fieldsDirectory))
        {
            Directory.CreateDirectory(fieldsDirectory);
            return;
        }

        foreach (var dirPath in Directory.GetDirectories(fieldsDirectory))
        {
            var fieldName = Path.GetFileName(dirPath);

            // Calculate area from boundary if available
            double area = 0;
            Boundary? boundary = null;
            try { boundary = _fieldService.PeekField(dirPath).Boundary; }
            catch (Exception) { /* not a field */ }
            if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
            {
                area = boundary.OuterBoundary.AreaHectares;
            }

            // Get NTRIP profile name for this field
            string ntripProfileName = string.Empty;
            var ntripProfile = _ntripProfileService.GetProfileForField(fieldName);
            if (ntripProfile != null)
            {
                // Show profile name, with "(Default)" suffix if it's the default profile
                // and not specifically associated with this field
                var isSpecificallyAssociated = ntripProfile.AssociatedFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase);
                ntripProfileName = isSpecificallyAssociated
                    ? ntripProfile.Name
                    : $"{ntripProfile.Name} (Default)";
            }

            var item = new FieldSelectionItem(fieldName, dirPath, 0, area, ntripProfileName);
            AvailableFields.Add(item);
        }
    }

    /// <summary>
    /// Populates the AvailableKmlFiles collection from the KML import directory.
    /// Looks in Documents/AgOpenWeb/Import for KML/KMZ files.
    /// </summary>
    private void PopulateAvailableKmlFiles()
    {
        AvailableKmlFiles.Clear();

        // Import folder lives under the shared AgOpenWeb data root (honors
        // AGOPENWEB_DATA on the headless appliance), same as Fields/Tools/Vehicles —
        // NOT MyDocuments, which diverges from the data root on headless installs and
        // left the KML/ISO import list empty even when files were present.
        var importDir = Path.Combine(AppDataRoot.Documents, "Import");

        if (!Directory.Exists(importDir))
        {
            Directory.CreateDirectory(importDir);
            return;
        }

        // Search for .kml and .kmz files
        var kmlFiles = Directory.GetFiles(importDir, "*.kml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(importDir, "*.kmz", SearchOption.AllDirectories));

        foreach (var filePath in kmlFiles)
        {
            var fileInfo = new FileInfo(filePath);
            AvailableKmlFiles.Add(new KmlFileItem
            {
                Name = fileInfo.Name,
                FullPath = filePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSizeBytes = fileInfo.Length
            });
        }
    }

    /// <summary>
    /// Parses a KML file to extract boundary coordinates.
    /// Parses ALL coordinate blocks: first = outer boundary, subsequent = inner boundaries.
    /// Results stored in both _kmlBoundaryPoints (first polygon, for field-creation flow)
    /// and _kmlParsedPolygons (all polygons, for import-to-existing flow).
    /// </summary>
    private void ParseKmlFile(string filePath)
    {
        _kmlBoundaryPoints.Clear();
        _kmlParsedPolygons.Clear();
        KmlBoundaryPointCount = 0;
        KmlCenterLatitude = 0;
        KmlCenterLongitude = 0;

        try
        {
            var read = Services.Kml.KmlBoundaryFile.Read(filePath);
            foreach (var polygonPoints in read.Polygons)
            {
                _kmlParsedPolygons.Add(polygonPoints);

                // First polygon also populates _kmlBoundaryPoints for backwards compatibility
                if (_kmlParsedPolygons.Count == 1)
                {
                    _kmlBoundaryPoints.AddRange(polygonPoints);
                }
            }

            if (read.PointCount > 0)
            {
                KmlCenterLatitude = read.CenterLatitude;
                KmlCenterLongitude = read.CenterLongitude;
            }

            KmlBoundaryPointCount = read.PointCount;
        }
        catch (Exception ex)
        {
            ReportFailure($"Error parsing KML: {ex.Message}");
        }
    }

    /// <summary>
    /// Imports KML boundaries into the currently open field.
    /// First polygon becomes outer (or inner if PendingBoundaryType is Inner),
    /// subsequent polygons are added as inner boundaries.
    /// </summary>
    /// <summary>
    /// Converts existing boundary polygons from local coordinates to WGS84
    /// for display as reference layers in the boundary map dialog.
    /// </summary>
    private void PopulateBoundaryMapExistingPolygons()
    {
        BoundaryMapExistingPolygons.Clear();

        if (State.Field.CurrentBoundary == null || (State.Field.OriginLatitude == 0 && State.Field.OriginLongitude == 0))
            return;

        try
        {
            var origin = new Wgs84(State.Field.OriginLatitude, State.Field.OriginLongitude);
            var sharedProps = new SharedFieldProperties();
            var localPlane = new LocalPlane(origin, sharedProps);

            // Add outer boundary
            if (State.Field.CurrentBoundary.OuterBoundary?.Points != null && State.Field.CurrentBoundary.OuterBoundary.Points.Count >= 3)
            {
                var wgs84Points = new List<(double Latitude, double Longitude)>();
                foreach (var pt in State.Field.CurrentBoundary.OuterBoundary.Points)
                {
                    var geoCoord = new GeoCoord(pt.Northing, pt.Easting);
                    var wgs84 = localPlane.ConvertGeoCoordToWgs84(geoCoord);
                    wgs84Points.Add((wgs84.Latitude, wgs84.Longitude));
                }
                BoundaryMapExistingPolygons.Add(wgs84Points);
            }

            // Add inner boundaries
            foreach (var inner in State.Field.CurrentBoundary.InnerBoundaries)
            {
                if (inner.Points.Count >= 3)
                {
                    var wgs84Points = new List<(double Latitude, double Longitude)>();
                    foreach (var pt in inner.Points)
                    {
                        var geoCoord = new GeoCoord(pt.Northing, pt.Easting);
                        var wgs84 = localPlane.ConvertGeoCoordToWgs84(geoCoord);
                        wgs84Points.Add((wgs84.Latitude, wgs84.Longitude));
                    }
                    BoundaryMapExistingPolygons.Add(wgs84Points);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[BoundaryMap] Failed to populate existing polygons: {ex.Message}");
        }
    }

    private void ImportKmlToExistingField()
    {
        if (string.IsNullOrEmpty(CurrentFieldName) || _kmlParsedPolygons.Count == 0)
        {
            ReportFailure("No field open or no KML polygons parsed");
            return;
        }

        try
        {
            var fieldPath = Path.Combine(_settingsService.Settings.FieldsDirectory, CurrentFieldName);
            var boundary = LoadFieldBoundary(fieldPath) ?? new Boundary();

            var origin = new Wgs84(State.Field.OriginLatitude, State.Field.OriginLongitude);
            var sharedProps = new SharedFieldProperties();
            var localPlane = new LocalPlane(origin, sharedProps);

            for (int polyIdx = 0; polyIdx < _kmlParsedPolygons.Count; polyIdx++)
            {
                var polygon = new BoundaryPolygon();
                foreach (var (lat, lon) in _kmlParsedPolygons[polyIdx])
                {
                    var wgs84 = new Wgs84(lat, lon);
                    var geoCoord = localPlane.ConvertWgs84ToGeoCoord(wgs84);
                    polygon.Points.Add(new BoundaryPoint(geoCoord.Easting, geoCoord.Northing, 0));
                }

                // First polygon: respects PendingBoundaryType
                // Subsequent polygons: always inner
                if (polyIdx == 0 && PendingBoundaryType == BoundaryType.Outer)
                    SetOuterBoundary(boundary, polygon, "a KML file");
                else
                    boundary.InnerBoundaries.Add(polygon);
            }

            SaveFieldBoundary(boundary, fieldPath);
            SetCurrentBoundary(boundary);
            CenterMapOnBoundary(boundary);
            RefreshBoundaryList();

            // Update boundary area stats
            if (boundary.OuterBoundary != null)
            {
                var boundaryAreas = new List<double> { boundary.AreaHectares * 10000 };
                _fieldStatistics.UpdateBoundaryAreas(boundaryAreas);
                OnPropertyChanged(nameof(BoundaryAreaDisplay));
            }

            State.UI.CloseDialog();
            PendingBoundaryType = BoundaryType.Outer;

            var innerCount = _kmlParsedPolygons.Count > 1 ? _kmlParsedPolygons.Count - 1 : 0;
            var innerMsg = innerCount > 0 ? $" + {innerCount} inner" : "";
            StatusMessage = $"KML boundary imported{innerMsg}";
        }
        catch (Exception ex)
        {
            ReportFailure($"Error importing KML boundary: {ex.Message}");
        }
    }

    /// <summary>
    /// Populates the AvailableIsoXmlFiles collection from the ISO-XML import directory.
    /// Looks for TASKDATA.xml files in Documents/AgOpenWeb/Import.
    /// </summary>
    private void PopulateAvailableIsoXmlFiles()
    {
        AvailableIsoXmlFiles.Clear();
        // Import folder lives under the shared AgOpenWeb data root (honors
        // AGOPENWEB_DATA on the headless appliance), same as Fields/Tools/Vehicles —
        // NOT MyDocuments, which diverges from the data root on headless installs and
        // left the KML/ISO import list empty even when files were present.
        var importDir = Path.Combine(AppDataRoot.Documents, "Import");
        if (!Directory.Exists(importDir))
        {
            Directory.CreateDirectory(importDir);
            return;
        }
        // One entry per partfield of every TASKDATA.XML (the web picker shows the same list).
        foreach (var e in Services.IsoXml.IsoXmlImportCatalog.Scan(importDir))
        {
            AvailableIsoXmlFiles.Add(new IsoXmlFileItem
            {
                Name = e.Name,
                FullPath = e.Folder,
                FieldIndex = e.FieldIndex,
                ModifiedDate = new DirectoryInfo(e.Folder).LastWriteTime,
                IsTaskData = true
            });
        }
    }

    /// <summary>
    /// Update the headland preview line on the map
    /// </summary>
    private void UpdateHeadlandPreview()
    {
        if (!IsFieldOpen || string.IsNullOrEmpty(CurrentFieldName))
        {
            HeadlandPreviewLine = null;
            return;
        }

        var fieldsDir = _settingsService.Settings.FieldsDirectory;
        if (string.IsNullOrEmpty(fieldsDir))
        {
            fieldsDir = Path.Combine(
                AppDataRoot.Documents, "Fields");
        }

        var fieldPath = Path.Combine(fieldsDir, CurrentFieldName);
        var boundary = LoadFieldBoundary(fieldPath);

        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid)
        {
            HeadlandPreviewLine = null;
            return;
        }

        // Get boundary points as Vec2
        var boundaryPoints = boundary.OuterBoundary.Points
            .Select(p => new Models.Base.Vec2(p.Easting, p.Northing))
            .ToList();

        // Determine join type based on curve/line mode
        var joinType = IsHeadlandCurveMode
            ? Services.Geometry.OffsetJoinType.Round
            : Services.Geometry.OffsetJoinType.Miter;

        // Create preview
        var preview = _headlandBuilderService.PreviewHeadland(boundaryPoints, HeadlandDistance, joinType);
        HeadlandPreviewLine = preview;
    }


    /// <summary>
    /// Calculate heading (in degrees) from point A to point B using Easting/Northing
    /// </summary>
    private double CalculateHeading(Position pointA, Position pointB)
    {
        double dx = pointB.Easting - pointA.Easting;
        double dy = pointB.Northing - pointA.Northing;
        double headingRadians = System.Math.Atan2(dx, dy); // atan2(east, north) for navigation heading
        double headingDegrees = headingRadians * 180.0 / System.Math.PI;
        if (headingDegrees < 0) headingDegrees += 360.0;
        return headingDegrees;
    }

    /// <summary>
    /// Save headland line to file in the active field directory
    /// </summary>
    private void SaveHeadlandToFile(List<Models.Base.Vec3>? headlandPoints)
    {
        var activeField = _fieldService.ActiveField;
        if (activeField == null || string.IsNullOrEmpty(activeField.DirectoryPath))
        {
            return; // No active field to save to
        }

        try
        {
            var headlandLine = new Models.Guidance.HeadlandLine();

            if (headlandPoints != null && headlandPoints.Count > 0)
            {
                var headlandPath = new Models.Guidance.HeadlandPath
                {
                    Name = "Headland",
                    TrackPoints = headlandPoints,
                    MoveDistance = HeadlandDistance,
                    Mode = 0,
                    APointIndex = 0
                };
                headlandLine.Tracks.Add(headlandPath);
            }

            Services.GeoJson.GeoJsonFieldService.SaveHeadlandLine(activeField.DirectoryPath, headlandLine);
            _logger.LogDebug($"[Headland] Saved headland to {activeField.DirectoryPath} ({headlandPoints?.Count ?? 0} points)");
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[Headland] Failed to save headland: {ex.Message}");
        }
    }

    /// <summary>
    /// Save tracks to TrackLines.txt in the active field directory.
    /// Uses WinForms-compatible format via TrackFilesService.
    /// </summary>
    public void SaveTracksToFile() => SaveTracksToFile(refreshNudgeFromGuidance: true);

    /// <param name="refreshNudgeFromGuidance">Recompute the selected track's NudgeDistance from
    /// the cycle's mirror (pass number + nudge). False when the caller has just set
    /// NudgeDistance itself and the mirror has not caught up yet (Swap A/B).</param>
    public void SaveTracksToFile(bool refreshNudgeFromGuidance)
    {
        var activeField = _fieldService.ActiveField;
        if (activeField == null || string.IsNullOrEmpty(activeField.DirectoryPath))
        {
            return; // No active field to save to
        }

        // Update selected track's NudgeDistance from current pass number + nudge offset before
        // saving — but only when State.Guidance is the cycle's mirror FOR this track. Right after
        // selecting a new track (e.g. just created), the mirror still holds the previous track's
        // pass/nudge until the next cycle, and writing it here stamped them onto the new track (#107).
        if (refreshNudgeFromGuidance && SelectedTrack != null && ReferenceEquals(State.Guidance.ActiveTrack, SelectedTrack))
        {
            double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
            SelectedTrack.NudgeDistance = State.Guidance.HowManyPathsAway * widthMinusOverlap + State.Guidance.NudgeOffset;
            _logger.LogDebug($"[NUDGE] SaveTracksToFile: SelectedTrack '{SelectedTrack.Name}' NudgeDistance = {State.Guidance.HowManyPathsAway} * {widthMinusOverlap:F2} + {State.Guidance.NudgeOffset:F3} = {SelectedTrack.NudgeDistance:F2}m");
        }

        // Debug: Log all tracks' NudgeDistance before saving
        foreach (var track in SavedTracks)
        {
            _logger.LogDebug($"[NUDGE] SaveTracksToFile: Saving '{track.Name}': NudgeDistance={track.NudgeDistance:F2}m");
        }

        try
        {
            Services.GeoJson.GeoJsonFieldService.SaveTracks(activeField.DirectoryPath, TracksForSave());
            _logger.LogDebug("[NUDGE] SaveTracksToFile: Saved {TrackCount} tracks", SavedTracks.Count);
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug("[NUDGE] SaveTracksToFile: FAILED - {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Load tracks from field directory.
    /// Supports WinForms TrackLines.txt format (primary) and legacy ABLines.txt format (fallback).
    /// </summary>
    private void LoadTracksFromField(Field? field)
    {
        // Clear existing tracks (State.Field.Tracks mirrors SavedTracks via the subscription)
        SavedTracks.Clear();

        if (field == null || string.IsNullOrEmpty(field.DirectoryPath))
        {
            _logger.LogDebug("[TrackFiles] No field directory to load from");
            return;
        }

        try
        {
            int loadedCount = 0;

            foreach (var track in Services.Track.TrackFile.Load(field.DirectoryPath, PassWidth))
            {
                AddSavedTrack(track); // mirrors into State.Field.Tracks

                // Debug: log track details
                _logger.LogDebug("[TrackFiles] Track: '{TrackName}', Points: {PointCount}, Type: {TrackType}, IsCurve: {IsCurve}", track.Name, track.Points.Count, track.Type, track.IsCurve);
                loadedCount++;
            }

            _logger.LogDebug($"[TrackFiles] Loaded {loadedCount} tracks");

            // Rebuild recorded paths and contour strips from loaded tracks
            RebuildRecordedPathsAndContours();

            // Re-activate the track this field was last worked with (#148).
            RestoreLastUsedTrack(field.DirectoryPath);
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[TrackFiles] Failed to load tracks: {ex.Message}");
        }
    }

    private void LoadRecPathFromField(string fieldPath)
    {
        try
        {
            var recPoints = Services.GeoJson.GeoJsonFieldService.LoadCurrentRecordedPath(fieldPath);
            var recPath = recPoints is { Count: >= 2 }
                ? Track.FromRecordedPath("Recorded Path", recPoints.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList())
                : null;
            // The path in use is usually among the field's tracks already (saved with them when it
            // was recorded): adding it again put one more copy in the track list at every open.
            if (recPath != null && SavedTracks.Any(t => t.IsRecordedPath && Services.Track.TrackFile.SamePoints(t, recPath)))
                recPath = null;
            if (recPath != null)
            {
                AddSavedTrack(recPath);
                RecordedPathTracks.Add(recPath);
                UpdateRecordedPathsOnMap();
                _logger.LogDebug($"[RecPath] Loaded recorded path with {recPath.Points.Count} points");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[RecPath] Failed to load the recorded path: {ex.Message}");
        }
    }
}

/// <summary>
/// View model item for boundary list display
/// </summary>
public class BoundaryListItem
{
    public int Index { get; set; }
    public string BoundaryType { get; set; } = string.Empty;
    /// <summary>Area in hectares — the storage unit. Formatting (ha vs ac) belongs to
    /// the display boundary, so this stays a number all the way to the client.</summary>
    public double AreaHectares { get; set; }
    public bool IsDriveThrough { get; set; }
    public bool IsHard { get; set; }
    public string DriveThruDisplay => IsDriveThrough ? "Yes" : "--";
    public string HardDisplay => IsHard ? "Hard" : "Soft";
}

/// <summary>
/// View model item for field selection list display.
/// </summary>
/// <param name="Name">Field name (directory name).</param>
/// <param name="DirectoryPath">Full path to the field directory.</param>
/// <param name="Distance">Distance to field (currently unused).</param>
/// <param name="Area">Field area in hectares.</param>
/// <param name="NtripProfileName">Associated NTRIP profile name.</param>
public record FieldSelectionItem(
    string Name,
    string DirectoryPath,
    double Distance,
    double Area,
    string NtripProfileName);

/// <summary>
/// View model item for KML file list display
/// </summary>
public class KmlFileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileSizeDisplay => FileSizeBytes < 1024 ? $"{FileSizeBytes} B" :
                                     FileSizeBytes < 1024 * 1024 ? $"{FileSizeBytes / 1024.0:F1} KB" :
                                     $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
}

/// <summary>
/// View model item for ISO-XML file/folder list display
/// </summary>
public class IsoXmlFileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    /// <summary>Which partfield (PFD) of the TASKDATA.XML, in file order.</summary>
    public int FieldIndex { get; set; }
    public DateTime ModifiedDate { get; set; }
    public bool IsTaskData { get; set; }
}