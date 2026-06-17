// Phase 1 wire contract (deliberate, not VM-mirroring). Field-local meters;
// the host sends the field origin once in the Scene, everything else is x=E, y=N.
// Camera is NOT here — it's client-owned (REMOTE_WEB_UI_SPLIT.md §5 rule 3).

namespace AgValoniaGPS.RemoteServer;

/// <summary>A field-local point in meters (x = easting, y = northing).</summary>
public record Vec2Dto(double E, double N);

public record TrackDto(string Id, string Name, int Type, IReadOnlyList<Vec2Dto> Points);

/// <summary>Static-ish geometry: sent on connect and whenever the fingerprint changes.</summary>
public record SceneDto(
    long Version,
    double OriginLat,
    double OriginLon,
    string FieldName,
    bool HasField,
    IReadOnlyList<IReadOnlyList<Vec2Dto>> Boundaries, // each = one outer ring
    IReadOnlyList<TrackDto> Tracks,
    IReadOnlyList<Vec2Dto>? Headland, // inner headland ring (green line), if any
    IReadOnlyList<Vec2Dto>? GuidanceLine, // the followed offset line (magenta), if guiding
    IReadOnlyList<SectionSpanDto> ToolSections, // section spans (static layout); pose is per-Tick
    IReadOnlyList<Vec2Dto>? UTurnPath, // the planned U-turn arc through the headland (green), if active
    IReadOnlyList<Vec2Dto>? NextTrack, // the next pass to pick up after the turn (cyan), if any
    IReadOnlyList<FlagDto> Flags, // field marker flags (Phase 8 follow-up)
    ImageryDto? Imagery); // background field image world-rect + version (PNG served over HTTP)

/// <summary>A field flag marker: field-local position (m) + display colour hex.</summary>
public record FlagDto(double E, double N, string ColorHex);

/// <summary>Background-imagery placement: the field-local world rectangle the
/// PNG (fetched from /backpic.png) covers, plus a version that changes per field
/// so the client cache-busts. Null when no enabled imagery.</summary>
public record ImageryDto(double MinE, double MinN, double MaxE, double MaxN, long Version);

/// <summary>One section's signed offsets from the tool centerline (meters; left
/// negative, right positive), perpendicular to the tool heading. Static-ish —
/// changes only with tool/section config.</summary>
public record SectionSpanDto(double Left, double Right);

/// <summary>Vehicle pose. Heading is RADIANS (0 = north, clockwise); Speed is m/s.</summary>
public record PoseDto(double E, double N, double Heading, double Speed);

// --- Coverage (Phase 2). Display layer streamed as RGB cells; init carries the
//     grid geometry, cells carry painted cells (snapshot on connect, deltas after). ---
public record CoverageInitDto(double CellSize, double OriginE, double OriginN, int Width, int Height);

/// <summary>Flat triples: [cellX, cellY, packedRgb, ...] where packedRgb = (R&lt;&lt;16)|(G&lt;&lt;8)|B.</summary>
public record CoverageCellsDto(int[] Cells);

/// <summary>~10 Hz dynamic feed the client extrapolates/draws from.</summary>
public record TickDto(
    long SceneVersion,
    PoseDto Pose,
    int Fix,
    byte[] Sections, // per-section SectionControlState.ColorCode: 0 off(red) 1 manual-on(yellow)
                     // 2 auto-on(green) 3 turning-off(cyan) 4 turning-on(orange) 5 auto-off(gray)
    // Guidance HUD: cross-track error (m, +right of line), whether guidance is
    // engaged, the line label ("3L"), and the name of the followed track so the
    // client can highlight it among the Scene tracks. CrossTrackError is only
    // meaningful when GuidanceActive.
    double CrossTrackError,
    bool GuidanceActive,
    string LineLabel,
    string? ActiveTrackName,
    // Tool pose (the section spans in the Scene are drawn relative to this).
    // Matches SectionControlService.GetSectionWorldPosition's frame: world edge =
    // (ToolE,ToolN) + (sin,cos)(ToolHeading + π/2) × span. ToolReady gates drawing.
    double ToolE,
    double ToolN,
    double ToolHeading,
    bool ToolReady,
    // Operational state for the right-nav toolbar (Phase 3). Lives at Tick rate so
    // the autosteer 3-state, U-turn arming and distance-to-trigger stay live.
    bool IsAutoSteerEngaged,
    bool AutoSteerAvailable,   // a track is active → autosteer may engage (else greyed)
    bool IsContourMode,
    bool IsSectionAutoMaster,
    bool IsSectionManualAll,
    bool IsYouTurnEnabled,
    bool TurnIsLeft,
    double DistanceToTrigger,
    bool IsActiveTrackClosed,
    double Roll, // vehicle roll angle (degrees) for the roll gauge
    // Bottom-nav field-tools (Phase 8). Toggle states for the bottom toolbar; the
    // AB-dependent buttons gate on ActiveTrackName client-side. TramMode is the
    // TramDisplayMode enum (0 Off / 1 All / 2 LinesOnly / 3 OuterOnly).
    bool HeadlandOn,
    bool SectionInHeadland,
    bool AutoTrack,
    int SkipRows,
    bool SkipRowsOn,
    int TramMode,
    // Headland-distance HUD: live distance to the headland (m; -1 = no headland / not
    // driving → HUD hidden) + the proximity warning flag (near → red box). Gated
    // client-side by Display.HeadlandDistanceVisible (Config frame). Mirrors
    // FieldState.HeadlandProximityDistance/…Warning.
    double HeadlandProximityDistance,
    bool HeadlandProximityWarning);

