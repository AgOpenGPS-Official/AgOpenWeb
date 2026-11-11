// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// On a curve the headland ahead is found by a straight ray along the heading. On the outside of
/// a bend it leaves the field while the pass runs on, and the turn was judged at that false end:
/// field job J10 (a recorded S-curve) said "End of field reached" 4 s into a pass and made the
/// turn at the real end 140 s later.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CurveEndOfFieldTests
{
    private YouTurnStateMachine _sm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;
        var offset = new PolygonOffsetService();
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);
    }

    [Test]
    public void On_a_curve_the_end_is_where_the_pass_leaves_the_field_not_where_a_straight_line_does()
    {
        // A field along a bend: the curve is an arc of radius 150 m round (150, 0), going north
        // and bending east; passes are 8 m apart, pass k at radius 150 - 8 (k + 0.5). The
        // cultivated strip runs from radius 152 (just outside pass 0, so pass -1 is out) in to
        // radius 120, its south end slanted (each pass in starts about 50 m later), its north end at 80°.
        // Just in from the south end on pass 0, a straight line along the heading leaves the
        // strip on the outside of the bend about 40 m on, while the pass runs on some 220 m.
        // Judged at that false end, pass 1 has only ~13 m in the field: no turn, and an end of
        // field said at the start of the pass (field job J10, a recorded S-curve). The turn is
        // for the real end.
        var c = ConfigurationStore.Instance;
        c.Tool.SetSectionWidth(0, 800);
        const double R = 150, W = 8;
        var curve = new List<Vec3>();
        for (double t = -0.6; t <= 1.6001; t += 0.02)
            curve.Add(new Vec3(150 - R * System.Math.Cos(t), R * System.Math.Sin(t), t));
        var track = Models.Track.Track.FromCurve("Bend", curve);
        static Vec2 P(double r, double t) => new(150 - r * System.Math.Cos(t), r * System.Math.Sin(t));
        var ring = new List<Vec2>();
        for (double t = -0.3; t <= 1.4001; t += 0.05) ring.Add(P(152, t));       // outside of the bend
        for (double t = 1.4; t >= 1.14 - 1e-9; t -= 0.02) ring.Add(P(120, t));   // inside, north to the slant
        for (double f = 0.95; f > 0.01; f -= 0.05) ring.Add(P(152 - 32 * f, -0.3 + 1.44 * f)); // the slant: 0.045 rad later per metre in
        var offset = new PolygonOffsetService();
        var cultivated = offset.CalculatePointHeadings(ring);
        var outer = offset.CreateInwardOffset(ring, -15)!;
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };

        double r0 = R - W * 0.5, t0 = 0.0;
        var at = P(r0, t0);
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = at.Easting, Northing = at.Northing, Heading = t0 * 180 / System.Math.PI, Speed = 2.78 },
            track, boundary, cultivated,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
            IsAlternateSkipMode: false);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        var effects = _sm.Tick(ctx, guidance, turn);

        Assert.That(turn.DistanceToHeadland, Is.LessThan(80), "the scenario: the straight line leaves the strip early");
        Assert.That(effects.StatusMessage ?? "", Does.Not.Contain("End of field"),
            "the pass runs on for some 220 m: not the end of the field");
        Assert.That(turn.TurnPath, Is.Null, "no turn judged at the false end either");

        // Some 36 m before the real end (80°): the straight line finds it, and the turn is made.
        double t1 = 1.15;
        var near = P(r0, t1);
        effects = _sm.Tick(ctx with { CurrentPosition = new Position { Easting = near.Easting, Northing = near.Northing, Heading = t1 * 180 / System.Math.PI, Speed = 2.78 } },
            guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn at the real end ({effects.StatusMessage})");
    }
}
