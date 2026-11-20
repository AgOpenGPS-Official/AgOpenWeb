// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.ViewModels.LineTune;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests.LineTune;

/// <summary>
/// The tuning run on the line (<see cref="LineTuneRun"/>) on a simulated tractor (<see cref="LineTuneSim"/>):
/// what it finds on a quick, a late and a slow steering, with Pure Pursuit and with Stanley, and
/// that every way out puts the operator's values and the line back.
/// </summary>
[TestFixture]
public class LineTuneRunTests
{
    private static LineTuneRun NewRun(LineTuneSim sim) => new(sim,
        new GuidanceGainsOverride(sim.Hold, sim.StanleyDist, sim.StanleyHead), sim.Stanley,
        sim.Mult, sim.Acquire, sim.MinLookAhead);

    /// <summary>Drive until the run is no longer active (or the time is up); calls <paramref name="each"/> before each cycle.</summary>
    private static void Drive(LineTuneSim sim, LineTuneRun run, double maxS, Action<double>? each = null,
        Action<LineTuneSample>? after = null)
    {
        double end = sim.Time + maxS;
        while (sim.Time < end)
        {
            each?.Invoke(sim.Time);
            var s = sim.Cycle();
            run.OnSample(s);
            after?.Invoke(s);
            if (!run.Active) return;
        }
    }

    private static void Settle(LineTuneSim sim, double seconds)
    {
        for (double t = 0; t < seconds; t += LineTuneSim.CycleS) sim.Cycle();
    }

    [Test]
    public void The_simulated_tractor_comes_onto_the_line()
    {
        foreach (bool stanley in new[] { false, true })
        {
            var sim = new LineTuneSim(startOffsetM: 0.5) { Stanley = stanley };
            Settle(sim, 20);
            Assert.That(Math.Abs(sim.LastXte), Is.LessThan(0.03), $"stanley={stanley}: xte {sim.LastXte:F3}");
        }
    }