/// <summary>Top status-bar readouts (Phase 1), sent at a low rate. GPS fix quality
/// + correction age + sat count; the units preference (so the client formats speed
/// itself — speed rides the Tick); and the four module connection states
/// (GPS/IMU/AutoSteer/Machine) with their detected IPs ("" = not detected).</summary>
public record StatusDto(
    int FixQuality,
    string FixQualityText,
    double Age,
    int SatelliteCount,
    bool IsMetric,
    bool GpsOk,
    bool ImuOk,
    bool AutoSteerOk,
    bool MachineOk,
    string ImuIp,
    string AutoSteerIp,
    string MachineIp,
    // Module-configured flags (the aggregate "Modules" dot is green only when every
    // CONFIGURED module is present — mirrors MainViewModel.ModuleStatusKind).
    bool GpsConfigured,
    bool ImuConfigured,
    bool AutoSteerConfigured,
    bool MachineConfigured,
    // Rotating bottom-line inputs the client can't derive from the Scene: the active
    // job's task name and the worked area (m²). Field name, workable area (boundary/
    // headland), tool width and speed the client already has, so it formats + rotates
    // the three pages (Field / Stats / AB-line) itself, matching the native strip.
    string JobName,
    double WorkedAreaSqM,
    // GPS-detail card (Phase 5): lat/lon (degrees), altitude (m) and HDOP for the
    // popup toggled by the strip's fix dot. Rides the ~2 Hz Status — plenty for a
    // readout. (Heading + roll for that card ride the 10 Hz Tick; sats/age/fix are
    // already above.) Lat/lon need f64 precision; alt/hdop go out as f32.
    double Latitude,
    double Longitude,
    double Altitude,
    double Hdop,
    // Simulator panel (Phase 6): enabled + raw speed (kph, before 10×) + steer angle
    // (deg) + 10× toggle. The client applies the 10× multiplier + unit formatting.
    bool SimEnabled,
    double SimSpeedKph,
    double SimSteerAngle,
    bool Sim10x,
    // AutoSteer live telemetry (Phase 9 AutoSteer panel): the steering-sensor /
    // test-mode tabs and Zero-WAS need the module's live readout. ActualSteerAngle is
    // the smoothed wheel angle (deg, from PGN 253); SensorPercent is the WAS position
    // 0..100; SetSteerAngle is the commanded angle; FreeDriveAngle / SteerFreeDrive
    // drive the free-drive test display. Rides the ~2 Hz Status (the panel display is
    // throttled to ~10 Hz native anyway).
    double ActualSteerAngle,
    double SensorPercent,
    double SetSteerAngle,
    double FreeDriveAngle,
    bool SteerFreeDrive,
    // Smart-WAS calibration (the dialog launched from the AutoSteer panel). Live
    // snapshot of the accumulating analysis — mirrors ISmartWasCalibrationService
    // .GetSnapshot(). Offset is degrees; the client multiplies by CountsPerDegree
    // for the counts readout. Rides Status (mostly zero unless collecting).
    bool SmartWasCollecting,
    int SmartWasSamples,
    double SmartWasMean,
    double SmartWasMedian,
    double SmartWasStdDev,
    double SmartWasOffsetDeg,
    double SmartWasConfidence,
    bool SmartWasValid);

