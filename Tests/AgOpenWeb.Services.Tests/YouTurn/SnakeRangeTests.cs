// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The snake (ignore worked tracks) plans every pass that lies in the cultivated area anywhere
/// along it, as the normal turn's next-pass check does (#289). It tested each pass at one point,
/// abreast of the middle of A and B: on an L-shaped field the passes of the arm, which don't
/// reach the middle, were left out, and the plan said the field was done with them unworked
/// (review of the U-turn code, 2026-11-03).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeRangeTests
{
    // 6 m passes on an AB line north along x = 0: pass n's centre is at x = 6n + 3.
    private const double Width = 6;

    [Test]
    public void LShapedField_ThePassesOfTheArmArePlanned()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var offset = new PolygonOffsetService();

        // An L: 120 m wide in the south (y -60..-10), 70 m (x -60..10) in the north.
        var outer = new List<Vec2> { new(-60, -60), new(60, -60), new(60, -10), new(10, -10), new(10, 60), new(-60, 60) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var headland = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        var turn = new YouTurnWorkingState { IsEnabled = true };

        pathing.BuildSnakeSequence(track, 0, new GuidanceWorkingState { HowManyPathsAway = -5 }, turn, boundary, headland);

        // Inside the headland the arm spans x -44..44 (y -44..-26): passes -7 (x -39) to 6 (x 39).
        Assert.That(turn.SnakeSequence, Is.SupersetOf(Enumerable.Range(-7, 14)),
            "[" + string.Join(", ", turn.SnakeSequence!) + "]");
    }
}
