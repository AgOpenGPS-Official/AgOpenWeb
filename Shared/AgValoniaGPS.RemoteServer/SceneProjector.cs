// Projects the live ApplicationState (the pipeline's single source of truth)
// into the wire contract. This is the litmus test in action: the data the map
// needs is filled from the pipeline/state, never from view-state.

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class SceneProjector
{
    private readonly ApplicationState _state;
    private readonly ISectionControlService _sections;
    private readonly IToolPositionService _tool;
    private readonly ConfigurationStore _config;
    private readonly ICoverageMapService _coverage;
    private readonly IJobService _jobs;
    private readonly IConfigurationService _configService;
    private readonly IAutoSteerService _autoSteer;
    private readonly ISmartWasCalibrationService _smartWas;
    private readonly IUdpCommunicationService _udp;
    private readonly INtripProfileService _ntripProfiles;

    public SceneProjector(ApplicationState state, ISectionControlService sections,
        IToolPositionService tool, ConfigurationStore config,
        ICoverageMapService coverage, IJobService jobs, IConfigurationService configService,
        IAutoSteerService autoSteer, ISmartWasCalibrationService smartWas,
        IUdpCommunicationService udp, INtripProfileService ntripProfiles)
    {
        _state = state;
        _sections = sections;
        _tool = tool;
        _config = config;
        _coverage = coverage;
        _jobs = jobs;
        _configService = configService;
        _autoSteer = autoSteer;
        _smartWas = smartWas;
        _udp = udp;
        _ntripProfiles = ntripProfiles;
    }

    public SceneDto BuildScene(long version)
    {
        var f = _state.Field;

        // The live boundary the UI draws lives on ActiveField.Boundary (a single
        // Boundary with an outer ring + optional inner holes) — NOT the (unused)
        // Field.Boundaries collection.
        var boundaries = new List<IReadOnlyList<Vec2Dto>>();
        var bnd = f.ActiveField?.Boundary;
        if (bnd is not null)
        {
            AddRing(boundaries, bnd.OuterBoundary);
            if (bnd.InnerBoundaries != null)
                foreach (var inner in bnd.InnerBoundaries.ToArray())
                    AddRing(boundaries, inner);
        }

        var tracks = f.Tracks.ToArray()
            .Where(t => t.IsVisible && t.Points.Count >= 2)
            .Select((t, i) => new TrackDto(
                i.ToString(),
                t.Name,
                (int)t.Type,
                t.Points.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList()))
            .ToList();

        // Headland (the green inner line) — the live one the app draws lives on
        // State.Field.HeadlandLine, set in lockstep with the VM's _currentHeadlandLine
        // (NOT Boundary.HeadlandPolygon, which is only seeded on file load).
        IReadOnlyList<Vec2Dto>? headland = null;
        var hl = f.HeadlandLine;
        if (hl != null && hl.Count >= 3)
            headland = hl.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // The followed offset line (DisplayLine) — the line the tractor is
        // steering to, parallel to the active reference track and shifted by the
        // current pass. Snapshot the list (it's mutated on the UI thread).
        IReadOnlyList<Vec2Dto>? guidanceLine = null;
        var dl = _state.Guidance.DisplayLine?.ToArray();
        if (dl is { Length: >= 2 })
            guidanceLine = dl.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // Tool/section layout — static spans (PositionLeft/Right, offsets from the
        // tool centerline). The pose is per-Tick; the client transforms these by it.
        int nSec = Math.Clamp(_sections.NumSections, 0, ToolConfig.MaxSections);
        var states = _sections.SectionStates;
        var toolSections = new List<SectionSpanDto>(nSec);
        for (int i = 0; i < nSec && i < states.Count; i++)
            toolSections.Add(new SectionSpanDto(states[i].PositionLeft, states[i].PositionRight));

        // Planned U-turn path through the headland (when a turn is active).
        IReadOnlyList<Vec2Dto>? uTurnPath = null;
        var tp = _state.YouTurn.TurnPath?.ToArray();
        if (tp is { Length: >= 2 })
            uTurnPath = tp.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // The next pass the tractor will pick up after the turn (cyan until it
        // becomes the active line). State.YouTurn.NextTrack is the upcoming Track.
        IReadOnlyList<Vec2Dto>? nextTrack = null;
        var nt = _state.YouTurn.NextTrack?.Points?.ToArray();
        if (nt is { Length: >= 2 })
            nextTrack = nt.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList();

        // Field flags (markers). Field-local position + display colour hex.
        var flags = (f.Flags ?? System.Array.Empty<Models.State.FlagMarker>())
            .Select(fl => new FlagDto(fl.Easting, fl.Northing, fl.ColorHex)).ToList();

        // Background imagery: just the world rectangle + a per-image version
        // (the PNG itself is fetched over HTTP at /backpic.png?v=version).
        ImageryDto? imagery = null;
        var im = f.Imagery;
        if (im is not null)
            imagery = new ImageryDto(im.MinE, im.MinN, im.MaxE, im.MaxN, ImageVersion(im.Path));

        return new SceneDto(
            version,
            f.OriginLatitude,
            f.OriginLongitude,
            f.FieldName,
            f.ActiveField != null,
            boundaries,
            tracks,
            headland,
            guidanceLine,
            toolSections,
            uTurnPath,
            nextTrack,
            flags,
            imagery);
    }

    // Deterministic per-path version (string.GetHashCode is process-randomized).
    private static long ImageVersion(string path)
    {
        long h = 17;
        foreach (char c in path) h = h * 31 + c;
        return h;
    }

    private static void AddRing(List<IReadOnlyList<Vec2Dto>> rings, BoundaryPolygon? poly)
    {
        if (poly is { Points.Count: >= 3 })
            rings.Add(poly.Points.Select(p => new Vec2Dto(p.Easting, p.Northing)).ToList());
    }

    public TickDto BuildTick(long sceneVersion)
    {
        var v = _state.Vehicle;
        // Section on/off comes from SectionControlService (the authoritative source
        // coverage paints from). IsOn = the section is on/laying product.
        // Per-section display state = the canonical 6-state ColorCode (same source
        // as the native section bar / map renderer): 0 off(red) 1 manual-on(yellow)
        // 2 auto-on(green) 3 turning-off(cyan) 4 turning-on(orange) 5 auto-off(gray).
        var secStates = _sections.SectionStates;
        int n = Math.Clamp(_sections.NumSections, 0, ToolConfig.MaxSections);
        var sections = new byte[n];
        for (int i = 0; i < n && i < secStates.Count; i++)
            sections[i] = (byte)secStates[i].ColorCode;

        var g = _state.Guidance;

        return new TickDto(
            sceneVersion,
            // VehicleState.Heading is DEGREES (0 = north, clockwise); the wire
            // carries RADIANS so the client can ctx.rotate / sin / cos directly
            // (matching the native map control's headingRadians convention).
            // Without this the marker spun ~57× per revolution during turns.
            new PoseDto(v.Easting, v.Northing, v.Heading * System.Math.PI / 180.0, v.Speed),
            v.FixQuality,
            sections,
            g.CrossTrackError,
            // Lightbar gate — mirror the native exactly (MainWindow feeds the
            // LightBarPanel with hasGuidance = HasActiveTrack || contour || rec-path,
            // NOT autosteer-engaged). IsGuidanceActive is dead (never set true).
            _state.Field.ActiveTrack != null
                || g.IsContourMode
                || _state.RecordedPath.IsDrivingRecordedPath,
            g.CurrentLineLabel,
            _state.Field.ActiveTrack?.Name,
            _tool.ToolPosition.Easting,
            _tool.ToolPosition.Northing,
            _tool.ToolHeading,
            _tool.IsToolPositionReady,
            // Operational state (right-nav toolbar). Engaged + contour + U-turn
            // direction/distance come from state; the three mode flags from the
            // VM mirror (ApplicationState.Operation).
            _state.Operation.IsAutoSteerEngaged,
            _state.Operation.IsAutoSteerAvailable,
            _state.Operation.IsContourOn,
            _state.Operation.IsSectionAutoMaster,
            _state.Operation.IsSectionManualAll,
            _state.Operation.IsYouTurnEnabled,
            _state.YouTurn.IsTurnLeft,
            _state.YouTurn.DistanceToTrigger,
            _state.Field.ActiveTrack?.IsClosed == true,
            _state.Vehicle.Roll,
            // Bottom-nav field-tools (Phase 8). Toggle states from the FieldTools
            // mirror; tram mode straight from config (no VM mirror needed).
            _state.FieldTools.IsHeadlandOn,
            _config.Tool.IsHeadlandSectionControl, // single source (read live from config)
            _state.FieldTools.IsAutoTrackEnabled,
            _state.FieldTools.UTurnSkipRows,
            _state.FieldTools.IsUTurnSkipRowsEnabled,
            (int)_config.Tram.DisplayMode,
            // Headland-distance HUD (-1 = no headland / not driving → hidden client-side).
            _state.Field.HeadlandProximityDistance ?? -1.0,
            _state.Field.HeadlandProximityWarning,
            // Steer-bar error: actual WAS angle − commanded guidance steer angle.
            _autoSteer.LastSteerData.ActualSteerAngle - _state.Guidance.SteerAngle);
    }

    // Top status-bar readouts (Phase 1). All state-projected: fix/age/sats from
    // VehicleState, module health + IPs from ConnectionState, units from config.
    public StatusDto BuildStatus()
    {
        var v = _state.Vehicle;
        var c = _state.Connections;
        var cfg = _config.Connections;
        var sw = _smartWas.GetSnapshot();
        return new StatusDto(
            v.FixQuality,
            v.FixQualityText,
            v.Age,
            v.SatelliteCount,
            _config.IsMetric,
            c.IsGpsDataOk,
            c.IsImuDataOk,
            c.IsAutoSteerDataOk,
            c.IsMachineDataOk,
            c.ImuIpAddress ?? "",
            c.AutoSteerIpAddress ?? "",
            c.MachineIpAddress ?? "",
            cfg.IsGpsConfigured,
            cfg.IsImuConfigured,
            cfg.IsAutoSteerConfigured,
            cfg.IsMachineConfigured,
            _jobs.ActiveJob?.TaskName ?? "",
            _coverage.TotalWorkedArea,
            v.Latitude,
            v.Longitude,
            v.Altitude,
            v.Hdop,
            _state.Simulator.IsEnabled,
            _state.Simulator.SpeedKph,
            _state.Simulator.SteerAngle,
            _state.Simulator.Is10x,
            // AutoSteer live telemetry: smoothed wheel angle + WAS position + free-drive
            // state for the AutoSteer panel's steering-sensor / test-mode tabs. In free
            // drive the commanded ("set") angle is the free-drive angle.
            _autoSteer.LastSteerData.ActualSteerAngle,
            _autoSteer.SensorPercent,
            _autoSteer.IsInFreeDriveMode ? _autoSteer.FreeDriveSteerAngle : 0.0,
            _autoSteer.FreeDriveSteerAngle,
            _autoSteer.IsInFreeDriveMode,
            // Smart-WAS calibration snapshot (atomic; SoT for the web dialog too).
            sw.IsCollecting, sw.SampleCount, sw.Mean, sw.Median, sw.StdDev,
            sw.RecommendedOffset, sw.Confidence, sw.HasValidCalibration,
            // Network IO panel.
            c.GpsIpAddress ?? "",
            c.ModuleSubnet ?? "",
            string.Join("\n", _udp.GetLocalIpAddresses()),
            c.IsNtripConnected,
            c.NtripStatus ?? "",
            c.NtripBytesReceived,
            c.NtripTestStatus ?? "");
    }

    // NTRIP profiles read-frame (Network IO). Projects INtripProfileService's saved
    // profiles + the fields they can associate with. Re-sent on a fingerprint change.
    public NtripProfilesDto BuildNtripProfiles()
    {
        var profiles = _ntripProfiles.Profiles
            .Select(p => new NtripProfileDto(
                p.Id, p.Name, p.CasterHost, p.CasterPort, p.MountPoint,
                p.Username, p.Password, p.AutoConnectOnFieldLoad, p.IsDefault,
                p.AssociatedFields.ToList()))
            .ToList();
        return new NtripProfilesDto(profiles, _ntripProfiles.GetAvailableFields().ToList());
    }

    // Re-send the NTRIP profiles frame on add / edit / delete / set-default.
    public long NtripProfilesFingerprint()
    {
        long h = 17;
        foreach (var p in _ntripProfiles.Profiles)
        {
            h = h * 31 + (p.Id?.GetHashCode() ?? 0);
            h = h * 31 + (p.Name?.GetHashCode() ?? 0);
            h = h * 31 + (p.CasterHost?.GetHashCode() ?? 0);
            h = h * 31 + p.CasterPort;
            h = h * 31 + (p.MountPoint?.GetHashCode() ?? 0);
            h = h * 31 + (p.Username?.GetHashCode() ?? 0);
            h = h * 31 + (p.IsDefault ? 1 : 0) + (p.AutoConnectOnFieldLoad ? 2 : 0);
            h = h * 31 + p.AssociatedFields.Count;
            foreach (var f in p.AssociatedFields) h = h * 31 + (f?.GetHashCode() ?? 0);
        }
        foreach (var f in _ntripProfiles.GetAvailableFields()) h = h * 31 + (f?.GetHashCode() ?? 0);
        return h;
    }

    // Config read-frame (Phase 9). Projects editable ConfigurationStore values for
    // the left-nav settings panels. Grows a section per sub-phase.
    public ConfigDto BuildConfig()
    {
        var v = _config.Vehicle;
        var c = _config.Connections;
        var a = _config.Ahrs;
        var t = _config.Tool;
        var g = _config.Guidance;
        var m = _config.Machine;
        int toolType = t.IsToolFrontFixed ? 0 : t.IsToolRearFixed ? 1 : t.IsToolTBT ? 2 : 3;
        var widths = new double[16]; for (int i = 0; i < 16; i++) widths[i] = t.GetSectionWidth(i);
        var colors = new int[16]; for (int i = 0; i < 16; i++) colors[i] = (int)t.GetSectionColor(i);
        var zones = new int[9]; for (int i = 0; i < 9; i++) zones[i] = t.GetZoneEndSection(i);
        var pins = new int[24]; for (int i = 0; i < 24; i++) pins[i] = (int)m.GetPinAssignment(i);
        return new ConfigDto(
            new VehicleConfigDto(v.Name, (int)v.Type, v.HitchType, v.HitchLength, v.Wheelbase,
                v.TrackWidth, v.AntennaPivot, v.AntennaHeight, v.AntennaOffset),
            new GpsConfigDto(c.IsDualGps, c.DualHeadingOffset, c.DualReverseDistance, c.AutoDualFix,
                c.DualSwitchSpeed, c.MinGpsStep, c.FixToFixDistance, c.HeadingFusionWeight,
                c.ReverseDetection, c.RtkLostAlarm, c.RtkLostAction),
            new RollConfigDto(a.RollZero, a.RollFilter, a.IsRollInvert),
            new ToolConfigDto(toolType, t.HitchType, t.HitchLength, t.TrailingHitchLength,
                t.TankTrailingHitchLength, t.Length, t.LookAheadOnSetting, t.LookAheadOffSetting,
                t.TurnOffDelay, t.Offset, t.Overlap, t.TrailingToolToPivotLength,
                t.IsSectionsNotZones, _config.NumSections, t.DefaultSectionWidth, widths,
                t.Zones, zones, t.IsMultiColoredSections, colors, (int)t.SingleCoverageColor,
                t.IsSectionOffWhenOut, t.IsHeadlandSectionControl, t.MinCoverage, t.SlowSpeedCutoff,
                t.CoverageMargin, t.IsWorkSwitchEnabled, t.IsWorkSwitchActiveLow, t.IsWorkSwitchManualSections,
                t.IsSteerSwitchEnabled, t.IsSteerSwitchManualSections, _config.ActualToolWidth),
            new UturnConfigDto(g.UTurnStyle, g.UTurnExtension, g.UTurnSmoothing, g.UTurnRadius, g.UTurnDistanceFromBoundary),
            new TramConfigDto(g.TramPasses, g.TramDisplay, g.TramLine),
            new MachineConfigDto(m.HydraulicLiftEnabled, m.RaiseTime, m.LookAhead, m.LowerTime, m.InvertRelay,
                m.User1Value, m.User2Value, m.User3Value, m.User4Value, pins),
            BuildDisplay(), BuildAutoSteer());
    }

    // AutoSteer config tab — projects the full 9-tab ConfigStore.AutoSteer surface.
    private AutoSteerConfigDto BuildAutoSteer()
    {
        var a = _config.AutoSteer;
        return new AutoSteerConfigDto(
            a.SteerResponseHold, a.IntegralGain, a.IsStanleyMode,
            a.StanleyAggressiveness, a.StanleyOvershootReduction,
            a.WasOffset, a.CountsPerDegree, a.Ackermann, a.MaxSteerAngle,
            a.DeadzoneHeading, a.DeadzoneDelay, a.SpeedFactor, a.AcquireFactor,
            a.ProportionalGain, a.MaxPwm, a.MinPwm,
            a.TurnSensorEnabled, a.PressureSensorEnabled, a.CurrentSensorEnabled,
            a.TurnSensorCounts, a.PressureTripPoint, a.CurrentTripPoint,
            a.DanfossEnabled, a.InvertWas, a.InvertMotor, a.InvertRelays,
            a.MotorDriver, a.AdConverter, a.ImuAxisSwap, a.ExternalEnable,
            a.UTurnCompensation, a.SideHillCompensation, a.SteerInReverse,
            a.ManualTurnsEnabled, a.ManualTurnsSpeed, a.MinSteerSpeed, a.MaxSteerSpeed,
            a.LineWidth, a.NudgeDistance, a.NextGuidanceTime, a.CmPerPixel,
            a.LightbarEnabled, a.SteerBarEnabled, a.GuidanceBarOn);
    }

    // Resolution label mirrors MainViewModel.DisplayResolutionLabel.
    private DisplayConfigDto BuildDisplay()
    {
        var d = _config.Display;
        var rm = d.DisplayResolutionMultiplier;
        string res = rm < 1.25 ? "Ultra" : rm < 2.0 ? "High" : rm < 3.25 ? "Med" : rm < 5.0 ? "Low" : "Min";
        return new DisplayConfigDto(
            d.GridVisible, d.FieldTextureVisible, d.FieldTextureMoveable, d.SvennArrowVisible,
            d.HeadlandDistanceVisible, d.LineSmoothEnabled, d.AutoDayNight, d.HardwareMessagesEnabled,
            d.ExtraGuidelines, d.ExtraGuidelinesCount, res,
            d.UTurnButtonVisible, d.LateralButtonVisible,
            d.AutoSteerSound, d.UTurnSound, d.HydraulicSound, d.SectionsSound,
            d.KeyboardEnabled, d.StartFullscreen, d.ElevationLogEnabled);
    }

    // Profiles read-frame (Phase 9) — the Vehicle & Tool picker hub: available
    // vehicle/tool profiles (name + preview) and the active pair.
    public ProfilesDto BuildProfiles()
    {
        var vehicles = _configService.GetAvailableProfiles()
            .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
            .Select(n => new ProfileEntryDto(n, _configService.GetVehicleProfilePreview(n))).ToList();
        var tools = _configService.GetAvailableToolProfiles()
            .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
            .Select(n => new ProfileEntryDto(n, _configService.GetToolProfilePreview(n))).ToList();
        return new ProfilesDto(_config.ActiveVehicleProfileName, _config.ActiveToolProfileName, vehicles, tools);
    }

    // Re-send the Profiles frame when the list, the active pair, or the active config
    // changes (so the active profile's preview stays live). Names + active + config fp.
    public long ProfilesFingerprint()
    {
        long h = 17;
        foreach (var n in _configService.GetAvailableProfiles()) h = h * 31 + n.GetHashCode();
        h = h * 31 + 7;
        foreach (var n in _configService.GetAvailableToolProfiles()) h = h * 31 + n.GetHashCode();
        h = h * 31 + _config.ActiveVehicleProfileName.GetHashCode();
        h = h * 31 + _config.ActiveToolProfileName.GetHashCode();
        return h * 31 + ConfigFingerprint();
    }

    // Change-detector so the broadcaster re-sends Config only when an editable value
    // actually changes (e.g. the user edits a field, or a profile loads).
    public long ConfigFingerprint()
    {
        var v = _config.Vehicle; var c = _config.Connections; var a = _config.Ahrs;
        long h = 17;
        h = h * 31 + v.Name.GetHashCode();
        h = h * 31 + (int)v.Type * 31 + v.HitchType;
        h = h * 31 + (c.IsDualGps ? 1 : 0) * 7 + (c.AutoDualFix ? 1 : 0) * 11
                   + (c.ReverseDetection ? 1 : 0) * 13 + (c.RtkLostAlarm ? 1 : 0) * 17
                   + c.RtkLostAction * 19 + (a.IsRollInvert ? 1 : 0) * 23;
        foreach (var d in new[] { v.HitchLength, v.Wheelbase, v.TrackWidth, v.AntennaPivot,
                                  v.AntennaHeight, v.AntennaOffset, c.DualHeadingOffset,
                                  c.DualReverseDistance, c.DualSwitchSpeed, c.MinGpsStep,
                                  c.FixToFixDistance, c.HeadingFusionWeight, a.RollZero, a.RollFilter })
            h = h * 31 + d.GetHashCode();
        // Tool / U-Turn / Tram / Machine (so Tool-dialog edits re-send the frame).
        var t = _config.Tool; var g = _config.Guidance; var mc = _config.Machine;
        h = h * 31 + (t.IsToolFrontFixed ? 1 : t.IsToolRearFixed ? 2 : t.IsToolTBT ? 3 : 4);
        h = h * 31 + t.HitchType + t.Zones * 31 + _config.NumSections * 7
              + (t.IsSectionsNotZones ? 1 : 0) + (t.IsMultiColoredSections ? 2 : 0)
              + (t.IsSectionOffWhenOut ? 4 : 0) + (t.IsHeadlandSectionControl ? 8 : 0) + t.MinCoverage
              + (t.IsWorkSwitchEnabled ? 16 : 0) + (t.IsWorkSwitchActiveLow ? 32 : 0) + (t.IsWorkSwitchManualSections ? 64 : 0)
              + (t.IsSteerSwitchEnabled ? 128 : 0) + (t.IsSteerSwitchManualSections ? 256 : 0);
        foreach (var d in new[] { t.HitchLength, t.TrailingHitchLength, t.TankTrailingHitchLength, t.Length,
                                  t.LookAheadOnSetting, t.LookAheadOffSetting, t.TurnOffDelay, t.Offset, t.Overlap,
                                  t.TrailingToolToPivotLength, t.DefaultSectionWidth, t.SlowSpeedCutoff, t.CoverageMargin,
                                  g.UTurnExtension, g.UTurnRadius, g.UTurnDistanceFromBoundary, mc.LookAhead })
            h = h * 31 + d.GetHashCode();
        for (int i = 0; i < 16; i++) h = h * 31 + t.GetSectionWidth(i).GetHashCode() + (int)t.GetSectionColor(i);
        for (int i = 0; i < 9; i++) h = h * 31 + t.GetZoneEndSection(i);
        h = h * 31 + (int)t.SingleCoverageColor;
        h = h * 31 + g.UTurnStyle * 7 + g.UTurnSmoothing * 11 + g.TramPasses * 13 + (g.TramDisplay ? 1 : 0) + g.TramLine * 17;
        h = h * 31 + (mc.HydraulicLiftEnabled ? 1 : 0) + mc.RaiseTime * 7 + mc.LowerTime * 11 + (mc.InvertRelay ? 64 : 0)
              + mc.User1Value + mc.User2Value * 3 + mc.User3Value * 5 + mc.User4Value * 7;
        for (int i = 0; i < 24; i++) h = h * 31 + (int)mc.GetPinAssignment(i);
        // Screen & Alerts (Display).
        var dp = _config.Display;
        int db = (dp.GridVisible ? 1 : 0) | (dp.FieldTextureVisible ? 2 : 0) | (dp.FieldTextureMoveable ? 4 : 0)
            | (dp.SvennArrowVisible ? 8 : 0) | (dp.HeadlandDistanceVisible ? 16 : 0) | (dp.LineSmoothEnabled ? 32 : 0)
            | (dp.AutoDayNight ? 64 : 0) | (dp.HardwareMessagesEnabled ? 128 : 0) | (dp.ExtraGuidelines ? 256 : 0)
            | (dp.UTurnButtonVisible ? 512 : 0) | (dp.LateralButtonVisible ? 1024 : 0) | (dp.AutoSteerSound ? 2048 : 0)
            | (dp.UTurnSound ? 4096 : 0) | (dp.HydraulicSound ? 8192 : 0) | (dp.SectionsSound ? 16384 : 0)
            | (dp.KeyboardEnabled ? 32768 : 0) | (dp.StartFullscreen ? 65536 : 0) | (dp.ElevationLogEnabled ? 131072 : 0);
        h = h * 31 + db;
        h = h * 31 + dp.ExtraGuidelinesCount * 7 + dp.DisplayResolutionMultiplier.GetHashCode();
        // AutoSteer config (so AutoSteer-panel edits re-send the frame).
        var asc = _config.AutoSteer;
        int ab = (asc.IsStanleyMode ? 1 : 0) | (asc.TurnSensorEnabled ? 2 : 0) | (asc.PressureSensorEnabled ? 4 : 0)
            | (asc.CurrentSensorEnabled ? 8 : 0) | (asc.DanfossEnabled ? 16 : 0) | (asc.InvertWas ? 32 : 0)
            | (asc.InvertMotor ? 64 : 0) | (asc.InvertRelays ? 128 : 0) | (asc.SteerInReverse ? 256 : 0)
            | (asc.ManualTurnsEnabled ? 512 : 0) | (asc.LightbarEnabled ? 1024 : 0) | (asc.SteerBarEnabled ? 2048 : 0)
            | (asc.GuidanceBarOn ? 4096 : 0);
        h = h * 31 + ab;
        h = h * 31 + asc.WasOffset * 3 + asc.Ackermann * 5 + asc.MaxSteerAngle * 7 + asc.DeadzoneDelay * 11
              + asc.ProportionalGain * 13 + asc.MaxPwm * 17 + asc.MinPwm * 19 + asc.TurnSensorCounts * 23
              + asc.PressureTripPoint * 29 + asc.CurrentTripPoint * 31 + asc.MotorDriver * 37 + asc.AdConverter * 41
              + asc.ImuAxisSwap * 43 + asc.ExternalEnable * 47 + asc.LineWidth * 53 + asc.NudgeDistance * 59 + asc.CmPerPixel * 61;
        foreach (var d in new[] { asc.SteerResponseHold, asc.IntegralGain, asc.StanleyAggressiveness,
                                  asc.StanleyOvershootReduction, asc.CountsPerDegree, asc.DeadzoneHeading,
                                  asc.SpeedFactor, asc.AcquireFactor, asc.UTurnCompensation, asc.SideHillCompensation,
                                  asc.ManualTurnsSpeed, asc.MinSteerSpeed, asc.MaxSteerSpeed, asc.NextGuidanceTime })
            h = h * 31 + d.GetHashCode();
        return h;
    }

    /// <summary>
    /// Cheap change-detector so the broadcaster only re-sends the Scene when the
    /// static-ish geometry actually changes (field swap, boundary/track edits).
    /// </summary>
    public long SceneFingerprint()
    {
        var f = _state.Field;
        long h = 17;
        h = h * 31 + f.FieldName.GetHashCode();
        var bnd = f.ActiveField?.Boundary;
        h = h * 31 + (bnd?.OuterBoundary?.Points.Count ?? 0);
        if (bnd?.InnerBoundaries != null)
            foreach (var inner in bnd.InnerBoundaries.ToArray()) h = h * 31 + inner.Points.Count;
        h = h * 31 + (f.HeadlandLine?.Count ?? 0);
        h = h * 31 + f.Tracks.Count;
        foreach (var t in f.Tracks.ToArray()) h = h * 31 + t.Points.Count;

        // Flags: re-send the Scene on place/delete. Count + last position (rounded to
        // 0.1 m) catches add/remove/move without per-tick churn.
        var flags = f.Flags;
        h = h * 31 + flags.Count;
        if (flags.Count > 0)
        {
            var last = flags[flags.Count - 1];
            h = h * 31 + (long)(last.Easting * 10) * 31 + (long)(last.Northing * 10);
        }

        // Followed offset line: re-send the Scene when the pass changes. The list
        // is replaced (not mutated) per pass, so count + first-point (rounded to
        // 0.1 m to avoid float jitter) detects the shift without per-tick churn.
        var dl = _state.Guidance.DisplayLine;
        if (dl is { Count: > 0 })
        {
            h = h * 31 + dl.Count;
            var p0 = dl[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Section layout — re-send on count or a span change (tool/section config).
        int nSec = _sections.NumSections;
        h = h * 31 + nSec;
        var states = _sections.SectionStates;
        for (int i = 0; i < nSec && i < states.Count; i++)
            h = h * 31 + (long)System.Math.Round((states[i].PositionLeft + states[i].PositionRight) * 100);

        // U-turn path: appears/changes per turn (replaced, not mutated).
        var tp = _state.YouTurn.TurnPath;
        if (tp is { Count: > 0 })
        {
            h = h * 31 + tp.Count;
            var p0 = tp[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Next track: appears when a turn is set up, clears once picked up.
        var nt = _state.YouTurn.NextTrack?.Points;
        if (nt is { Count: > 0 })
        {
            h = h * 31 + nt.Count;
            var p0 = nt[0];
            h = h * 31 + (long)System.Math.Round(p0.Easting * 10);
            h = h * 31 + (long)System.Math.Round(p0.Northing * 10);
        }

        // Imagery: re-send (client reloads /backpic.png) when it toggles or the
        // field's image changes.
        var im = _state.Field.Imagery;
        h = h * 31 + (im is not null ? ImageVersion(im.Path) : 0);
        return h;
    }
}