/// <summary>Config read-frame (Phase 9). A structured projection of
/// ConfigurationStore for the left-nav settings panels — seeded on connect and
/// re-sent when the config fingerprint changes. Grows a section per sub-phase;
/// the wire stays append-only. Vehicle dialog = Vehicle + Gps + Roll; Tool dialog =
/// Tool + Uturn + Tram + Machine.</summary>
public record ConfigDto(VehicleConfigDto Vehicle, GpsConfigDto Gps, RollConfigDto Roll,
    ToolConfigDto Tool, UturnConfigDto Uturn, TramConfigDto Tram, MachineConfigDto Machine,
    DisplayConfigDto Display, AutoSteerConfigDto AutoSteer);

/// <summary>AutoSteer config panel (ConfigStore.AutoSteer) — the full native 9-tab
/// surface. Grouped by tab: Pure-Pursuit/Stanley, Steering-Sensor, Deadzone/Timing,
/// Gain/PWM, Turn-Sensors, Hardware-Config, Algorithm, Speed-Limits, Display. Mirrors
/// AutoSteerConfig 1:1 so config.set|autosteer.&lt;field&gt; round-trips.</summary>
public record AutoSteerConfigDto(
    // Tab 1 — Pure Pursuit / Stanley
    double SteerResponseHold, double IntegralGain, bool IsStanleyMode,
    double StanleyAggressiveness, double StanleyOvershootReduction,
    // Tab 2 — Steering Sensor
    int WasOffset, double CountsPerDegree, int Ackermann, int MaxSteerAngle,
    // Tab 3 — Deadzone / Timing
    double DeadzoneHeading, int DeadzoneDelay, double SpeedFactor, double AcquireFactor,
    // Tab 4 — Gain / PWM
    int ProportionalGain, int MaxPwm, int MinPwm,
    // Tab 5 — Turn Sensors
    bool TurnSensorEnabled, bool PressureSensorEnabled, bool CurrentSensorEnabled,
    int TurnSensorCounts, int PressureTripPoint, int CurrentTripPoint,
    // Tab 6 — Hardware Config
    bool DanfossEnabled, bool InvertWas, bool InvertMotor, bool InvertRelays,
    int MotorDriver, int AdConverter, int ImuAxisSwap, int ExternalEnable,
    // Tab 7 — Algorithm
    double UTurnCompensation, double SideHillCompensation, bool SteerInReverse,
    // Tab 8 — Speed Limits
    bool ManualTurnsEnabled, double ManualTurnsSpeed, double MinSteerSpeed, double MaxSteerSpeed,
    // Tab 9 — Display
    int LineWidth, int NudgeDistance, double NextGuidanceTime, int CmPerPixel,
    bool LightbarEnabled, bool SteerBarEnabled, bool GuidanceBarOn);

/// <summary>Screen &amp; Alerts panel (ConfigStore.Display): display toggles, on-screen
/// buttons, alert sounds, plus the App-Settings device flags (keyboard / start
/// fullscreen / elevation log). ResolutionLabel mirrors DisplayResolutionLabel.</summary>
public record DisplayConfigDto(
    bool GridVisible, bool FieldTextureVisible, bool FieldTextureMoveable, bool SvennArrowVisible,
    bool HeadlandDistanceVisible, bool LineSmoothEnabled, bool AutoDayNight, bool HardwareMessagesEnabled,
    bool ExtraGuidelines, int ExtraGuidelinesCount, string ResolutionLabel,
    bool UTurnButtonVisible, bool LateralButtonVisible,
    bool AutoSteerSound, bool UTurnSound, bool HydraulicSound, bool SectionsSound,
    bool KeyboardEnabled, bool StartFullscreen, bool ElevationLogEnabled);

/// <summary>Tool/Implement tab (ConfigStore.Tool + NumSections). Type: 0 front, 1 rear,
/// 2 TBT, 3 trailing. Arrays fixed-size (16 widths/colours, 9 zone ranges).</summary>
public record ToolConfigDto(
    int Type, int HitchType, double HitchLength, double TrailingHitchLength,
    double TankTrailingHitchLength, double Length,
    double LookAheadOn, double LookAheadOff, double TurnOffDelay,
    double Offset, double Overlap, double TrailingToolToPivotLength,
    bool IsSectionsNotZones, int NumSections, double DefaultSectionWidth,
    IReadOnlyList<double> SectionWidths, int Zones, IReadOnlyList<int> ZoneRanges,
    bool IsMultiColoredSections, IReadOnlyList<int> SectionColors, int SingleCoverageColor,
    bool IsSectionOffWhenOut, bool IsHeadlandSectionControl, int MinCoverage,
    double SlowSpeedCutoff, double CoverageMargin,
    bool IsWorkSwitchEnabled, bool IsWorkSwitchActiveLow, bool IsWorkSwitchManualSections,
    bool IsSteerSwitchEnabled, bool IsSteerSwitchManualSections,
    double TotalWidth);

