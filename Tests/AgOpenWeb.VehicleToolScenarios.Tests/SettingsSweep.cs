// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>What a setting is expected to change.</summary>
[Flags]
public enum Effect
{
    /// <summary>Display only: nothing the modules get may change.</summary>
    None = 0,
    /// <summary>The steer angle AgOpenWeb computes or sends in PGN 254.</summary>
    Steer = 1,
    /// <summary>Whether it steers: PGN 254's status byte, AutoSteer engaged.</summary>
    Status = 2,
    /// <summary>The settings the modules are sent (PGN 251/252/247/238/236/235).</summary>
    Pgn = 4,
    /// <summary>Where the sections switch (PGN 239 bytes 11-12).</summary>
    Sections = 8,
    /// <summary>Which pass AgOpenWeb shows and steers to.</summary>
    Pass = 16,
}

/// <summary>One setting tried one way: from <see cref="Base"/> to <see cref="Flip"/> on
/// <see cref="Probe"/>, expecting <see cref="Expect"/>. <see cref="Setup"/> is set first
/// (for every row of its group), so the setting is tried where it can act. An empty
/// <see cref="Base"/> is an action (a button), undone by setting the setup again.</summary>
public sealed record SweepCase(string Key, string Base, string Flip, Probe Probe, Effect Expect, string What, params string[] Setup)
{
    public string Group => Probe + (Setup.Length > 0 ? " (" + string.Join(", ", Setup) + ")" : "");
}

/// <summary>A row of the sweep's report table.</summary>
public sealed class SweepRow
{
    public SweepCase Case = null!;
    public string Changed = "", Verdict = "", Kind = "";
}

/// <summary>
/// Item 3.3 of the harness plan: for every guidance and AutoSteer setting, flip it and check
/// that something measurable changes: the steer angle, whether it steers, the bytes the
/// modules are sent, or where the sections switch. Settings that changed nothing were the
/// largest early class of bugs (#99, #101, #105, #106, #110, #111, #112).
///
/// Each group of rows gets a fresh AgOpenWeb (Renato's Case Puma with a UM982 and the 8 m
/// sprayer, set up by the careful operator), a field with an AB line and a job. The group's
/// probe runs twice on the base values (how much a run differs from itself: the noise), then
/// once per row with that row's setting flipped and set back, then once more on the base
/// values (still the same as at the start?). The list of settings is checked against the
/// settings the host accepts and the web client sends, read from the source, so a new
/// setting no row tries shows up in the report.
/// </summary>
public static class SettingsSweep
{
    public const string Name = "settings sweep";

