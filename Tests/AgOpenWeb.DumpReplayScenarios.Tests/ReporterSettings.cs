// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>
/// The reporter's Vehicle &amp; Tool settings, from the dump's <c>configuration.json</c>, as the
/// operator types them: <c>config.set|key:value</c> commands, the web client's own. A setting the
/// web client has no key for is written to the configuration store directly (the reporter's app
/// had that value; there is no other way in). A setting the dump does not carry keeps
/// AgOpenWeb's default, and the report lists it.
/// </summary>
public static class ReporterSettings
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>configuration.json path → config.set key. Booleans go as 1/0.</summary>
    private static readonly (string[] Path, string Key)[] Keys =
    {
        (new[] { "Vehicle", "Wheelbase" }, "vehicle.wheelbase"),
        (new[] { "Vehicle", "TrackWidth" }, "vehicle.trackWidth"),
        (new[] { "Vehicle", "AntennaHeight" }, "vehicle.antennaHeight"),
        (new[] { "Vehicle", "AntennaPivot" }, "vehicle.antennaPivot"),
        (new[] { "Vehicle", "AntennaOffset" }, "vehicle.antennaOffset"),
        (new[] { "Vehicle", "MaxSteerAngle" }, "autosteer.maxSteerAngle"),
        (new[] { "Tool", "Overlap" }, "tool.overlap"),
        (new[] { "Tool", "Offset" }, "tool.offset"),
        (new[] { "Tool", "HitchLength" }, "tool.hitchLength"),
        (new[] { "Tool", "TrailingHitchLength" }, "tool.trailingHitchLength"),
        (new[] { "Tool", "TrailingToolToPivotLength" }, "tool.trailingToolToPivotLength"),
        (new[] { "Tool", "TankTrailingHitchLength" }, "tool.tankTrailingHitchLength"),
        (new[] { "Tool", "LookAheadOnSetting" }, "tool.lookAheadOn"),
        (new[] { "Tool", "LookAheadOffSetting" }, "tool.lookAheadOff"),
        (new[] { "Tool", "TurnOffDelay" }, "tool.turnOffDelay"),
        (new[] { "Tool", "SlowSpeedCutoff" }, "tool.slowSpeedCutoff"),
        (new[] { "Tool", "CoverageMargin" }, "tool.coverageMargin"),
        (new[] { "Tool", "MinCoverage" }, "tool.minCoverage"),
        (new[] { "Tool", "IsMultiColoredSections" }, "tool.isMultiColoredSections"),
        (new[] { "Guidance", "GoalPointLookAheadHold" }, "autosteer.steerResponseHold"),
        (new[] { "Guidance", "StanleyHeadingErrorGain" }, "autosteer.stanleyOvershootReduction"),
        (new[] { "Guidance", "StanleyDistanceErrorGain" }, "autosteer.stanleyAggressiveness"),
        (new[] { "Guidance", "UTurnRadius" }, "uturn.radius"),
        (new[] { "Guidance", "UTurnExtension" }, "uturn.extension"),
        (new[] { "Guidance", "UTurnDistanceFromBoundary" }, "uturn.distanceFromBoundary"),
        (new[] { "Guidance", "UTurnStyle" }, "uturn.style"),
        (new[] { "Guidance", "UTurnSmoothing" }, "uturn.smoothing"),
        (new[] { "Guidance", "UTurnCompensation" }, "autosteer.uTurnCompensation"),
        (new[] { "Gps", "IsDualGps" }, "gps.isDualGps"),
        (new[] { "Gps", "DualHeadingOffset" }, "gps.dualHeadingOffset"),
        (new[] { "Gps", "DualReverseDistance" }, "gps.dualReverseDistance"),
        (new[] { "Gps", "AutoDualFix" }, "gps.autoDualFix"),
        (new[] { "Gps", "DualSwitchSpeed" }, "gps.dualSwitchSpeed"),
        (new[] { "Gps", "MinGpsStep" }, "gps.minGpsStep"),
        (new[] { "Gps", "FixToFixDistance" }, "gps.fixToFixDistance"),
        (new[] { "Gps", "HeadingFusionWeight" }, "gps.headingFusionWeight"),
        (new[] { "Gps", "ReverseDetection" }, "gps.reverseDetection"),
        (new[] { "Gps", "RtkLostAlarm" }, "gps.rtkLostAlarm"),
        (new[] { "Gps", "RtkLostAction" }, "gps.rtkLostAction"),
        (new[] { "Ahrs", "RollZero" }, "roll.rollZero"),
        (new[] { "Ahrs", "IsRollInvert" }, "roll.isRollInvert"),
        (new[] { "AutoSteer", "WasOffset" }, "autosteer.wasOffset"),
        (new[] { "AutoSteer", "CountsPerDegree" }, "autosteer.countsPerDegree"),
        (new[] { "AutoSteer", "Ackermann" }, "autosteer.ackermann"),
        (new[] { "AutoSteer", "DeadzoneHeading" }, "autosteer.deadzoneHeading"),
        (new[] { "AutoSteer", "DeadzoneDelay" }, "autosteer.deadzoneDelay"),
        (new[] { "AutoSteer", "AcquireFactor" }, "autosteer.acquireFactor"),
        (new[] { "AutoSteer", "ProportionalGain" }, "autosteer.proportionalGain"),
        (new[] { "AutoSteer", "MaxPwm" }, "autosteer.maxPwm"),
        (new[] { "AutoSteer", "MinPwm" }, "autosteer.minPwm"),
        (new[] { "AutoSteer", "SideHillCompensation" }, "autosteer.sideHillCompensation"),
        (new[] { "AutoSteer", "SteerInReverse" }, "autosteer.steerInReverse"),
        (new[] { "AutoSteer", "ManualTurnsEnabled" }, "autosteer.manualTurnsEnabled"),
        (new[] { "AutoSteer", "ManualTurnsSpeed" }, "autosteer.manualTurnsSpeed"),
        (new[] { "AutoSteer", "MinSteerSpeed" }, "autosteer.minSteerSpeed"),
        (new[] { "AutoSteer", "MaxSteerSpeed" }, "autosteer.maxSteerSpeed"),
        (new[] { "AutoSteer", "NudgeDistance" }, "autosteer.nudgeDistance"),
        (new[] { "AutoSteer", "RefNudgeDistance" }, "autosteer.refNudgeDistance"),
        (new[] { "AutoSteer", "NextGuidanceTime" }, "autosteer.nextGuidanceTime"),
    };

    /// <summary>configuration.json path → a direct write to the store (no web key exists).</summary>
    private static readonly (string[] Path, Action<ConfigurationStore, JsonElement> Set)[] Direct =
    {
        (new[] { "Gps", "GpsUpdateRate" }, (s, v) => s.Connections.GpsUpdateRate = v.GetInt32()),
        (new[] { "Gps", "UseRtk" }, (s, v) => s.Connections.UseRtk = v.GetBoolean()),
        (new[] { "Gps", "HeadingSource" }, (s, v) => s.Connections.HeadingSource = v.GetInt32()),
        (new[] { "Gps", "MinFixQuality" }, (s, v) => s.Connections.MinFixQuality = v.GetInt32()),
        (new[] { "Gps", "MaxDifferentialAge" }, (s, v) => s.Connections.MaxDifferentialAge = v.GetDouble()),
        (new[] { "Gps", "MaxHdop" }, (s, v) => s.Connections.MaxHdop = v.GetDouble()),
        (new[] { "Ahrs", "FusionWeight" }, (s, v) => s.Ahrs.FusionWeight = v.GetDouble()),
        (new[] { "Ahrs", "ForwardCompensation" }, (s, v) => s.Ahrs.ForwardCompensation = v.GetDouble()),
        (new[] { "Ahrs", "ReverseCompensation" }, (s, v) => s.Ahrs.ReverseCompensation = v.GetDouble()),
        (new[] { "Ahrs", "IsAutoSteerAuto" }, (s, v) => s.Ahrs.IsAutoSteerAuto = v.GetBoolean()),
        (new[] { "Ahrs", "IsDualAsIMU" }, (s, v) => s.Ahrs.IsDualAsIMU = v.GetBoolean()),
    };

    /// <summary>Settings that steer, turn or switch sections and that a dump of this build does
    /// not carry (configuration.json is a summary): AgOpenWeb's default is used for them.</summary>
    public static readonly string[] NotInDump =
    {
        "Pure Pursuit or Stanley (IsPurePursuit)", "Pure Pursuit integral gain", "look-ahead speed factor",
        "section off outside boundary", "section control in the headland", "vehicle type", "hitch type",
        "machine relay functions", "tram line settings",
    };

    /// <summary>The roll filter: off for the replay. The GPS log's roll is the roll after the
    /// reporter's filter; filtering it again would lag it twice (and move the antenna the replay
    /// rebuilds from it).</summary>
    public const string RollFilterOff = "roll.rollFilter:0";

    /// <summary>The configuration.json paths (dotted, an array's item as <c>[i]</c>) the harness
    /// sets, and the <c>config.set</c> key that sets each ("store" for a direct write). Null for a
    /// path the harness does not set.</summary>
    public static string? KeyOf(string path)
    {
        foreach (var (p, key) in Keys) if (string.Join(".", p) == path) return key;
        foreach (var (p, _) in Direct) if (string.Join(".", p) == path) return "store";
        if (path is "Tool.IsToolTBT" or "Tool.IsToolTrailing" or "Tool.IsToolFrontFixed" or "Tool.IsToolRearFixed") return "tool.type";
        if (path == "NumSections") return "tool.numSections";
        if (path.StartsWith("SectionWidthsCm[", StringComparison.Ordinal)) return "tool.sectionWidth";
        if (path == "Ahrs.RollFilter") return "roll.rollFilter";
        return null;
    }

    /// <summary>The commands and direct writes for this dump's settings, and the paths the dump
    /// did not have (an older build's configuration.json has fewer).</summary>
    public static (List<string> Commands, List<Action<ConfigurationStore>> Writes, List<string> Missing) From(BugDump d)
    {
        var cmds = new List<string>();
        var writes = new List<Action<ConfigurationStore>>();
        var missing = new List<string>();
        if (d.Config is null) { missing.Add("configuration.json"); return (cmds, writes, missing); }

        // The implement's type first: it decides which hitch lengths apply.
        string? type = d.Bool("Tool", "IsToolTBT") == true ? "tbt"
            : d.Bool("Tool", "IsToolTrailing") == true ? "trailing"
            : d.Bool("Tool", "IsToolFrontFixed") == true ? "front"
            : d.Bool("Tool", "IsToolRearFixed") == true ? "rear"
            // Older dumps have no IsToolFrontFixed: the other three all off is a front tool.
            : d.Bool("Tool", "IsToolTrailing") == false && d.Bool("Tool", "IsToolRearFixed") == false && d.Bool("Tool", "IsToolTBT") == false ? "front" : null;
        if (type != null) cmds.Add("tool.type:" + type); else missing.Add("Tool type");

        // Sections: count, then each width (pass spacing and coverage width come from these).
        var widths = BugDump.Get(d.Config, "SectionWidthsCm");
        int n = (int)(d.Num("NumSections") ?? 0);
        if (n > 0 && widths is { ValueKind: JsonValueKind.Array } arr)
        {
            cmds.Add("tool.numSections:" + n.ToString(Inv));
            int i = 0;
            foreach (var w in arr.EnumerateArray())
            {
                if (i >= n) break;
                cmds.Add("tool.sectionWidth:" + i.ToString(Inv) + "," + w.GetDouble().ToString("0.###", Inv));
                i++;
            }
        }
        else missing.Add("sections");

        foreach (var (path, key) in Keys)
        {
            var v = BugDump.Get(d.Config, path);
            if (v is not { } e) { missing.Add(string.Join(".", path)); continue; }
            string? text = e.ValueKind switch
            {
                // The dump holds the multiplier (1.0 = none); the web key takes the panel's percent.
                JsonValueKind.Number when key == "autosteer.uTurnCompensation" =>
                    GuidanceConfig.UTurnCompensationToPercent(e.GetDouble()).ToString("R", Inv),
                JsonValueKind.Number => e.GetDouble().ToString("R", Inv),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null,
            };
            if (text != null) cmds.Add(key + ":" + text);
        }
        cmds.Add(RollFilterOff);
        foreach (var (path, set) in Direct)
        {
            var v = BugDump.Get(d.Config, path);
            if (v is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { missing.Add(string.Join(".", path)); continue; }
            var copy = e.Clone();
            writes.Add(s => { try { set(s, copy); } catch (InvalidOperationException) { } catch (FormatException) { } });
        }
        return (cmds, writes, missing);
    }
}
