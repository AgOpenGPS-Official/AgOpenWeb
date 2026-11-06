// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The operator who does everything wrong, for the obstacle scenarios: on the way to a U-turn
/// obstacle they abort the planned turn, nudge and snap the line, ask for a manual turn, switch
/// AutoSteer or the U-turn off and on, engage late, back up, change the obstacle's choice or
/// its hardness while driving, add an obstacle in front of the tractor, force the turn's
/// direction, change the skip rows, or change the Go around radius and margin beside a tree. Which of these, when and how much comes from a seed, so
/// a run can be repeated. Everything done is logged in <see cref="ObstacleScenarioRig.Log"/>.
/// The operator never drives into an obstacle by hand: what AutoSteer does with the mess is
/// the host's responsibility, what the operator does with the wheel is not.
/// </summary>
public sealed class BadOperator
{
    public enum Mischief
    {
        AbortTurn, Nudge, Snap, ManualUTurn, SteerOffAndOn, UTurnOffMidArc, EngageLate, Reverse,
        ChangeChoice, AddObstacle, DirectionOverride, SkipRows, ToggleHard, GoAroundSettings,
    }

    public static readonly Mischief[] All = (Mischief[])Enum.GetValues(typeof(Mischief));

    private sealed class Pending
    {
        public required Mischief What;
        public required Func<bool> When;
        public required Action Do;
    }

    private readonly ObstacleScenarioRig _rig;
    private readonly ObstacleTestField.Obstacle _target;
    private readonly Random _rnd;
    private readonly List<Pending> _pending = new();
    private int _addedObstacles;

    public BadOperator(ObstacleScenarioRig rig, ObstacleTestField.Obstacle target, int seed, IReadOnlyList<Mischief>? only = null)
    {
        _rig = rig;
        _target = target;
        _rnd = new Random(seed);
        var pool = (only ?? All).ToList();
        int count = only != null ? pool.Count : Math.Min(pool.Count, 2 + _rnd.Next(3)); // 2 to 4 things wrong per pass
        for (int i = 0; i < count; i++)
        {
            var m = pool[_rnd.Next(pool.Count)];
            pool.Remove(m);
            Plan(m);
        }
        Chosen = _pending.Select(p => p.What).ToList();
    }

    public IReadOnlyList<Mischief> Chosen { get; }
    public List<string> Done { get; } = new();

    /// <summary>Whether the operator wants the pass to start with AutoSteer off.</summary>
    public bool StartsDisengaged => Chosen.Contains(Mischief.EngageLate);

    /// <summary>What the operator has done to <paramref name="obstacle"/>'s choice: Turn, or driven through now.</summary>
    public ObstacleAction ChoiceOf(ObstacleTestField.Obstacle obstacle) => obstacle.Polygon.AtObstacle;

    private double Ahead => _rig.ObstacleAhead();
    private double Between(double a, double b) => a + _rnd.NextDouble() * (b - a);