    // Units in the "What" column are the panel's.
    public static readonly SweepCase[] Cases =
    {
        // --- What the steer module is sent: PGN 252 (steer settings), 251 (steer config) ---
        new("autosteer.proportionalGain", "50", "120", Probe.Module, Effect.Pgn, "Proportional gain (PGN 252)"),
        new("autosteer.maxPwm", "180", "230", Probe.Module, Effect.Pgn, "Maximum PWM (PGN 252)"),
        new("autosteer.minPwm", "25", "60", Probe.Module, Effect.Pgn, "Minimum PWM (PGN 252)"),
        new("autosteer.countsPerDegree", "110", "80", Probe.Module, Effect.Pgn, "Counts per degree (PGN 252)"),
        new("autosteer.wasOffset", "3", "250", Probe.Module, Effect.Pgn, "WAS offset (PGN 252)"),
        new("autosteer.ackermann", "100", "70", Probe.Module, Effect.Pgn, "Ackermann (PGN 252)"),
        new("autosteer.invertWas", "0", "1", Probe.Module, Effect.Pgn, "Invert WAS (PGN 251)"),
        new("autosteer.invertMotor", "0", "1", Probe.Module, Effect.Pgn, "Invert motor (PGN 251)"),
        new("autosteer.invertRelays", "0", "1", Probe.Module, Effect.Pgn, "Invert relays (PGN 251)"),
        new("autosteer.motorDriver", "1", "0", Probe.Module, Effect.Pgn, "Motor driver (PGN 251)"),
        new("autosteer.adConverter", "1", "0", Probe.Module, Effect.Pgn, "A/D converter (PGN 251)"),
        new("autosteer.danfossEnabled", "0", "1", Probe.Module, Effect.Pgn, "Danfoss valve (PGN 251)"),
        new("autosteer.imuAxisSwap", "0", "1", Probe.Module, Effect.Pgn, "IMU axis swap (PGN 251)"),
        new("autosteer.externalEnable", "0", "1", Probe.Module, Effect.Pgn, "Steer switch type (PGN 251)"),
        new("autosteer.turnSensorEnabled", "0", "1", Probe.Module, Effect.Pgn, "Turn sensor on (PGN 251)"),
        new("autosteer.turnSensorCounts", "255", "100", Probe.Module, Effect.Pgn, "Turn sensor counts (PGN 251)"),
        new("autosteer.pressureSensorEnabled", "0", "1", Probe.Module, Effect.Pgn, "Pressure sensor on (PGN 251)"),
        new("autosteer.currentSensorEnabled", "0", "1", Probe.Module, Effect.Pgn, "Current sensor on (PGN 251)"),
        new("autosteer.minSteerSpeed", "0", "3", Probe.Module, Effect.Pgn, "Minimum steer speed (PGN 251 byte 7)"),
        new("autosteer.maxSteerAngle", "40", "25", Probe.Module, Effect.Pgn, "Maximum steer angle (PGN 247)"),
        new("autosteer.pressureTripPoint", "0", "50", Probe.Module, Effect.Pgn, "Pressure trip point (PGN 251)", "autosteer.pressureSensorEnabled:1"),
        new("autosteer.currentTripPoint", "0", "50", Probe.Module, Effect.Pgn, "Current trip point (PGN 251)", "autosteer.currentSensorEnabled:1"),
        // --- What the machine module is sent: PGN 238 (config), 236 (pins), 235 (section widths) ---
        new("machine.raiseTime", "4", "8", Probe.Module, Effect.Pgn, "Hydraulic raise time (PGN 238)"),
        new("machine.lowerTime", "2", "5", Probe.Module, Effect.Pgn, "Hydraulic lower time (PGN 238)"),
        new("machine.hydraulicLiftEnabled", "0", "1", Probe.Module, Effect.Pgn, "Hydraulic lift on (PGN 238)"),
        new("machine.invertRelay", "0", "1", Probe.Module, Effect.Pgn, "Invert relays (PGN 238)"),
        new("machine.user1", "0", "7", Probe.Module, Effect.Pgn, "User value 1 (PGN 238)"),
        new("machine.user2", "0", "7", Probe.Module, Effect.Pgn, "User value 2 (PGN 238)"),
        new("machine.user3", "0", "7", Probe.Module, Effect.Pgn, "User value 3 (PGN 238)"),
        new("machine.user4", "0", "7", Probe.Module, Effect.Pgn, "User value 4 (PGN 238)"),
        new("machine.pin", "0,1", "0,2", Probe.Module, Effect.Pgn, "Pin 1 function (PGN 236)"),
        new("machine.resetPins", "", "", Probe.Module, Effect.Pgn, "Reset pins button (PGN 236)", "machine.pin:0,5"),
        new("tool.numSections", "8", "4", Probe.Module, Effect.Pgn, "Number of sections (PGN 235)"),
        new("tool.defaultSectionWidth", "100", "50", Probe.Module, Effect.Pgn, "Default section width, cm (PGN 235)"),
        new("tool.sectionWidth", "0,100", "0,50", Probe.Module, Effect.Pgn, "Section 1 width, cm (PGN 235)"),
        new("vehicle.wheelbase", "2.88", "3.5", Probe.Module, Effect.Pgn, "Wheelbase, m (PGN 247)"),
        new("vehicle.trackWidth", "1.9", "2.3", Probe.Module, Effect.Pgn, "Track width, m (PGN 247)"),
        new("vehicle.antennaHeight", "3.1", "2", Probe.Module, Effect.Pgn, "Antenna height, m (PGN 247)"),
        new("vehicle.antennaPivot", "1", "1.5", Probe.Module, Effect.Pgn, "Antenna ahead of the pivot, m (PGN 247)"),
        new("vehicle.antennaOffset", "0", "0.3", Probe.Module, Effect.Pgn, "Antenna offset, m (PGN 247)"),
        new("vehicle.imuPivot", "0", "0.5", Probe.Module, Effect.Pgn, "IMU ahead of the pivot, m (PGN 247)"),
        new("vehicle.imuHeight", "0", "1.2", Probe.Module, Effect.Pgn, "IMU height, m (PGN 247)"),
        new("vehicle.imuOffset", "0", "0.2", Probe.Module, Effect.Pgn, "IMU offset, m (PGN 247)"),

        // --- The steer angle: the weave across the line, AutoSteer on ---
        new("autosteer.steerResponseHold", "3", "1.5", Probe.Approach, Effect.Steer, "Look-ahead hold (Pure Pursuit)"),
        new("autosteer.speedFactor", "1.5", "0.5", Probe.Approach, Effect.Steer, "Look-ahead speed factor"),
        new("autosteer.acquireFactor", "0.9", "0.5", Probe.Approach, Effect.Steer, "Acquire factor (look-ahead off the line)"),
        new("autosteer.integralGain", "0", "0.8", Probe.Approach, Effect.Steer, "Integral (Pure Pursuit)"),
        new("autosteer.isStanleyMode", "0", "1", Probe.Approach, Effect.Steer, "Stanley instead of Pure Pursuit"),
        new("autosteer.maxSteerAngle", "40", "15", Probe.Approach, Effect.Steer, "Maximum steer angle (the limit)"),
        new("vehicle.wheelbase", "2.88", "3.8", Probe.Approach, Effect.Steer, "Wheelbase"),
        new("vehicle.antennaPivot", "1", "2", Probe.Approach, Effect.Steer, "Antenna ahead of the pivot"),
        new("vehicle.antennaOffset", "0", "0.4", Probe.Approach, Effect.Steer, "Antenna offset"),
        new("tool.offset", "0", "0.5", Probe.Approach, Effect.Steer, "Implement offset (moves the passes)"),
        new("tool.overlap", "0", "0.5", Probe.Approach, Effect.Steer, "Overlap (pass spacing)"),
        new("gps.dualHeadingOffset", "0", "5", Probe.Approach, Effect.Steer, "Dual heading offset"),
        new("gps.isDualGps", "1", "0", Probe.Approach, Effect.Steer, "Dual antenna"),
        new("gps.autoDualFix", "0", "1", Probe.Approach, Effect.Steer, "Dual: switch to fix-to-fix above a speed", "gps.dualSwitchSpeed:5"),
        new("gps.dualSwitchSpeed", "1.2", "12", Probe.Approach, Effect.Steer, "Dual: speed to switch to fix-to-fix", "gps.autoDualFix:1"),
        new("autosteer.lineWidth", "2", "6", Probe.Approach, Effect.None, "Guidance line width (display)"),
        new("autosteer.cmPerPixel", "5", "12", Probe.Approach, Effect.None, "Light bar cm per pixel (display)"),
        new("autosteer.lightbarEnabled", "1", "0", Probe.Approach, Effect.None, "Light bar on (display)"),
        new("autosteer.steerBarEnabled", "0", "1", Probe.Approach, Effect.None, "Steer bar instead of light bar (display)"),
        new("autosteer.guidanceBarOn", "1", "0", Probe.Approach, Effect.None, "Guidance bar on (display)"),
        new("vehicle.antennaSide", "right", "left", Probe.Approach, Effect.Steer, "Antenna left/right button", "vehicle.antennaOffset:0.4"),
        new("tool.offsetSide", "right", "left", Probe.Approach, Effect.Steer, "Implement offset left/right button", "tool.offset:0.5"),
        new("tool.overlapSide", "overlap", "gap", Probe.Approach, Effect.Steer, "Overlap/gap button", "tool.overlap:0.3"),
        new("autosteer.stanleyAggressiveness", "1", "2.5", Probe.Approach, Effect.Steer, "Stanley aggressiveness", "autosteer.isStanleyMode:1"),
        new("autosteer.stanleyOvershootReduction", "1", "0.3", Probe.Approach, Effect.Steer, "Stanley overshoot reduction", "autosteer.isStanleyMode:1"),
        new("autosteer.nudgeDistance", "20", "60", Probe.Nudge, Effect.Steer, "Nudge distance, cm"),
        new("autosteer.refNudgeDistance", "5", "40", Probe.RefNudge, Effect.Steer, "Reference nudge distance, cm"),
        new("autosteer.manualTurnsEnabled", "0", "1", Probe.ManualTurn, Effect.Steer, "Manual turns speed limit on", "autosteer.manualTurnsSpeed:5"),
        new("autosteer.manualTurnsSpeed", "20", "5", Probe.ManualTurn, Effect.Steer, "Manual turns speed limit, km/h", "autosteer.manualTurnsEnabled:1"),
        new("autosteer.deadzoneHeading", "0", "2", Probe.Deadzone, Effect.Steer, "Deadzone heading, °", "autosteer.deadzoneDelay:0"),
        new("autosteer.deadzoneDelay", "0", "5", Probe.Deadzone, Effect.Steer, "Deadzone delay, s", "autosteer.deadzoneHeading:2"),

        // --- On a 6° cross slope, roll bumping ±2° ---
        new("autosteer.sideHillCompensation", "0", "1", Probe.Slope, Effect.Steer, "Side hill compensation"),
        new("vehicle.antennaHeight", "3.1", "1.5", Probe.Slope, Effect.Steer, "Antenna height (roll correction)"),
        new("roll.rollZero", "0", "3", Probe.Slope, Effect.Steer, "Roll zero, °"),
        new("roll.isRollInvert", "0", "1", Probe.Slope, Effect.Steer, "Invert roll"),
        new("roll.rollFilter", "0", "0.8", Probe.Slope, Effect.Steer, "Roll filter"),
        new("roll.setZero", "", "", Probe.Slope, Effect.Steer, "Zero roll button", "roll.rollZero:3"),

        // --- Single antenna ---
        new("gps.headingFusionWeight", "0.3", "0.9", Probe.SingleImu, Effect.Steer, "Heading fusion (IMU vs GPS)", "gps.isDualGps:0"),
        new("gps.minGpsStep", "0.05", "1", Probe.SingleImu, Effect.Steer, "Minimum GPS step, m", "gps.isDualGps:0"),
        new("gps.fixToFixDistance", "0.5", "3", Probe.SingleNoImu, Effect.Steer, "Fix-to-fix heading distance, m", "gps.isDualGps:0"),
        new("gps.minGpsStep", "0.05", "1", Probe.SingleNoImu, Effect.Steer, "Minimum GPS step, m (no IMU)", "gps.isDualGps:0"),

        // --- Whether it steers ---
        new("autosteer.steerInReverse", "0", "1", Probe.Reverse, Effect.Status, "Steer in reverse"),
        // In Dual, AgOpenGPS tells reverse from the travel whatever this setting (Position.designer.cs).
        new("gps.reverseDetection", "1", "0", Probe.ReverseImu, Effect.Status, "Reverse detection (single antenna + IMU)", "gps.isDualGps:0"),
        new("gps.dualReverseDistance", "0.25", "2", Probe.Reverse, Effect.Status, "Dual: travel to tell reverse, m"),
        new("autosteer.minSteerSpeed", "0", "3", Probe.Slow, Effect.Status, "Minimum steer speed, km/h"),
        new("autosteer.maxSteerSpeed", "25", "15", Probe.Fast, Effect.Status, "Maximum steer speed, km/h"),
        new("gps.rtkLostAlarm", "1", "0", Probe.RtkDrop, Effect.Status, "RTK lost alarm", "gps.rtkLostAction:1"),
        new("gps.rtkLostAction", "0", "1", Probe.RtkDrop, Effect.Status, "RTK lost: stop AutoSteer", "gps.rtkLostAlarm:1"),
        new("autosteer.nextGuidanceTime", "1.5", "5", Probe.FreeDrive, Effect.Pass, "Next guidance line look-ahead, s"),

        // --- Sections: back over a worked strip ---
        new("tool.lookAheadOn", "0", "2", Probe.Sections, Effect.Sections, "Look-ahead on, s"),
        new("tool.lookAheadOff", "0", "1.5", Probe.Sections, Effect.Sections, "Look-ahead off, s"),
        new("tool.turnOffDelay", "0", "2", Probe.Sections, Effect.Sections, "Turn-off delay, s"),
        new("tool.hitchLength", "1.5", "4", Probe.Sections, Effect.Sections, "Hitch length (mounted)"),
        // AgOpenGPS uses the vehicle type for the picture and, for a combine, a header in front only.
        new("vehicle.type", "0", "1", Probe.Sections, Effect.Sections, "Vehicle type (tractor → combine: header in front)"),
        new("tool.type", "rear", "front", Probe.Sections, Effect.Sections, "Implement type (rear → front)"),
        new("tool.numSections", "8", "4", Probe.Sections, Effect.Sections, "Number of sections"),
        new("tool.isSectionsNotZones", "1", "0", Probe.Sections, Effect.Sections, "Sections, not zones"),
        new("tool.isMultiColoredSections", "0", "1", Probe.Sections, Effect.None, "Multi-coloured coverage (display)"),
        new("tool.singleCoverageColor", "98FB98", "FF0000", Probe.Sections, Effect.None, "Coverage colour (display)"),
        new("tool.sectionColor", "0,98FB98", "0,FF0000", Probe.Sections, Effect.None, "Section 1 colour (display)"),
        new("tool.zones", "2", "4", Probe.Sections, Effect.Sections, "Number of zones", "tool.isSectionsNotZones:0"),
        new("tool.zoneEnd", "0,4", "0,2", Probe.Sections, Effect.Sections, "Zone 1 last section", "tool.isSectionsNotZones:0"),
        new("vehicle.hitchLength", "1.1", "3", Probe.Sections, Effect.Sections, "Drawbar length (trailed)", "tool.type:trailing"),
        new("tool.trailingHitchLength", "2.5", "5", Probe.Sections, Effect.Sections, "Trailed hitch length", "tool.type:trailing"),
        new("tool.trailingToolToPivotLength", "0", "1.5", Probe.Sections, Effect.Sections, "Trailed: working line behind its wheels", "tool.type:trailing"),
        new("tool.pivotSide", "behind", "ahead", Probe.Sections, Effect.Sections, "Working line behind/ahead button", "tool.type:trailing", "tool.trailingToolToPivotLength:1"),
        new("tool.tankTrailingHitchLength", "3", "5", Probe.Sections, Effect.Sections, "Tank hitch length (TBT)", "tool.type:tbt"),
        new("tool.minCoverage", "0", "90", Probe.SectionsShifted, Effect.Sections, "Minimum coverage, %"),
        // The margin widens what is painted, so the edge section 0.2 m beside the strip finds
        // 85 % or 100 % of it worked: under or over a 90 % minimum coverage.
        new("tool.coverageMargin", "5", "40", Probe.SectionsShifted, Effect.Sections, "Coverage margin, cm", "tool.minCoverage:90"),
        new("tool.slowSpeedCutoff", "0.5", "3", Probe.SectionsSlow, Effect.Sections, "Slow speed cut-off, km/h"),
        new("tool.isWorkSwitchEnabled", "0", "1", Probe.Switches, Effect.Sections, "Work switch on"),
        new("tool.isSteerSwitchEnabled", "0", "1", Probe.Switches, Effect.Sections, "Steer switch controls sections"),
        new("tool.isWorkSwitchActiveLow", "0", "1", Probe.Switches, Effect.Sections, "Work switch active low", "tool.isWorkSwitchEnabled:1"),
        new("tool.isWorkSwitchManualSections", "0", "1", Probe.Switches, Effect.Sections, "Work switch: manual sections", "tool.isWorkSwitchEnabled:1"),
        new("tool.isSteerSwitchManualSections", "0", "1", Probe.Switches, Effect.Sections, "Steer switch: manual sections", "tool.isSteerSwitchEnabled:1"),

        // --- Not tried yet ---
        new("uturn.radius", "", "", Probe.None, Effect.Steer, "needs a boundary and a headland"),
        new("uturn.extension", "", "", Probe.None, Effect.Steer, "needs a boundary and a headland"),
        new("uturn.smoothing", "", "", Probe.None, Effect.Steer, "needs a boundary and a headland"),
        new("uturn.style", "", "", Probe.None, Effect.Steer, "needs a boundary and a headland"),
        new("uturn.distanceFromBoundary", "", "", Probe.None, Effect.Steer, "needs a boundary and a headland"),
        new("autosteer.uTurnCompensation", "", "", Probe.None, Effect.Steer, "needs a U-turn"),
        new("tool.isSectionOffWhenOut", "", "", Probe.None, Effect.Sections, "needs a boundary"),
        new("tool.isHeadlandSectionControl", "", "", Probe.None, Effect.Sections, "needs a headland"),
        new("machine.lookAhead", "", "", Probe.None, Effect.Pgn, "hydraulic lift look-ahead: needs a headland"),
        new("tool.length", "", "", Probe.None, Effect.Steer, "hard-boundary U-turn clearance: needs a hard boundary"),
        new("tram.passes", "", "", Probe.None, Effect.Pgn, "needs tram lines"),
        new("tram.display", "", "", Probe.None, Effect.Pgn, "needs tram lines"),
        new("tram.line", "", "", Probe.None, Effect.Pgn, "needs tram lines"),
        new("tram.width", "", "", Probe.None, Effect.Pgn, "needs tram lines"),
        new("tram.outerInverted", "", "", Probe.None, Effect.Pgn, "needs tram lines"),
        new("tool.hitchType", "", "", Probe.None, Effect.None, "ISOBUS connector type (the ISOBUS harness)"),
        new("vehicle.hitchType", "", "", Probe.None, Effect.None, "ISOBUS connector type (the ISOBUS harness)"),
    };

