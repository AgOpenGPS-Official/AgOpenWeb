// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// The values a Vehicle &amp; Tool setting can take on a real machine, so a slip of the unit
/// (centimetres typed in a metre box), of the sign, or a value never filled in is refused
/// with the reason instead of being steered with. A wheelbase of 0 never steers and a
/// negative one steers away from the line (pure pursuit's angle is proportional to it); an
/// overlap as wide as the implement puts every pass on one line, so AutoSteer pulls the
/// tractor back onto the first pass from wherever it engages.
/// <para>The ranges are AgOpenGPS's settings forms' (FormConfig: wheelbase 50 to 1999 cm,
/// antenna height 0 to 10 m, sections up to 50 m, …), in metres; a sign the form takes from
/// a left/right or ahead/behind choice is a sign here.</para>
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
        if (key is "tool.numSections" or "tool.zones" && v != Math.Floor(v))
            return r.Name + " must be a whole number";
        if (v < r.Min - 1e-9 || v > r.Max + 1e-9)
        {
            string unit = Symbol(r.Unit, metric);
            if (unit.Length > 0 && unit != "°" && unit != "%") unit = " " + unit;
            return $"{r.Name} must be {Show(r.Min, r.Unit, metric)} to {Show(r.Max, r.Unit, metric)}{unit} (not {Show(v, r.Unit, metric)}{unit})";
        }

        // The passes are the implement width less the overlap apart: they must stay apart.
        if (key == "tool.overlap" && v >= store.ActualToolWidth - 0.01)
            return $"Overlap must be less than the implement width ({Show(store.ActualToolWidth, Unit.Metre, metric)} {Symbol(Unit.Metre, metric)})";
        return null;
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
        return null;
    }

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
