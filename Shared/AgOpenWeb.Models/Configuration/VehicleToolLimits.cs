// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// The values a Vehicle &amp; Tool or AutoSteer setting can take on a real machine, so a slip of the unit
/// (centimetres typed in a metre box), of the sign, or a value never filled in is refused
/// with the reason instead of being steered with. A wheelbase of 0 never steers and a
/// negative one steers away from the line (pure pursuit's angle is proportional to it); an
/// overlap as wide as the implement puts every pass on one line, so AutoSteer pulls the
/// tractor back onto the first pass from wherever it engages.
/// <para>The ranges are AgOpenGPS's settings forms' (FormConfig: wheelbase 50 to 1999 cm,
/// antenna height 0 to 10 m, sections up to 50 m, …), in metres; a sign the form takes from
/// a left/right or ahead/behind choice is a sign here. The AutoSteer ranges are the web
/// AutoSteer panel's sliders' (index.html), which follow AgOpenGPS's FormSteer but are wider in
/// places (max steer angle to 90°, deadzone delay to 50, steer response hold to 10, the
/// Stanley gains to 10, Ackermann from 0): a gain of 0 never steers, 999 is clamped to 200 by
/// the module but was shown as typed, and a minimum speed above the maximum never engages.</para>
/// </summary>
public static class VehicleToolLimits
{
    private enum Unit { Metre, Centimetre, Degree, Second, KmH, Percent, None }

    private readonly record struct Range(double Min, double Max, Unit Unit, string Name);