    /// <summary>Settings of the device, the screen, the network and the units: not guidance or
    /// AutoSteer settings (the web client, GPS/NTRIP and imperial-units items cover them).</summary>
    public static bool OutOfScope(string key) =>
        key == "units" || key.StartsWith("display.", StringComparison.Ordinal) || key.StartsWith("conn.", StringComparison.Ordinal);

    public static async Task RunAsync(Journal journal, string? only)
    {
        var v = Machines.Puma;
        var tool = Machines.Sprayer8;
        var sys = Machines.PumaSys;
        var careful = Drive.Careful(v, tool, sys);

        var cases = Cases.Where(c => only == null || only.Split('|').Any(o =>
            c.Key.Contains(o, StringComparison.OrdinalIgnoreCase) || c.Group.Contains(o, StringComparison.OrdinalIgnoreCase)
            || "sweep".Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var c in cases.Where(c => c.Probe == Probe.None))
            journal.Sweep.Add(new SweepRow { Case = c, Changed = "–", Verdict = "not tried yet: " + c.What, Kind = "harness gap" });

        foreach (var group in cases.Where(c => c.Probe != Probe.None).GroupBy(c => c.Group))
        {
            var list = group.ToList();
            var probe = list[0].Probe;
            var truthTool = list[0].Setup.Any(s => s == "tool.type:trailing") ? Machines.Trailing6
                : list[0].Setup.Any(s => s == "tool.type:tbt") ? Machines.Tbt6 : tool;
            string dir = Path.Combine(Drive.DataRootBase, "s-" + probe + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            FieldRun? run = null;
            try
            {
                run = await FieldRun.StartAsync(dir, new TruthMachine(v, truthTool, 0, sys));
                if (!await run.MakeFieldAsync())
                {
                    journal.Add(new Finding("HARNESS", Severity.Bug, "the sweep could not make its field: " + string.Join("; ", run.Problems)), group.Key);
                    continue;
                }
                var driver = new SweepDriver(run);
                await run.ApplyAsync(careful);
                foreach (var s in list[0].Setup) await driver.SetAsync(s);
                foreach (var c in list.Where(c => c.Base.Length > 0)) await driver.SetAsync(c.Key + ":" + c.Base);
                await run.Rig.WaitForVmAsync();
                run.CollectMessages();

                var b1 = await driver.RunAsync(probe);
                var b2 = await driver.RunAsync(probe);
                var noise = TraceDiff.Of(b1, b2);
                // A strip worked with every section on automatic paints the implement's width.
                if (double.IsFinite(b1.StripExpected) && b1.StripArea < 0.9 * b1.StripExpected)
                    journal.Add(new Finding("SECTIONS-PAINT-SHORT", Severity.Bug, F(
                        $"a 100 m strip worked over fresh ground with every section on automatic painted {b1.StripArea:0} m² of the {b1.StripExpected:0} m² the implement covered (settings: {(list[0].Setup.Length > 0 ? string.Join(", ", list[0].Setup) : "the careful operator's")}); the way back over it then finds ground it never painted")),
                        group.Key);
                if (b1.Problem.Length > 0)
                    journal.Add(new Finding("SWEEP-PROBE-" + probe.ToString().ToUpperInvariant(), Severity.Improvement,
                        $"the {probe} probe on the base settings: {b1.Problem} (harness artifact until shown otherwise)"), group.Key);

                foreach (var c in list)
                {
                    string before = c.Base.Length > 0 ? c.Key + ":" + c.Base : "";
                    await driver.SetAsync(c.Key + ":" + c.Flip);
                    var t = await driver.RunAsync(probe);
                    if (before.Length > 0) await driver.SetAsync(before);
                    else foreach (var s in c.Setup) await driver.SetAsync(s);
                    // The module probe compares what was sent before and after the change.
                    var d = TraceDiff.Of(probe == Probe.Module ? b2 : b1, t);
                    var row = Judge(journal, c, d, noise, t);
                    journal.Sweep.Add(row);
                    if (probe == Probe.Module) b2 = await driver.RunAsync(probe);
                }

                var b3 = await driver.RunAsync(probe);
                var drift = TraceDiff.Of(b1, b3);
                var (moved, _) = Changes(drift, noise);
                if (moved != Effect.None && probe != Probe.Module)
                    journal.Add(new Finding("SWEEP-BASELINE-DRIFT", Severity.Improvement,
                        $"the {probe} probe on the base settings gave a different result after the group's rows than before ({Describe(drift)}): a setting was not put back, or the app kept state (harness artifact until shown otherwise)"),
                        group.Key);
            }
            catch (Exception ex)
            {
                journal.Add(new Finding("HARNESS", Severity.Bug, "the sweep failed: " + ex.GetType().Name + ": " + ex.Message), group.Key);
            }
            finally
            {
                if (run != null) await run.DisposeAsync();
                try { Directory.Delete(dir, true); } catch { /* temp */ }
            }
        }

        CheckKeys(journal, cases.Count == Cases.Length);
    }

    /// <summary>What changed beyond the noise, as effect flags and words.</summary>
    private static (Effect Effects, List<string> Words) Changes(TraceDiff d, TraceDiff noise)
    {
        var e = Effect.None;
        var w = new List<string>();
        double steerTol = Math.Max(0.05, 3 * Math.Max(noise.SteerMax, noise.SentMax) + 0.02);
        int countTol = Math.Max(3, 3 * Math.Max(Math.Max(noise.StatusFixes, noise.EngagedFixes), Math.Max(noise.GuidanceCount, noise.HeldDiff)) + 2);
        if (d.SteerMax > steerTol || d.SentMax > steerTol)
        {
            e |= Effect.Steer;
            w.Add(F($"steer angle up to {Math.Max(d.SteerMax, d.SentMax):0.0}°"));
        }
        if (d.GuidanceCount > countTol) { e |= Effect.Steer; w.Add(F($"guidance on {d.GuidanceCount} fixes more/fewer")); }
        if (d.HeldDiff > countTol) { e |= Effect.Steer; w.Add(F($"PGN 254 angle held on {d.HeldDiff} fixes more/fewer")); }
        if (d.StatusFixes > countTol || d.EngagedFixes > countTol || (d.DisengageDiffers && !noise.DisengageDiffers))
        {
            e |= Effect.Status;
            w.Add(F($"steering on/off on {Math.Max(d.StatusFixes, d.EngagedFixes)} fixes") + (d.DisengageDiffers ? " (AutoSteer went off)" : ""));
        }
        if (d.PgnChanges.Count > 0) { e |= Effect.Pgn; w.Add(string.Join("; ", d.PgnChanges)); }
        int sectTol = Math.Max(3, 3 * noise.SectionFixes + 2);
        // Switch by switch: each one against how far that same switch moved between the two
        // base runs (a section that switched a different number of times there is noise).
        double moved = 0;
        bool count = false;
        foreach (var (k, dd) in d.EdgeDiffs)
        {
            if (k.Switch < 0)
            {
                if (!noise.EdgeDiffs.ContainsKey(k)) count = true;
                continue;
            }
            if (noise.EdgeDiffs.ContainsKey((k.Section, -1))) continue;
            double tol = Math.Max(0.5, 3 * noise.EdgeDiffs.GetValueOrDefault(k) + 0.2);
            if (dd > tol) moved = Math.Max(moved, dd);
        }
        if (d.SectionFixes > sectTol || moved > 0 || count)
        {
            e |= Effect.Sections;
            w.Add(count ? F($"sections switch differently ({d.SectionFixes} fixes)")
                : moved > 0 ? F($"sections switch up to {moved:0.0} m apart ({d.SectionFixes} fixes)")
                : F($"sections differ on {d.SectionFixes} fixes"));
        }
        if (d.PassFixes > Math.Max(3, 3 * noise.PassFixes + 2)) { e |= Effect.Pass; w.Add(F($"another pass shown on {d.PassFixes} fixes")); }
        if (d.MachineFixes > Math.Max(3, 3 * noise.MachineFixes + 2)) w.Add(F($"PGN 239 U-turn/lift/tram bytes on {d.MachineFixes} fixes"));
        return (e, w);
    }

    private static string Describe(TraceDiff d)
    {
        var (_, w) = Changes(d, new TraceDiff(0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, new List<string>()));
        return w.Count == 0 ? "nothing" : string.Join(", ", w);
    }

    private static SweepRow Judge(Journal journal, SweepCase c, TraceDiff d, TraceDiff noise, Trace t)
    {
        var (e, words) = Changes(d, noise);
        var row = new SweepRow { Case = c, Changed = words.Count == 0 ? "nothing" : string.Join("; ", words) };
        string where = c.Key + " " + (c.Base.Length > 0 ? c.Base + " → " + c.Flip : "(pressed)") + ", " + c.Group;
        if (t.Problem.Length > 0) row.Changed += " ⚠ " + t.Problem;
        if (c.Expect == Effect.None)
        {
            bool bad = (e & (Effect.Steer | Effect.Status | Effect.Sections)) != 0;
            row.Verdict = bad ? "changes what the modules get" : "display only: nothing else moved (as it should)";
            row.Kind = bad ? "app defect" : "";
            if (bad)
                journal.Add(new Finding("SETTING-UNEXPECTED-EFFECT " + c.Key, Severity.Inconsistency,
                    $"{c.What}: a display setting changed what AgOpenWeb sends ({row.Changed})"), where);
        }
        else if ((e & c.Expect) != 0)
        {
            row.Verdict = "works";
        }
        else
        {
            row.Verdict = "**no effect**";
            row.Kind = "app defect (to confirm)";
            journal.Add(new Finding("SETTING-NO-EFFECT " + c.Key, Severity.Bug,
                $"{c.What}: {(c.Base.Length > 0 ? c.Base + " → " + c.Flip : "pressed")} changed nothing in {Expected(c.Expect)} on the {c.Probe} probe" +
                (words.Count > 0 ? " (it did change: " + row.Changed + ")" : "")), where);
        }
        return row;
    }

    public static string Expected(Effect e) => e == Effect.None ? "nothing (display)" : string.Join(" or ", new[]
    {
        (e & Effect.Steer) != 0 ? "the steer angle" : null,
        (e & Effect.Status) != 0 ? "whether it steers" : null,
        (e & Effect.Pgn) != 0 ? "the module PGNs" : null,
        (e & Effect.Sections) != 0 ? "the sections" : null,
        (e & Effect.Pass) != 0 ? "the pass shown" : null,
    }.Where(s => s != null));

    /// <summary>Every setting the host accepts or the web client sends, against the rows.</summary>
    private static void CheckKeys(Journal journal, bool all)
    {
        var root = SettingKeys.RepoRoot();
        if (root == null) { journal.SweepNotes.Add("The repository was not found from the test folder: the setting list was not checked against the source."); return; }
        var host = SettingKeys.HostKeys(root);
        var client = SettingKeys.ClientKeys(root);
        var rows = new HashSet<string>(Cases.Select(c => c.Key), StringComparer.Ordinal);
        foreach (var k in client.Where(k => !host.Contains(k)))
            journal.Add(new Finding("SETTING-IGNORED-BY-HOST " + k, Severity.Bug,
                $"the web client sends config.set {k}, which the host's config bridge does not handle: the setting is dropped"), "config bridge");
        var missing = host.Where(k => !rows.Contains(k) && !OutOfScope(k)).ToList();
        if (all)
            foreach (var k in missing)
                journal.Add(new Finding("SETTING-NOT-SWEPT " + k, Severity.Improvement,
                    $"the host accepts config.set {k}, and no sweep row tries it (harness gap: add a row to SettingsSweep.Cases)"), "settings sweep");
        journal.SweepNotes.Add(F($"Settings the host accepts: {host.Count}; the web client sends: {client.Count}; out of scope (screen, device, network, units): {host.Count(OutOfScope)}; rows: {Cases.Length} for {rows.Count} settings."));
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
