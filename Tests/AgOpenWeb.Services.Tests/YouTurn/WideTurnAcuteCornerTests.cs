// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
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
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The wide turn walks the turn line from where its first arc meets it to where its second arc
/// meets it. Where the field ends in an acute corner (the end of the passes and a side fence
/// slanting in to meet it), the first arc is seated back from the end of the pass and meets the
/// side's turn line, not the end's. The way along the turn line was read from the end's, where the
/// pass crosses it (as AgOpenGPS), and it led into the corner: on a tester's field (25 Nov 2026,
/// 5 rows skipped) the turn ran 27 m on into the tip and straight back. It is walked the shorter way.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class WideTurnAcuteCornerTests
{
    private const double FromBoundary = 2.0;
    private const double Width = 3.0;
    private const double PassX = Width / 2; // pass 0 of an AB line along x = 0

    // A tester's field ("monte", 25 Nov 2026) in the frame of its passes: they run north, 3 m
    // apart, pass 0 along x = 1.5. The end of the field slants across them (y = 121.5 on pass 0),
    // and the west side slants in, 8.5° to the passes, to meet it in a tip at (-11.5, 127.1). The
    // pass 6 over (x = -16.5) ends 40 m before pass 0 does.
    private static List<Vec2> FenceWithTip(bool clockwise)
    {
        var fence = new List<Vec2>
        {
            new(-18.4, 80.7), new(-11.5, 127.1), new(-5.8, 124.1), new(5.5, 120.1), new(17.7, 116.0), new(32.1, 108.8),
            new(46.4, 101.0), new(59.7, 88.2), new(71.9, 69.1), new(76.7, 47.1), new(76.6, 18.1), new(66.6, -27.0),
            new(64.4, -59.6), new(60.0, -200.0), new(-50.0, -200.0), new(-44.2, -88.3), new(-32.2, -12.0), new(-25.3, 34.4),
        };
        if (!clockwise) fence.Reverse();
        return fence;
    }

    private static YouTurnCreationService NewCreation(ListLogger log)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Tool.Width = Width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        c.Guidance.UTurnDistanceFromBoundary = FromBoundary;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 20.0;
        c.Guidance.UTurnStyle = 0;
        return new YouTurnCreationService(log, new PolygonOffsetService(), c);
    }

    /// <summary>Pass 0 (x = 1.5) north from y = 60, 5 rows skipped to the left: the next pass is x = -16.5.</summary>
    private static YouTurnCreationService.TurnPathResult TurnAtTheTip(ListLogger log, bool clockwise)
    {
        const double y = 60;
        var creation = NewCreation(log);
        var fence = FenceWithTip(clockwise);
        var field = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var turnLine = new PolygonOffsetService().CreateInwardOffset(fence, FromBoundary)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -300, 0), new Vec3(0, 300, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        double toEnd = 121.5 - FromBoundary - y;
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = toEnd };
        return creation.CreateTurnPath(new Position { Easting = PassX, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 5, headlandCalculatedWidth: 12, headlandDistance: toEnd);
    }

    private static double Length(IReadOnlyList<Vec3> path)
    {
        double length = 0;
        for (int i = 1; i < path.Count; i++)
            length += GeometryMath.Distance(path[i - 1], path[i]);
        return length;
    }

    [TestCase(false, TestName = "At an acute corner the wide turn walks the turn line towards the next pass (fence anticlockwise)")]
    [TestCase(true, TestName = "At an acute corner the wide turn walks the turn line towards the next pass (fence clockwise)")]
    public void The_wide_turn_walks_the_turn_line_towards_the_next_pass(bool clockwise)
    {
        var log = new ListLogger();
        var turn = TurnAtTheTip(log, clockwise);

        Assert.That(turn.Path, Is.Not.Null, "a turn");
        var path = turn.Path!;
        int leaves = path.FindIndex(p => Math.Abs(p.Easting - PassX) > 0.3);
        Assert.That(leaves, Is.GreaterThan(0), "the scenario: the turn starts on the pass");
        double leavesAt = path[leaves].Northing;
        double furthest = path.Where(p => p.Easting < PassX - 1).Max(p => p.Northing);
        TestContext.Out.WriteLine($"{Length(path):F0} m, leaves the pass at y {leavesAt:F1}, furthest y {furthest:F1}, "
            + $"lands ({path[^1].Easting:F1},{path[^1].Northing:F1})");
        Assert.Multiple(() =>
        {
            // Walked into the tip, the path ran 27 m on along the west side's turn line and came
            // straight back (cumulative turn 489°).
            Assert.That(log.Lines, Has.None.Contains("Service path rejected"), "the wide turn is a U-turn");
            Assert.That(furthest, Is.LessThan(leavesAt + 8 + 1), "nothing of it runs further into the corner than its first arc");
            Assert.That(leavesAt, Is.GreaterThan(90), "it leaves the pass near its end (the end of the crop is at y 108.8)");
            Assert.That(path[^1].Easting, Is.EqualTo(PassX - 6 * Width).Within(0.5), "it lands on the next pass");
            Assert.That(path[^1].Northing, Is.LessThan(leavesAt - 20), "it ends on the way back down the next pass");
            Assert.That(Length(path), Is.LessThan(90), "the shorter way round the turn line (73 m here)");
        });
    }

    private sealed class ListLogger : ILogger<YouTurnCreationService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