/// <summary>U-Turn tab (ConfigStore.Guidance). Style: 0 Omega, 1 Sagitta.</summary>
public record UturnConfigDto(int Style, double Extension, int Smoothing, double Radius, double DistanceFromBoundary);

/// <summary>Tram Lines tab (ConfigStore.Guidance tram fields).</summary>
public record TramConfigDto(int Passes, bool Display, int Line);

/// <summary>Machine Control tab (ConfigStore.Machine). PinAssignments: 24 PinFunction ints.</summary>
public record MachineConfigDto(
    bool HydraulicLiftEnabled, int RaiseTime, double LookAhead, int LowerTime, bool InvertRelay,
    int User1, int User2, int User3, int User4, IReadOnlyList<int> PinAssignments);

/// <summary>Vehicle &amp; Tool picker hub (Phase 9): available profiles (name + preview)
/// and the active pair. Seeded on connect, re-sent when the list/active/config changes.</summary>
public record ProfilesDto(
    string ActiveVehicle,
    string ActiveTool,
    IReadOnlyList<ProfileEntryDto> Vehicles,
    IReadOnlyList<ProfileEntryDto> Tools);

public record ProfileEntryDto(string Name, string Preview);

/// <summary>Steer Wizard frame (Phase 9) — host-driven: the real SteerWizardViewModel
/// runs on the host and this projects its state each tick while the remote wizard is
/// open. The browser renders a layout per <see cref="StepKind"/> (binding editable
/// values to the existing Config frame + writing via wizard.set) and forwards
/// navigation / actions. The live blob carries the calibration-step dynamics (phase /
/// progress / measured diameter / RTK gating) that aren't in ConfigStore.</summary>
public record WizardDto(
    int StepIndex, int TotalSteps, string StepKind, string Title, string Description,
    bool CanBack, bool CanNext, bool CanSkip, bool IsLast, string Validation,
    // Persistent status bar (live hardware data across every step).
    double StatusWas, double StatusRoll, string StatusGps, double StatusSpeed, int StatusPwm, bool StatusConnected,
    // Per-step live state (zeroed/empty for steps that don't use a field).
    int HardwareLevel,
    double LiveAngle, double LiveRoll, double LiveError,
    string TestPhase, string TestResult, double TestProgress, bool TestActive,
    bool RtkFixed, string FixLabel, double Diameter);

/// <summary>Vehicle tab: type / hitch / dimensions / antenna (ConfigStore.Vehicle).</summary>
public record VehicleConfigDto(
    string Name,
    int Type,            // VehicleType: 0 Tractor, 1 Harvester, 2 FourWD
    int HitchType,       // code; index into HitchTypeOptions = HitchType + 1
    double HitchLength,
    double Wheelbase,
    double TrackWidth,
    double AntennaPivot,
    double AntennaHeight,
    double AntennaOffset);

/// <summary>Data Sources → GPS tab (ConfigStore.Connections).</summary>
public record GpsConfigDto(
    bool IsDualGps,
    double DualHeadingOffset,
    double DualReverseDistance,
    bool AutoDualFix,
    double DualSwitchSpeed,
    double MinGpsStep,
    double FixToFixDistance,
    double HeadingFusionWeight,
    bool ReverseDetection,
    bool RtkLostAlarm,
    int RtkLostAction);  // 0 Warn, 1 Pause AutoSteer

/// <summary>Data Sources → Roll tab (ConfigStore.Ahrs).</summary>
public record RollConfigDto(
    double RollZero,
    double RollFilter,
    bool IsRollInvert);

/// <summary>Remote-actuation authority state (Phase 2). Broadcast on change; the
/// client compares HolderId to its own id (sent once in the Hello frame) to know
/// whether it is the holder. Held=false means no client has control.</summary>
public record ControlStateDto(bool Held, string HolderId, string HolderName);
