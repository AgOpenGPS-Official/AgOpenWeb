// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The careless operator: the mistakes people make in Vehicle &amp; Tool. Measures typed in the
/// wrong unit (cm in a metre box), with the wrong sign, forgotten (left at 0), an antenna
/// set up the wrong way round, a roll sensor never calibrated or zeroed on a slope, section
/// widths in metres, an overlap as wide as the boom, a trailed implement set as mounted,
/// settings changed with AutoSteer on, profiles with odd names.
///
/// Each mistake is driven on the real machine (it is set right; only AgOpenWeb is told
/// wrong) to see what it does in the field: the effect table is for the operator. Where the
/// value is impossible (a negative wheelbase, a 310 m antenna) the app could have refused
/// it, so taking it with no word is a finding; where AutoSteer then steers the machine
/// off its pass, that is a safety finding whatever the value.
///
/// Some values cannot be typed in the web client as they are sent here; they are sent
/// anyway, because the host is the last line: any browser on the LAN can send any command.
/// </summary>
public static class CarelessOperator
{
    public const string Name = "careless operator";

    private static readonly VehicleSpec Puma = Machines.Puma;
    private static readonly ToolSpec Boom = Machines.Sprayer8;
    private static readonly SystemSpec Um = Machines.PumaSys;                      // UM982 + TM171 + Keya
    private static readonly SystemSpec X20 = new("x20p", "tm171", "keya");         // single antenna, roll from the IMU

    private static List<string> W(VehicleSpec v, ToolSpec t, SystemSpec s, params string[] m) => Drive.With(v, t, s, m);

