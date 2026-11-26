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
/// Onto a pass closer than twice the radius the turn loops, and the loop is moved in to stay
/// inside the turn line (as AgOpenGPS). With a headland narrower than the loop is deep, the turn
/// leaves its pass in the cultivated area with the sections still on: a wedge of the pass end is
/// left unsprayed and the other side sprayed twice (field job scenarios: a 16 m headland, an 8 m
/// radius on 8 m passes). The operator is told, with how far inside the headland line it starts.
/// Rig as <see cref="KeyholeTurnDepthTests"/>: fence at 100 m, turn line at 98 m, 6 m passes,
/// 8 m radius; the Sagitta turn leaves its pass at about 76 m.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HeadlandTooNarrowTests
{
    private const double Width = 6;

    private static Vec3? ArcStart()
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
        var turnLine = Square(98);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 1, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true, IsTurnLeft = false, NextTrackTurnOffset = Width };
        var pos = new Position { Easting = 1.5 * Width, Northing = 60, Heading = 0 };

        var result = creation.CreateTurnPath(
            pos, track, headingRadians: 0, abHeading: 0,
            boundary, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 0, headlandDistance: 0);
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        return result.ArcStart;
    }

    private static List<Vec3> Square(double h) => new() { new(-h, -h, 0), new(h, -h, 0), new(h, h, 0), new(-h, h, 0) };

    [Test]
    public void A_16_m_headland_is_too_narrow_for_the_loop_and_says_how_far_inside_it_starts()
    {
        var arcStart = ArcStart();
        Assert.That(arcStart, Is.Not.Null, "the turn reports where it leaves its pass");
        double inside = YouTurnStateMachine.TurnStartInsideHeadlandLine(arcStart, Square(84)); // 16 m headland
        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.EqualTo(84 - arcStart!.Value.Northing).Within(0.01));
            Assert.That(inside, Is.GreaterThan(6).And.LessThan(10), "the loop leaves the pass about 8 m inside the headland line");
            Assert.That(YouTurnStateMachine.HeadlandTooNarrowMessage(inside),
                Does.StartWith("Headland too narrow for this U-turn: it leaves the pass ").And.Contains(" m before the headland line"));
        });
    }

    [Test]
    public void A_headland_deeper_than_the_loop_says_nothing()
    {
        // A 30 m headland: the line at 70 m, the turn leaving its pass in the headland.
        Assert.That(YouTurnStateMachine.TurnStartInsideHeadlandLine(ArcStart(), Square(70)), Is.Zero);
    }

    [Test]
    public void No_headland_line_says_nothing()
        => Assert.That(YouTurnStateMachine.TurnStartInsideHeadlandLine(new Vec3(0, 0, 0), null), Is.Zero);
}
