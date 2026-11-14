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
        // Answers a whole second late: every value swings, the calmest too.
        var sim = new LineTuneSim { DeadS = 1.0, RateDegS = 5, LagS = 0.5, Hold = 4.0 };
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
        Drive(sim, run, 0.3);
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
}
