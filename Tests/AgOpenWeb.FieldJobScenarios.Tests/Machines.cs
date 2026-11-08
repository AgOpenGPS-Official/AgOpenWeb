// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// A real vehicle's geometry, in AgOpenWeb's terms: the pivot is the axle that is not steered
/// (rear axle of a tractor, front axle of a combine), pivot distances are ahead of it (+),
/// offsets right of the centreline (+), heights above the ground.
/// </summary>
public sealed record VehicleSpec(
    string Name,
    int Type,                // AgOpenWeb VehicleType: 0 tractor, 1 harvester, 2 articulated
    double Wheelbase,        // m, pivot axle to steered axle
    double TrackWidth,       // m
    double AntennaHeight,    // m above the ground
    double AntennaPivot,     // m ahead of the pivot axle
    double AntennaOffset,    // m right of the centreline
    double HitchLength,      // m, pivot axle to the drawbar / hitch pin (behind)
    double MaxSteerAngle)    // deg
{
    /// <summary>The Vehicle tab as a careful operator fills it in for this guidance hardware.</summary>
    public IEnumerable<string> Settings(SystemSpec? sys = null)
    {
        sys ??= new SystemSpec();
        var i = CultureInfo.InvariantCulture;
        yield return "vehicle.type:" + Type.ToString(i);
        yield return "vehicle.wheelbase:" + Wheelbase.ToString(i);
        yield return "vehicle.trackWidth:" + TrackWidth.ToString(i);
        yield return "vehicle.antennaHeight:" + AntennaHeight.ToString(i);
        yield return "vehicle.antennaPivot:" + AntennaPivot.ToString(i);
        yield return "vehicle.antennaOffset:" + AntennaOffset.ToString(i);
        yield return "vehicle.hitchLength:" + HitchLength.ToString(i);
        yield return "autosteer.maxSteerAngle:" + MaxSteerAngle.ToString(i);
        // A dual receiver (UM982, two F9Ps) gives the vehicle's heading and roll itself.
        yield return "gps.isDualGps:" + (sys.Dual ? "1" : "0");
        yield return "gps.dualHeadingOffset:0";
        yield return "roll.isRollInvert:0";
        yield return "roll.rollZero:0";
    }
}

/// <summary>An implement, in AgOpenWeb's terms (see <see cref="Settings"/>).</summary>
public sealed record ToolSpec(
    string Name,
    string Type,                 // front / rear / trailing / tbt
    double Width,                // m, working width
    int Sections,
    double Offset = 0,           // m right of the tractor's centreline (+)
    double HitchLength = 0,      // rigid: pivot axle to the working line; trailing / TBT: hitch pin to the tool's wheels
    double ToolToPivot = 0,      // trailing / TBT: the tool's wheels to the working line (+ behind)
    double TankHitch = 0,        // TBT: hitch pin to the tank's wheels
    double Overlap = 0)          // m
{
    public double SectionWidthCm => Width * 100.0 / Sections;

    public IEnumerable<string> Settings()
    {
        var i = CultureInfo.InvariantCulture;
        yield return "tool.type:" + Type;
        yield return "tool.numSections:" + Sections.ToString(i);
        for (int s = 0; s < Sections; s++) yield return "tool.sectionWidth:" + s.ToString(i) + "," + SectionWidthCm.ToString("0.###", i);
        if (Type is "front" or "rear") yield return "tool.hitchLength:" + HitchLength.ToString(i);
        else
        {
            yield return "tool.trailingHitchLength:" + HitchLength.ToString(i);
            yield return "tool.trailingToolToPivotLength:" + ToolToPivot.ToString(i);
            if (Type == "tbt") yield return "tool.tankTrailingHitchLength:" + TankHitch.ToString(i);
        }
        yield return "tool.offset:" + Offset.ToString(i);
        yield return "tool.overlap:" + Overlap.ToString(i);
    }
}

/// <summary>The vehicles and implements of the scenarios: Renato's own rig (Case Puma 180 CVX
/// with a UM982 on the cab, 8 m mounted sprayer in 8 sections of 1 m) and the machines of the
/// obstacle tests (3/6/12 m front, rear, trailed and TBT, side-shifted ones, a trailed 10 m
/// sprayer, hay rakes of 1, 2 and 4 rotors, a combine with a 6 m header).</summary>
public static class Machines
{
    // Case Puma 180 CVX: wheelbase 2.88 m (catalogue), antenna on the cab roof 3.10 m up and
    // 1.0 m ahead of the rear axle, drawbar 1.10 m behind it.
    public static readonly VehicleSpec Puma = new("Case Puma 180 CVX", 0, 2.88, 1.90, 3.10, 1.00, 0.0, 1.10, 40);
    public static readonly VehicleSpec Vineyard = new("Vineyard tractor 80 hp", 0, 2.10, 1.30, 2.30, 0.60, 0.0, 0.80, 45);
    // Antenna 25 cm right of the centreline (a bar mounted to one side of the beacon).
    public static readonly VehicleSpec Big = new("Tractor 300 hp", 0, 3.10, 2.10, 3.40, 1.20, 0.25, 1.40, 38);
    public static readonly VehicleSpec Articulated = new("Articulated 4WD 500 hp", 2, 3.90, 2.50, 3.80, 1.80, 0.0, 2.00, 35);
    // A combine steers with the rear axle: its pivot is the front axle; the antenna sits 0.5 m behind it.
    public static readonly VehicleSpec Combine = new("Combine", 1, 3.60, 2.80, 3.90, -0.50, 0.0, 1.00, 35);

