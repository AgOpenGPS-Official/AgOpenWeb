// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The U-turn skip button's third mode, "ignore worked tracks" (the snake order): AgOpenWeb plans
/// the order of the passes, skipping the rows set, and at each end turns onto the next pass of the
/// order still to do; when that one has no room at this end or its turn does not build, onto the
/// nearest pass still to do that has (#807). A failed end is not tried again on every cycle. No
/// other job drives this mode, so its order, its re-planning and its fallback were held by unit
/// tests only. Measured besides the whole-field checks: whether a turn ever landed on a pass
/// already worked, and the order the passes were worked in.
/// </summary>
public static class SkipWorkedJobs
{
    public static IEnumerable<JobSpec> Jobs()
    {
        var w1 = new SnakeWatch(careless: false);
        yield return new JobSpec("W1", "Baetas, skip worked passes (snake order, 1 row)",
            "The 8 m sprayer on Baetas (6.3 ha, AB line at 8°), whose narrow end leaves some passes too short to land on, with the skip " +
            "button on its third mode, ignore worked tracks, 1 row: AgOpenWeb works every other pass on the way out and the rest on the way " +
            "back, choosing the next pass at every end.",
            "Baetas", "1 Principal AB 8 deg", Machines.Puma, Machines.Sprayer8, RigJobs.Puma)
        { BeforeStart = SkipWorkedAsync, OnFix = w1.OnFixAsync, ExtraJudge = w1.Judge };

        var w2 = new SnakeWatch(careless: true);
        yield return new JobSpec("W2", "Olival with a hard boundary, skip worked passes, a careless operator",
            "J7's field (the outer boundary hard: AgOpenWeb keeps the boom's swept path inside the fence, and at some ends no turn " +
            "fits) in the snake order, 1 row. The operator taps the U-turn arrow in the middle of the fourth pass (the turn goes the " +
            "other way and the order is planned again from where it lands), and after the seventh turn taps the row button (2 rows).",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, RigJobs.Puma)
        { HardBoundary = true, BeforeStart = SkipWorkedAsync, OnFix = w2.OnFixAsync, ExtraJudge = w2.Judge };
    }

    /// <summary>Two taps of the skip button: normal, alternative, ignore worked tracks (1 row).</summary>
    public static async Task SkipWorkedAsync(JobRun r)
    {
        await r.SendAsync("youturn.skipToggle");
        await r.SendAsync("youturn.skipToggle");
        var (mode, rows, worked) = await r.Rig.ReadAsync(() => (r.Rig.Vm.UTurnSkipMode, r.Rig.Vm.UTurnSkipRows, r.Rig.Vm.IsSkipWorkedMode));
        r.S.Step(JobRun.F($"skip rows: mode {mode} (ignore worked tracks), {rows} row(s)"));
        if (mode != 2 || rows != 1 || !worked) r.Harness(JobRun.F($"the skip button did not go to ignore worked tracks, 1 row (mode {mode}, {rows} rows, skip worked {worked})"));
    }

    private sealed class SnakeWatch
    {
        private readonly bool _careless;
        private bool _arrowTapped, _rowsTapped;
        private int _passStartFix = -1, _passStartTurns = -1;

        public SnakeWatch(bool careless) => _careless = careless;

        public async Task OnFixAsync(JobRun r)
        {
            if (!_careless || r.InTurn) { _passStartFix = -1; return; }
            // The fix a pass began on (out of the last turn), to find the middle of it.
            if (_passStartFix < 0 || _passStartTurns != r.TurnsStarted) { _passStartFix = r.Fix; _passStartTurns = r.TurnsStarted; }
            double onPass = (r.Fix - _passStartFix) * JobRun.FixSeconds;
            if (!_arrowTapped && r.TurnsStarted == 3 && onPass > 25)
            {
                _arrowTapped = true;
                r.Did("the operator tapped the U-turn arrow in the middle of the pass (the next turn the other way)");
                await r.SendAsync("youturn.direction");
            }
            if (!_rowsTapped && r.TurnsStarted == 7 && onPass > 10)
            {
                _rowsTapped = true;
                await r.SendAsync("youturn.skipCycle");
                var (mode, rows) = await r.Rig.ReadAsync(() => (r.Rig.Vm.UTurnSkipMode, r.Rig.Vm.UTurnSkipRows));
                r.Did(JobRun.F($"the operator tapped the row button: {rows} row(s) (mode {mode})"));
            }
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            // The passes the implement worked, in order: where the first turn began, then where
            // each turn put it (15 s after the turn ended).
            var turns = r.Turns.Where(t => t.TruthPassBefore != int.MinValue).ToList();
            if (turns.Count == 0) { r.Harness("no U-turn with the implement's pass known: the snake order was not seen"); return; }
            var order = new List<int> { turns[0].TruthPassBefore };
            var ontoWorked = new List<string>();
            foreach (var t in turns)
            {
                if (t.TruthPassAfter == int.MinValue) continue;
                if (order.Contains(t.TruthPassAfter))
                    ontoWorked.Add(JobRun.F($"turn {t.Index} at {t.StartSeconds:0} s from pass {t.TruthPassBefore} onto {t.TruthPassAfter}, worked before"));
                order.Add(t.TruthPassAfter);
            }
            sc.Row.Metrics.Add(("The passes in the order worked (the implement's pass after each U-turn)", string.Join(", ", order)));
            if (_careless)
                sc.Row.Metrics.Add(("The operator's taps: the U-turn arrow mid-pass / the row button", JobRun.F($"{(_arrowTapped ? "tapped" : "not reached")} / {(_rowsTapped ? "tapped" : "not reached")}")));
            if (ontoWorked.Count > 0)
                flag(Severity.Bug, "TURN-ONTO-WORKED-PASS",
                    JobRun.F($"with skip worked passes on, {ontoWorked.Count} U-turn(s) went onto a pass already worked: {string.Join("; ", ontoWorked.Take(3))}{(ontoWorked.Count > 3 ? "; …" : "")}"));
            else
                sc.Ok(JobRun.F($"no U-turn went onto a pass already worked ({order.Count} passes)"));
        }
    }
}
