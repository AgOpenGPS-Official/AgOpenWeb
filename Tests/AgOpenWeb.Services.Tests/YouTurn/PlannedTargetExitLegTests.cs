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
/// #411: the U-turn's exit leg lands on the pass the turn was planned for (its width), not
/// on the one the skip setting would give. Skip-and-fill forces the skip to at least 1 and
/// then plans an adjacent pass; the exit leg went two passes over, outside the fence.
/// Rig from the report: 8 m passes, 8 m radius (an adjacent pass needs the keyhole turn).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class PlannedTargetExitLegTests
{
    private const double Width = 8;
    // The AB line is at the tool's edge, so pass n's centre is n widths plus half a width over.
    private static double Pass(int n) => n * Width + Width / 2;

    private static List<Vec3> Turn(double turnOffset, int uTurnSkipRows)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.Wheelbase = 2.5;
        config.Tool.Width = Width;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(Width * 100));
        config.Guidance.UTurnRadius = 8.0;
        config.Guidance.UTurnDistanceFromBoundary = 2;

        var creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), config);
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<Vec2> { new(-100, -100), new(100, -100), new(100, 100), new(-100, 100) }
                    .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var turnLine = new List<Vec3> { new(-98, -98, 0), new(98, -98, 0), new(98, 98, 0), new(-98, 98, 0) };
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));

        var guidance = new GuidanceWorkingState { HowManyPathsAway = 1, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true, IsTurnLeft = false, NextTrackTurnOffset = turnOffset };
        var pos = new Position { Easting = Pass(1), Northing = 60, Heading = 0 };

        var result = creation.CreateTurnPath(
            pos, track, headingRadians: 0, abHeading: 0,
            boundary, turnLine, guidance, turn,
            uTurnSkipRows, headlandCalculatedWidth: 0, headlandDistance: 0);
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        return result.Path!;
    }

    [Test]
    public void SkipAndFill_AdjacentTarget_ExitsOnTheAdjacentPass()
    {
        // Planned target: one pass over (8 m). The skip setting says 1 (two passes over).
        var path = Turn(turnOffset: Width, uTurnSkipRows: 1);
        Assert.That(path[^1].Easting, Is.EqualTo(Pass(2)).Within(0.5),
            "exit leg on pass 2 (the planned adjacent pass), not pass 3");
    }

    [Test]
    public void Normal_Skip1_StillExitsTwoPassesOver()
    {
        var path = Turn(turnOffset: 2 * Width, uTurnSkipRows: 1);
        Assert.That(path[^1].Easting, Is.EqualTo(Pass(3)).Within(0.5));
    }

    [Test]
    public void Normal_Skip0_ExitsOnTheNextPass()
    {
        var path = Turn(turnOffset: Width, uTurnSkipRows: 0);
        Assert.That(path[^1].Easting, Is.EqualTo(Pass(2)).Within(0.5));
    }
}
