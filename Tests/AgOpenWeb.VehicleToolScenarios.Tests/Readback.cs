// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>Reads back from the store the value a <c>config.set</c> key writes.</summary>
public static class Readback
{
    public static string Get(ConfigurationStore s, string key)
    {
        var i = CultureInfo.InvariantCulture;
        string D(double d) => d.ToString("0.####", i);
        var v = s.Vehicle; var t = s.Tool; var a = s.Ahrs; var c = s.Connections;
        if (key.StartsWith("tool.sectionWidth:", StringComparison.Ordinal))
        {
            int idx = int.Parse(key[(key.IndexOf(':') + 1)..], i);
            return D(t.GetSectionWidth(idx));
        }
        return key switch
        {
            "vehicle.type" => ((int)v.Type).ToString(i),
            "vehicle.wheelbase" => D(v.Wheelbase),
            "vehicle.trackWidth" => D(v.TrackWidth),
            "vehicle.antennaHeight" => D(v.AntennaHeight),
            "vehicle.antennaPivot" => D(v.AntennaPivot),
            "vehicle.antennaOffset" => D(v.AntennaOffset),
            "vehicle.hitchLength" => D(v.HitchLength),
            "vehicle.threePointHitchLength" => D(v.ThreePointHitchLength),
            "vehicle.frontHitchLength" => D(v.FrontHitchLength),
            "autosteer.maxSteerAngle" => D(v.MaxSteerAngle),
            "gps.isDualGps" => c.IsDualGps ? "1" : "0",
            "gps.dualHeadingOffset" => D(c.DualHeadingOffset),
            "roll.isRollInvert" => a.IsRollInvert ? "1" : "0",
            "roll.rollZero" => D(a.RollZero),
            "roll.rollFilter" => D(a.RollFilter),
            "tool.type" => t.IsToolFrontFixed ? "front" : t.IsToolRearFixed ? "rear" : t.IsToolTBT ? "tbt" : t.IsToolTrailing ? "trailing" : "none",
            "tool.numSections" => s.NumSections.ToString(i),
            "tool.workingPointFromHitch" => D(t.WorkingPointFromHitch),
            "tool.trailingHitchLength" => D(t.TrailingHitchLength),
            "tool.trailingToolToPivotLength" => D(t.TrailingToolToPivotLength),
            "tool.tankTrailingHitchLength" => D(t.TankTrailingHitchLength),
            "tool.offset" => D(t.Offset),
            "tool.overlap" => D(t.Overlap),
            "tool.lookAheadOn" => D(t.LookAheadOnSetting),
            "tool.lookAheadOff" => D(t.LookAheadOffSetting),
            "tool.turnOffDelay" => D(t.TurnOffDelay),
            "tool.slowSpeedCutoff" => D(t.SlowSpeedCutoff),
            "tool.coverageMargin" => D(t.CoverageMargin),
            "tool.minCoverage" => t.MinCoverage.ToString(i),
            "tool.zones" => t.Zones.ToString(i),
            "tool.defaultSectionWidth" => D(t.DefaultSectionWidth),
            _ => "?",
        };
    }

    /// <summary>The key a "key:value" setting writes, with the section index for widths.</summary>
    public static string KeyOf(string setting)
    {
        int c = setting.IndexOf(':');
        string key = setting[..c];
        if (key == "tool.sectionWidth") return key + ":" + setting[(c + 1)..setting.IndexOf(',')];
        return key;
    }

    public static string ValueOf(string setting)
    {
        int c = setting.IndexOf(':');
        string v = setting[(c + 1)..];
        return setting.StartsWith("tool.sectionWidth:", StringComparison.Ordinal) ? v[(v.IndexOf(',') + 1)..] : v;
    }

    /// <summary>Same value, numbers within a millimetre (or a hundredth of a unit).</summary>
    public static bool Same(string want, string got)
    {
        var i = CultureInfo.InvariantCulture;
        if (double.TryParse(want, NumberStyles.Float, i, out var a) && double.TryParse(got, NumberStyles.Float, i, out var b))
            return Math.Abs(a - b) < 0.0011 || (double.IsNaN(a) && double.IsNaN(b));
        return string.Equals(want, got, StringComparison.OrdinalIgnoreCase);
    }
}
