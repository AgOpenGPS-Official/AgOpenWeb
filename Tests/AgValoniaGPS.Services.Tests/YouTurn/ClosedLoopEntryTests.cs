using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.YouTurn;
using AgValoniaGPS.Services.Geometry;
using AgValoniaGPS.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgValoniaGPS.Services.Tests.YouTurn;

/// <summary>
/// Closed-loop steering sim of the APPROACH→TURN handoff (the e2e test teleports along
/// the path, so it can't see a steering transient). Drives a bicycle model: straight
/// approach along the pass, then once the state machine triggers, steer with the real
/// YouTurnGuidanceService each 10 Hz cycle. Asserts the entry handoff doesn't slam the
/// wheels / oscillate (the operator's "gets lost & wiggles for ½ s entering the turn").
/// </summary>
[TestFixture]
[NonParallelizable]
public class ClosedLoopEntryTests
{
    private const double Dt = 0.1;            // 10 Hz
    private const double SpeedMs = 2.78;      // ~10 km/h
    private const double Wheelbase = 2.5;

    [Test]
    public void EntryHandoff_NoSteeringSpike()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = Wheelbase;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600); // 6 m
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 2;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        var guidance = new YouTurnGuidanceService();
        var trackGuidance = new AgValoniaGPS.Services.Track.TrackGuidanceService();

        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) }
                    .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var headland = new List<Vec3> { new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0) };

        // Gentle CURVE that ENDS at the field edge (y=45, inside boundary ±50) — the
        // operator's case, where the goal-point tangent projection is active on final
        // approach. Points every 2 m + headings.
        var curve = new List<Vec3>();
        for (double y = -45; y <= 24 + 1e-9; y += 2.0)
            curve.Add(new Vec3(8.0 * Math.Sin(y / 60.0), y, 0));
        for (int k = 0; k < curve.Count - 1; k++)
        {
            double dE = curve[k + 1].Easting - curve[k].Easting, dN = curve[k + 1].Northing - curve[k].Northing;
            double h = Math.Atan2(dE, dN); if (h < 0) h += Math.PI * 2;
            curve[k] = new Vec3(curve[k].Easting, curve[k].Northing, h);
        }
        curve[^1] = new Vec3(curve[^1].Easting, curve[^1].Northing, curve[^2].Heading);
        var track = Models.Track.Track.FromCurve("c", curve);

        // Walk the curve by arc length for a clean on-curve approach.
        (Vec2 pos, double head) WalkCurve(double dist)
        {
            double traveled = 0;
            for (int k = 1; k < curve.Count; k++)
            {
                double dE = curve[k].Easting - curve[k - 1].Easting, dN = curve[k].Northing - curve[k - 1].Northing;
                double seg = Math.Sqrt(dE * dE + dN * dN);
                if (traveled + seg >= dist)
                {
                    double f = (dist - traveled) / seg;
                    return (new Vec2(curve[k - 1].Easting + dE * f, curve[k - 1].Northing + dN * f),
                            Math.Atan2(dE, dN) < 0 ? Math.Atan2(dE, dN) + Math.PI * 2 : Math.Atan2(dE, dN));
                }
                traveled += seg;
            }
            return (new Vec2(curve[^1].Easting, curve[^1].Northing), curve[^1].Heading);
        }

        var gState = new GuidanceWorkingState();
        var tState = new YouTurnWorkingState { IsEnabled = true };

        // Start ~35 m of arc from the curve start (y=-45) → ≈ y=-10, well before the
        // y=40 headland. Closed loop with a bicycle model the whole way.
        var (pivot, heading) = WalkCurve(20.0);
        double speedKmh = SpeedMs * 3.6;
        double LookAhead()
        {
            double la = c.Guidance.GoalPointLookAheadHold;
            if (speedKmh > 1)
                la = Math.Max(c.Guidance.MinLookAheadDistance,
                    c.Guidance.GoalPointLookAheadHold + speedKmh * c.Guidance.GoalPointLookAheadMult * 0.1);
            return la;
        }

        var samples = new List<(string phase, double steer, double y)>();
        int executingCount = 0;

        for (int i = 0; i < 600; i++)
        {
            var pos = new Position { Easting = pivot.Easting, Northing = pivot.Northing, Heading = heading * 180 / Math.PI, Speed = SpeedMs };
            var ctx = new YouTurnStateMachine.TickContext(pos, track, boundary, headland,
                UTurnSkipRows: 0, IsSkipWorkedMode: false, HeadlandCalculatedWidth: 10.0, HeadlandDistance: 5.0);
            tState.YouTurnCounter++;
            sm.Tick(in ctx, gState, tState);

            bool executing = tState.IsExecuting && tState.TurnPath != null && tState.TurnPath.Count > 2;
            double steerDeg;

            if (executing)
            {
                var gin = new YouTurnGuidanceInput
                {
                    TurnPath = tState.TurnPath!,
                    PivotPosition = new Vec3(pivot.Easting, pivot.Northing, heading),
                    SteerPosition = new Vec3(pivot.Easting + Math.Sin(heading) * Wheelbase,
                                             pivot.Northing + Math.Cos(heading) * Wheelbase, heading),
                    Wheelbase = Wheelbase, MaxSteerAngle = 35, UseStanley = false,
                    GoalPointDistance = LookAhead(), UTurnCompensation = c.Guidance.UTurnCompensation,
                    FixHeading = heading, AvgSpeed = speedKmh, IsReverse = false, UTurnStyle = 0,
                };
                var gout = guidance.CalculateGuidance(gin);
                if (gout.IsTurnComplete) break;
                steerDeg = gout.SteerAngle;
                samples.Add(("turn", steerDeg, pivot.Northing));
                if (++executingCount > 12) break;
            }
            else
            {
                var tin = new Models.Track.TrackGuidanceInput
                {
                    Track = track,
                    PivotPosition = new Vec3(pivot.Easting, pivot.Northing, heading),
                    SteerPosition = new Vec3(pivot.Easting + Math.Sin(heading) * Wheelbase,
                                             pivot.Northing + Math.Cos(heading) * Wheelbase, heading),
                    UseStanley = false, Wheelbase = Wheelbase, MaxSteerAngle = 35,
                    GoalPointDistance = LookAhead(), IsHeadingSameWay = true, FixHeading = heading,
                };
                var tout = trackGuidance.CalculateGuidance(tin);
                steerDeg = tout.SteerAngle;
                samples.Add(("approach", steerDeg, pivot.Northing));
            }

            double steerRad = steerDeg * Math.PI / 180.0;
            heading += (SpeedMs / Wheelbase) * Math.Tan(steerRad) * Dt;
            pivot = new Vec2(pivot.Easting + SpeedMs * Dt * Math.Sin(heading),
                             pivot.Northing + SpeedMs * Dt * Math.Cos(heading));
        }

        Assert.That(samples.Any(s => s.phase == "turn"), "turn never executed");

        // Window around the handoff: the last ~12 approach cycles + the turn cycles.
        int firstTurn = samples.FindIndex(s => s.phase == "turn");
        int from = Math.Max(0, firstTurn - 12);
        var window = samples.GetRange(from, samples.Count - from);
        TestContext.WriteLine("handoff window (phase steer@y):");
        foreach (var s in window) TestContext.WriteLine($"  {s.phase} {s.steer,7:F1}° @ y={s.y:F1}");

        double maxAbs = window.Select(s => Math.Abs(s.steer)).DefaultIfEmpty(0).Max();
        Assert.That(maxAbs, Is.LessThan(15.0),
            $"steering hit {maxAbs:F0}° around the turn entry — the approach/handoff glitch");
    }
}