    public static readonly ToolSpec Sprayer8 = new("Mounted sprayer 8 m (8 x 1 m)", "rear", 8, 8, HitchLength: 1.5);
    public static readonly ToolSpec Sprayer24x48 = new("Mounted sprayer 24 m (48 x 0.5 m)", "rear", 24, 48, HitchLength: 1.8);
    public static readonly ToolSpec Rear3 = new("Mounted cultivator 3 m", "rear", 3, 3, HitchLength: 1.2);
    public static readonly ToolSpec Rear6 = new("Mounted harrow 6 m", "rear", 6, 6, HitchLength: 1.5);
    public static readonly ToolSpec Rear3Shift = new("Side-shifted mower 3 m (+2.5 m)", "rear", 3, 1, Offset: 2.5, HitchLength: 1.0);
    public static readonly ToolSpec Front3 = new("Front mower 3 m", "front", 3, 1, HitchLength: 4.0);
    public static readonly ToolSpec Front6 = new("Front cultivator 6 m", "front", 6, 6, HitchLength: 4.2);
    public static readonly ToolSpec Header6 = new("Grain header 6 m", "front", 6, 6, HitchLength: 4.0);
    public static readonly ToolSpec Trailing3Shift = new("Trailed mower 3 m (-2.5 m)", "trailing", 3, 1, Offset: -2.5, HitchLength: 3.0);
    public static readonly ToolSpec Trailing6 = new("Trailed disc 6 m", "trailing", 6, 6, HitchLength: 3.0);
    public static readonly ToolSpec Trailing12 = new("Trailed cultivator 12 m", "trailing", 12, 12, HitchLength: 4.5);
    public static readonly ToolSpec Sprayer10 = new("Trailed sprayer 10 m (10 x 1 m)", "trailing", 10, 10, HitchLength: 4.0, ToolToPivot: 0.6);
    public static readonly ToolSpec Tbt12 = new("Seed drill 12 m with tank (TBT)", "tbt", 12, 12, HitchLength: 2.5, TankHitch: 3.0);
    public static readonly ToolSpec Tbt6 = new("Seed drill 6 m with tank (TBT)", "tbt", 6, 6, HitchLength: 2.0, TankHitch: 2.5);
    public static readonly ToolSpec Rake1 = new("Hay rake 1 rotor 4.2 m", "trailing", 4.2, 1, Offset: 0.9, HitchLength: 3.0);
    public static readonly ToolSpec Rake2 = new("Hay rake 2 rotors 7.5 m", "trailing", 7.5, 2, HitchLength: 5.0);
    public static readonly ToolSpec Rake4 = new("Hay rake 4 rotors 12.5 m", "trailing", 12.5, 4, HitchLength: 7.0);

    // The guidance hardware of each machine (the Boards simulator's choices).
    public static readonly SystemSpec PumaSys = new("um982", "tm171", "keya");          // Renato's
    public static readonly SystemSpec VineyardSys = new("f9p", "bno", "cytron");
    public static readonly SystemSpec BigSys = new("dualf9p", "bno", "danfoss");
    public static readonly SystemSpec ArticulatedSys = new("x20p", "tm171", "danfoss");
    public static readonly SystemSpec CombineSys = new("um982", "none", "danfoss");

    /// <summary>Every vehicle with the implements it would pull (the careful operator's matrix).</summary>
    /// <summary>Renato's no-till seed drill: 19 sections of 42 cm (7.98 m), trailed, a Seeder_ESP32
    /// that drives relay i from section i (PGN 229 above 16).</summary>
    public static readonly ToolSpec Seeder19 = new("Trailed seed drill 7.98 m (19 x 42 cm)", "trailing", 7.98, 19, HitchLength: 3.0, ToolToPivot: 0.8);
    /// <summary>A wide trailed implement: 21 m in 7 sections of 3 m.</summary>
    public static readonly ToolSpec Trailing21 = new("Trailed cultivator 21 m (7 x 3 m)", "trailing", 21, 7, HitchLength: 4.5, ToolToPivot: 1.0);
}