    private static readonly Dictionary<string, Range> Ranges = new(StringComparer.Ordinal)
    {
        ["vehicle.wheelbase"] = new(0.5, 20, Unit.Metre, "Wheelbase"),
        ["vehicle.trackWidth"] = new(0.2, 20, Unit.Metre, "Track width"),
        ["vehicle.antennaHeight"] = new(0, 10, Unit.Metre, "Antenna height"),
        ["vehicle.antennaPivot"] = new(-10, 10, Unit.Metre, "Antenna pivot"),
        ["vehicle.antennaOffset"] = new(-5, 5, Unit.Metre, "Antenna offset"),
        ["vehicle.hitchLength"] = new(0, 40, Unit.Metre, "Hitch length"),
        ["vehicle.imuPivot"] = new(-10, 10, Unit.Metre, "IMU pivot"),
        ["vehicle.imuHeight"] = new(0, 10, Unit.Metre, "IMU height"),
        ["vehicle.imuOffset"] = new(-5, 5, Unit.Metre, "IMU offset"),
        ["vehicle.type"] = new(0, 2, Unit.None, "Vehicle type"),
        ["gps.dualHeadingOffset"] = new(-180, 180, Unit.Degree, "Heading offset"),
        // AgOpenGPS offers two pairs only (5 cm / 50 cm and 10 cm / 100 cm, ConfigData): any
        // positive step a receiver can resolve is allowed here, 0 or less is not.
        ["gps.minGpsStep"] = new(0.01, 1, Unit.Metre, "Minimum GPS step"),
        ["gps.fixToFixDistance"] = new(0.1, 10, Unit.Metre, "Fix-to-fix heading distance"),
        ["roll.rollZero"] = new(-30, 30, Unit.Degree, "Roll zero"),
        ["roll.rollFilter"] = new(0, 0.99, Unit.None, "Roll filter"),
        ["tool.hitchLength"] = new(0, 30, Unit.Metre, "Working center"),
        ["tool.trailingHitchLength"] = new(0.1, 30, Unit.Metre, "Trailing hitch"),
        ["tool.tankTrailingHitchLength"] = new(0.1, 30, Unit.Metre, "Tank trailing hitch"),
        ["tool.trailingToolToPivotLength"] = new(-20, 20, Unit.Metre, "Pivot distance"),
        ["tool.length"] = new(0, 50, Unit.Metre, "Implement length"),
        ["tool.offset"] = new(-25, 25, Unit.Metre, "Tool offset"),
        ["tool.overlap"] = new(-10, 10, Unit.Metre, "Overlap / gap"),
        ["tool.lookAheadOn"] = new(0, 22, Unit.Second, "Look ahead on"),
        ["tool.lookAheadOff"] = new(0, 20, Unit.Second, "Look ahead off"),
        ["tool.turnOffDelay"] = new(0, 10, Unit.Second, "Turn off delay"),
        ["tool.slowSpeedCutoff"] = new(0, 30, Unit.KmH, "Slow speed cutoff"),
        ["tool.coverageMargin"] = new(0, 100, Unit.Centimetre, "Coverage margin"),
        ["tool.defaultSectionWidth"] = new(10, 1000, Unit.Centimetre, "Default width"),
        // At least 10 cm (no row or nozzle is closer): 1 typed for 1 m is refused.
        ["tool.sectionWidth"] = new(10, 5000, Unit.Centimetre, "Section width"),
        ["tool.numSections"] = new(1, ToolConfig.MaxSections, Unit.None, "Number of sections"),
        ["tool.minCoverage"] = new(0, 100, Unit.Percent, "Min coverage"),
        ["tool.zones"] = new(1, 8, Unit.None, "Number of zones"),
        // AgOpenGPS's tram settings: width 1 cm to 100 m (FormConfig), 1 to 999 passes (FormTram).
        // The tram editor's own width and passes use the same rules (MainViewModel.Tram.Remote).
        ["tram.width"] = new(0.01, 100, Unit.Metre, "Tram width"),
        ["tram.passes"] = new(1, 999, Unit.None, "Tram passes"),
        // AutoSteer: the panel's and the wizard's boxes (config.set|autosteer.*).
        ["autosteer.wasOffset"] = new(-4000, 4000, Unit.None, "WAS offset"),
        ["autosteer.countsPerDegree"] = new(1, 255, Unit.None, "Counts per degree"),
        ["autosteer.ackermann"] = new(0, 200, Unit.Percent, "Ackermann"),
        ["autosteer.maxSteerAngle"] = new(10, 90, Unit.Degree, "Max steer angle"),
        ["autosteer.deadzoneHeading"] = new(0, 5, Unit.Degree, "Deadzone heading"),
        ["autosteer.deadzoneDelay"] = new(0, 50, Unit.None, "Deadzone delay"),
        ["autosteer.speedFactor"] = new(0.5, 3, Unit.None, "Speed factor"),
        ["autosteer.acquireFactor"] = new(0.2, 3, Unit.None, "Acquire factor"),
        ["autosteer.proportionalGain"] = new(1, 200, Unit.None, "Proportional gain"),
        ["autosteer.integralGain"] = new(0, 1, Unit.None, "Integral gain"),
        ["autosteer.steerResponseHold"] = new(1, 10, Unit.None, "Steer response hold"),
        ["autosteer.stanleyAggressiveness"] = new(0, 10, Unit.None, "Stanley aggressiveness"),
        ["autosteer.stanleyOvershootReduction"] = new(0, 10, Unit.None, "Stanley overshoot reduction"),
        ["autosteer.maxPwm"] = new(50, 255, Unit.None, "Max PWM"),
        ["autosteer.minPwm"] = new(1, 200, Unit.None, "Min PWM"),
        ["autosteer.turnSensorCounts"] = new(0, 255, Unit.None, "Turn sensor counts"),
        ["autosteer.pressureTripPoint"] = new(0, 99, Unit.Percent, "Pressure trip point"),
        ["autosteer.currentTripPoint"] = new(0, 99, Unit.Percent, "Current trip point"),
        ["autosteer.uTurnCompensation"] = new(-80, 100, Unit.Percent, "U-turn compensation"),
        ["autosteer.sideHillCompensation"] = new(0, 1, Unit.None, "Side hill compensation"),
        ["autosteer.lineWidth"] = new(1, 8, Unit.None, "Line width"),
        ["autosteer.manualTurnsSpeed"] = new(0, 20, Unit.KmH, "Manual turns speed"),
        ["autosteer.minSteerSpeed"] = new(0, 10, Unit.KmH, "Min steer speed"),
        ["autosteer.maxSteerSpeed"] = new(0, 50, Unit.KmH, "Max steer speed"),
    };

    private static readonly string[] ToolTypes = { "front", "rear", "trailing", "tbt" };

