// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careful operator with the rigs and tracks testers use and the field jobs did not: an
/// implement shifted to one side of the tractor (mounted and trailed), a curve recorded by
/// driving it, and a wide implement skipping a row at every turn. Each job is ordinary work, so
/// what it finds is what a tester with that rig would meet on the first day.
/// </summary>
public static class RigJobs
{
    public static readonly SystemSpec Puma = new("um982", "tm171", "keya");

    public static IEnumerable<JobSpec> Jobs()
    {
        yield return new JobSpec("J8", "Olival, mounted mower shifted 2.5 m to the right",
            "The Case Puma with a 3 m mower mounted at the back and shifted 2.5 m to the right of the tractor (one section), AB line at 159°, headland 16 m; the first pass along the edge with the mower on the fence side. At every U-turn the mower changes side relative to the field, so the tractor's own lines are 8 m and then -2 m apart (it lands behind its last line): the passes must still meet, the turns stay inside and the map follow the mower.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Rear3Shift, Puma)
        { PassCheck = true };
        yield return new JobSpec("J9", "Olival, trailed mower 2.5 m to the left",
            "The same with a 3 m trailed mower whose cutting width sits 2.5 m left of its drawbar (one section): the offset follows the trailed mower's own heading, which lags the tractor's in the turns.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Trailing3Shift, Puma)
        { PassCheck = true };
        yield return new JobSpec("J10", "Quinta Torta, a curve recorded by driving it",
            "The operator drives the field's curve (a gentle S from south to north) by hand with the sprayer off: Curve > Record as the boom crosses the boundary going in, Finish as it crosses it going out. Then the 8 m sprayer works the field from that curve, AutoSteer and U-turns at the 16 m headland.",
            "Quinta Torta", "Curve", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.DrivenCurve, SimulatedClock = true };
        yield return new JobSpec("J11", "Couto Ribeira, 21 m cultivator skipping a row at every turn",
            "The trailed 21 m cultivator (7 sections of 3 m), AB line at 119°, headland 42 m, U-turn radius 10 m, with skip rows on (alternative, 1 row): every turn goes 42 m across, more than twice the radius, so AgOpenWeb makes its wide turn along the turn line, and the skipped passes are worked on the way back.",
            "Couto Ribeira", "1 Principal AB 119 deg", Machines.Puma, Machines.Trailing21, Puma)
        { HeadlandM = 42, UTurnRadius = 10, PassCheck = true, BeforeStart = SkipOneRowAsync };
        var edge = new EdgePassWatch();
        yield return new JobSpec("J12", "Olival, the shifted mower on the edge pass",
            "J8's mower, started on the first pass along the west edge: the mower's 3 m run in the cultivated area along the headland line, the tractor 2.5 m further out, in the headland, for the whole pass. One pass only: where does AgOpenWeb turn at its end?",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Rear3Shift, Puma)
        { EdgePass = true, OnFix = edge.OnFixAsync, ExtraJudge = edge.Judge, Expected = new[] { "SPRAYED-IN-HEADLAND" } };
        yield return new JobSpec("J13", "Olival, Renato's Case Maxxum and front sprayer (2/1/2/1/2 m)",
            "A tester's own rig with the settings the suggested-settings page gives it: the Case IH Maxxum 135 (wheelbase 2.70 m, 42°, antenna 0.30 m left) with an 8 m front-mounted boom in 5 sections of 2, 1, 2, 1 and 2 m, 2 m ahead of the front axle; U-turn radius 4 m, Sagitta, distance from boundary 6.3 m (the boom swings out ahead of the tractor), extension 8 m, headland 16 m. Unequal sections, the boom in front.",
            "Olival", "AB 159 deg", Machines.Maxxum, Machines.FrontSprayer8x5, Puma)
        { UTurnRadius = 4, UTurnDistanceFromBoundary = 6.3, Extra = new[] { "uturn.extension:8" }, PassCheck = true };
        yield return new JobSpec("J14", "Baetas, Renato's semi-mounted no-till drill 3 m, Keyhole turns",
            "The Case Maxxum with the Gaspardo Diretta Corsa 300 as Renato measured it: semi-mounted, so trailed for AgOpenWeb (hitch 1.54 m behind the rear axle, pivot to wheels 4.15 m, the discs 1.6 m ahead of the wheels), 3 m in one section, a 5 cm gap left between passes; the page's settings for it: radius 4.3 m, Keyhole, distance from boundary 2.8 m, extension 12.5 m, headland 18 m (6 passes). AB line at 8°.",
            "Baetas", "1 Principal AB 8 deg", Machines.Maxxum, Machines.DirettaCorsa300, Puma)
        { HeadlandM = 18, UTurnRadius = 4.3, UTurnDistanceFromBoundary = 2.8, UTurnStyle = 2, Extra = new[] { "uturn.extension:12.5" }, PassCheck = true };
        yield return new JobSpec("J15", "Quinta Torta, a combine with a 6 m header",
            "A combine (pivot on the front axle, steered by the rear wheels; antenna 0.5 m behind the front axle) with a 6 m grain header in front in 6 sections, AB line at 0°, headland 18 m (three header widths), U-turn radius 8 m.",
            "Quinta Torta", "AB 0°", Machines.Combine, Machines.Header6, Machines.CombineSys)
        { HeadlandM = 18, PassCheck = true };
        yield return new JobSpec("J16", "Olival, a 6 m seed drill with its tank between tractor and drill (TBT)",
            "The Case Puma with a 6 m drill in 6 sections towed behind a seed tank on its own axle (tractor, tank, drill: two hinges), AB line at 159°, headland 18 m. The drill follows the tank, which follows the tractor, so in the turns it cuts in further than a plain trailed drill.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Tbt6, Puma)
        { HeadlandM = 18, PassCheck = true };
        yield return new JobSpec("J17", "Tapada da Casa, a trailed manure spreader with a 12 m bout",
            "The Case Puma with a one-axle manure spreader (drawbar to axle 5.5 m) whose spinners throw a 12 m bout centred 4 m behind the axle, one section, AB line at 162°, the outer boundary only, headland 24 m. The spread lands far behind the tractor, so the section must open and close late enough at the headland line.",
            "Tapada da Casa", "1 Principal AB 162 deg", Machines.Puma, Machines.ManureSpreader12, Puma)
        { HeadlandM = 24, PassCheck = true };
        yield return new JobSpec("J18", "Olival, a trailed disc harrow on wheels in the middle",
            "The Case Puma with a 5 m trailed disc harrow whose wheels sit in the middle of the frame, so the discs work around its axle 3.5 m behind the drawbar (pivot to tool 0), one section, AB line at 159°, headland 16 m.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.DiscHarrow5, Puma)
        { PassCheck = true };
        yield return new JobSpec("J20", "Olival, Renato's seeding roller on a bar on the lift arms, Keyhole turns",
            "The Case Maxxum with a 3.2 m seeding roller towed from a bar across the lift arms (the pivot taken as the drill's, 1.54 m behind the rear axle; the roller 2.0 m behind it is its own axle, assumed), which swings about 60° each way before the bar stops it (120° in all), one section, AB line at 159°, J14's page settings: radius 4.3 m, Keyhole, distance from boundary 2.8 m, extension 12.5 m, headland 18 m. Does a Keyhole turn ask the roller to swing past its stop?",
            "Olival", "AB 159 deg", Machines.Maxxum, Machines.Roller32, Puma)
        { HeadlandM = 18, UTurnRadius = 4.3, UTurnDistanceFromBoundary = 2.8, UTurnStyle = 2, Extra = new[] { "uturn.extension:12.5" }, PassCheck = true };
        var plough = new PloughWatch();
        yield return new JobSpec("J19", "Olival, a mounted reversible plough (6 furrows)",
            "The Case Puma with a 6-furrow reversible plough (2.7 m), 7 km/h, AB line at 159°, headland 16 m. The plough turns over at each end so it always throws the soil the same way: its working width sits 0.9 m to the same side of the field whichever way the tractor drives. AgOpenWeb's tool offset is to one side of the tractor and would change side with it, so the operator leaves it at 0. Do the passes still meet, and where does AgOpenWeb show the plough?",
            "Olival", "AB 159 deg", Machines.Puma, Machines.ReversiblePlough6, Puma)
        {
            SpeedKmh = 7, OnFix = plough.OnFixAsync, ExtraJudge = plough.Judge,
            // AgOpenWeb draws the plough 0.9 m from where it works: its map is off by that much at the field's sides.
            Expected = new[] { "MAP-SHOWS-UNSPRAYED", "SPRAYED-NOT-ON-MAP", "TOOL-POSITION-OFF" },
        };
    }

    /// <summary>J12: one pass with the tractor in the headland and the implement in the cultivated
    /// area. The operator stops the job when the first U-turn has ended, or takes over when the
    /// tractor is 3 m past the boundary with no U-turn.</summary>
    private sealed class EdgePassWatch
    {
        private readonly Dictionary<AgOpenWeb.Models.State.TractorZone, int> _zones = new();
        private bool _turned, _inside, _wentOut;
        private (double E, double N) _outAt;
        private string _said = "";

        public Task OnFixAsync(JobRun r)
        {
            var yt = r.Rig.LastCycle?.YouTurn;
            if (!_inside && Ring.Inside((r.Truth.E, r.Truth.N), r.Outer)) _inside = true;
            if (!_inside) return Task.CompletedTask;
            if (yt != null && !r.InTurn && r.TurnsStarted == 0) _zones[yt.CurrentZone] = _zones.GetValueOrDefault(yt.CurrentZone) + 1;
            if (r.TurnsStarted > 0) _turned = true;
            if (_turned && !r.InTurn)
            {
                r.EndedBy = "the operator stopped after the first U-turn (this job is one pass)";
                r.EndedEarly = true; r.Stopped = true; r.Did(r.EndedBy);
            }
            else if (!_turned && r.OutsideBoundaryM().Pivot > 3)
            {
                _outAt = (r.Truth.E, r.Truth.N); _wentOut = true;
                _said = string.Join(" | ", r.Said.TakeLast(3));
                r.EndedBy = "the operator took over: the tractor was 3 m past the boundary and no U-turn had started";
                r.EndedEarly = true; r.Stopped = true; r.Did(r.EndedBy);
            }
            return Task.CompletedTask;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            int all = Math.Max(1, _zones.Values.Sum());
            string zones = string.Join(", ", _zones.OrderByDescending(z => z.Value).Select(z => JobRun.F($"{z.Key} {100.0 * z.Value / all:0} %")));
            sc.Row.Metrics.Add(("AgOpenWeb's zone for the tractor on the edge pass (share of fixes)", zones));
            if (!_turned && !_wentOut && r.TookOverAt >= 0)
                flag(Severity.Bug, "EDGE-PASS-NO-TURN", JobRun.F($"on the first pass along the edge, with the mower in the cultivated area and the tractor 2.5 m outside it in the headland, AgOpenWeb planned no U-turn at the end of the pass and said so ({r.EndedBy}); a turn is due there, since the pass line (the mower's run) is in the cultivated area (#761 plans from it); AgOpenWeb's zone for the tractor: {zones}."));
            else if (!_turned && !_wentOut)
                r.Harness(JobRun.F($"the edge pass ended with no U-turn and the tractor never 3 m past the boundary ({(r.EndedBy.Length > 0 ? r.EndedBy : r.FieldDone ? "AgOpenWeb reported the end of the field" : "the job stopped")}): where AgOpenWeb turns at the pass's end was not seen. Last messages: {string.Join(" | ", r.Said.TakeLast(3))}"));
            else if (!_turned)
                flag(Severity.Safety, "EDGE-PASS-NO-TURN", JobRun.F($"on the first pass along the edge, with the mower in the cultivated area and the tractor 2.5 m outside it in the headland, AgOpenWeb planned no U-turn at the end of the pass and AutoSteer drove the tractor on past the boundary (3 m past at ({_outAt.E:0},{_outAt.N:0})), with no warning on the screen; AgOpenWeb's zone for the tractor: {zones}. Last messages: {(_said.Length > 0 ? _said : "none")}."));
            else
                sc.Ok("AgOpenWeb turned at the end of the edge pass");
        }
    }

    /// <summary>Skip rows on before the job: one tap of the skip button (normal -> alternative, 1 row).</summary>
    public static async Task SkipOneRowAsync(JobRun r)
    {
        await r.SendAsync("youturn.skipToggle");
        var (mode, rows) = await r.Rig.ReadAsync(() => (r.Rig.Vm.UTurnSkipMode, r.Rig.Vm.UTurnSkipRows));
        r.S.Step(JobRun.F($"skip rows: mode {mode} ({(mode == 1 ? "alternative" : mode == 2 ? "ignore worked tracks" : "normal")}), {rows} row(s)"));
        if (mode != 1 || rows != 1) r.Harness(JobRun.F($"skip rows did not go to alternative, 1 row (mode {mode}, {rows} rows)"));
    }

    /// <summary>J19: where AgOpenWeb shows the reversible plough against where it works, across the
    /// field (the plough keeps to one side of the field, AgOpenWeb's offset to one side of the tractor).</summary>
    private sealed class PloughWatch
    {
        private double _sum, _abs;
        private int _n;

        public Task OnFixAsync(JobRun r)
        {
            var c = r.Rig.LastCycle;
            if (c == null || r.InTurn || r.Module.SectionsOpen == 0 || (c.ToolEasting == 0 && c.ToolNorthing == 0)) return Task.CompletedTask;
            var t = r.Truth.ToolCentre;
            double d = r.U((c.ToolEasting, c.ToolNorthing)) - r.U((t.E, t.N));
            _sum += d; _abs += Math.Abs(d); _n++;
            return Task.CompletedTask;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            if (_n == 0) return;
            double mean = _sum / _n, abs = _abs / _n, left = r.UnsprayedShare();
            sc.Row.Metrics.Add(("AgOpenWeb's plough against the real one, across the field (mean / mean size)", JobRun.F($"{mean:+0.00;-0.00;+0.00} m / {abs:0.00} m")));
            if (abs > 0.3)
                flag(Severity.Improvement, "REVERSIBLE-PLOUGH-NO-SETTING", JobRun.F($"a reversible plough works {r.Spec.T.FieldOffset:0.0} m to the same side of the field whichever way it is driven; AgOpenWeb's tool offset is to one side of the tractor and changes side with it at every turn, so the offset stays at 0 and AgOpenWeb shows the plough {abs:0.00} m across from where it works on every pass (its map and section timing with it). The passes still meet ({left * 100:0.0} % of the cultivated area left). A 'reversible' setting (the offset kept to one side of the field) would put it right."));
            else sc.Ok(JobRun.F($"AgOpenWeb shows the plough where it works (within {abs:0.00} m across)"));
        }
    }
}
