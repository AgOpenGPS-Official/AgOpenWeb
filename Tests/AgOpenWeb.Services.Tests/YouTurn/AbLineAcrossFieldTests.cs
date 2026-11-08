// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
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
/// An AB line is endless, but its A and B can be close together: an A+ or Lat/Lon line has B
/// 100 m from A. The turn generator followed the densified A–B points plus 200 m each way, so
/// in a longer field the pass never reached the turn line at the far end and no turn could be
/// built there; since #698 refuses a fallback that leaves the field, that meant no turn at all
/// (the field job harness's olive grove: every turn at its south end).
/// </summary>
[TestFixture]
[NonParallelizable]
public class AbLineAcrossFieldTests
{
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800); // 8 m
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnRadius = 8.0;
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
    }

    // 200 m wide, 800 m long, north-south; the headland 16 m in.
    private static Boundary LongField() => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(-100, -400), new(100, -400), new(100, 400), new(-100, 400) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        },
    };

    private static List<Vec3> Headland() => new()
    {
        new(-84, -384, 0), new(84, -384, 0), new(84, 384, 0), new(-84, 384, 0),
    };

    // An A+ line set at the south end: A on the headland line, B 100 m on.
    private static Models.Track.Track APlusAtSouthEnd() =>
        Models.Track.Track.FromABLine("A+", new Vec3(0, -384, 0), new Vec3(0, -284, 0));

    [TestCase(true, 360.0, TestName = "Far end (680 m past B): a real turn")]
    [TestCase(false, -360.0, TestName = "Near end (behind A): a real turn")]
    public void A_short_AB_line_turns_at_both_ends_of_a_long_field(bool north, double y)
    {
        var track = APlusAtSouthEnd();
        double heading = north ? 0 : Math.PI;
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = north, HowManyPathsAway = 2 };
        var turn = new YouTurnWorkingState { IsTurnLeft = false };
        // Pass 2's centre: half a tool plus two passes right of the line (heading north).
        double x = 2 * 8 + 4;
        var pos = new Position { Easting = x, Northing = y };

        var result = _creation.CreateTurnPath(pos, track, heading, 0, LongField(), Headland(),
            guidance, turn, uTurnSkipRows: 0, headlandCalculatedWidth: 16, headlandDistance: 16);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "a turn is built");
        Assert.That(result.UsedFallback, Is.False, "the turn generator ran, not the semicircle fallback");
        Assert.That(result.Path!.Max(p => Math.Abs(p.Northing)), Is.LessThan(400 - 1.9), "inside the distance from the boundary");
    }

    [Test]
    public void The_line_is_laid_across_the_field_unchanged()
    {
        var ab = new List<Vec3> { new(0, -384, 0), new(0, -284, 0) };
        var outer = new List<Vec2> { new(-100, -400), new(100, -400), new(100, 400), new(-100, 400) };
        var across = YouTurnCreationService.AbLineAcrossField(ab, outer);
        Assert.That(across[0].Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(across[1].Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(across[0].Northing, Is.LessThan(-400));
        Assert.That(across[1].Northing, Is.GreaterThan(400));
    }
}