    /// <summary>
    /// Null when <paramref name="value"/> (as <c>config.set</c> sends it: metric, invariant
    /// culture; <c>i,cm</c> for a section width) is a value <paramref name="key"/> can take
    /// on <paramref name="store"/>'s machine; otherwise why not, for the operator, in their
    /// units. Keys it has no range for are always fine.
    /// </summary>
    public static string? Check(string key, string value, ConfigurationStore store)
    {
        if (key == "tool.type")
            return ToolTypes.Contains(value.ToLowerInvariant()) ? null
                : "Implement type must be front, rear, trailing or TBT";
        if (!Ranges.TryGetValue(key, out var r)) return null;

        string number = value;
        if (key == "tool.sectionWidth")
        {
            int comma = value.IndexOf(',');
            if (comma <= 0 || !int.TryParse(value[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                || idx < 0 || idx >= ToolConfig.MaxSections)
                return "There is no such section";
            number = value[(comma + 1)..];
        }
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return null; // not a number at all: the setter ignores it, as before
        bool metric = store.IsMetric;
        if (!double.IsFinite(v))
            return r.Name + " must be a number";
        if (key is "tool.numSections" or "tool.zones" or "tram.passes" && v != Math.Floor(v))
            return r.Name + " must be a whole number";
        double slack = RoundingSlack(r.Unit, metric);
        if (v < r.Min - slack || v > r.Max + slack)
        {
            string unit = Symbol(r.Unit, metric);
            if (unit.Length > 0 && unit != "°" && unit != "%") unit = " " + unit;
            return $"{r.Name} must be {Show(r.Min, r.Unit, metric)} to {Show(r.Max, r.Unit, metric)}{unit} (not {Show(v, r.Unit, metric)}{unit})";
        }

        if (key == "autosteer.proportionalGain" && v != Math.Floor(v))
            return r.Name + " must be a whole number";

        // The passes are the implement width less the overlap apart: they must stay apart.
        if (key == "tool.overlap" && v >= store.ActualToolWidth - 0.01)
            return $"Overlap must be less than the implement width ({Show(store.ActualToolWidth, Unit.Metre, metric)} {Symbol(Unit.Metre, metric)})";
        // A gap as wide as the implement leaves a whole pass unworked between passes (an 8 m
        // boom with a 10 m gap drove passes 18 m apart): a typing slip, not a gap.
        if (key == "tool.overlap" && -v >= store.ActualToolWidth - 0.01)
            return $"A gap must be less than the implement width ({Show(store.ActualToolWidth, Unit.Metre, metric)} {Symbol(Unit.Metre, metric)})";
        // Fewer or narrower sections must leave the implement wider than the overlap too. Checked
        // before anything changes: undoing a section count afterwards brought the sections back
        // at the default width, not their own.
        if (key is "tool.numSections" or "tool.sectionWidth" or "tool.defaultSectionWidth"
            && ImplementWidthAfter(key, value, v, store) - store.Tool.Overlap <= 0.01)
            return "The implement must stay wider than the overlap: lower the overlap first";
        // AutoSteer engages only between the two speeds: they must leave a band.
        if (key == "autosteer.minSteerSpeed" && v >= store.AutoSteer.MaxSteerSpeed - 1e-9)
            return $"Min steer speed must be below the max steer speed ({Show(store.AutoSteer.MaxSteerSpeed, Unit.KmH, metric)} {Symbol(Unit.KmH, metric)})";
        if (key == "autosteer.maxSteerSpeed" && v <= store.AutoSteer.MinSteerSpeed + 1e-9)
            return $"Max steer speed must be above the min steer speed ({Show(store.AutoSteer.MinSteerSpeed, Unit.KmH, metric)} {Symbol(Unit.KmH, metric)})";
        return null;
    }

    // The implement width (m) the change would leave: a section count or a default width sets
    // every section to the default width (#676); a width counts only for a section in use.
    private static double ImplementWidthAfter(string key, string value, double v, ConfigurationStore store)
    {
        var tool = store.Tool;
        int now = store.NumSections;
        if (key == "tool.numSections")
            return Math.Clamp((int)v, 1, ToolConfig.MaxSections) * tool.DefaultSectionWidth / 100.0;
        if (key == "tool.defaultSectionWidth")
            return now * v / 100.0;
        int idx = int.Parse(value[..value.IndexOf(',')], NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (idx >= now) return store.ActualToolWidth;
        return store.ActualToolWidth + (v - tool.GetSectionWidth(idx)) / 100.0;
    }

    /// <summary>
    /// AgOpenGPS's section timing rules, applied after <paramref name="key"/> was set
    /// (ConfigTool: nudLookAhead, nudLookAheadOff, nudTurnOffDelay): look ahead off is at most
    /// 0.8 × look ahead on, and look ahead off and the turn off delay exclude each other (one
    /// set above 0 puts the other to 0). Returns what was changed, for the operator, or null.
    /// </summary>
    public static string? ApplySectionTiming(string key, ConfigurationStore store)
    {
        var t = store.Tool;
        var notes = new List<string>();
        if (key is "tool.lookAheadOn" or "tool.lookAheadOff" && t.LookAheadOffSetting > t.LookAheadOnSetting * 0.8 + 1e-9)
        {
            t.LookAheadOffSetting = Math.Round(t.LookAheadOnSetting * 0.8, 2);
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Look ahead off set to {t.LookAheadOffSetting:0.##} s: at most 0.8 × look ahead on"));
        }
        if (key == "tool.lookAheadOff" && t.LookAheadOffSetting > 0 && t.TurnOffDelay > 0)
        {
            t.TurnOffDelay = 0;
            notes.Add("Turn off delay set to 0: it and look ahead off don't work together");
        }
        if (key == "tool.turnOffDelay" && t.TurnOffDelay > 0 && t.LookAheadOffSetting > 0)
        {
            t.LookAheadOffSetting = 0;
            notes.Add("Look ahead off set to 0: it and the turn off delay don't work together");
        }
        return notes.Count == 0 ? null : string.Join(". ", notes);
    }

    /// <summary>
    /// Null when the guidance can steer this machine; otherwise why not. AutoSteer checks
    /// it before it engages, whatever brought the values (a profile saved before the ranges
    /// above, a hand-edited file).
    /// </summary>
    public static string? WhyCannotSteer(ConfigurationStore store)
    {
        var v = store.Vehicle;
        if (!double.IsFinite(v.Wheelbase) || v.Wheelbase < Ranges["vehicle.wheelbase"].Min)
            return "set the wheelbase in Vehicle settings";
        if (!double.IsFinite(v.AntennaHeight) || !double.IsFinite(v.AntennaPivot) || !double.IsFinite(v.AntennaOffset))
            return "set the antenna position in Vehicle settings";
        double spacing = store.ActualToolWidth - store.Tool.Overlap;
        if (!double.IsFinite(spacing) || spacing <= 0.01 || !double.IsFinite(store.Tool.Offset))
            return "the overlap must be less than the implement width (Tool settings)";
        var a = store.AutoSteer;
        if (!(a.MinSteerSpeed < a.MaxSteerSpeed))
            return "the min steer speed must be below the max (AutoSteer settings)";
        return null;
    }

    // An imperial box shows the value converted and rounded (31.1 mph for the 50 km/h limit), and
    // the value typed back converts to a little past the limit (50.05 km/h): half a shown step
    // (0.05 of the unit) past either end is the limit itself. Metric input is what is stored.
    private static double RoundingSlack(Unit u, bool metric) => metric ? 1e-9 : u switch
    {
        Unit.Metre => 0.05 * 0.3048,
        Unit.Centimetre => 0.05 * 2.54,
        Unit.KmH => 0.05 * 1.609344,
        _ => 1e-9,
    };

    private static string Show(double v, Unit u, bool metric)
    {
        double shown = metric ? v : u switch
        {
            Unit.Metre => v / 0.3048,
            Unit.Centimetre => v / 2.54,
            Unit.KmH => v / 1.609344,
            _ => v,
        };
        return shown.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Symbol(Unit u, bool metric) => u switch
    {
        Unit.Metre => metric ? "m" : "ft",
        Unit.Centimetre => metric ? "cm" : "in",
        Unit.Degree => "°",
        Unit.Second => "s",
        Unit.KmH => metric ? "km/h" : "mph",
        Unit.Percent => "%",
        _ => "",
    };
}