    private static IEnumerable<DriveSpec> Drives()
    {
        // --- The tractor ----------------------------------------------------------------
        yield return new("V-WB0", "Wheelbase left at 0", Puma, Boom, Um, 0, W(Puma, Boom, Um, "vehicle.wheelbase:0"), Impossible: true);
        yield return new("V-WBNEG", "Wheelbase typed negative (-2.88 m)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "vehicle.wheelbase:-2.88"), Impossible: true);
        yield return new("V-WBCM", "Wheelbase typed in cm (288 in the metre box)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "vehicle.wheelbase:288"), Impossible: true);
        yield return new("V-WBSHORT", "Wheelbase measured short (1.5 m, really 2.88 m)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "vehicle.wheelbase:1.5"));
        yield return new("V-AH0", "Antenna height left at 0, 6° slope", Puma, Boom, Um, 6, W(Puma, Boom, Um, "vehicle.antennaHeight:0"));
        yield return new("V-AHCM", "Antenna height typed in cm (310), 6° slope", Puma, Boom, Um, 6, W(Puma, Boom, Um, "vehicle.antennaHeight:310"), Impossible: true);
        yield return new("V-PIVSIGN", "Antenna pivot with the wrong sign (-1.0 m, really 1.0 m ahead)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "vehicle.antennaPivot:-1"));
        yield return new("V-OFFSIGN", "Antenna offset with the wrong sign (-0.25 m, really 0.25 m right)", Machines.Big, Machines.Rear6, Machines.BigSys, 0, W(Machines.Big, Machines.Rear6, Machines.BigSys, "vehicle.antennaOffset:-0.25"));
        yield return new("V-OFF0", "Antenna offset forgotten (0, really 0.25 m right)", Machines.Big, Machines.Rear6, Machines.BigSys, 0, W(Machines.Big, Machines.Rear6, Machines.BigSys, "vehicle.antennaOffset:0"));
        yield return new("V-OFFCM", "Antenna offset typed in cm (25 in the metre box, really 0.25 m)", Machines.Big, Machines.Rear6, Machines.BigSys, 0, W(Machines.Big, Machines.Rear6, Machines.BigSys, "vehicle.antennaOffset:25"), Impossible: true);
        yield return new("V-HITCH0", "Tractor hitch left at 0 with a trailed sprayer (really 1.10 m)", Puma, Machines.Sprayer10, Um, 0, W(Puma, Machines.Sprayer10, Um, "vehicle.hitchLength:0"));
        yield return new("V-HITCHCM", "Tractor hitch typed in cm (110) with a trailed sprayer", Puma, Machines.Sprayer10, Um, 0, W(Puma, Machines.Sprayer10, Um, "vehicle.hitchLength:110"), Impossible: true);
        yield return new("V-HITCHNEG", "Tractor hitch typed negative (-1.10 m) with a trailed sprayer", Puma, Machines.Sprayer10, Um, 0, W(Puma, Machines.Sprayer10, Um, "vehicle.hitchLength:-1.1"), Impossible: true);

        // --- GPS and roll ---------------------------------------------------------------
        yield return new("G-DUALOFF", "UM982 dual, but Dual antenna left off, 6° slope", Puma, Boom, Um, 6, W(Puma, Boom, Um, "gps.isDualGps:0"));
        yield return new("G-DUALON", "Single F9P + TM171, but Dual antenna switched on, 6° slope", Puma, Boom, new("f9p", "tm171", "keya"), 6, W(Puma, Boom, new("f9p", "tm171", "keya"), "gps.isDualGps:1"));
        yield return new("G-HDG90", "Dual heading offset 90° (antennas really front to back)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "gps.dualHeadingOffset:90"));
        yield return new("G-HDG180", "Dual heading offset 180° (antennas swapped in the setup)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "gps.dualHeadingOffset:180"));
        yield return new("G-HDG180SIR", "Dual heading offset 180°, Steer in reverse on", Puma, Boom, Um, 0, W(Puma, Boom, Um, "gps.dualHeadingOffset:180", "autosteer.steerInReverse:1"));
        yield return new("G-HDG5", "Dual antennas 5° off square, offset left at 0", Puma, Boom, Um with { HeadingErrorDeg = 5 }, 0, Drive.Careful(Puma, Boom, Um));
        yield return new("R-INV", "Roll sensor reads the wrong way round, Invert roll off (X20P + TM171), 6° slope", Puma, Boom, X20 with { RollInverted = true }, 6, Drive.Careful(Puma, Boom, X20));
        yield return new("R-INVSET", "Invert roll switched on for a sensor that reads right (X20P + TM171), 6° slope", Puma, Boom, X20, 6, W(Puma, Boom, X20, "roll.isRollInvert:1"));
        yield return new("R-ZEROSLOPE", "Roll zero set while standing on the 6° slope (6°)", Puma, Boom, X20, 6, W(Puma, Boom, X20, "roll.rollZero:6"));
        yield return new("R-BIAS", "IMU 2° off level, roll zero never set, flat field", Puma, Boom, X20 with { RollBiasDeg = 2 }, 0, Drive.Careful(Puma, Boom, X20));
        yield return new("R-FILTER5", "Roll filter typed 5 (the box is 0 to 1), 6° slope", Puma, Boom, X20, 6, W(Puma, Boom, X20, "roll.rollFilter:5"), Impossible: true);

        // --- The implement --------------------------------------------------------------
        yield return new("T-SECM", "Section widths typed in metres (1 = 1 cm, really 100 cm)", Puma, Boom, Um, 0, SectionWidths(Puma, Boom, Um, 1), Impossible: true);
        yield return new("T-SEC0", "One section left at 0 cm", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.sectionWidth:7,0"), Impossible: true);
        yield return new("T-SECNEG", "One section typed negative (-100 cm)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.sectionWidth:7,-100"), Impossible: true);
        yield return new("T-SECBIG", "Section widths typed in mm (1000 = 10 m each)", Puma, Boom, Um, 0, SectionWidths(Puma, Boom, Um, 1000));
        yield return new("T-NUM0", "Number of sections typed 0", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.numSections:0"), Impossible: true);
        yield return new("T-NUMFRAC", "Number of sections typed 8.5", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.numSections:8.5"), Impossible: true);
        yield return new("T-OFFCM", "Side-shift typed in cm (250 in the metre box)", Puma, Machines.Rear3Shift, Um, 0, W(Puma, Machines.Rear3Shift, Um, "tool.offset:250"), Impossible: true);
        yield return new("T-OFFSIGN", "Side-shift with the wrong sign (-2.5 m, really 2.5 m right)", Puma, Machines.Rear3Shift, Um, 0, W(Puma, Machines.Rear3Shift, Um, "tool.offset:-2.5"));
        yield return new("T-OV8", "Overlap typed as wide as the boom (8 m)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.overlap:8"), Impossible: true);
        yield return new("T-OV10", "Overlap typed wider than the boom (10 m)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.overlap:10"), Impossible: true);
        yield return new("T-OVCM", "Overlap typed in cm (20 in the metre box)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.overlap:20"), Impossible: true);
        yield return new("T-RIG0", "Boom distance behind the axle left at 0 (really 1.5 m)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.hitchLength:0"));
        yield return new("T-RIGCM", "Boom distance typed in cm (150)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.hitchLength:150"), Impossible: true);
        yield return new("T-TRAILNEG", "Trailed sprayer's drawbar typed negative (-4 m)", Puma, Machines.Sprayer10, Um, 0, W(Puma, Machines.Sprayer10, Um, "tool.trailingHitchLength:-4"), Impossible: true);
        yield return new("T-TRAIL0", "Trailed sprayer's drawbar left at 0", Puma, Machines.Sprayer10, Um, 0, W(Puma, Machines.Sprayer10, Um, "tool.trailingHitchLength:0"), Impossible: true);
        yield return new("T-TYPEXYZ", "Implement type that does not exist ('xyz')", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.type:xyz"), Impossible: true);
        yield return new("T-TYPERIGID", "Trailed sprayer set as mounted (rear, 4 m behind)", Puma, Machines.Sprayer10, Um, 0,
            W(Puma, Machines.Sprayer10, Um, "tool.type:rear", "tool.hitchLength:4"));
        yield return new("T-TBTASTRAIL", "TBT seed drill set as a plain trailed one", Machines.Big, Machines.Tbt12, Machines.BigSys, 0,
            W(Machines.Big, Machines.Tbt12, Machines.BigSys, "tool.type:trailing"));
        yield return new("T-SLOWCUT", "Slow speed cut-off typed 100 km/h (work speed 9)", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.slowSpeedCutoff:100"), Impossible: true);
        yield return new("T-MINCOV", "Minimum coverage typed 150 %", Puma, Boom, Um, 0, W(Puma, Boom, Um, "tool.minCoverage:150"), Impossible: true);

        // --- Changed with AutoSteer on, in the middle of the second pass ------------------
        yield return new("L-NUMSEC", "Sections cut from 8 to 6 in the middle of a pass, AutoSteer on", Puma, Boom, Um, 0, Drive.Careful(Puma, Boom, Um),
            MidPass: (r, k) => k == 1 ? r.Rig.SendAsync("config.set", "tool.numSections:6") : Task.CompletedTask);
        yield return new("L-ANTOFF", "Antenna offset changed (0 to 0.5 m) in the middle of a pass, AutoSteer on", Puma, Boom, Um, 0, Drive.Careful(Puma, Boom, Um),
            MidPass: (r, k) => k == 1 ? r.Rig.SendAsync("config.set", "vehicle.antennaOffset:0.5") : Task.CompletedTask);
        yield return new("L-TOOLOFF", "Implement offset changed (0 to 2.5 m) in the middle of a pass, AutoSteer on", Puma, Boom, Um, 0, Drive.Careful(Puma, Boom, Um),
            MidPass: (r, k) => k == 1 ? r.Rig.SendAsync("config.set", "tool.offset:2.5") : Task.CompletedTask);
        yield return new("L-TYPE", "Implement type changed (rear to trailing) in the middle of a pass, AutoSteer on", Puma, Boom, Um, 0, Drive.Careful(Puma, Boom, Um),
            MidPass: (r, k) => k == 1 ? r.Rig.SendAsync("config.set", "tool.type:trailing") : Task.CompletedTask);
        yield return new("L-PROFILE", "Another vehicle + implement profile loaded in the middle of a pass, AutoSteer on", Puma, Boom, Um, 0, Drive.Careful(Puma, Boom, Um),
            MidPass: (r, k) => k == 1 ? r.Rig.SendAsync("profile.load", "Vineyard\tCultivator 3 m") : Task.CompletedTask,
            Prepare: async r =>
            {
                // The other pair, as saved some other day; then back to the Puma.
                string veh = r.Rig.Store.ActiveVehicleProfileName, tool = r.Rig.Store.ActiveToolProfileName;
                await r.Rig.SendAsync("profile.new", "vehicle\tVineyard");
                await r.Rig.SendAsync("profile.new", "tool\tCultivator 3 m");
                await r.Rig.SendAsync("profile.load", "Vineyard\tCultivator 3 m");
                await r.ApplyAsync(Drive.Careful(Machines.Vineyard, Machines.Rear3, Machines.VineyardSys));
                await r.Rig.SendAsync("profile.load", veh + "\t" + tool);
            });
    }

    private static List<string> SectionWidths(VehicleSpec v, ToolSpec t, SystemSpec s, double cm) =>
        W(v, t, s, Enumerable.Range(0, t.Sections).Select(i => "tool.sectionWidth:" + i.ToString(CultureInfo.InvariantCulture) + "," + cm.ToString(CultureInfo.InvariantCulture)).ToArray());

    public static async Task RunAsync(Journal journal, string? only)
    {
        foreach (var d in Drives())
        {
            if (only != null && !(d.Code + " " + d.Mistake).Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var row = await Drive.RunAsync(journal, Name, d);
            Judge(journal, row, d);
        }
        if (only == null || "settings".Contains(only, StringComparison.OrdinalIgnoreCase))
            await SettingsAsync(journal);
        if (only == null || "profiles".Contains(only, StringComparison.OrdinalIgnoreCase))
            await ProfilesAsync(journal);
    }

    private static void Judge(Journal journal, DriveRow row, DriveSpec d)
    {
        var passes = row.Passes.Where(p => p.Samples > 0).ToList();
        // A pass AutoSteer refused to engage for (said why) was driven by hand.
        foreach (var p in passes.Where(p => p.Engaged))
        {
            // AutoSteer swinging the machine across its line is unsafe whatever the setting.
            double swing = p.MaxX - p.MinX;
            if (swing > 1.0 && p.Disengaged == null)
                Drive.Add(row, journal, new Finding(d.Code + "-SWING", Severity.Safety,
                    Drive.F($"{d.Mistake}: with AutoSteer on the implement swung {swing:0.0} m across the pass (pass {p.Index + 1}, steer up to {p.MaxSteerCmd:0}°)")));
            // A setting that only moves where AgOpenWeb thinks the line is (a forgotten antenna
            // offset) still steers: it shows the implement on its line. Not steering while it
            // shows the implement off the line is what the settings must never do.
            else if (p.Disengaged == null && p.MaxSteerWhole < 0.5 && p.AppXte > 0.2)
                Drive.Add(row, journal, new Finding(d.Code + "-NO-STEER", Severity.Safety,
                    Drive.F($"{d.Mistake}: AutoSteer is on and AgOpenWeb shows the implement {p.AppXte:0.00} m off its line, yet it never steers (steer angle 0 from engaging to the end of pass {p.Index + 1}); on a curve it would drive straight on")));
            else if (p.SaturatedShare > 0.3 && p.Disengaged == null)
                Drive.Add(row, journal, new Finding(d.Code + "-STEER-STOP", Severity.Safety,
                    Drive.F($"{d.Mistake}: AutoSteer held the wheels at the end stop {p.SaturatedShare * 100:0} % of a straight pass (pass {p.Index + 1})")));
        }
        if (!d.Impossible) return;

        // An impossible value: the app should refuse it, or at least say so.
        bool said = row.Said.Any(m => !m.Contains("did not engage", StringComparison.Ordinal));
        if (said) return;
        string effect = Effect(row);
        Drive.Add(row, journal, new Finding(d.Code, Severity.Robustness,
            d.Mistake + ": taken with no word" + (effect.Length > 0 ? "; in the field " + effect : "")));
    }

    /// <summary>The effect of a drive in a few words, for a finding.</summary>
    private static string Effect(DriveRow row)
    {
        var parts = new List<string>();
        if (row.Problems.Count > 0) parts.Add("the machine left its pass");
        if (Math.Abs(row.LineError) > 0.05) parts.Add(Drive.F($"every pass {Math.Abs(row.LineError):0.00} m off its line"));
        if (row.Joins.Gap > 0.10) parts.Add(Drive.F($"{row.Joins.Gap:0.00} m gaps between passes"));
        if (row.Joins.Overlap < -0.10) parts.Add(Drive.F($"{-row.Joins.Overlap:0.00} m overlaps between passes"));
        if (row.ToolSide > 0.2) parts.Add(Drive.F($"the implement drawn {row.ToolSide:0.0} m to the side of where it is"));
        if (row.ToolAlong > 0.6) parts.Add(Drive.F($"the implement drawn {row.ToolAlong:0.0} m ahead of or behind where it is"));
        if (!double.IsNaN(row.SectionsOn) && row.SectionsOn < 0.9) parts.Add(Drive.F($"sections on {row.SectionsOn * 100:0} % of the time"));
        if (parts.Count == 0) parts.Add("no visible effect on these straight passes");
        return string.Join(", ", parts);
    }

    // ── Settings typed wrong: what the app keeps, what the module is sent ────────────────

    private sealed record Bad(string Group, string What, string Setting, bool Impossible = true);

    private static IEnumerable<Bad> BadSettings()
    {
        yield return new("Vehicle", "Wheelbase 0", "vehicle.wheelbase:0");
        yield return new("Vehicle", "Wheelbase negative", "vehicle.wheelbase:-2.88");
        yield return new("Vehicle", "Wheelbase in cm", "vehicle.wheelbase:288");
        yield return new("Vehicle", "Wheelbase not a number", "vehicle.wheelbase:NaN");
        yield return new("Vehicle", "Track width 0", "vehicle.trackWidth:0");
        yield return new("Vehicle", "Track width negative", "vehicle.trackWidth:-1.9");
        yield return new("Vehicle", "Track width in cm", "vehicle.trackWidth:190");
        yield return new("Vehicle", "Antenna height negative", "vehicle.antennaHeight:-3.1");
        yield return new("Vehicle", "Antenna height in cm", "vehicle.antennaHeight:310");
        yield return new("Vehicle", "Antenna height infinite", "vehicle.antennaHeight:Infinity");
        yield return new("Vehicle", "Antenna pivot in cm", "vehicle.antennaPivot:100");
        yield return new("Vehicle", "Antenna offset in cm", "vehicle.antennaOffset:25");
        yield return new("Vehicle", "Antenna offset not a number", "vehicle.antennaOffset:NaN");
        yield return new("Vehicle", "Tractor hitch negative", "vehicle.hitchLength:-1.1");
        yield return new("Vehicle", "Tractor hitch in cm", "vehicle.hitchLength:110");
        yield return new("Vehicle", "Vehicle type that does not exist", "vehicle.type:7");
        yield return new("Vehicle", "Vehicle type negative", "vehicle.type:-1");
        yield return new("GPS", "Dual heading offset 400°", "gps.dualHeadingOffset:400");
        yield return new("GPS", "Dual heading offset not a number", "gps.dualHeadingOffset:NaN");
        yield return new("GPS", "Minimum GPS step 0", "gps.minGpsStep:0");
        yield return new("GPS", "Fix-to-fix distance negative", "gps.fixToFixDistance:-1");
        yield return new("Roll", "Roll filter 5 (box is 0 to 1)", "roll.rollFilter:5");
        yield return new("Roll", "Roll filter negative", "roll.rollFilter:-1");
        yield return new("Roll", "Roll zero 45°", "roll.rollZero:45");
        yield return new("Implement", "Type that does not exist", "tool.type:xyz");
        yield return new("Implement", "Type empty", "tool.type:");
        yield return new("Implement", "Sections 0", "tool.numSections:0");
        yield return new("Implement", "Sections negative", "tool.numSections:-3");
        yield return new("Implement", "Sections 65", "tool.numSections:65");
        yield return new("Implement", "Sections 8.5", "tool.numSections:8.5");
        yield return new("Implement", "Section width 1 cm (metres typed)", "tool.sectionWidth:0,1");
        yield return new("Implement", "Section width 0", "tool.sectionWidth:0,0");
        yield return new("Implement", "Section width negative", "tool.sectionWidth:0,-100");
        yield return new("Implement", "Section width 1 km", "tool.sectionWidth:0,100000");
        yield return new("Implement", "Section width not a number", "tool.sectionWidth:0,NaN");
        yield return new("Implement", "Section index past the end", "tool.sectionWidth:99,100");
        yield return new("Implement", "Default section width negative", "tool.defaultSectionWidth:-10");
        yield return new("Implement", "Offset in cm", "tool.offset:250");
        yield return new("Implement", "Offset not a number", "tool.offset:NaN");
        yield return new("Implement", "Overlap = width", "tool.overlap:8");
        yield return new("Implement", "Overlap > width", "tool.overlap:10");
        yield return new("Implement", "Gap wider than the implement", "tool.overlap:-10");
        yield return new("Implement", "Mounted distance negative", "tool.hitchLength:-1.5");
        yield return new("Implement", "Mounted distance in cm", "tool.hitchLength:150");
        yield return new("Implement", "Drawbar negative", "tool.trailingHitchLength:-4");
        yield return new("Implement", "Drawbar 0", "tool.trailingHitchLength:0");
        yield return new("Implement", "TBT tank hitch negative", "tool.tankTrailingHitchLength:-3");
        yield return new("Implement", "Axle to working line in cm", "tool.trailingToolToPivotLength:100");
        yield return new("Implement", "Look-ahead on negative", "tool.lookAheadOn:-5");
        yield return new("Implement", "Look-ahead off longer than on", "tool.lookAheadOff:5");
        yield return new("Implement", "Turn-off delay 100 s", "tool.turnOffDelay:100");
        yield return new("Implement", "Slow speed cut-off 100 km/h", "tool.slowSpeedCutoff:100");
        yield return new("Implement", "Slow speed cut-off negative", "tool.slowSpeedCutoff:-1");
        yield return new("Implement", "Minimum coverage 150 %", "tool.minCoverage:150");
        yield return new("Implement", "Minimum coverage negative", "tool.minCoverage:-10");
        yield return new("Implement", "Coverage margin negative", "tool.coverageMargin:-50");
        yield return new("Implement", "Zones 0", "tool.zones:0");
        yield return new("Implement", "Zones 9", "tool.zones:9");
    }

    /// <summary>Derived values a wrong setting can move: width, pass spacing, turning
    /// radius, implement type, and what the modules are sent (PGN 247 and 235).</summary>
    private static Dictionary<string, string> Derived(ConfigurationStore s)
    {
        var i = CultureInfo.InvariantCulture;
        var d = new Dictionary<string, string>
        {
            ["width"] = s.ActualToolWidth.ToString("0.###", i) + " m",
            ["pass spacing"] = (s.ActualToolWidth - s.Tool.Overlap).ToString("0.###", i) + " m",
            ["sections"] = s.NumSections.ToString(i),
            ["turning radius"] = s.Vehicle.MinTurningRadius.ToString("0.##", i) + " m",
            ["type"] = Readback.Get(s, "tool.type"),
        };
        var g = PgnBuilder.BuildVehicleGeometryPgn(s.Vehicle);
        int U(int at) => g[at] | g[at + 1] << 8;
        int S(int at) => (short)U(at);
        d["module wheelbase"] = U(7) + " cm";
        d["module track"] = U(9) + " cm";
        d["module antenna height"] = U(11) + " cm";
        d["module antenna pivot"] = S(13) + " cm";
        d["module antenna offset"] = S(15) + " cm";
        var sec = PgnBuilder.BuildSectionDimensionsPgn(s.Tool, s.NumSections);
        d["module sections"] = sec[37] + " × " + string.Join("/", Enumerable.Range(0, Math.Min(3, (int)sec[37])).Select(k => (sec[5 + k * 2] | sec[6 + k * 2] << 8).ToString(i))) + " cm";
        return d;
    }

    private static async Task SettingsAsync(Journal journal)
    {
        string dir = Path.Combine(Drive.DataRootBase, "settings-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            await using var rig = await VehicleToolRig.StartAsync(dir);
            var careful = Drive.Careful(Puma, Boom, Um);
            async Task Restore()
            {
                await rig.SendAsync("config.set", "tool.type:rear");
                foreach (var kv in careful) await rig.SendAsync("config.set", kv);
                foreach (var kv in new[] { "tool.lookAheadOn:1", "tool.lookAheadOff:0.5", "tool.turnOffDelay:0", "tool.slowSpeedCutoff:0.5", "tool.minCoverage:100", "tool.coverageMargin:0", "tool.zones:2", "gps.minGpsStep:0.05", "gps.fixToFixDistance:1", "roll.rollFilter:0" })
                    await rig.SendAsync("config.set", kv);
            }
            await Restore();
            rig.TakeEvents();
            foreach (var b in BadSettings())
            {
                var before = await rig.ReadAsync(() => Derived(rig.Store));
                string key = Readback.KeyOf(b.Setting);
                await rig.SendAsync("config.set", b.Setting);
                await rig.WaitForVmAsync();
                string kept = await rig.ReadAsync(() => Readback.Get(rig.Store, key));
                var after = await rig.ReadAsync(() => Derived(rig.Store));
                var row = new SettingRow { Group = b.Group, What = b.What, Sent = b.Setting, Stored = kept };
                row.Effect = string.Join("; ", after.Where(kv => before[kv.Key] != kv.Value).Select(kv => kv.Key + " " + before[kv.Key] + " → " + kv.Value));
                row.Said.AddRange(rig.TakeEvents().Where(Drive.Spoken).Select(e => e.ToString()));
                journal.Settings.Add(row);

                string where = "settings: " + b.What;
                if (row.Said.Any(m => m.StartsWith("Exception", StringComparison.Ordinal) || m.StartsWith("Error", StringComparison.Ordinal)))
                    Add(journal, row, new Finding("SET-THROWS-" + key, Severity.Bug, b.Setting + " threw on the host: " + row.Said[0]), where);
                bool accepted = Readback.Same(Readback.ValueOf(b.Setting), kept)
                    || (key == "tool.type" && kept == "none");
                if (b.Impossible && accepted && row.Said.Count == 0)
                    Add(journal, row, new Finding("ACCEPT-" + key.Split(':')[0], Severity.Robustness,
                        $"{key.Split(':')[0]} takes impossible values with no word (e.g. {Readback.ValueOf(b.Setting)}){(row.Effect.Length > 0 ? ": " + row.Effect : "")}"), where);
                await Restore();
                rig.TakeEvents();
            }

            // A harvester takes only front tools (#111): asking for another type is silently ignored.
            await rig.SendAsync("config.set", "vehicle.type:1");
            await rig.SendAsync("config.set", "tool.type:trailing");
            string t1 = await rig.ReadAsync(() => Readback.Get(rig.Store, "tool.type"));
            var hrow = new SettingRow { Group = "Implement", What = "Trailed implement behind a combine", Sent = "vehicle.type:1, tool.type:trailing", Stored = t1, Effect = t1 == "front" ? "kept as front (#111)" : "" };
            hrow.Said.AddRange(rig.TakeEvents().Where(Drive.Spoken).Select(e => e.ToString()));
            journal.Settings.Add(hrow);
            if (t1 == "front" && hrow.Said.Count == 0)
                Add(journal, hrow, new Finding("HARVESTER-TYPE-SILENT", Severity.Improvement,
                    "on a combine the implement type stays front whatever the operator picks (#111), and nothing says why"), "settings: trailed implement behind a combine");
            await rig.SendAsync("config.set", "vehicle.type:0");
            await Restore();

            // A negative drawbar typed live is used as typed; a profile load turns it positive.
            await rig.SendAsync("config.set", "tool.type:trailing");
            await rig.SendAsync("config.set", "tool.trailingHitchLength:-4");
            await rig.SendAsync("profile.save");
            string live = await rig.ReadAsync(() => Readback.Get(rig.Store, "tool.trailingHitchLength"));
            await rig.SendAsync("profile.load", rig.Store.ActiveVehicleProfileName + "\t" + rig.Store.ActiveToolProfileName);
            string loaded = await rig.ReadAsync(() => Readback.Get(rig.Store, "tool.trailingHitchLength"));
            var trow = new SettingRow { Group = "Implement", What = "Negative drawbar, saved and loaded again", Sent = "tool.trailingHitchLength:-4, profile.save, profile.load", Stored = live + " live, " + loaded + " after a load" };
            journal.Settings.Add(trow);
            if (live != loaded)
                Add(journal, trow, new Finding("DRAWBAR-SIGN-ON-LOAD", Severity.Inconsistency,
                    $"a negative drawbar is used as typed ({live} m) until the profile is loaded again, then it is {loaded} m: the implement moves after a restart"), "settings: negative drawbar");
        }
        catch (Exception ex)
        {
            journal.Add(new Finding("HARNESS", Severity.Bug, "the settings checks threw " + ex.GetType().Name + ": " + ex.Message), "settings");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void Add(Journal journal, SettingRow row, Finding f, string where)
    {
        row.Findings.Add(f);
        journal.Add(f, where);
    }

    // ── Profiles: odd names and the picker's buttons used the wrong way ──────────────────

    private static async Task ProfilesAsync(Journal journal)
    {
        string dir = Path.Combine(Drive.DataRootBase, "names-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            await using (var rig = await VehicleToolRig.StartAsync(dir))
            {
                string vehDir = await rig.ReadAsync(() => rig.Config.ProfilesDirectory);
                async Task<List<string>> Vehicles() => await rig.ReadAsync(() => rig.Config.GetAvailableProfiles().ToList());
                async Task<SettingRow> Step(string what, string sent, Func<Task> act)
                {
                    rig.TakeEvents();
                    var row = new SettingRow { Group = "Profiles", What = what, Sent = sent };
                    try { await act(); }
                    catch (Exception ex) { row.Said.Add("threw " + ex.GetType().Name + ": " + ex.Message); }
                    await rig.WaitForVmAsync();
                    row.Said.AddRange(rig.TakeEvents().Where(Drive.Spoken).Select(e => e.ToString()));
                    journal.Settings.Add(row);
                    return row;
                }
                string Active() => rig.Store.ActiveVehicleProfileName + " / " + rig.Store.ActiveToolProfileName;

                // The Puma, done right, as the starting point.
                await rig.SendAsync("profile.new", "vehicle\tPuma");
                await rig.SendAsync("profile.new", "tool\tSprayer");
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                foreach (var kv in Drive.Careful(Puma, Boom, Um)) await rig.SendAsync("config.set", kv);
                await rig.SendAsync("profile.save");

                // A slash in the name.
                var r = await Step("Name with a slash", "profile.new vehicle\\tPuma 180/CVX", () => rig.SendAsync("profile.new", "vehicle\tPuma 180/CVX"));
                var list = await Vehicles();
                r.Stored = list.Contains("Puma 180/CVX") ? "listed" : "not listed";
                bool sub = Directory.Exists(Path.Combine(vehDir, "Puma 180"));
                r.Effect = sub ? "a folder 'Puma 180' made in Vehicles, the profile inside it" : "";
                // Refused with the reason is right: a slash is a folder on every head.
                if (!list.Contains("Puma 180/CVX") && (sub || r.Said.Count == 0))
                    Add(journal, r, new Finding("PROFILE-NAME-SLASH", Severity.Bug,
                        "a vehicle named with a slash ('Puma 180/CVX') is not saved as a profile" + (sub ? " (it goes into a sub-folder)" : "") + (r.Said.Count == 0 ? ", and nothing says so" : ": " + r.Said[0])), "profiles: name with a slash");

                // A name that climbs out of the Vehicles folder.
                r = await Step("Name that climbs out of the folder", "profile.new vehicle\\t../escaped", () => rig.SendAsync("profile.new", "vehicle\t../escaped"));
                string outside = Path.GetFullPath(Path.Combine(vehDir, "..", "escaped.json"));
                r.Stored = File.Exists(outside) ? "written to " + Path.GetFileName(Path.GetDirectoryName(outside)) + "/escaped.json" : "not written outside";
                if (File.Exists(outside))
                    Add(journal, r, new Finding("PROFILE-NAME-PATH", Severity.Robustness,
                        "a profile name with '../' is written outside the Vehicles folder (any browser on the LAN can send one)"), "profiles: name with ../");

                // Names that differ only in capitals.
                r = await Step("Same name in capitals", "profile.new vehicle\\tPUMA", () => rig.SendAsync("profile.new", "vehicle\tPUMA"));
                list = await Vehicles();
                int files = Directory.GetFiles(vehDir, "*.json").Count(f => string.Equals(Path.GetFileNameWithoutExtension(f), "puma", StringComparison.OrdinalIgnoreCase));
                r.Stored = files + " file(s), " + list.Count(n => string.Equals(n, "puma", StringComparison.OrdinalIgnoreCase)) + " listed";
                if (files > 1 && list.Count(n => string.Equals(n, "puma", StringComparison.OrdinalIgnoreCase)) == 1)
                    Add(journal, r, new Finding("PROFILE-NAME-CASE", Severity.Inconsistency,
                        "'Puma' and 'PUMA' are two files on Linux and Android but the picker lists one of them; on Windows and macOS they are the same file"), "profiles: names in capitals");

                // New over an existing name.
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                await rig.SendAsync("config.set", "vehicle.wheelbase:2.10");   // the vineyard tractor's, typed over the live Puma
                r = await Step("New with the name of an existing profile", "profile.new vehicle\\tPuma", () => rig.SendAsync("profile.new", "vehicle\tPuma"));
                await rig.SendAsync("profile.load", "Default\tSprayer");
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                string wb = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.wheelbase"));
                r.Stored = "Puma wheelbase now " + wb + " m";
                if (wb == "2.1" && r.Said.Count == 0)
                    Add(journal, r, new Finding("PROFILE-NEW-OVERWRITES", Severity.Improvement,
                        "New with the name of an existing profile overwrites it with no question"), "profiles: New over an existing name");
                foreach (var kv in Drive.Careful(Puma, Boom, Um)) await rig.SendAsync("config.set", kv);
                await rig.SendAsync("profile.save");

                // New, then type the new tractor's measurements without loading it.
                r = await Step("New 'Vineyard', then type its measurements and Save without loading it", "profile.new vehicle\\tVineyard; config.set …; profile.save", async () =>
                {
                    await rig.SendAsync("profile.new", "vehicle\tVineyard");
                    foreach (var kv in Machines.Vineyard.Settings(Machines.VineyardSys)) await rig.SendAsync("config.set", kv);
                    await rig.SendAsync("profile.save");
                });
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                string pumaWb = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.wheelbase"));
                await rig.SendAsync("profile.load", "Vineyard\tSprayer");
                string vinWb = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.wheelbase"));
                r.Stored = "Puma wheelbase " + pumaWb + " m, Vineyard wheelbase " + vinWb + " m";
                if (pumaWb == "2.1")
                    Add(journal, r, new Finding("PROFILE-NEW-NOT-ACTIVE", Severity.Bug,
                        "New saves a copy under the new name but keeps the old profile loaded: the next measurements and Save Profile overwrite the old one (the Puma got the vineyard tractor's 2.10 m wheelbase, the 'Vineyard' profile kept the Puma's). The picker's status says 'Creating Vineyard…' and nothing more."), "profiles: New, then edit without Load");
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                foreach (var kv in Drive.Careful(Puma, Boom, Um)) await rig.SendAsync("config.set", kv);
                await rig.SendAsync("profile.save");

                // Delete the profile in use.
                r = await Step("Delete the profile in use", "profile.delete vehicle\\tPuma", () => rig.SendAsync("profile.delete", "vehicle\tPuma"));
                list = await Vehicles();
                r.Stored = list.Contains("Puma") ? "kept" : "deleted";
                if (list.Contains("Puma") && r.Said.Count == 0)
                    Add(journal, r, new Finding("PROFILE-REFUSED-SILENT", Severity.Improvement,
                        "the picker refuses some actions with no word: deleting the profile in use, a rename onto an existing name, loading a profile that is not there"), "profiles: delete the profile in use");

                // Rename onto an existing name.
                r = await Step("Rename onto an existing name", "profile.rename vehicle\\tVineyard\\tPuma", () => rig.SendAsync("profile.rename", "vehicle\tVineyard\tPuma"));
                list = await Vehicles();
                r.Stored = list.Contains("Vineyard") ? "not renamed" : "renamed";
                if (list.Contains("Vineyard") && r.Said.Count == 0)
                    Add(journal, r, new Finding("PROFILE-REFUSED-SILENT", Severity.Improvement, "the picker refuses some actions with no word"), "profiles: rename onto an existing name");

                // Load a profile that is not there.
                string before = Active();
                r = await Step("Load a profile that is not there", "profile.load Nope\\tNope", () => rig.SendAsync("profile.load", "Nope\tNope"));
                r.Stored = "active " + Active() + " (was " + before + ")";
                if (r.Said.Count == 0)
                    Add(journal, r, new Finding("PROFILE-REFUSED-SILENT", Severity.Improvement, "the picker refuses some actions with no word"), "profiles: load a profile that is not there");

                // Reset the Default vehicle while working with the Puma.
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                r = await Step("Reset the Default vehicle while the Puma is loaded", "profile.reset vehicle", () => rig.SendAsync("profile.reset", "vehicle"));
                string act = Active();
                string liveWb = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.wheelbase"));
                string liveAh = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.antennaHeight"));
                r.Stored = "active " + act + ", wheelbase " + liveWb + " m, antenna height " + liveAh + " m";
                if (act.StartsWith("Default", StringComparison.Ordinal) && liveWb == "2.88")
                    Add(journal, r, new Finding("PROFILE-RESET-HALF", Severity.Bug,
                        "Reset of the Default vehicle while another profile is loaded switches to Default and saves a mix: the type, antenna height and pivot go back to defaults, the Puma's wheelbase, track, antenna offset and hitch stay, so 'Default' is not a default any more"), "profiles: reset Default");

                // A combine with a trailed implement, by loading two profiles.
                await rig.SendAsync("profile.new", "tool\tTrailed disc");
                await rig.SendAsync("profile.load", "Puma\tTrailed disc");
                foreach (var kv in Machines.Trailing6.Settings()) await rig.SendAsync("config.set", kv);
                await rig.SendAsync("profile.save");
                await rig.SendAsync("profile.new", "vehicle\tCombine");
                await rig.SendAsync("profile.load", "Combine\tSprayer");
                foreach (var kv in Machines.Combine.Settings(Machines.CombineSys)) await rig.SendAsync("config.set", kv);
                await rig.SendAsync("profile.save");
                r = await Step("Load the combine with the trailed disc", "profile.load Combine\\tTrailed disc", () => rig.SendAsync("profile.load", "Combine\tTrailed disc"));
                string ty = await rig.ReadAsync(() => Readback.Get(rig.Store, "tool.type"));
                r.Stored = "implement type " + ty;
                if (ty != "front")
                    Add(journal, r, new Finding("HARVESTER-TRAILING-BY-LOAD", Severity.Inconsistency,
                        "a combine takes only a front implement when the type is picked (#111), but loading a combine with a trailed implement's profile gives a combine pulling a trailed implement"), "profiles: combine + trailed disc");

                // Typed, not saved, restart.
                await rig.SendAsync("profile.load", "Puma\tSprayer");
                await rig.SendAsync("config.set", "vehicle.antennaHeight:2.5");
                bool unsaved = await rig.ReadAsync(() => rig.Store.HasUnsavedChanges);
                var ur = new SettingRow { Group = "Profiles", What = "Antenna height typed, AgOpenWeb closed without Save Profile", Sent = "config.set vehicle.antennaHeight:2.5, restart", Stored = "unsaved flag " + unsaved };
                journal.Settings.Add(ur);
            }
            await using (var rig = await VehicleToolRig.StartAsync(dir))
            {
                var ur = journal.Settings[^1];
                string ah = await rig.ReadAsync(() => Readback.Get(rig.Store, "vehicle.antennaHeight"));
                ur.Stored += ", after the restart " + ah + " m";
                if (ah == "3.1")
                    ur.Effect = "the change is lost (expected: Save Profile keeps it)";
            }
        }
        catch (Exception ex)
        {
            journal.Add(new Finding("HARNESS", Severity.Bug, "the profile checks threw " + ex.GetType().Name + ": " + ex.Message), "profiles");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
