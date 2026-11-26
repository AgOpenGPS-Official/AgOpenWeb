// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Skip mode "Ignore worked tracks" (the snake) plans every pass inside the cultivated area,
/// whichever pass of the line that is. A line drawn on the fence (All Edges, a boundary edge)
/// has pass 0 half a tool in, inside a headland wider than that; the range search stopped
/// there and the plan was pass 0 alone, so no turns were planned.
/// Field: 100 × 200 m, headland line 10 m in (cultivated x from -40 to 40), 6 m passes.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeFenceLineTests
{
    private static List<int> Plan(double lineEasting, int currentPass)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);

        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<BoundaryPoint> { new(-50, -100, 0), new(50, -100, 0), new(50, 100, 0), new(-50, 100, 0) },
            },
        };
        var headland = new List<Vec3> { new(-40, -90, 0), new(40, -90, 0), new(40, 90, 0), new(-40, 90, 0) };
        var track = Models.Track.Track.FromABLine("AB", new Vec3(lineEasting, -100, 0), new Vec3(lineEasting, 100, 0));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = currentPass, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState();

        pathing.BuildSnakeSequence(track, abHeading: 0, guidance, turn, boundary, headland);
        return turn.SnakeSequence!.ToList();
    }

    [Test]
    public void LineOnTheWestFence_PlansEveryPassInside()
    {
        // Pass p's centre is at -47 + 6p: passes 2 (-35) to 14 (37) are inside.
        var plan = Plan(-50, currentPass: 2);
        Assert.Multiple(() =>
        {
            Assert.That(plan.OrderBy(p => p), Is.EqualTo(Enumerable.Range(2, 13)));
            Assert.That(plan[0], Is.EqualTo(2), "starts from the pass the tractor is on");
        });
    }

    [Test]
    public void LineOnTheEastFence_PlansEveryPassInside()
    {
        // Pass p's centre is at 53 + 6p: passes -15 (-37) to -3 (35) are inside.
        Assert.That(Plan(50, currentPass: -3).OrderBy(p => p), Is.EqualTo(Enumerable.Range(-15, 13)));
    }

    [Test]
    public void LineMidField_Unchanged()
    {
        // Pass p's centre is at 3 + 6p: passes -7 (-39) to 6 (39).
        Assert.That(Plan(0, currentPass: 0).OrderBy(p => p), Is.EqualTo(Enumerable.Range(-7, 14)));
    }
}
