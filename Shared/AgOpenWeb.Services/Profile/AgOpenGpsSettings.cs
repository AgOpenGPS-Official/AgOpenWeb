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
    /// AutoSteer, GPS, roll and tram settings AgOpenWeb has. <paramref name="filePath"/> becomes the
    /// active vehicle profile's path (an AgOpenGPS .xml: <see cref="ConfigurationService"/> then
    /// keeps the AutoSteer settings read here instead of looking for its own AutoSteer file).
    /// </summary>
    public static void ApplyToStore(IReadOnlyDictionary<string, string> settings, string profileName, string filePath, ConfigurationStore store)
    {
        // Vehicle config
        store.Vehicle.Name = profileName;
        store.Vehicle.Type = (VehicleType)GetInt(settings, "setVehicle_vehicleType", 0);
        store.Vehicle.AntennaHeight = GetDouble(settings, "setVehicle_antennaHeight", 3.0);
        store.Vehicle.AntennaPivot = GetDouble(settings, "setVehicle_antennaPivot", 0.0);
        store.Vehicle.AntennaOffset = GetDouble(settings, "setVehicle_antennaOffset", 0.0);
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
        store.Guidance.UTurnSmoothing = GetInt(settings, "setAS_uTurnSmoothing", 14);

        // Tool config
        store.Tool.Width = GetDouble(settings, "setVehicle_toolWidth", 6.0);
        store.Tool.Overlap = GetDouble(settings, "setVehicle_toolOverlap", 0.0);
        store.Tool.Offset = GetDouble(settings, "setVehicle_toolOffset", 0.0);
        // AOG's single setVehicle_hitchLength is the tractor hitch pin (now a vehicle
        // property, used by trailing/TBT) AND doubles as the rigid working-center distance.
        // Import into both so the value is present whichever tool type the profile uses.
        // AgOpenGPS writes it negative behind the tractor; AgOpenWeb keeps the distance (the
        // tool's direction gives the side), and Tool.HitchLength took a negative one as 0.
        double xmlHitch = Math.Abs(GetDouble(settings, "setVehicle_hitchLength", 1.8));
        store.Vehicle.HitchLength = xmlHitch;
        store.Tool.HitchLength = xmlHitch;
        // AgOpenGPS stores the trailing hitch negative (behind the hitch); AgOpenWeb keeps the
        // distance. Before 6.8.2 the combined file named it setTool_…, the tool profile names
        // it setVehicle_….
        store.Tool.TrailingHitchLength = Math.Abs(GetDouble(settings, "setVehicle_toolTrailingHitchLength",
            GetDouble(settings, "setTool_toolTrailingHitchLength", 2.5)));
        store.Tool.TankTrailingHitchLength = GetDouble(settings, "setVehicle_tankTrailingHitchLength", 3.0);
        store.Tool.TrailingToolToPivotLength = GetDouble(settings, "setTool_trailingToolToPivotLength", 0.0);
        store.Tool.IsToolTrailing = GetBool(settings, "setTool_isToolTrailing", false);
        store.Tool.IsToolTBT = GetBool(settings, "setTool_isToolTBT", false);
        store.Tool.IsToolRearFixed = GetBool(settings, "setTool_isToolRearFixed", true);
        store.Tool.IsToolFrontFixed = GetBool(settings, "setTool_isToolFront", false);
        store.Tool.LookAheadOnSetting = GetDouble(settings, "setVehicle_toolLookAheadOn", 1.0);
        store.Tool.LookAheadOffSetting = GetDouble(settings, "setVehicle_toolLookAheadOff", 0.5);
        store.Tool.TurnOffDelay = GetDouble(settings, "setVehicle_toolOffDelay", 0.0);
        store.Tool.MinCoverage = GetInt(settings, "setVehicle_minCoverage", 100);
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
            if (settings.TryGetValue("setTool_zones", out var zoneList)
                && int.TryParse(zoneList.Split(',')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int zoneCount))
                store.Tool.Zones = zoneCount;
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

        // Tram lines
        store.Tram.TramWidth = GetDouble(settings, "setTram_tramWidth", new TramConfig().TramWidth);
        store.Guidance.TramPasses = GetInt(settings, "setTram_passes", new GuidanceConfig().TramPasses);

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
    /// The steer module settings (AgOpenGPS's Steer Settings form): gains, PWM, WAS, and the
    /// two setting bytes of PGN 251 exactly as AgOpenGPS sends them. Everything else is
    /// AgOpenWeb's default.
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
        // PGN 251 byte 6: the pressure or current trip point (raw 0-255; AgOpenWeb keeps %),
        // otherwise the turn sensor count.
        if (TryGetInt(settings, "setArdSteer_maxPulseCounts", out int pulse))
        {
            int pct = (int)Math.Round(Math.Clamp(pulse, 0, 255) * 100.0 / 255.0);
            if (a.PressureSensorEnabled) a.PressureTripPoint = pct;
            else if (a.CurrentSensorEnabled) a.CurrentTripPoint = pct;
            else a.TurnSensorCounts = Math.Clamp(pulse, 0, 255);
        }
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

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
        => settings.TryGetValue(key, out var text) && bool.TryParse(text, out var v) ? v : defaultValue;
}
