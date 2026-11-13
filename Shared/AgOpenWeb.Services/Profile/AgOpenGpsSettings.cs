// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Profile;

/// <summary>
/// AgOpenGPS's settings files, read one way into the configuration store. Before 6.8.2 a profile
/// was one file, <c>Vehicles/&lt;name&gt;.XML</c>; 6.8.2 split it (its <c>FormConvertProfiles</c>)
/// into <c>VehicleProfiles/&lt;name&gt;.xml</c> (steering, IMU, GPS), <c>ToolProfiles/&lt;name&gt;.xml</c>
/// (implement, sections, guidance gains) and <c>Environment/environment.xml</c> (U-turn, tram,
/// display), keeping the setting names. Every file is a .NET settings file of
/// <c>&lt;setting name="…"&gt;&lt;value&gt;…&lt;/value&gt;&lt;/setting&gt;</c>, so one reader and one
/// mapping serve all of them: a missing setting reads as AgOpenWeb's default.
/// </summary>
public static class AgOpenGpsSettings
{
    /// <summary>The settings of one AgOpenGPS settings file, by name (a repeated name: the last wins).</summary>
    public static Dictionary<string, string> Read(string path) => Parse(XDocument.Load(path));

    public static Dictionary<string, string> Parse(XDocument doc)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in doc.Descendants("setting"))
        {
            var name = s.Attribute("name")?.Value;
            if (name != null) settings[name] = s.Element("value")?.Value ?? "";
        }
        return settings;
    }

    /// <summary>
    /// Apply AgOpenGPS settings to the store: the vehicle, guidance, U-turn, tool, sections,
    /// machine module, AutoSteer, GPS, roll and tram settings AgOpenWeb has. <paramref name="filePath"/> becomes the
    /// active vehicle profile's path (an AgOpenGPS .xml: <see cref="ConfigurationService"/> then
    /// keeps the AutoSteer settings read here instead of looking for its own AutoSteer file).
    /// </summary>
    /// <summary>
    /// Not AgOpenGPS's: the tractor's rear 3-point and front hitch, from the rear- and front-mounted
    /// implements saved last (<see cref="AgOpenGpsProfileImport"/>), so a fixed implement's distance
    /// from its hitch keeps it where AgOpenGPS has it (#547).
    /// </summary>
    public const string ThreePointHitchKey = "agopenweb_threePointHitchLength";
    /// <inheritdoc cref="ThreePointHitchKey"/>
    public const string FrontHitchKey = "agopenweb_frontHitchLength";

    public static void ApplyToStore(IReadOnlyDictionary<string, string> settings, string profileName, string filePath, ConfigurationStore store)
    {
        // Vehicle config
        store.Vehicle.Name = profileName;
        store.Vehicle.Type = (VehicleType)GetInt(settings, "setVehicle_vehicleType", 0);
        store.Vehicle.AntennaHeight = GetDouble(settings, "setVehicle_antennaHeight", 3.0);
        store.Vehicle.AntennaPivot = GetDouble(settings, "setVehicle_antennaPivot", 0.0);
        // AgOpenGPS measures the antenna offset to the left (ConfigVehicle: "Left" is checked when
        // it is above 0 and "Right" is saved negative; Position moves the fix by +offset to the
        // right to reach the centreline). AgOpenWeb measures it to the right
        // (AntennaToPivotTransform), so the sign turns round.
        store.Vehicle.AntennaOffset = Negated(GetDouble(settings, "setVehicle_antennaOffset", 0.0));
        store.Vehicle.Wheelbase = GetDouble(settings, "setVehicle_wheelbase", 2.5);
        store.Vehicle.TrackWidth = GetDouble(settings, "setVehicle_trackWidth", 1.8);
        store.Vehicle.MaxSteerAngle = GetDouble(settings, "setVehicle_maxSteerAngle", 35.0);
        store.Vehicle.MaxAngularVelocity = GetDouble(settings, "setVehicle_maxAngularVelocity", 35.0);

        // Guidance config
        // setVehicle_isStanleyUsed is AgOpenGPS's algorithm choice. (setMenu_isPureOn, read
        // here before #99, is its "show the Pure Pursuit point" display toggle.)
        store.Guidance.IsPurePursuit = !GetBool(settings, "setVehicle_isStanleyUsed", false);
        store.Guidance.GoalPointLookAheadHold = GetDouble(settings, "setVehicle_goalPointLookAheadHold", 4.0);
        store.Guidance.GoalPointLookAheadMult = GetDouble(settings, "setVehicle_goalPointLookAheadMult", 1.4);
        store.Guidance.GoalPointAcquireFactor = GetDouble(settings, "setVehicle_goalPointAcquireFactor", 0.9);
        store.Guidance.StanleyDistanceErrorGain = GetDouble(settings, "stanleyDistanceErrorGain", 0.8);
        store.Guidance.StanleyHeadingErrorGain = GetDouble(settings, "stanleyHeadingErrorGain", 1.0);
        store.Guidance.StanleyIntegralGainAB = GetDouble(settings, "stanleyIntegralGainAB", 0.0);
        store.Guidance.PurePursuitIntegralGain = GetDouble(settings, "purePursuitIntegralGainAB", 0.0);
        store.Guidance.UTurnCompensation = GetDouble(settings, "setAS_uTurnCompensation", 1.0);

        // U-Turn settings
        store.Guidance.UTurnRadius = GetDouble(settings, "set_youTurnRadius", 8.0);
        store.Guidance.UTurnExtension = GetDouble(settings, "set_youTurnExtensionLength", 20.0);
        store.Guidance.UTurnDistanceFromBoundary = GetDouble(settings, "set_youTurnDistanceFromBoundary", 2.0);
        store.Guidance.UTurnStyle = GetInt(settings, "set_uTurnStyle", (int)Models.YouTurn.YouTurnType.SagittaStyle);
        store.Guidance.UTurnSmoothing = GetRounded(settings, "setAS_uTurnSmoothing", 14); // a double in AgOpenGPS

        // Tool config
        store.Tool.Width = GetDouble(settings, "setVehicle_toolWidth", 6.0);
        store.Tool.Overlap = GetDouble(settings, "setVehicle_toolOverlap", 0.0);
        store.Tool.Offset = GetDouble(settings, "setVehicle_toolOffset", 0.0);
        // AgOpenGPS's one setVehicle_hitchLength, kept per implement: a trailed implement's is the
        // tractor's drawbar pin, a fixed implement's is its working point from the rear axle (CTool:
        // the working point is at the hitch). AgOpenWeb keeps the tractor's hitch points with the
        // tractor and a fixed tool's distance from its hitch (#547). The import names the tractor's
        // 3-point and front hitch (ThreePointHitchKey, FrontHitchKey: from the implements saved last);
        // without them the hitch length is both, and a fixed tool's distance from it is 0: the tool
        // where AgOpenGPS has it. AgOpenGPS writes it negative behind the tractor; AgOpenWeb keeps
        // the distance (the hitch's side gives the direction).
        double xmlHitch = Math.Abs(GetDouble(settings, "setVehicle_hitchLength", 1.8));
        store.Vehicle.HitchLength = xmlHitch;
        store.Vehicle.ThreePointHitchLength = Math.Abs(GetDouble(settings, ThreePointHitchKey, xmlHitch));
        // AgOpenGPS stores the trailing hitch negative (behind the hitch); AgOpenWeb keeps the
        // distance. 6.8.2+ names it setVehicle_… (ToolSettings, and SettingsLegacy for a combined
        // file); the combined file AgOpenGPS wrote up to 6.8.1 names it setTool_… (its Settings.cs).
        store.Tool.TrailingHitchLength = Math.Abs(GetDouble(settings, "setVehicle_toolTrailingHitchLength",
            GetDouble(settings, "setTool_toolTrailingHitchLength", 2.5)));
        // The tank's hitch of a tow-between tool: AgOpenGPS saves what the farmer types negative
        // (ConfigTool: nudTankHitch × -1; its default is +3) and shows Math.Abs of it; AgOpenWeb
        // keeps the distance (ToolPositionService).
        store.Tool.TankTrailingHitchLength = Math.Abs(GetDouble(settings, "setVehicle_tankTrailingHitchLength", 3.0));
        store.Tool.TrailingToolToPivotLength = GetDouble(settings, "setTool_trailingToolToPivotLength", 0.0);
        store.Tool.IsToolTrailing = GetBool(settings, "setTool_isToolTrailing", false);
        store.Tool.IsToolTBT = GetBool(settings, "setTool_isToolTBT", false);
        store.Tool.IsToolRearFixed = GetBool(settings, "setTool_isToolRearFixed", true);
        store.Tool.IsToolFrontFixed = GetBool(settings, "setTool_isToolFront", false);
        store.Vehicle.FrontHitchLength = Math.Abs(GetDouble(settings, FrontHitchKey, store.Tool.IsToolFrontFixed ? xmlHitch : 0));
        store.Tool.WorkingPointFromHitch = xmlHitch - (store.Tool.IsToolFrontFixed
            ? store.Vehicle.FrontHitchLength : store.Vehicle.ThreePointHitchLength);
        store.Tool.LookAheadOnSetting = GetDouble(settings, "setVehicle_toolLookAheadOn", 1.0);
        store.Tool.LookAheadOffSetting = GetDouble(settings, "setVehicle_toolLookAheadOff", 0.5);
        store.Tool.TurnOffDelay = GetDouble(settings, "setVehicle_toolOffDelay", 0.0);
        store.Tool.MinCoverage = GetRounded(settings, "setVehicle_minCoverage", 100); // a double in AgOpenGPS
        store.Tool.SlowSpeedCutoff = GetDouble(settings, "setVehicle_slowSpeedCutoff", 0.5);
        // AgOpenGPS writes setColor_isMultiColorSections; setTool_isMultiColoredSections was read before.
        store.Tool.IsMultiColoredSections = GetBool(settings, "setColor_isMultiColorSections",
            GetBool(settings, "setTool_isMultiColoredSections", false));
        store.Tool.IsSectionsNotZones = GetBool(settings, "setTool_isSectionsNotZones", true);
        store.Tool.IsSectionOffWhenOut = GetBool(settings, "setTool_isSectionOffWhenOut", true);
        // AgOpenGPS names it setHeadland_isSectionControlled (setTool_isHeadlandSectionControl,
        // read here before, is a name it never wrote).
        store.Tool.IsHeadlandSectionControl = GetBool(settings, "setHeadland_isSectionControlled",
            GetBool(settings, "setTool_isHeadlandSectionControl", true));
        // The work switch and the steer switch as section switches (AgOpenGPS ConfigTool's
        // switches tab, CModuleComm). 6.8.2+ keeps them in the environment, but the steer
        // switch's own on/off with the implement.
        var defaultTool = new ToolConfig();
        store.Tool.IsWorkSwitchEnabled = GetBool(settings, "setF_isWorkSwitchEnabled", defaultTool.IsWorkSwitchEnabled);
        store.Tool.IsWorkSwitchActiveLow = GetBool(settings, "setF_isWorkSwitchActiveLow", defaultTool.IsWorkSwitchActiveLow);
        store.Tool.IsWorkSwitchManualSections = GetBool(settings, "setF_isWorkSwitchManualSections", defaultTool.IsWorkSwitchManualSections);
        store.Tool.IsSteerSwitchEnabled = GetBool(settings, "setF_isSteerWorkSwitchEnabled", defaultTool.IsSteerSwitchEnabled);
        store.Tool.IsSteerSwitchManualSections = GetBool(settings, "setF_isSteerWorkSwitchManualSections", defaultTool.IsSteerSwitchManualSections);

        // Section config. AgOpenGPS runs setVehicle_numSections sections between the edges
        // setSection_position1..17 (metres, left first), or in zones mode setTool_numSectionsMulti
        // sections of setTool_sectionWidthMulti metres. AgOpenWeb's section widths are what the
        // section control and the tool profile use, so they are set from those; left at the
        // default width, a tool of 12 sections of 25 cm was 12 m wide.
        bool zones = !store.Tool.IsSectionsNotZones;
        store.NumSections = zones
            ? GetInt(settings, "setTool_numSectionsMulti", 20)
            : GetInt(settings, "setVehicle_numSections", 1);
        int sectionCount = Math.Max(1, store.NumSections);
        var sectionPositions = new double[ToolConfig.MaxSections + 1];
        for (int i = 0; i < 17; i++)
        {
            sectionPositions[i] = GetDouble(settings, $"setSection_position{i + 1}", 0.0);
        }
        if (zones)
        {
            double width = GetDouble(settings, "setTool_sectionWidthMulti", 0.5);
            if (width > 0)
            {
                for (int i = 0; i < sectionCount; i++)
                    store.Tool.SetSectionWidth(i, width * 100.0);
                sectionPositions[0] = -width * sectionCount / 2.0 + store.Tool.Offset;
                for (int i = 0; i < sectionCount; i++)
                    sectionPositions[i + 1] = sectionPositions[i] + width;
            }
        }
        else
        {
            for (int i = 0; i < sectionCount && i + 1 < 17; i++)
            {
                double width = sectionPositions[i + 1] - sectionPositions[i];
                if (width > 0)
                    store.Tool.SetSectionWidth(i, width * 100.0);
            }
        }
        store.SectionPositions = sectionPositions;
        if (settings.TryGetValue("setTool_zones", out var zoneList))
            ApplyZones(zoneList, store.Tool);

        // Tram lines
        store.Tram.TramWidth = GetDouble(settings, "setTram_tramWidth", new TramConfig().TramWidth);
        store.Guidance.TramPasses = GetInt(settings, "setTram_passes", new GuidanceConfig().TramPasses);
        // AgOpenGPS ConfigModule's "Override" of the tram control side (CTram: isOuter = !isOuter).
        store.Tram.IsOuterInverted = GetBool(settings, "setTool_isTramOuterInverted", new TramConfig().IsOuterInverted);

        ApplyMachine(settings, store.Machine);
        ApplyAutoSteer(settings, store.AutoSteer);
        ApplyGpsAndRoll(settings, store);

        // Display config — IsMetric used to live in the vehicle XML.
        // It now lives in AppSettings; apply the legacy XML value to the
        // store so the post-load ReconcileIsMetricAfterProfileLoad can
        // perform the one-shot migration (the same path the JSON profile
        // services use). Once migration has completed, AppSettings
        // overrides the XML value on subsequent loads.
        if (settings.ContainsKey("setMenu_isMetric"))
            store.IsMetric = GetBool(settings, "setMenu_isMetric", true);

        // Profile metadata
        store.ActiveVehicleProfileName = profileName;
        store.ActiveVehicleProfilePath = filePath;
    }

    /// <summary>
    /// AgOpenGPS's zones: <c>setTool_zones</c> is "count,end1,end2,…" (CTool: its zoneRanges[0]
    /// is the count, zoneRanges[i] the section zone i ends at). AgOpenWeb keeps the count apart
    /// and <see cref="ToolConfig.ZoneRanges"/>[0] at 0, so each end keeps its index.
    /// </summary>
    private static void ApplyZones(string zoneList, ToolConfig tool)
    {
        var words = zoneList.Split(',');
        if (!int.TryParse(words[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            return;
        var ranges = new int[9];
        for (int i = 1; i < ranges.Length && i < words.Length; i++)
        {
            if (int.TryParse(words[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
                ranges[i] = end;
        }
        tool.Zones = count;
        tool.ZoneRanges = ranges;
    }

    /// <summary>
    /// The machine module settings (AgOpenGPS ConfigModule): the relay pin map, the hydraulic
    /// lift and the four user values of PGN 238. AgOpenWeb keeps them with the implement.
    /// </summary>
    public static void ApplyMachine(IReadOnlyDictionary<string, string> settings, MachineConfig m)
    {
        var d = new MachineConfig();
        // setArdMac_setting0 is PGN 238's set0 byte: bit 0 inverts the relays, bit 1 is
        // ConfigModule's hydraulic lift switch (it also shows the lift button). No form writes
        // setArdMac_isHydEnabled, so it is not read.
        bool hasSet0 = TryGetInt(settings, "setArdMac_setting0", out int set0);
        m.InvertRelay = hasSet0 ? (set0 & 1) != 0 : d.InvertRelay;
        m.HydraulicLiftEnabled = hasSet0 ? (set0 & 2) != 0 : d.HydraulicLiftEnabled;
        m.RaiseTime = GetInt(settings, "setArdMac_hydRaiseTime", d.RaiseTime);
        m.LowerTime = GetInt(settings, "setArdMac_hydLowerTime", d.LowerTime);
        m.LookAhead = GetDouble(settings, "setVehicle_hydraulicLiftLookAhead", d.LookAhead);
        m.User1Value = GetInt(settings, "setArdMac_user1", d.User1Value);
        m.User2Value = GetInt(settings, "setArdMac_user2", d.User2Value);
        m.User3Value = GetInt(settings, "setArdMac_user3", d.User3Value);
        m.User4Value = GetInt(settings, "setArdMac_user4", d.User4Value);

        // setRelay_pinConfig: the 24 pins of PGN 236, each with AgOpenGPS's function number
        // (0 none, 1-16 a section, 17 Hyd Up, 18 Hyd Down, 19 Tram Right, 20 Tram Left,
        // 21 Geo Stop), the numbers PinFunction keeps.
        if (!settings.TryGetValue("setRelay_pinConfig", out var pinList))
        {
            m.ResetPinAssignments();
            return;
        }
        var words = pinList.Split(',');
        var pins = new PinFunction[24];
        for (int i = 0; i < pins.Length && i < words.Length; i++)
        {
            if (int.TryParse(words[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int function)
                && Enum.IsDefined(typeof(PinFunction), function))
                pins[i] = (PinFunction)function;
        }
        m.PinAssignments = pins;
    }

    /// <summary>
    /// The steer module settings (AgOpenGPS's Steer Settings form): gains, PWM, WAS, and the
    /// two setting bytes of PGN 251 exactly as AgOpenGPS sends them; the dead zone and the nudge
    /// steps, which 6.8.2+ keeps with the implement. Everything else is AgOpenWeb's default.
    /// </summary>
    public static void ApplyAutoSteer(IReadOnlyDictionary<string, string> settings, AutoSteerConfig a)
    {
        a.ResetToDefaults();
        a.ProportionalGain = GetInt(settings, "setAS_Kp", a.ProportionalGain);
        a.CountsPerDegree = GetDouble(settings, "setAS_countsPerDegree", a.CountsPerDegree);
        a.Ackermann = GetInt(settings, "setAS_ackerman", a.Ackermann);
        a.WasOffset = GetInt(settings, "setAS_wasOffset", a.WasOffset);
        a.MaxPwm = GetInt(settings, "setAS_highSteerPWM", a.MaxPwm);
        a.MinPwm = GetInt(settings, "setAS_minSteerPWM", a.MinPwm);
        a.SideHillCompensation = GetDouble(settings, "setAS_sideHillComp", a.SideHillCompensation);
        a.SteerInReverse = GetBool(settings, "setAS_isSteerInReverse", a.SteerInReverse);
        a.ManualTurnsSpeed = GetDouble(settings, "setAS_functionSpeedLimit", a.ManualTurnsSpeed);
        // Steering speed limits (enforced by the pipeline since #106).
        a.MinSteerSpeed = GetDouble(settings, "setAS_minSteerSpeed", a.MinSteerSpeed);
        a.MaxSteerSpeed = GetDouble(settings, "setAS_maxSteerSpeed", a.MaxSteerSpeed);
        // PGN 251's setting bytes: Invert WAS / relays / motor, A/D converter, motor driver,
        // steer switch, turn sensor; Danfoss, pressure and current sensor, IMU axis swap. The
        // WAS offset is kept as AgOpenGPS wrote it: it was already set for the inverted sensor.
        if (TryGetInt(settings, "setArdSteer_setting0", out int set0)) a.SetFromSetting0Byte((byte)set0);
        if (TryGetInt(settings, "setArdSteer_setting1", out int set1)) a.SetFromSetting1Byte((byte)set1);
        // PGN 251 byte 6: the pressure or current trip point (raw 0-255; AgOpenWeb keeps the
        // % AgOpenGPS shows for it, FormSteer: (int)(raw × 0.392), cut not rounded), otherwise
        // the turn sensor count.
        if (TryGetInt(settings, "setArdSteer_maxPulseCounts", out int pulse))
        {
            int pct = (int)(Math.Clamp(pulse, 0, 255) * 0.3921568627);
            if (a.PressureSensorEnabled) a.PressureTripPoint = pct;
            else if (a.CurrentSensorEnabled) a.CurrentTripPoint = pct;
            else a.TurnSensorCounts = Math.Clamp(pulse, 0, 255);
        }
        // The dead zone: AgOpenGPS keeps the heading in hundredths of a degree (FormSteer:
        // nudDeadZoneHeading × 100), AgOpenWeb in degrees; the delay is seconds in both.
        if (TryGetDouble(settings, "setAS_deadZoneHeading", out double deadZoneHeading))
            a.DeadzoneHeading = deadZoneHeading / 100.0;
        a.DeadzoneDelay = GetInt(settings, "setAS_deadZoneDelay", a.DeadzoneDelay);
        // The nudge steps (FormNudge, FormRefNudge), cm in both.
        a.NudgeDistance = GetRounded(settings, "setAS_snapDistance", a.NudgeDistance);
        a.RefNudgeDistance = GetRounded(settings, "setAS_snapDistanceRef", a.RefNudgeDistance);
    }

    /// <summary>The GPS and roll settings AgOpenWeb keeps with the vehicle.</summary>
    public static void ApplyGpsAndRoll(IReadOnlyDictionary<string, string> settings, ConfigurationStore store)
    {
        var dc = new ConnectionConfig();
        var c = store.Connections;
        c.IsDualGps = settings.TryGetValue("setGPS_headingFromWhichSource", out var source)
            ? string.Equals(source.Trim(), "Dual", StringComparison.OrdinalIgnoreCase)
            : dc.IsDualGps;
        c.DualHeadingOffset = GetDouble(settings, "setGPS_dualHeadingOffset", dc.DualHeadingOffset);
        c.DualReverseDistance = GetDouble(settings, "setGPS_dualReverseDetectionDistance", dc.DualReverseDistance);
        c.AutoDualFix = GetBool(settings, "setAutoSwitchDualFixOn", dc.AutoDualFix);
        c.DualSwitchSpeed = GetDouble(settings, "setAutoSwitchDualFixSpeed", dc.DualSwitchSpeed);
        c.MinGpsStep = GetDouble(settings, "setGPS_minimumStepLimit", dc.MinGpsStep);
        c.ReverseDetection = GetBool(settings, "setIMU_isReverseOn", dc.ReverseDetection);
        // AgOpenGPS keeps the fusion weight itself (0.06 = 30 % GPS); AgOpenWeb the GPS share.
        c.HeadingFusionWeight = TryGetDouble(settings, "setIMU_fusionWeight2", out var weight)
            ? Math.Clamp(weight / GpsHeadingFusionService.FusionShareToWeight, 0.0, 1.0)
            : dc.HeadingFusionWeight;
        // AgOpenGPS's RTK alarm and "kill AutoSteer" on it (ConfigData; the environment).
        c.RtkLostAlarm = GetBool(settings, "setGPS_isRTK", dc.RtkLostAlarm);
        c.RtkLostAction = settings.ContainsKey("setGPS_isRTK_KillAutoSteer")
            ? (GetBool(settings, "setGPS_isRTK_KillAutoSteer", false) ? 1 : 0)
            : dc.RtkLostAction;

        store.Ahrs.RollZero = GetDouble(settings, "setIMU_rollZero", 0.0);
        store.Ahrs.RollFilter = GetDouble(settings, "setIMU_rollFilter", 0.0);
        store.Ahrs.IsRollInvert = GetBool(settings, "setIMU_invertRoll", false);
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> settings, string key, out double value)
    {
        value = 0;
        return settings.TryGetValue(key, out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> settings, string key, out int value)
    {
        value = 0;
        return settings.TryGetValue(key, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> settings, string key, double defaultValue)
        => TryGetDouble(settings, key, out var v) ? v : defaultValue;

    private static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
        => TryGetInt(settings, key, out var v) ? v : defaultValue;

    /// <summary>A setting AgOpenGPS keeps as a double and AgOpenWeb as a whole number.</summary>
    private static int GetRounded(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
        => TryGetDouble(settings, key, out var v) ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : defaultValue;

    /// <summary>The other sign, and 0 stays 0 (not -0, which a profile file would show).</summary>
    private static double Negated(double value) => value == 0 ? 0 : -value;

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
        => settings.TryGetValue(key, out var text) && bool.TryParse(text, out var v) ? v : defaultValue;
}
