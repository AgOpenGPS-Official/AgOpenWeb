// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careless operator during a U-turn: the careful operator's Olival job (the Case Puma, the
/// 8 m sprayer, AB line at 159°, headland 16 m) with one thing done while AgOpenWeb turns, or just
/// before or after: a U-turn setting changed, skip rows, the direction arrow, a nudge, reversing,
/// AutoSteer off and on; and U-turns made by hand with the manual buttons. The bug reports found
/// their bugs here (#585, #577, #410, #588, #93, #272, #163); the field jobs' careless operator
/// acts before the turn, not in it. What the operator's own doing must cost is listed in
/// <see cref="JobSpec.Expected"/> and noted, not counted.
/// </summary>
public static class TurnInterventions
{
    private static JobSpec Olival(string code, string title, string story) =>
        new(code, title, story, "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, CarefulOperator.Puma);

    public static IEnumerable<JobSpec> Jobs()
    {
        // ---- a U-turn setting changed while the turn is being driven
        foreach (var (code, title, key, value, what) in new[]
        {
            ("T1", "U-turn radius changed during a turn", "uturn.radius", "12", "the U-turn radius from 8 to 12 m"),
            ("T2", "Distance from boundary changed during a turn", "uturn.distanceFromBoundary", "6", "the distance from boundary from 2 to 6 m"),
            ("T3", "U-turn style changed during a turn", "uturn.style", "2", "the U-turn style from Sagitta to Keyhole"),
        })
        {
            var w = new TurnWatch();
            yield return Olival(code, title,
                $"Ten seconds into the second U-turn the operator changes {what} in the U-turn settings. The turn being driven must go on as it was built and land on its pass; the change is for the next turns.") with
            {
                OnFix = Seq(InTurnAfter(2, 10, async r =>
                {
                    await w.SnapAsync(r, $"changed {what}");
                    await r.SendAsync("config.set", key + ":" + value);
                    r.Did($"changed {what} (turn 2 in progress)");
                }), w.TrackAsync),
                ExtraJudge = w.Judge,
            };
        }

        // ---- skip rows during the turn, and with the turn planned
        {
            var w = new TurnWatch();
            yield return Olival("T4", "Skip rows set during a turn (#585)",
                "Ten seconds into the second U-turn the operator sets skip rows (alternative, 1 row) and leaves it on. The turn being driven must land on the pass it was built for; the skipping starts from the next turn, from that pass.") with
            {
                OnFix = Seq(InTurnAfter(2, 10, async r =>
                {
                    await w.SnapAsync(r, "set skip rows on");
                    await r.SendAsync("youturn.skipToggle");
                    r.Did("skip rows ON, alternative 1 row (turn 2 in progress)");
                }), w.TrackAsync),
                ExtraJudge = (sc, r, flag) =>
                {
                    w.Judge(sc, r, flag);
                    var next = r.Turns.FirstOrDefault(t => t.Index == 3);
                    var prev = r.Turns.FirstOrDefault(t => t.Index == 2);
                    if (next is { TruthPassBefore: not int.MinValue, TruthPassAfter: not int.MinValue })
                    {
                        int d = next.TruthPassAfter - next.TruthPassBefore, step = Math.Abs(d);
                        // The way the turns had been stepping across the field: the worked side is behind.
                        int way = prev is { TruthPassBefore: not int.MinValue, TruthPassAfter: not int.MinValue } ? Math.Sign(prev.TruthPassAfter - prev.TruthPassBefore) : 0;
                        if (way != 0 && Math.Sign(d) == -way)
                            flag(Severity.Bug, "SKIP-TURNS-BACK", JobRun.F($"skip rows (alternative, 1 row) was set during turn 2; turn 3 went from pass {next.TruthPassBefore} back to pass {next.TruthPassAfter}, over passes already worked (the turns had been stepping from pass {prev!.TruthPassBefore} to {prev.TruthPassAfter}). Switched on after the first turn, alternative skip takes its first direction from the pass number's sign: HandleAlternateCreation asks WouldNextLineBeInsideBoundary without the way the turns step (keepPositive null), the rule #523 removed from the plain turns."));
                        else if (step != 2)
                            flag(Severity.Bug, "SKIP-NOT-APPLIED", JobRun.F($"skip rows (alternative, 1 row) was set during turn 2; turn 3 went from pass {next.TruthPassBefore} to pass {next.TruthPassAfter} ({step} over), not 2 over."));
                        else sc.Ok(JobRun.F($"the next turn skipped a row, on the side not yet worked: pass {next.TruthPassBefore} -> {next.TruthPassAfter}"));
                    }
                },
            };
        }
        {
            var w = new TurnWatch();
            yield return Olival("T5", "Skip rows set with the turn already planned (#410, #577)",
                "On the pass before the second U-turn, with the turn on the screen and the headland 40 m ahead, the operator sets skip rows (alternative, 1 row). The planned turn must be re-planned to land two passes over, to the same side.") with
            {
                OnFix = Seq(BeforeTurnPlanned(2, 40, async r =>
                {
                    await w.SnapAsync(r, "set skip rows on", expectStep: 2);
                    await r.SendAsync("youturn.skipToggle");
                    r.Did("skip rows ON, alternative 1 row (turn 2 planned, not started)");
                }), w.TrackAsync),
                ExtraJudge = w.Judge,
            };
        }
        {
            var w = new TurnWatch();
            yield return Olival("T6", "Direction arrow tapped twice before a turn",
                "On the pass before the second U-turn, with the turn on the screen and the headland 40 m ahead, the operator taps the U-turn arrow twice (the other way, then back). The turn must go the way it was planned, onto the same pass.") with
            {
                OnFix = Seq(BeforeTurnPlanned(2, 40, async r =>
                {
                    await w.SnapAsync(r, "tapped the direction arrow twice");
                    await r.SendAsync("youturn.direction");
                    // The tap is taken on the next cycle: one fix between the two taps, as a hand would.
                    w.SecondTapPending = true;
                    r.Did("tapped the U-turn arrow (turn 2 planned)");
                }), async r =>
                {
                    if (w.SecondTapPending && w.SnapFix >= 0 && r.Fix - w.SnapFix == 10)
                    {
                        w.SecondTapPending = false;
                        await r.SendAsync("youturn.direction");
                        r.Did("tapped the U-turn arrow again, 1 s later");
                    }
                }, w.TrackAsync),
                ExtraJudge = w.Judge,
            };
        }

        // ---- nudge, reverse, AutoSteer off during the turn
        {
            var w = new TurnWatch();
            yield return Olival("T7", "Track nudged 50 cm during a turn (#93)",
                "Ten seconds into the second U-turn the operator nudges the line 50 cm to the right (a finger on the button by accident). The turn must still land on its pass, moved 50 cm; the 50 cm strip that follows is the operator's doing.") with
            {
                Extra = new[] { "autosteer.nudgeDistance:50" },
                Expected = new[] { "MISSED-STRIPS", "DOUBLE-SPRAYED" },
                OnFix = Seq(InTurnAfter(2, 10, async r =>
                {
                    await w.SnapAsync(r, "nudged the line 50 cm right");
                    await r.SendAsync("track.nudgeRight");
                    r.Did("nudged the track 50 cm right (turn 2 in progress)");
                }), w.TrackAsync),
                ExtraJudge = w.Judge,
            };
        }
        {
            var w = new TurnWatch();
            int at = -1;
            yield return Olival("T8", "Reversing 7 m in the middle of a turn (#588)",
                "Twelve seconds into the second U-turn the operator stops and backs up for 8 s at 3 km/h (a hedge too close), then drives on. AgOpenWeb must take up the same turn again and land on its pass.") with
            {
                OnFix = Seq(InTurnAfter(2, 12, async r =>
                {
                    await w.SnapAsync(r, "started reversing");
                    r.Truth.SpeedKmh = -3; at = r.Fix;
                    r.Did("reversing at 3 km/h (turn 2 in progress)");
                }), r =>
                {
                    if (at >= 0 && r.Fix - at == 80) { r.Truth.SpeedKmh = r.Spec.SpeedKmh; r.Did("forward again"); }
                    return Task.CompletedTask;
                }, w.TrackAsync),
                ExtraJudge = w.Judge,
            };
        }
        {
            var back = new ReverseAfterTurn();
            yield return Olival("T9", "Reversing out of a turn just finished (#588)",
                "One second after the second U-turn ends the operator backs up 10 m at 3 km/h (to start the pass right at the headland line), then drives on. AgOpenWeb must keep the pass the turn landed on and not start another turn until the far end.") with
            {
                OnFix = back.OnFixAsync,
                ExtraJudge = back.Judge,
            };
        }
        {
            var w = new TurnWatch();
            int offAt = -1;
            yield return Olival("T10", "AutoSteer off half way round a turn, on again",
                "Ten seconds into the second U-turn the operator switches AutoSteer off, holds the wheel where it was for 3 s, and switches it on again (a touch on the wheel, a bump of the button). AgOpenWeb drops the turn when AutoSteer goes off; re-engaging is meant to build it again from scratch (GpsPipelineService). Does it, with the tractor already in the headland, and does the turn land on its pass?") with
            {
                OnFix = Seq(InTurnAfter(2, 10, async r =>
                {
                    await w.SnapAsync(r, "switched AutoSteer off");
                    double hold = r.Truth.Steer;
                    r.ExpectEngaged = false;
                    r.HandSteer = () => hold;
                    await r.SendAsync("autosteer.toggle");
                    offAt = r.Fix;
                    r.Did(JobRun.F($"AutoSteer OFF (turn 2 in progress), holding the wheel at {hold:0} deg"));
                }), async r =>
                {
                    if (offAt >= 0 && r.Fix - offAt == 30)
                    {
                        r.HandSteer = null;
                        await r.SendAsync("autosteer.toggle");
                        bool on = await r.Rig.ReadAsync(() => r.Rig.Vm.IsAutoSteerEngaged);
                        r.ExpectEngaged = true;
                        r.Did("AutoSteer ON again" + (on ? "" : " (did NOT engage)"));
                    }
                }, w.TrackAsync),
                Expected = new[] { "AUTOSTEER-DROPPED" },
                ExtraJudge = w.Judge,
            };
        }

        // ---- U-turns by hand
        {
            var m = new ManualTurns();
            yield return Olival("T11", "U-turns made by hand with the manual buttons (#272, #163)",
                "Automatic U-turns off. At each end the operator taps the manual U-turn button towards the unworked side as the tractor crosses the headland line, AutoSteer on; the job ends when no pass is left. Each turn must start, land on the next pass and hand back to the line as the automatic turns do.") with
            {
                UTurns = false,
                OnFix = m.OnFixAsync,
                ExtraJudge = m.Judge,
            };
        }
        {
            var w = new ManualMidPass();
            yield return Olival("T12", "A manual U-turn in the middle of a pass, then automatic ones",
                "In the middle of the third pass the operator taps the manual U-turn button towards the unworked side (to leave a wet patch), then lets the automatic U-turns go on. The manual turn must land on the next pass, and the automatic turns carry on from there; the unworked half passes are the operator's doing.") with
            {
                Expected = new[] { "FIELD-LEFT-UNDONE" },
                OnFix = w.OnFixAsync,
                ExtraJudge = w.Judge,
            };
        }
    }

    // ------------------------------------------------------------------ moments

    /// <summary>Runs every hook each fix, in order.</summary>
    public static Func<JobRun, Task> Seq(params Func<JobRun, Task>[] hooks) => async r =>
    {
        foreach (var h in hooks) await h(r);
    };

    /// <summary>Once, <paramref name="seconds"/> into U-turn number <paramref name="turn"/>.</summary>
    public static Func<JobRun, Task> InTurnAfter(int turn, double seconds, Func<JobRun, Task> act)
    {
        int start = -1; bool fired = false;
        return async r =>
        {
            if (fired || !r.InTurn || r.TurnsStarted != turn) return;
            if (start < 0) start = r.Fix;
            if (r.Fix - start >= (int)Math.Round(seconds * 10)) { fired = true; await act(r); }
        };
    }

    /// <summary>Once, on the pass before U-turn number <paramref name="turn"/>, when AgOpenWeb has
    /// the turn planned (on the screen) and the headland is less than <paramref name="metres"/> ahead.</summary>
    public static Func<JobRun, Task> BeforeTurnPlanned(int turn, double metres, Func<JobRun, Task> act)
    {
        bool fired = false;
        return async r =>
        {
            if (fired || r.InTurn || r.TurnsStarted != turn - 1) return;
            var yt = r.Rig.LastCycle?.YouTurn;
            if (yt?.TurnPath is { Count: > 1 } && yt.DistanceToHeadland < metres && yt.DistanceToHeadland > 5) { fired = true; await act(r); }
        };
    }

    /// <summary>The pass a point on the AB line's passes is on.</summary>
    public static int PassAt(JobRun r, (double E, double N) p) => (int)Math.Round(r.U(p) / r.Spacing - 0.5);

    public static double DistToPath((double E, double N) q, IReadOnlyList<(double E, double N)> path)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < path.Count; i++) best = Math.Min(best, Ring.DistToSegment(q, path[i], path[i + 1]));
        return best;
    }

    // ------------------------------------------------------------------ what the operator watches

    /// <summary>
    /// One U-turn the operator does something to: the turn as AgOpenWeb had it at that moment (its
    /// path, side and the pass it lands on), then while it is driven how far AgOpenWeb's path moves
    /// near the tractor and the tractor from the path, and the pass the implement is on 15 s after.
    /// </summary>
    public sealed class TurnWatch
    {
        public int Turn = -1, SnapFix = -1;
        public string What = "";
        public List<(double E, double N)>? Path;
        public int StartPass = int.MinValue, PlannedPass = int.MinValue, ExpectedPass = int.MinValue;
        public bool PlannedLeft, Executing;
        public double PathMovedM, TractorOffM;
        public (double E, double N) PathMovedAt;
        public bool SecondTapPending;
        private int _expectStep;

        /// <summary>Note the turn as it is now; <paramref name="expectStep"/> &gt; 0: the operator's
        /// change should move the landing that many passes from the start pass, to the same side.</summary>
        public async Task SnapAsync(JobRun r, string what, int expectStep = 0)
        {
            var yt = await r.Rig.ReadAsync(() => r.Rig.LastCycle?.YouTurn);
            SnapFix = r.Fix; What = what; _expectStep = expectStep;
            Executing = yt?.IsExecuting == true;
            Turn = Executing ? r.TurnsStarted : r.TurnsStarted + 1;
            Path = yt?.TurnPath?.Select(p => (p.Easting, p.Northing)).ToList();
            PlannedLeft = yt?.IsTurnLeft ?? false;
            var tc = r.Truth.ToolCentre;
            StartPass = Executing ? r.Turns.LastOrDefault()?.TruthPassBefore ?? int.MinValue : PassAt(r, (tc.E, tc.N));
            if (Path is { Count: > 1 }) PlannedPass = PassAt(r, Path[^1]);
            ExpectedPass = expectStep > 0 && StartPass != int.MinValue && PlannedPass != int.MinValue
                ? StartPass + Math.Sign(PlannedPass - StartPass) * expectStep : PlannedPass;
            Journal.Trace(JobRun.F($"[fj] {r.Seconds,7:0.0} s   turn {Turn} {(Executing ? "executing" : "planned")} when the operator {what}: {(PlannedLeft ? "left" : "right")}, from pass {StartPass} to pass {PlannedPass}, path {Path?.Count ?? 0} points"));
        }

        public Task TrackAsync(JobRun r)
        {
            if (Path is not { Count: > 1 } || r.TurnsStarted != Turn || !r.InTurn || (r.Fix - SnapFix) % 5 != 0) return Task.CompletedTask;
            var p = (r.Truth.E, r.Truth.N);
            TractorOffM = Math.Max(TractorOffM, DistToPath(p, Path));
            var now = r.Rig.LastCycle?.YouTurn?.TurnPath;
            if (now is { Count: > 1 })
                foreach (var q in now)
                {
                    double dx = q.Easting - p.E, dy = q.Northing - p.N;
                    if (dx * dx + dy * dy > 25 * 25) continue;
                    double d = DistToPath((q.Easting, q.Northing), Path);
                    if (d > PathMovedM) { PathMovedM = d; PathMovedAt = (q.Easting, q.Northing); }
                }
            return Task.CompletedTask;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            if (Turn < 0) { sc.Note("the moment for the operator's action never came (no such turn)"); return; }
            var t = r.Turns.FirstOrDefault(x => x.Index == Turn);
            sc.Row.Metrics.Add(($"Turn {Turn} ({What}): side and pass planned / landed; AgOpenWeb's path moved near the tractor; the tractor off the path it had", t == null
                ? "the turn never started"
                : JobRun.F($"{(PlannedLeft ? "left" : "right")}, pass {StartPass} -> {PlannedPass} / {(t.Left ? "left" : "right")}, pass {t.TruthPassBefore} -> {(t.TruthPassAfter == int.MinValue ? "?" : t.TruthPassAfter.ToString())}; {PathMovedM:0.0} m; {TractorOffM:0.0} m")));
            if (t == null)
            {
                flag(Severity.Bug, "TURN-LOST", JobRun.F($"the operator {What} with turn {Turn} {(Executing ? "under way" : "planned")}; that turn never started."));
                return;
            }
            if (Executing && PathMovedM > 1.0)
                flag(Severity.Bug, "TURN-CHANGED-MID-TURN", JobRun.F($"the operator {What} during turn {Turn}: AgOpenWeb changed the turn under the tractor, its path moved {PathMovedM:0.0} m within 25 m of it (at ({PathMovedAt.E:0},{PathMovedAt.N:0})); the tractor went up to {TractorOffM:0.0} m from the path it was on."));
            if (!Executing && t.Left != PlannedLeft)
                flag(Severity.Bug, "TURN-SIDE-CHANGED", JobRun.F($"turn {Turn} was planned to the {(PlannedLeft ? "left" : "right")}; after the operator {What} it went to the {(t.Left ? "left" : "right")}."));
            if (t.TruthPassAfter != int.MinValue && ExpectedPass != int.MinValue && t.TruthPassAfter != ExpectedPass)
                flag(Severity.Bug, "TURN-LANDED-ELSEWHERE", JobRun.F($"turn {Turn} {(Executing ? "was under way" : "was planned")} from pass {StartPass} to pass {PlannedPass} when the operator {What}; {(_expectStep > 0 ? JobRun.F($"it should then land on pass {ExpectedPass}; ") : "")}the implement was on pass {t.TruthPassAfter} 15 s after it ended."));
            else if (t.TruthPassAfter != int.MinValue)
                sc.Ok(JobRun.F($"turn {Turn} landed on pass {t.TruthPassAfter}, the pass {(_expectStep > 0 ? "expected" : "it was built for")}"));
        }
    }

    /// <summary>T9: backing up right after a turn, then on along the pass.</summary>
    private sealed class ReverseAfterTurn
    {
        private int _endFix = -1, _backAt = -1, _passBefore = int.MinValue, _passAfter = int.MinValue, _aowBefore, _aowAfter;
        private double _forwardAt = double.NaN;
        private int _turnsAtForward = -1;

        public Task OnFixAsync(JobRun r)
        {
            if (_endFix < 0 && r.TurnsStarted == 2 && !r.InTurn) _endFix = r.Fix;
            if (_endFix >= 0 && _backAt < 0 && r.Fix - _endFix == 10)
            {
                _backAt = r.Fix;
                var tc = r.Truth.ToolCentre; _passBefore = PassAt(r, (tc.E, tc.N)); _aowBefore = r.Pass;
                r.Truth.SpeedKmh = -3;
                r.Did("reversing 10 m at 3 km/h, 1 s after turn 2 ended");
            }
            if (_backAt >= 0 && r.Fix - _backAt == 120)
            {
                r.Truth.SpeedKmh = r.Spec.SpeedKmh; _forwardAt = r.Seconds; _turnsAtForward = r.TurnsStarted;
                r.Did("forward again");
            }
            if (!double.IsNaN(_forwardAt) && _passAfter == int.MinValue && r.Seconds - _forwardAt >= 20)
            {
                var tc = r.Truth.ToolCentre; _passAfter = PassAt(r, (tc.E, tc.N)); _aowAfter = r.Pass;
            }
            return Task.CompletedTask;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            if (_backAt < 0) { sc.Note("turn 2 never ended; nothing was done"); return; }
            var t3 = r.Turns.FirstOrDefault(t => t.Index == 3);
            double driven = t3 != null && !double.IsNaN(_forwardAt) ? (t3.StartSeconds - _forwardAt) * r.Spec.SpeedKmh / 3.6 : double.NaN;
            sc.Row.Metrics.Add(("After reversing out of turn 2: the implement's pass before / 20 s after driving on (AgOpenWeb's pass number); metres driven before the next turn started", JobRun.F($"{_passBefore} / {_passAfter} ({_aowBefore} / {_aowAfter}); {driven:0} m")));
            if (_turnsAtForward >= 0 && _turnsAtForward > 2)
                flag(Severity.Bug, "TURN-REPEATED", JobRun.F($"while the operator backed up out of turn 2, AgOpenWeb started turn {_turnsAtForward} at the same end."));
            else if (t3 != null && driven < 60)
                flag(Severity.Bug, "TURN-REPEATED", JobRun.F($"after the operator backed up out of turn 2 and drove on, AgOpenWeb started turn 3 after only {driven:0} m, not at the far end of the pass."));
            if (_passAfter != int.MinValue && _passBefore != int.MinValue && _passAfter != _passBefore)
                flag(Severity.Bug, "PASS-LOST-REVERSING", JobRun.F($"the implement was on pass {_passBefore} when the operator backed up out of turn 2, and on pass {_passAfter} 20 s after driving on (AgOpenWeb's pass {_aowBefore} -> {_aowAfter})."));
            else if (_passAfter != int.MinValue)
                sc.Ok(JobRun.F($"the pass held after reversing out of the turn (pass {_passAfter}); the next turn came {driven:0} m later"));
        }
    }

    /// <summary>T11: every U-turn by hand. The operator taps the manual button towards the
    /// unworked side as the tractor crosses the headland line, and stops when no pass is left.</summary>
    private sealed class ManualTurns
    {
        private bool _wasInWork; private double _inWorkM;
        public readonly List<(int Index, double At, bool Left, bool Started, string Said)> Taps = new();

        public async Task OnFixAsync(JobRun r)
        {
            if (r.InTurn || r.Work == null) { _wasInWork = false; _inWorkM = 0; return; }
            bool inWork = Ring.Inside((r.Truth.E, r.Truth.N), r.Work);
            if (inWork) _inWorkM += Math.Abs(r.Truth.SpeedKmh) / 3.6 * JobRun.FixSeconds;
            if (_wasInWork && !inWork && _inWorkM > 20)
            {
                var tc = r.Truth.ToolCentre;
                int k = PassAt(r, (tc.E, tc.N));
                if (r.InsideChordM(k + 1) < 20)
                {
                    r.EndedBy = JobRun.F($"the operator finished at the headland: no pass left after pass {k}");
                    r.FieldDone = true; r.Stopped = true; r.Did(r.EndedBy);
                    return;
                }
                bool sameWay = Math.Cos(r.Truth.Heading - r.Frame.TangentHeading((r.Truth.E, r.Truth.N))) > 0;
                bool left = !sameWay;   // passes count up to the right of A->B
                int before = r.TurnsStarted;
                await r.SendAsync(left ? "youturn.manualLeft" : "youturn.manualRight");
                // Whether it started is judged from the turns recorded (the turn shows on the next
                // GPS cycle, not at once): Judge counts a tap with no turn within 1.5 s as refused.
                bool started = await r.Rig.ReadAsync(() => r.Rig.State.YouTurn.IsExecuting);
                string said = string.Join(" | ", r.Collect().Select(s => { r.Said.Add(JobRun.F($"{r.Seconds:0.0} s: {s}")); return s; }).TakeLast(2));
                Taps.Add((before + 1, r.Seconds, left, started, said));
                r.Did(JobRun.F($"manual U-turn {(left ? "left" : "right")} at the headland line, from pass {k}"));
                _inWorkM = 0;
            }
            _wasInWork = inWork;
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            bool Refused((int Index, double At, bool Left, bool Started, string Said) t) => !t.Started && !r.Turns.Any(x => Math.Abs(x.StartSeconds - t.At) < 1.5);
            int refused = Taps.Count(Refused);
            // A turn hands back to the line with 4 m of its arc left (EarlyCompletionLookahead), the
            // automatic ones and the manual ones alike: 4/R radians off the pass at the hand-back.
            double handBackDeg = 4.0 / Math.Max(r.Spec.UTurnRadius, 1) * 180 / Math.PI, limitDeg = handBackDeg + 10;
            var early = r.Turns.Where(t => !double.IsNaN(t.EndHeadingOffDeg) && t.EndHeadingOffDeg > limitDeg).ToList();
            var wrong = r.Turns.Where(t => t.TruthPassBefore != int.MinValue && t.TruthPassAfter != int.MinValue && t.TruthPassAfter - t.TruthPassBefore != 1).ToList();
            var ended = r.Turns.Where(t => !double.IsNaN(t.EndHeadingOffDeg)).ToList();
            sc.Row.Metrics.Add((JobRun.F($"Manual U-turns tapped / started / handed back to the line as designed (4 m of arc left: {handBackDeg:0}° off the pass, flagged beyond {limitDeg:0}°) / landed on the next pass; at the hand-back the most off the pass direction / the implement furthest from its pass"), JobRun.F($"{Taps.Count} / {r.Turns.Count} / {r.Turns.Count - early.Count} / {r.Turns.Count(t => t.TruthPassAfter != int.MinValue && t.TruthPassAfter - t.TruthPassBefore == 1)}; {(ended.Count > 0 ? ended.Max(t => t.EndHeadingOffDeg) : 0):0}° / {(ended.Count > 0 ? ended.Max(t => t.EndOffPassM) : 0):0.0} m")));
            if (Taps.Count == 0) { sc.Note("the operator never reached a headland line with AutoSteer on"); return; }
            var outside = r.Turns.Where(t => t.MaxPivotOutsideM > 0.5).ToList();
            if (outside.Count > 0)
                sc.Note(JobRun.F($"manual U-turns are built with no look at the boundary (a Dubins path from 4 m ahead of the tractor, as AgOpenGPS's BuildManualYouTurn): tapped at the headland line of a {r.Spec.HeadlandM:0} m headland, radius {r.Spec.UTurnRadius:0} m for {r.Spacing:0} m passes, {outside.Count} of {r.Turns.Count} took the tractor past the boundary, up to {outside.Max(t => t.MaxPivotOutsideM):0.0} m. A manual turn that would leave the field could be refused as an automatic one is (LeavesField, #698)"));
            if (refused > 0)
                flag(Severity.Bug, "MANUAL-TURN-REFUSED", JobRun.F($"{refused} of {Taps.Count} taps on the manual U-turn button started no turn: {Taps.First(Refused).Said}"));
            if (early.Count > 0)
            {
                var w = early.OrderByDescending(t => t.EndHeadingOffDeg).First();
                flag(Severity.Bug, "MANUAL-TURN-ENDS-EARLY", JobRun.F($"{early.Count} of {r.Turns.Count} manual U-turns handed back to the line earlier than designed: turn {w.Index} ended {w.EndHeadingOffDeg:0}° off the pass direction (4 m of arc left is {handBackDeg:0}°) with the implement {w.EndOffPassM:0.0} m from the nearest pass, and AutoSteer took the line from there."));
            }
            if (wrong.Count > 0)
            {
                var w = wrong.First();
                flag(Severity.Bug, "MANUAL-TURN-WRONG-PASS", JobRun.F($"{wrong.Count} of {r.Turns.Count} manual U-turns did not land on the next pass: turn {w.Index} from pass {w.TruthPassBefore} to pass {w.TruthPassAfter}."));
            }
            if (refused == 0 && early.Count == 0 && wrong.Count == 0)
                sc.Ok(JobRun.F($"all {r.Turns.Count} manual U-turns started, landed on the next pass and handed back to the line as designed (at most {(ended.Count > 0 ? ended.Max(t => t.EndHeadingOffDeg) : 0):0}° off the pass, {handBackDeg:0}° for 4 m of arc left)"));
        }
    }

    /// <summary>T12: one manual U-turn in the middle of the third pass; the automatic ones go on.</summary>
    private sealed class ManualMidPass
    {
        private int _endFix = -1; private bool _tapped; private int _from = int.MinValue; private bool _left;
        private string _said = "";

        public async Task OnFixAsync(JobRun r)
        {
            if (_endFix < 0 && r.TurnsStarted == 2 && !r.InTurn) _endFix = r.Fix;
            if (_tapped || _endFix < 0 || r.Fix - _endFix != 300) return;
            _tapped = true;
            var tc = r.Truth.ToolCentre; _from = PassAt(r, (tc.E, tc.N));
            bool sameWay = Math.Cos(r.Truth.Heading - r.Frame.TangentHeading((r.Truth.E, r.Truth.N))) > 0;
            _left = !sameWay;
            // The rest of this pass, ahead of where the operator turns back, is left on purpose.
            double s0 = r.Spacing, k = _from, vTap = r.Vv((tc.E, tc.N)), way = sameWay ? 1 : -1;
            r.LeftOnPurpose = p => { double u = r.U(p); return u > k * s0 - 0.3 && u < (k + 1) * s0 + 0.3 && way * (r.Vv(p) - vTap) > -3; };
            await r.SendAsync(_left ? "youturn.manualLeft" : "youturn.manualRight");
            _said = string.Join(" | ", r.Collect().Select(s => { r.Said.Add(JobRun.F($"{r.Seconds:0.0} s: {s}")); return s; }).TakeLast(2));
            r.Did(JobRun.F($"manual U-turn {(_left ? "left" : "right")} in the middle of pass {_from} (30 s after turn 2)"));
        }

        public void Judge(Scenario sc, JobRun r, Action<Severity, string, string> flag)
        {
            if (!_tapped) { sc.Note("turn 2 never ended; nothing was done"); return; }
            var manual = r.Turns.FirstOrDefault(t => t.Index == 3);
            var after = r.Turns.Where(t => t.Index > 3).ToList();
            sc.Row.Metrics.Add(("The manual turn mid-pass: from / to (pass); the automatic turns after it, passes", manual == null ? "no turn" : JobRun.F($"{manual.TruthPassBefore} -> {manual.TruthPassAfter}; {string.Join(", ", after.Take(4).Select(t => JobRun.F($"{t.TruthPassBefore}->{t.TruthPassAfter}")))}")));
            if (manual == null || Math.Abs(manual.StartSeconds - (r.Turns.FirstOrDefault(t => t.Index == 2)?.StartSeconds ?? 0)) < 1)
            {
                flag(Severity.Bug, "MANUAL-TURN-REFUSED", "the manual U-turn tapped in the middle of a pass started no turn: " + _said);
                return;
            }
            if (manual.TruthPassAfter != int.MinValue && manual.TruthPassAfter != _from + 1)
                flag(Severity.Bug, "MANUAL-TURN-WRONG-PASS", JobRun.F($"the manual U-turn in the middle of pass {_from} landed on pass {manual.TruthPassAfter}, not {_from + 1}."));
            var next = after.FirstOrDefault();
            if (next is { TruthPassBefore: not int.MinValue, TruthPassAfter: not int.MinValue } && next.TruthPassAfter - next.TruthPassBefore != 1)
                flag(Severity.Bug, "PATTERN-LOST", JobRun.F($"after the manual U-turn the next automatic turn went from pass {next.TruthPassBefore} to pass {next.TruthPassAfter}, not on to the next pass."));
            else if (next != null)
                sc.Ok(JobRun.F($"after the manual U-turn the automatic turns carried on: pass {next.TruthPassBefore} -> {next.TruthPassAfter}"));
        }
    }
}
