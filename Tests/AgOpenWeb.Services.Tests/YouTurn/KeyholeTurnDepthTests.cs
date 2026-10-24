// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #418: onto a pass closer than twice the radius the turn loops, and how deep the loop is
/// decides how far before the headland it leaves the pass. Rig from the report: 6 m passes,
/// 8 m radius, 2 m from the fence (turn line at 98 m, fence at 100 m). Sagitta (the default)
/// keeps Brian's one-sided shape; Keyhole centres the loop.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class KeyholeTurnDepthTests
{
    private const double Width = 6;
    private static double Pass(int n) => n * Width + Width / 2;

    private static List<Vec3> Turn(YouTurnType style)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.Wheelbase = 2.5;
        config.Tool.Width = Width;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(Width * 100));
        config.Guidance.UTurnRadius = 8.0;
        config.Guidance.UTurnDistanceFromBoundary = 2;
        config.Guidance.UTurnStyle = (int)style;

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
        var turn = new YouTurnWorkingState { IsEnabled = true, IsTurnLeft = false, NextTrackTurnOffset = Width };
        var pos = new Position { Easting = Pass(1), Northing = 60, Heading = 0 };

        var result = creation.CreateTurnPath(
            pos, track, headingRadians: 0, abHeading: 0,
            boundary, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 0, headlandDistance: 0);
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        return result.Path!;
    }

    [TestCase(YouTurnType.SagittaStyle)]
    [TestCase(YouTurnType.KeyholeStyle)]
    public void Stays_inside_the_turn_line_and_lands_on_the_next_pass(YouTurnType style)
    {
        var path = Turn(style);
        Assert.Multiple(() =>
        {
            Assert.That(path.Max(p => p.Northing), Is.LessThanOrEqualTo(98.05));
            Assert.That(path.Max(p => p.Northing), Is.GreaterThan(96.5), "reaches up to the turn line");
            Assert.That(path[^1].Easting, Is.EqualTo(Pass(2)).Within(0.3));
        });
    }

    private static (double Leave, double Rejoin) LeaveAndRejoin(List<Vec3> path) => (
        path.First(p => System.Math.Abs(p.Easting - Pass(1)) > 0.1).Northing,
        path.Last(p => System.Math.Abs(p.Easting - Pass(2)) > 0.1).Northing);

    [Test]
    public void Sagitta_keeps_its_one_sided_shape()
    {
        // Brian's turn: leaves at 75.0 m, lands on the next pass already heading back at 88.9 m.
        var (leave, rejoin) = LeaveAndRejoin(Turn(YouTurnType.SagittaStyle));
        Assert.Multiple(() =>
        {
            Assert.That(leave, Is.EqualTo(75.0).Within(0.5));
            Assert.That(rejoin, Is.EqualTo(88.9).Within(0.5));
        });
    }

    [Test]
    public void Keyhole_leaves_later_and_rejoins_level()
    {
        // 19.6 m of geometry against 22.8 m, plus what smoothing spreads: leaves at 77.4 m.
        var (leave, rejoin) = LeaveAndRejoin(Turn(YouTurnType.KeyholeStyle));
        Assert.Multiple(() =>
        {
            Assert.That(leave, Is.GreaterThan(76.5));
            Assert.That(rejoin, Is.EqualTo(leave).Within(1.0), "leaves and rejoins level");
        });
    }
}
