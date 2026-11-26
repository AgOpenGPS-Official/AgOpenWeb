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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #544, second round: the field from the reporter's dump (a 21 m spreader, turn radius 8 m,
/// 10.5 m from the boundary, rows skipped). At the north-east end the wide turn was not built
/// ("phase 11") and the omega, square to the track, was used instead.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class WideTurnOnGraemesFieldTests
{
    private const double Width = 21, Radius = 8, Setback = 10.5;
    private static readonly double H = Math.Atan2(127.388 - 3.918, -28.143 + 178.820); // the AB line's heading

    private static readonly List<Vec2> Outer = new()
    {
        new(-230.31, 57.25), new(-197.93, 24.16), new(-165.55, -8.93), new(-133.17, -42.03), new(-100.80, -75.12), new(-68.42, -108.21),
        new(-36.04, -141.31), new(-3.66, -174.40), new(6.65, -175.48), new(33.67, -142.52), new(60.68, -109.55), new(87.70, -76.58),
        new(114.71, -43.61), new(117.38, -28.47), new(90.51, 3.19), new(63.64, 34.85), new(36.78, 66.51), new(9.91, 98.17),
        new(-16.96, 129.83), new(-38.67, 146.76), new(-58.84, 152.60), new(-90.60, 143.03), new(-125.53, 121.58), new(-160.46, 100.14),
        new(-195.38, 78.70),
    };
    private static readonly List<Vec2> Headland = new()
    {
        new(103.44, -38.44), new(104.58, -31.94), new(-25.31, 121.13), new(-44.23, 135.88), new(-58.77, 140.09),
        new(-85.65, 131.98), new(-211.21, 54.89), new(1.64, -162.66),
    };

    // The two turns from the dumps that fell back to the omega: heading south-west, planned just
    // after the turn at the north-east end, 156 m from the headland (pass -8 to -2 with 5 rows
    // skipped, -10 to -2 with 7, -5 to -15 with 9). Then the other way from those passes, nearer the end, and the
    // north-east end. `along` is metres from the line's A point.
    [TestCase(-8, 5, false, true, 183)]
    [TestCase(-5, 9, false, false, 89)]   // the second dump: -5 to -15, 9 rows skipped
    [TestCase(-10, 7, false, true, 188)]
    [TestCase(-8, 5, false, false, 183)]
    [TestCase(-10, 7, false, false, 188)]
    [TestCase(-8, 5, false, true, 140)]
    [TestCase(-8, 5, false, false, 140)]
    [TestCase(-8, 5, true, true, 60)]
    [TestCase(-8, 5, true, false, 60)]
    [TestCase(-10, 7, true, true, 60)]
    [TestCase(-10, 7, true, false, 60)]
    // #577: heading north-east on pass -6 with 7 or 8 rows skipped. The next pass ends before
    // the tractor's place there, so from 140 m on (80 m with 8) the wide turn failed and the
    // omega began behind the tractor: the turn started at once, or was called missed.
    [TestCase(-6, 7, true, true, 140)]
    [TestCase(-6, 7, true, true, 160)]
    [TestCase(-6, 8, true, true, 80)]
    [TestCase(-6, 8, true, true, 109)]   // the dump's place, 86.6 m from the headland
    public void BothEnds_TheWideTurnIsBuilt(int pass, int skip, bool northEast, bool left, double along)
    {
        var log = new ListLogger();
        var path = Plan(pass, skip, northEast, left, along, log);
        Assert.That(log.Lines.Any(l => l.Contains("Wide turn not built")), Is.False, "the wide turn, not the omega");

        // It keeps its distance from the boundary: it comes to the set distance and never closer.
        double closest = path.Min(DistanceToBoundary);
        Assert.That(closest, Is.EqualTo(Setback).Within(0.8), "touches the turn line");
        Assert.That(path.All(p => InsideOuter(p)), "inside the field");

        // And it lands on the next pass: n + skip + 1 passes over, either side.
        var end = path[^1];
        double offEnd = (end.Easting - 3.918) * Math.Cos(H) - (end.Northing + 178.820) * Math.Sin(H);
        double passes = (offEnd - Width / 2) / Width - pass;
        Assert.That(Math.Abs(passes), Is.EqualTo(skip + 1).Within(0.05), "the next pass");
    }

    private static double DistanceToBoundary(Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < Outer.Count; i++)
        {
            var a = Outer[i]; var b = Outer[(i + 1) % Outer.Count];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double t = Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / (dx * dx + dy * dy), 0, 1);
            double ex = a.Easting + t * dx - p.Easting, ey = a.Northing + t * dy - p.Northing;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    private static bool InsideOuter(Vec3 p)
    {
        bool inside = false;
        for (int i = 0, j = Outer.Count - 1; i < Outer.Count; j = i++)
            if ((Outer[i].Northing > p.Northing) != (Outer[j].Northing > p.Northing)
                && p.Easting < (Outer[j].Easting - Outer[i].Easting) * (p.Northing - Outer[i].Northing) / (Outer[j].Northing - Outer[i].Northing) + Outer[i].Easting)
                inside = !inside;
        return inside;
    }

    private static List<Vec3> Plan(int pass, int skip, bool northEast, bool left, double along, ListLogger log)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        c.Guidance.UTurnRadius = Radius;
        c.Guidance.UTurnExtension = 20;
        c.Guidance.UTurnDistanceFromBoundary = Setback;
        c.Guidance.UTurnStyle = 2;
        c.Guidance.UTurnSmoothing = 14;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(log, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        var dense = Outer;   // as recorded: 25 corners, long straight edges
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = dense.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var headland = offset.CalculatePointHeadings(Headland);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(3.918, -178.820, H), new Vec3(127.388, -28.143, H));

        // The pass's centre line: n passes plus half a tool from the line, to its right.
        double off = pass * Width + Width / 2;
        double rE = Math.Cos(H), rN = -Math.Sin(H), dE = Math.Sin(H), dN = Math.Cos(H);
        double e = 3.918 + off * rE + along * dE, n = -178.820 + off * rN + along * dN;
        double headingDeg = (northEast ? H : H + Math.PI) * 180 / Math.PI;

        var guidance = new GuidanceWorkingState { HowManyPathsAway = pass, IsHeadingSameWay = !northEast };
        var turn = new YouTurnWorkingState { IsEnabled = true, NextUTurnDirectionLeftOverride = left };
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
            track, boundary, headland,
            UTurnSkipRows: skip, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 21.0, HeadlandDistance: 12.0);
        sm.Tick(ctx, guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null.And.Count.GreaterThan(10), "a turn was planned");
        return turn.TurnPath!;
    }

    private static List<Vec2> Densify(List<Vec2> ring, double step)
    {
        var o = new List<Vec2>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double len = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
            int k = Math.Max(1, (int)Math.Ceiling(len / step));
            for (int j = 0; j < k; j++) o.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * j / k, a.Northing + (b.Northing - a.Northing) * j / k));
        }
        return o;
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