    private void Plan(Mischief m)
    {
        switch (m)
        {
            case Mischief.AbortTurn:
                Add(m, () => _rig.PlannedAtObstacle && !_rig.Executing, () =>
                {
                    _rig.Intents.RequestClearYouTurn();
                    Say("aborted the planned turn");
                });
                break;

            case Mischief.Nudge:
            {
                double at = Between(60, 100);
                double step = (_rnd.Next(2) == 0 ? -1 : 1) * (_rnd.Next(2) == 0 ? 0.1 : 0.2);
                int clicks = 3 + _rnd.Next(3);
                Add(m, () => Ahead < at, () =>
                {
                    for (int i = 0; i < clicks; i++) _rig.Intents.RequestGuidanceNudge(step);
                    Say($"nudged the line {clicks} × {step:+0.0;-0.0} m");
                    _rig.Drive(Between(2, 6));
                    _rig.Intents.RequestGuidanceResetNudge();
                    Say("reset the nudge");
                });
                break;
            }

            case Mischief.Snap:
            {
                double at = Between(50, 90);
                bool left = _rnd.Next(2) == 0;
                Add(m, () => Ahead < at, () =>
                {
                    _rig.Intents.RequestGuidanceSnap(left);
                    Say($"snapped the line {(left ? "left" : "right")}");
                });
                break;
            }

            case Mischief.ManualUTurn:
            {
                double at = Between(30, 80);
                bool left = _rnd.Next(2) == 0;
                Add(m, () => Ahead < at && !_rig.Executing, () =>
                {
                    _rig.Intents.RequestManualYouTurn(left);
                    Say($"asked for a manual U-turn to the {(left ? "left" : "right")}");
                });
                break;
            }

            case Mischief.SteerOffAndOn:
            {
                double at = Between(20, 70);
                double off = Between(0.5, 2);
                Add(m, () => Ahead < at && _rig.Engaged, () =>
                {
                    _rig.Disengage();
                    Say($"AutoSteer off for {off:F1} s");
                    _rig.Drive(off);
                    if (Ahead > 12) _rig.Engage();
                    Say(Ahead > 12 ? "AutoSteer on again" : "left AutoSteer off, too close");
                });
                break;
            }

            case Mischief.UTurnOffMidArc:
            {
                double after = Between(1, 6);
                Add(m, () => _rig.Executing && _rig.ArcStartedAt is { } t0 && _rig.Time - t0 > after, () =>
                {
                    _rig.Pipeline.SetYouTurnEnabled(false);
                    Say("switched the U-turn off in the middle of the arc");
                    _rig.Drive(Between(1, 3));
                    _rig.Pipeline.SetYouTurnEnabled(true);
                    Say("U-turn on again");
                    _rig.Arm("U-turn on again");
                });
                break;
            }

            case Mischief.EngageLate:
            {
                double at = Between(14, 50);
                Add(m, () => !_rig.Engaged && Ahead < at, () =>
                {
                    _rig.Engage();
                    Say("engaged AutoSteer late");
                });
                break;
            }

            case Mischief.Reverse:
            {
                double at = Between(40, 90);
                double back = Between(5, 25);
                Add(m, () => Ahead < at && _rig.Engaged && !_rig.Executing, () =>
                {
                    _rig.Disengage();
                    _rig.Reversing = true;
                    Say($"backed up {back:F0} m");
                    _rig.Drive(back / _rig.Speed);
                    _rig.Reversing = false;
                    _rig.Drive(0.5);
                    if (Ahead > 12) _rig.Engage();
                    Say(Ahead > 12 ? "forward again, AutoSteer on" : "forward again, AutoSteer left off");
                });
                break;
            }

            case Mischief.ChangeChoice:
            {
                double at = Between(25, 60), back = Between(5, 15);
                var polygon = _target.Polygon;
                var was = polygon.AtObstacle;
                string name = was == ObstacleAction.GoRound ? "Go around" : "a U-turn";
                Add(m, () => Ahead < at, () =>
                {
                    polygon.AtObstacle = ObstacleAction.DriveThrough;
                    Say("made the obstacle drive-through");
                    _rig.BoundaryChanged("obstacle now drive-through");
                    // ... and changes their mind when it is right in front of the tractor.
                    Add(m, () => DistanceToTarget() < back, () =>
                    {
                        polygon.AtObstacle = was;
                        Say($"made the obstacle {name} again, {back:F0} m from it");
                        _rig.BoundaryChanged($"obstacle {name} again");
                    });
                });
                break;
            }

            case Mischief.GoAroundSettings:
            {
                double at = Between(10, 40);
                double radius = new[] { 2.0, 4.0, 10.0, 15.0 }[_rnd.Next(4)], margin = new[] { 0.0, 0.5, 2.0, 3.0 }[_rnd.Next(4)];
                Add(m, () => DistanceToTarget() < at, () =>
                {
                    var g = ConfigurationStore.Instance.Guidance;
                    g.GoAroundRadius = radius;
                    g.GoAroundMargin = margin;
                    Say($"set the Go around radius to {radius:F0} m and the margin to {margin:F1} m, {at:F0} m from the obstacle");
                    _rig.Arm("Go around settings changed");
                });
                break;
            }

            case Mischief.AddObstacle:
            {
                double at = Between(70, 110), inFront = Between(30, 50), size = Between(4, 8);
                Add(m, () => Ahead < at, () =>
                {
                    double se = Math.Sin(_rig.Heading), ce = Math.Cos(_rig.Heading);
                    double e = _rig.E + se * inFront, n = _rig.N + ce * inFront;
                    var box = ObstacleTestField.Box(e, n, size, size, _rig.Heading * 180 / Math.PI);
                    box.AtObstacle = ObstacleAction.Turn;
                    box.IsHard = true;
                    Say($"added a {size:F0} m obstacle {inFront:F0} m in front");
                    _rig.AddObstacle(new ObstacleTestField.Obstacle($"New obstacle {++_addedObstacles}", box, e, n));
                });
                break;
            }

            case Mischief.DirectionOverride:
            {
                bool left = _rnd.Next(2) == 0;
                Add(m, () => true, () =>
                {
                    _rig.Pipeline.SetNextUTurnDirectionLeftOverride(left);
                    Say($"forced the next turn to the {(left ? "left" : "right")}");
                });
                break;
            }

            case Mischief.SkipRows:
            {
                int skip = 1 + _rnd.Next(2);
                Add(m, () => true, () =>
                {
                    _rig.Pipeline.SetYouTurnConfig(uTurnSkipRows: skip, isSkipWorkedMode: false,
                        headlandCalculatedWidth: ObstacleTestField.HeadlandWidth, headlandDistance: ObstacleTestField.HeadlandWidth,
                        isAlternateSkipMode: false);
                    Say($"set skip rows to {skip}");
                });
                break;
            }

            case Mischief.ToggleHard:
            {
                double at = Between(30, 70);
                var polygon = _target.Polygon;
                Add(m, () => Ahead < at, () =>
                {
                    polygon.IsHard = !polygon.IsHard;
                    Say(polygon.IsHard ? "made the obstacle Hard" : "made the obstacle Soft");
                    _rig.BoundaryChanged("hardness changed");
                });
                break;
            }
        }
    }

    private double DistanceToTarget() => ObstacleTestField.SignedDistance(_target.Polygon, _rig.E, _rig.N);

    private void Add(Mischief what, Func<bool> when, Action @do) => _pending.Add(new Pending { What = what, When = when, Do = @do });

    private void Say(string what)
    {
        Done.Add(what);
        _rig.Note($"operator: {what}");
    }

    /// <summary>Called before every fix: does whatever is due now.</summary>
    public void Tick()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (!p.When()) continue;
            _pending.RemoveAt(i);
            p.Do();
            return; // one thing at a time
        }
    }
}