    [Test]
    public void Quick_steering_Pure_Pursuit_finds_a_value_and_leaves_the_line_and_settings_as_found()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 600);
        TestContext.Out.WriteLine(run.ResultText);
        TestContext.Out.WriteLine(run.Measurements);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Found, Is.Not.Null);
        Assert.That(sim.Override, Is.Null, "the operator's values steer after the run");
        Assert.That(sim.NudgeOffset, Is.EqualTo(0).Within(1e-9), "the line is back where it was");
        var chosen = run.Levels.Last(l => l.Gains == run.Found);
        Assert.That(chosen.Good(LineTuneRun.StepM), Is.True);
    }

    private static void Report(LineTuneRun run, string name)
    {
        TestContext.Out.WriteLine($"--- {name}: {run.Stage}");
        TestContext.Out.WriteLine(run.ResultText);
        TestContext.Out.WriteLine(run.Measurements);
    }

    private static void AssertLeftAsFound(LineTuneSim sim, double homeNudge = 0)
    {
        Assert.That(sim.Override, Is.Null, "the operator's values steer");
        // A move back asked in the last cycle reaches the line in the next one.
        sim.Cycle();
        Assert.That(sim.NudgeOffset, Is.EqualTo(homeNudge).Within(1e-9), "the line is back where it was");
    }

    [Test]
    public void The_operators_own_value_is_measured_first()
    {
        var sim = new LineTuneSim { Hold = 4.0 };
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 600);
        Assert.That(run.Levels[0].Gains.LookAheadHold, Is.EqualTo(4.0));
        Assert.That(run.ResultText, Does.Contain("(now 4.0)"));
    }

    [Test]
    public void Late_steering_with_a_quick_setting_is_given_a_calmer_one()
    {
        // A valve that answers 0.35 s late and turns 15 deg/s, on a Steer Response of 1.25.
        var sim = new LineTuneSim { DeadS = 0.35, RateDegS = 15, LagS = 0.3, Hold = 1.25, MinLookAhead = 1.0 };
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 900);
        Report(run, "late");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Levels[0].Good(LineTuneRun.StepM), Is.False, "the quick setting swings on this steering");
        Assert.That(run.Found!.LookAheadHold, Is.GreaterThan(1.25));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Slow_calm_setting_is_made_quicker()
    {
        var sim = new LineTuneSim { Hold = 8.0 };
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 900);
        Report(run, "calm");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Found!.LookAheadHold, Is.LessThan(8.0));
        var first = run.Levels[0];
        var chosen = run.Levels.Last(l => l.Gains == run.Found);
        Assert.That(chosen.SettleS, Is.LessThan(first.SettleS));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Stanley_finds_a_distance_gain([Values(0.3, 0.8, 3.2)] double own)
    {
        var sim = new LineTuneSim { Stanley = true, StanleyDist = own };
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 900);
        Report(run, $"stanley {own}");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Levels.Last(l => l.Gains == run.Found).Good(LineTuneRun.StepM), Is.True);
        Assert.That(run.Found!.LookAheadHold, Is.EqualTo(sim.Hold), "Stanley leaves Steer Response alone");
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Driving_the_other_way_along_the_track_moves_the_line_back_too()
    {
        var sim = new LineTuneSim { HeadingSameWay = false, Heading = Math.PI };
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 600);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Very_slow_steering_gets_a_calmer_value()
    {
        // A steering that turns 2 deg/s and answers 0.6 s late, on Steer Response 4.0.
        var sim = new LineTuneSim { DeadS = 0.6, RateDegS = 2, LagS = 0.5 };
        Settle(sim, 60);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1800);
        Report(run, "very slow");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Found!.LookAheadHold, Is.GreaterThan(4.0));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Steering_no_value_can_hold_ends_without_a_value_and_changes_nothing()
    {
        // Answers a second and a half late: every value swings, the calmest too.
        var sim = new LineTuneSim { DeadS = 1.5, RateDegS = 5, LagS = 0.5, Hold = 4.0 };
        Settle(sim, 60);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1800);
        Report(run, "hopeless");
        Assert.That(run.Stage, Is.AnyOf(LineTuneStage.Failed, LineTuneStage.Stopped));
        Assert.That(run.Found, Is.Null);
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Values_tried_and_the_calm_value_after_them_stay_within_their_bounds(
        [Values(0.35, 0.5)] double dead, [Values(1.0, 1.5)] double own, [Values(6.0, 10.0)] double kmh)
    {
        // Late steering on quick values: moves are cut; while a value tried steers, the tractor
        // gets no further from the line than where it was when the value went in (or the move)
        // plus the runaway margin; while the calm value brings it back, no further than where it
        // took over plus RecoverRunawayM; and the run ends with the operator's values and line.
        var sim = new LineTuneSim { DeadS = dead, RateDegS = 20, LagS = 0.4, Hold = own, MinLookAhead = 1.0, SpeedKmh = kmh };
        Settle(sim, 30);
        var run = NewRun(sim);
        run.Start();
        double worstTried = double.NegativeInfinity, worstRescue = double.NegativeInfinity;
        GuidanceGainsOverride? on = null;
        double entry = 0;
        var recent = new List<(double T, double E)>();
        Drive(sim, run, 1800, after: s =>
        {
            recent.Add((s.TimeS, s.Xte));
            recent.RemoveAll(r => r.T < s.TimeS - LineTuneRun.RecentS);
            if (sim.Override != null)
            {
                if (!ReferenceEquals(sim.Override, on))
                {
                    on = sim.Override;
                    entry = recent.Take(recent.Count - 1).Select(r => Math.Abs(r.E)).DefaultIfEmpty(0).Max();
                }
                bool rescue = sim.Override.LookAheadHold == LineTuneRun.HoldLadder[0];
                if (rescue)
                    worstRescue = Math.Max(worstRescue, Math.Abs(s.Xte) - (Math.Max(entry, LineTuneRun.SteadyMaxM) + LineTuneRun.RecoverRunawayM));
                else
                    worstTried = Math.Max(worstTried, Math.Abs(s.Xte)
                        - (Math.Max(Math.Max(entry, LineTuneRun.SteadyMaxM), LineTuneRun.StepM) + LineTuneRun.RunawayM));
            }
            else on = null;
        });
        Report(run, $"bounds dead {dead} own {own} {kmh} km/h");
        TestContext.Out.WriteLine($"worst over the bound: tried {worstTried:F3}, calm {worstRescue:F3}");
        Assert.That(run.Active, Is.False);
        Assert.That(worstTried, Is.LessThanOrEqualTo(0.05), "a value tried steered past its bound");
        Assert.That(worstRescue, Is.LessThanOrEqualTo(0.1), "the calm value let it go past its bound");
        AssertLeftAsFound(sim);
    }

    [Test]
    public void AutoSteer_off_at_any_moment_takes_the_value_off_at_once_and_the_run_goes_on_after()
    {
        // The sweep: AutoSteer goes off at every 0.7 s of a whole run, for 4 s.
        var probe = new LineTuneSim();
        Settle(probe, 10);
        var probeRun = NewRun(probe);
        probeRun.Start();
        double t0 = probe.Time;
        Drive(probe, probeRun, 600);
        double length = probe.Time - t0;
        int runs = 0;
        for (double at = 0.35; at < length; at += 0.7)
        {
            var sim = new LineTuneSim();
            Settle(sim, 10);
            var run = NewRun(sim);
            run.Start();
            double start = sim.Time;
            bool sawOffSample = false;
            Drive(sim, run, 900,
                each: t => sim.Engaged = !(t - start >= at && t - start < at + 4.0),
                after: s =>
                {
                    if (!s.Engaged && !sawOffSample)
                    {
                        sawOffSample = true;
                        Assert.That(sim.Override, Is.Null, $"off at {at:F2} s: the value is taken off in the same cycle");
                    }
                    if (!s.Engaged)
                        Assert.That(run.Stage, Is.Not.EqualTo(LineTuneStage.Measuring));
                });
            if (run.Stage != LineTuneStage.Result) Report(run, $"off at {at:F2}");
            Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result), $"off at {at:F2} s");
            AssertLeftAsFound(sim);
            runs++;
        }
        TestContext.Out.WriteLine($"{runs} runs over {length:F0} s");
        Assert.That(runs, Is.GreaterThan(20));
    }

    [Test]
    public void The_line_moved_by_hand_stops_the_run_and_keeps_the_operators_move()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        // Into the first move, then the operator nudges 5 cm left.
        Drive(sim, run, 4.5);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        sim.HandNudge(-0.05);
        Drive(sim, run, 5);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(run.ResultText, Does.Contain("moved by hand"));
        AssertLeftAsFound(sim, homeNudge: -0.05);
    }

    [Test]
    public void A_hand_nudge_in_the_cycle_of_the_runs_own_move_stops_the_run_and_is_kept()
    {
        // The operator nudges 5 cm left in the very cycle the run's 30 cm move shows: the line is
        // at 25 cm, neither where it was nor where the run expects it. Taken as the run's own move
        // still on its way, a value tried steered on for 1.5 s on a line the operator had moved.
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        bool nudged = false;
        double stoppedAt = double.NaN, askedAt = double.NaN;
        Drive(sim, run, 30, each: t =>
        {
            if (!nudged && sim.NudgeCalls == 1) { nudged = true; askedAt = t; sim.HandNudge(-0.05); }
        }, after: s => { if (!run.Active && double.IsNaN(stoppedAt)) stoppedAt = s.TimeS; });
        Assert.That(nudged, Is.True);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(run.ResultText, Does.Contain("moved by hand"));
        Assert.That(stoppedAt - askedAt, Is.LessThan(0.3), "in the cycle it shows, not after the 1.5 s wait");
        AssertLeftAsFound(sim, homeNudge: -0.05);
    }

    [Test]
    public void A_move_that_never_shows_is_not_moved_back()
    {
        // The nudge reset in the cycle of the run's move: the line stays where it was, and the
        // move is not on it. After the 1.5 s wait the run moved "back" a move that was never
        // there, 30 cm the other way.
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        bool undone = false;
        double least = 0;
        Drive(sim, run, 12, each: t =>
        {
            if (!undone && sim.NudgeCalls == 1) { undone = true; sim.HandNudge(-LineTuneRun.StepM); }
        }, after: s => least = Math.Min(least, s.NudgeOffset));
        Assert.That(undone, Is.True);
        Assert.That(least, Is.GreaterThan(-0.01), "the line was never moved the other way");
    }

    [Test]
    public void Another_track_chosen_during_a_move_stops_the_run_without_moving_that_track()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 4.5);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        Assert.That(sim.NudgeOffset, Is.EqualTo(LineTuneRun.StepM).Within(1e-9), "the scenario: the run's move is on the line");
        // Another track, with a nudge of its own: the run's move is not in it.
        sim.Track = new object();
        sim.NudgeOffset = 0.12;
        Drive(sim, run, 2);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(sim.Override, Is.Null);
        Settle(sim, 2);
        Assert.That(sim.NudgeOffset, Is.EqualTo(0.12).Within(1e-9), "the new track keeps its own nudge");
    }

    [Test]
    public void The_calm_value_after_a_cut_is_never_quicker_than_the_operators_own([Values(0.2, 0.25, 0.3, 0.8)] double own)
    {
        // Stanley with the operator's Aggressiveness under the calm value's floor of 0.3: the floor
        // made the "calm" value (0.3) quicker than the operator's own.
        var sim = new LineTuneSim { Stanley = true, StanleyDist = own };
        var run = NewRun(sim);
        foreach (double cut in new[] { 0.4, 1.0, 4.0 })
        {
            var calm = run.RescueFor(new LineTuneLevel
            {
                Gains = new GuidanceGainsOverride(sim.Hold, cut, sim.StanleyHead), Value = cut, Failure = "swung",
            });
            Assert.That(calm.StanleyDistanceGain, Is.LessThanOrEqualTo(own), $"own {own}, cut {cut}");
            Assert.That(calm.StanleyDistanceGain, Is.LessThan(cut), $"own {own}, cut {cut}: calmer than the one that swung");
        }
    }

    [Test]
    public void A_snap_to_the_next_pass_stops_the_run_without_moving_that_line()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 4.5);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        // The snap: another pass and the nudge zeroed, in one cycle.
        sim.Pass = 1;
        sim.NudgeOffset = 0;
        Drive(sim, run, 2);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(sim.Override, Is.Null);
        sim.Cycle();
        Assert.That(sim.NudgeOffset, Is.EqualTo(0).Within(1e-9), "the new pass is not moved");
    }

    [Test]
    public void A_speed_change_drops_the_value_and_the_run_waits_for_the_speed()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 6);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        sim.SpeedKmh = 12;
        // The speed is a mean of one second (LineTuneRun.SpeedMeanS): the change shows within it.
        Drive(sim, run, 1.0);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(sim.Override, Is.Null);
        Assert.That(run.Description, Does.Contain("8.0 km/h"));
        Drive(sim, run, 20);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting), "still too fast");
        sim.SpeedKmh = 8.3;
        Drive(sim, run, 900);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void No_value_starts_near_the_headland_or_in_a_turn()
    {
        var sim = new LineTuneSim { RoomAheadM = 40 };
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 10);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(run.Description, Does.Contain("headland"));
        Assert.That(sim.NudgeCalls, Is.Zero);
        sim.RoomAheadM = double.PositiveInfinity;
        sim.Turning = true;
        Drive(sim, run, 10);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(run.Description, Does.Contain("turn"));
        sim.Turning = false;
        Drive(sim, run, 2);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting), "a few seconds after the turn");
        Drive(sim, run, 900);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
    }

    [Test]
    public void The_headland_coming_near_drops_the_value_and_moves_the_line_back()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 4.5);
        Assert.That(sim.NudgeOffset, Is.Not.EqualTo(0), "the first move is out");
        sim.RoomAheadM = 8;
        Drive(sim, run, 0.2);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Stop_puts_everything_back()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 4.5);
        run.Stop("Stopped.");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Stopped));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void Without_an_RTK_fix_nothing_starts()
    {
        var sim = new LineTuneSim { FixOk = false };
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 30);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(sim.NudgeCalls, Is.Zero);
        Assert.That(run.Description, Does.Contain("RTK"));
    }

    [Test]
    public void Values_the_minimum_look_ahead_makes_the_same_are_not_tried()
    {
        // At 4 km/h with a 4 m minimum look-ahead, everything under about 3 m steers the same.
        var sim = new LineTuneSim { SpeedKmh = 4, MinLookAhead = 4.0, Hold = 3.0 };
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 900);
        Report(run, "min look-ahead");
        Assert.That(run.Levels.Count(l => l.Gains.LookAheadHold * (1 + 4 * 0.05 * sim.Mult) <= 4.0), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void A_noisy_position_still_settles()
    {
        var sim = new LineTuneSim { NoiseM = 0.015 };
        Settle(sim, 10);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 900);
        Report(run, "noise");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
    }

    // ------------------------------------------------------------ on a tractor (18 Nov 2026)
    //
    // The run on a real tractor, at 3 to 4 km/h and then at 6: it started a value while
    // AutoSteer was still bringing the tractor onto the line, its waits in seconds were too
    // short at walking pace, one speed reading dropped a value, and a tractor that rests a few
    // centimetres from where it was before a move never "settled". The steering below is that
    // tractor's, as its Steer Wizard measured it.

    /// <summary>A steering that turns 24 deg/s, answers 0.19 s late and goes to 23 deg, on Steer Response 2.5.</summary>
    private static LineTuneSim Tractor(double kmh, double startOffsetM = 0) => new(startOffsetM)
    {
        SpeedKmh = kmh, Wheelbase = 2.7, MaxSteer = 23, DeadS = 0.19, RateDegS = 24, LagS = 0.1,
        Hold = 2.5, Mult = 1.5, MinLookAhead = 2.0,
    };

    [Test]
    public void Start_pressed_while_the_tractor_still_joins_the_line_waits_until_it_holds_it()
    {
        // AutoSteer just on, 0.8 m from the line, at walking pace: Start is pressed at once.
        var sim = Tractor(3.3, startOffsetM: 0.8);
        var run = NewRun(sim);
        run.Start();
        double firstValueAt = double.NaN, xteThen = double.NaN;
        bool waited = false;
        Drive(sim, run, 1500, after: s =>
        {
            waited |= run.Description.Contains("Waiting for AutoSteer to hold the line");
            if (double.IsNaN(firstValueAt) && sim.Override != null)
            {
                firstValueAt = s.TimeS;
                xteThen = s.Xte;
            }
        });
        Report(run, "start pressed early");
        Assert.That(waited, Is.True, "the panel says what it waits for");
        Assert.That(firstValueAt, Is.GreaterThan(LineTuneRun.JoinHoldS), "no value before the line was held");
        Assert.That(Math.Abs(xteThen), Is.LessThan(LineTuneRun.SteadyMaxM), "the first value starts on the line");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Levels[0].Failure, Is.Null, "the operator's own value is measured, not blamed for the join");
        AssertLeftAsFound(sim);
    }

    /// <summary>The run on samples made by hand: what it asked of the tractor.</summary>
    private sealed class Asked : ILineTuneActions
    {
        public GuidanceGainsOverride? Gains;
        public int GainsCalls, Nudges;
        public void Nudge(double meters) => Nudges++;
        public void SetGains(GuidanceGainsOverride? gains) { Gains = gains; if (gains != null) GainsCalls++; }
    }

    private static readonly object HandTrack = new();

    private static LineTuneSample At(double t, double xte, double kmh = 8, int pass = 0) =>
        new(t, true, kmh, xte, 0, true, HandTrack, pass, false, double.PositiveInfinity, true, false, false);

    private static LineTuneRun HandRun(Asked asked) =>
        new(asked, new GuidanceGainsOverride(4.0, 0.8, 1.0), false, 1.4, 0.9, 2.0);

    [Test]
    public void While_the_tractor_is_on_its_way_to_the_line_no_value_starts_however_long_it_takes()
    {
        var asked = new Asked();
        var run = HandRun(asked);
        run.Start();
        // Ten minutes 0.8 m from the line (a line it can't reach: a ditch, a hand on the wheel).
        for (double t = 0; t < 600; t += 0.1)
            run.OnSample(At(t, 0.8));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(asked.GainsCalls, Is.Zero);
        Assert.That(asked.Nudges, Is.Zero);
        Assert.That(run.Description, Does.Contain("0.80 m"));
        Assert.That(run.Description, Does.Contain("8.0 km/h"), "and the speed it would measure at");
    }

    [Test]
    public void Own_values_that_wander_about_the_line_are_measured_after_a_while_all_the_same()
    {
        // Never steady, never far: 8 cm either side, every 5 s. That is a value to measure.
        var asked = new Asked();
        var run = HandRun(asked);
        run.Start();
        double t = 0;
        for (; t < LineTuneRun.JoinTimeoutS - 1; t += 0.1)
            run.OnSample(At(t, 0.08 * Math.Sin(t * 2 * Math.PI / 5)));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting), "it has the time to hold the line first");
        for (; t < LineTuneRun.JoinTimeoutS + 2; t += 0.1)
            run.OnSample(At(t, 0.08 * Math.Sin(t * 2 * Math.PI / 5)));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        Assert.That(asked.GainsCalls, Is.GreaterThan(0));
    }

    [Test]
    public void Own_values_that_keep_swinging_the_tractor_across_the_line_get_the_calm_value()
    {
        var asked = new Asked();
        var run = HandRun(asked);
        run.Start();
        // 0.8 m on one side, 0.5 m on the other, and 0.5 m out on the first again: 4 s each way.
        double t = 0;
        for (; t < 4; t += 0.1)
            run.OnSample(At(t, 0.8 - 1.3 * t / 4));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting), "once across the line is a tractor joining it");
        Assert.That(asked.GainsCalls, Is.Zero);
        for (; t < 8; t += 0.1)
            run.OnSample(At(t, -0.5 + 1.0 * (t - 4) / 4));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        Assert.That(asked.Gains, Is.Not.Null);
        Assert.That(asked.Gains!.LookAheadHold, Is.EqualTo(LineTuneRun.HoldLadder[0]), "the calmest value steers");
        Assert.That(asked.Nudges, Is.Zero, "and nothing is measured yet");
        Assert.That(run.Description, Does.Contain("swing"));
    }

    [Test]
    public void A_tractor_that_joins_the_line_at_an_angle_and_goes_past_it_once_is_left_alone()
    {
        // AutoSteer turned on in a turn, at 6 km/h: 1.2 m from the line, across it, 1.9 m out on
        // the other side, and back onto it. Nothing is tried, and no calm value put in, on the way.
        var asked = new Asked();
        var run = HandRun(asked);
        run.Start();
        double t = 0;
        for (; t < 5; t += 0.1)
            run.OnSample(At(t, 1.2 - 3.1 * t / 5));
        for (; t < 15; t += 0.1)
            run.OnSample(At(t, -1.9 + 1.9 * (t - 5) / 10));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(asked.GainsCalls, Is.Zero, "the operator's own values bring it onto the line");
        for (; t < 15 + LineTuneRun.JoinHoldS + 0.3; t += 0.1)
            run.OnSample(At(t, 0));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        Assert.That(asked.Gains!.LookAheadHold, Is.EqualTo(4.0), "then the operator's own value is the first one measured");
    }

    [Test]
    public void A_tractor_steered_onto_the_next_pass_while_joining_is_not_taken_for_a_swing()
    {
        // As above, but 1.9 m past the line the nearest pass is the next one (3 m passes): the
        // distance jumps from 1.9 m on one side to 1.1 m on the other, with no swing in it.
        var asked = new Asked();
        var run = HandRun(asked);
        run.Start();
        double t = 0;
        for (; t < 5; t += 0.1)
            run.OnSample(At(t, 1.2 - 3.1 * t / 5));
        for (; t < 12; t += 0.1)
            run.OnSample(At(t, 1.1 - 1.1 * (t - 5) / 7, pass: 1));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(asked.GainsCalls, Is.Zero);
        for (; t < 12 + LineTuneRun.JoinHoldS + 0.3; t += 0.1)
            run.OnSample(At(t, 0, pass: 1));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Measuring));
        Assert.That(asked.Gains!.LookAheadHold, Is.EqualTo(4.0));
    }

    [Test]
    public void At_walking_pace_every_wait_is_as_long_in_metres_as_at_working_speed()
    {
        Assert.That(LineTuneRun.Stretch(8), Is.EqualTo(1));
        Assert.That(LineTuneRun.Stretch(12), Is.EqualTo(1), "never shorter than they were");
        Assert.That(LineTuneRun.Stretch(4), Is.EqualTo(2));
        Assert.That(LineTuneRun.RoomNeeded(4), Is.EqualTo(LineTuneRun.RoomNeeded(8)).Within(1e-9));

        // Stanley at 3.3 km/h takes 10 to 12 s to move 30 cm sideways, whatever the gain: in
        // 15 s it "was too slow to get there", and the next value had even less time.
        var sim = Tractor(3.3);
        sim.Stanley = true;
        sim.StanleyDist = 0.8;
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1500);
        Report(run, "walking pace, Stanley");
        Assert.That(run.TimeFactor, Is.EqualTo(8 / run.RunSpeedKmh).Within(1e-9));
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Levels[0].Failure, Is.Null, "the operator's own gain is measured");
        Assert.That(run.Levels[0].Out!.Settled && run.Levels[0].Back!.Settled, Is.True);
        Assert.That(run.ResultText, Does.Contain("If you work faster than that"));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void A_speed_reading_out_of_the_band_does_not_drop_the_value()
    {
        // A receiver at a steady 6 km/h reads 4.8 to 7.2 from one cycle to the next.
        var sim = Tractor(6);
        sim.SpeedNoiseKmh = 1.2;
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        bool measuring = false, dropped = false;
        Drive(sim, run, 1500, after: _ =>
        {
            measuring |= run.Stage == LineTuneStage.Measuring;
            dropped |= measuring && run.Description.Contains("Drive at about");
        });
        Report(run, "speed noise");
        Assert.That(dropped, Is.False);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.RunSpeedKmh, Is.EqualTo(6).Within(0.5));
        AssertLeftAsFound(sim);
    }

    [Test]
    public void A_steering_with_slack_still_gets_a_value_and_is_told_about_the_slack()
    {
        // The wheels stay put for commands under a third of a degree: the tractor rests a few
        // centimetres from where it was before each move, and goes past the line one way
        // whatever the value.
        var sim = Tractor(6);
        sim.SlackDeg = 0.35;
        Settle(sim, 20);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1500);
        Report(run, "slack");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        var chosen = run.Levels.Last(l => l.Gains == run.Found);
        Assert.That(chosen.CameBack, Is.True);
        if (!chosen.Good(LineTuneRun.StepM))
        {
            Assert.That(run.ResultText, Does.Contain("went past it the least"));
            Assert.That(run.ResultText, Does.Contain("slack"));
            double least = run.Levels.Where(l => l.CameBack).Min(l => l.OvershootM);
            Assert.That(chosen.OvershootM, Is.LessThanOrEqualTo(least + LineTuneRun.LeastPastSlackM + 1e-9));
        }
        AssertLeftAsFound(sim);
    }

    [Test]
    public void A_move_that_ends_steady_beside_where_the_tractor_was_is_measured_against_where_it_rests()
    {
        // Samples by hand: the line jumps 30 cm, the tractor comes back in 4 s, goes 3 cm past
        // where it will rest, and rests 7 cm from where it was before the move.
        var asked = new Asked();
        var run = HandRun(asked);
        var log = new List<string>();
        run.Log = log.Add;
        run.Start();
        double t = 0, nudge = 0;
        LineTuneSample S(double xte) => new(t, true, 8, xte, nudge, true, HandTrack, 0, false, double.PositiveInfinity, true, false, false);
        for (; asked.Nudges == 0 && t < 30; t += 0.1)
            run.OnSample(S(0));
        Assert.That(asked.Nudges, Is.EqualTo(1), "the line is moved once the tractor has held it");
        nudge = LineTuneRun.StepM;
        double t0 = t;
        for (; run.Levels.Count == 0 && run.Stage == LineTuneStage.Measuring && asked.Nudges == 1 && t < t0 + 30; t += 0.1)
        {
            double dt = t - t0;
            double xte = dt < 4 ? 0.30 - 0.26 * dt / 4      // back towards the line
                : dt < 5 ? 0.04                            // 3 cm past where it will rest
                : 0.07;                                    // and there it rests
            run.OnSample(S(xte));
        }
        Assert.That(asked.Nudges, Is.EqualTo(2), "it settled: the line is moved back");
        Assert.That(t - t0, Is.EqualTo(LineTuneRun.MaxStepS).Within(0.3), "only when the time was up");
        Assert.That(log.Any(l => l.Contains("line out") && l.Contains("where it rests")), Is.True, string.Join("\n", log));
    }

    [Test]
    public void The_run_tells_the_log_what_it_did()
    {
        var sim = new LineTuneSim();
        Settle(sim, 10);
        var run = NewRun(sim);
        var log = new List<string>();
        run.Log = log.Add;
        run.Start();
        Assert.That(log[0], Is.EqualTo("Started: Pure Pursuit, Steer Response 4.0."));
        Assert.That(run.Description, Does.Contain("the speed you work at"));
        Drive(sim, run, 600);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(log.Any(l => l.StartsWith("Trying Steer Response 4.0") && l.Contains("8.0 km/h")), Is.True);
        Assert.That(log.Any(l => l.Contains("line out: moved 30 cm")), Is.True);
        Assert.That(log.Any(l => l.Contains("line back:")), Is.True);
        Assert.That(log.Any(l => l.StartsWith("Ended (Result): Steer Response")), Is.True);
        Assert.That(log[^1], Does.StartWith("Values tried: Steer Response 4.0:"));
        Assert.That(log.All(l => !l.Contains('\n')), Is.True, "one line each");
        Assert.That(log.All(l => !l.Contains("-+")), Is.True, "no \"-+0\" for a distance that rounds to nothing");
        Assert.That(log.Any(l => l.StartsWith("Waiting for AutoSteer to hold the line:")), Is.True);
        run.MarkAccepted("In use now: Steer Response 3.0.");
        Assert.That(log[^1], Is.EqualTo("In use now: Steer Response 3.0."));
    }

    [Test]
    public void No_value_found_does_not_send_the_operator_to_the_wizard_he_has_just_done()
    {
        var sim = new LineTuneSim { DeadS = 1.5, RateDegS = 5, LagS = 0.5, Hold = 4.0 };
        Settle(sim, 60);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1800);
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Failed));
        Assert.That(run.ResultText, Does.Not.Contain("first"));
        Assert.That(run.ResultText, Does.Contain("If the wheels themselves swing or answer late"));
        Assert.That(run.ResultText, Does.Contain("what each one did is below"));
    }

    [Test]
    public void A_steering_a_second_late_is_given_the_calmest_value()
    {
        // Every value but the calmest swings; the calmest comes back cleanly, and rests where
        // the swing of the value before it had not yet died out. Where the tractor was before
        // the move is not held against it.
        var sim = new LineTuneSim { DeadS = 1.0, RateDegS = 5, LagS = 0.5, Hold = 4.0 };
        Settle(sim, 60);
        var run = NewRun(sim);
        run.Start();
        Drive(sim, run, 1800);
        Report(run, "a second late");
        Assert.That(run.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(run.Found!.LookAheadHold, Is.EqualTo(LineTuneRun.HoldLadder[0]));
        Assert.That(run.Levels.Last(l => l.Gains == run.Found).Good(LineTuneRun.StepM), Is.True);
        AssertLeftAsFound(sim);
    }
}
