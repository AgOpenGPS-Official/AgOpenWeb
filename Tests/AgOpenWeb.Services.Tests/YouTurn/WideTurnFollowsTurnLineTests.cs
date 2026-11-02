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
/// #544: rows wider than twice the turn radius get AgOpenGPS's wide turn, which follows the
/// turn line between the passes, so the distance from the boundary holds along the whole turn
/// whatever the headland's angle. The omega turn (one shape square to the track, pushed out
/// until it touches the turn line at one point) started too late on one side of an angled
/// headland and too early on the other: with a 21 m spreader, metres off at a hedge.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class WideTurnFollowsTurnLineTests
{
    private const double Width = 21;       // the spreader in the report
    private const double Radius = 8;       // 21 m rows against a 16 m turn diameter: the wide turn
    private const double Setback = 2;      // U-turn distance from boundary
    private static double PassX(int n) => n * Width + Width / 2;

    // The field: 120 m wide, the south edge straight, the north edge rising from
    // (-60, 40) to (60, 80), 18° off square to the north-south passes.
    private static readonly List<Vec2> Skewed = new() { new(-60, -60), new(60, -60), new(60, 80), new(-60, 40) };
    private static readonly List<Vec2> Square = new() { new(-60, -60), new(60, -60), new(60, 60), new(-60, 60) };

    [Test]
    public void AngledHeadland_BothArcsReachTheTurnLine()
    {
        var bench = new Bench(Skewed);
        var path = bench.PlanFromPass(0, out bool turnLeft);
        int target = turnLeft ? -1 : 1;

        // Each side of the turn comes to the set distance from the boundary: its arc is
        // tangent to the turn line. The omega left one side metres short (here about 7 m
        // over the 21 m between the passes).
        double mid = (PassX(0) + PassX(target)) / 2;
        var inSide = path.Where(p => turnLeft ? p.Easting > mid : p.Easting < mid).ToList();
        var outSide = path.Where(p => turnLeft ? p.Easting < mid : p.Easting > mid).ToList();
        Assert.That(inSide.Min(Bench.DistanceToSkewedNorthEdge), Is.EqualTo(Setback).Within(0.8), "the arc out of the current pass");
        Assert.That(outSide.Min(Bench.DistanceToSkewedNorthEdge), Is.EqualTo(Setback).Within(0.8), "the arc into the next pass");

        // Never closer to the boundary than the setback, and the exit lands on the next pass.
        Assert.That(path.Min(Bench.DistanceToSkewedNorthEdge), Is.GreaterThan(Setback - 0.6));
        Assert.That(path[^1].Easting, Is.EqualTo(PassX(target)).Within(0.5));
        Assert.That(path.All(p => Math.Abs(p.Easting) < 60 && p.Northing > -60), "inside the field");
    }

    [Test]
    public void SquareHeadland_StillAWholeTurn()
    {
        var bench = new Bench(Square);
        var path = bench.PlanFromPass(0, out bool turnLeft);
        int target = turnLeft ? -1 : 1;

        double closest = 60 - path.Max(p => p.Northing);
        Assert.That(closest, Is.EqualTo(Setback).Within(0.8), "tangent to the turn line");
        Assert.That(path[0].Easting, Is.EqualTo(PassX(0)).Within(0.5), "the entry leg is on the current pass");
        Assert.That(path[^1].Easting, Is.EqualTo(PassX(target)).Within(0.5), "the exit leg is on the next pass");
        // Between the arcs the path runs along the turn line: every point on the far side of
        // both passes' apexes sits at the setback.
        var between = path.Where(p => turnLeft ? p.Easting < PassX(0) - Radius && p.Easting > PassX(target) + Radius
                                               : p.Easting > PassX(0) + Radius && p.Easting < PassX(target) - Radius).ToList();
        Assert.That(between, Is.Not.Empty);
        Assert.That(between.All(p => Math.Abs(60 - p.Northing - Setback) < 0.8), "along the turn line");
    }

    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _headland;
        private readonly Models.Track.Track _track;

        public Bench(List<Vec2> outer)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = Radius;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = Setback;
            c.Guidance.UTurnStyle = 0;

            var offset = new PolygonOffsetService();
            var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            _sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            // A recorded boundary has a point every metre or two; the turn line follows it.
            var dense = Densify(outer, 2.0);
            _boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = dense.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _headland = offset.CalculatePointHeadings(offset.CreateInwardOffset(dense, 16.0)!);
            _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        }

        /// <summary>The tractor on pass <paramref name="pass"/> heading north, 20 m south of the origin; the planned path.</summary>
        public List<Vec3> PlanFromPass(int pass, out bool turnLeft)
        {
            var guidance = new GuidanceWorkingState { HowManyPathsAway = pass };
            var turn = new YouTurnWorkingState { IsEnabled = true };
            var ctx = new YouTurnStateMachine.TickContext(
                new Position { Easting = PassX(pass), Northing = -20, Heading = 0, Speed = 2.78 },
                _track, _boundary, _headland,
                UTurnSkipRows: 0, IsSkipWorkedMode: false,
                HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);
            _sm.Tick(ctx, guidance, turn);
            Assert.That(turn.TurnPath, Is.Not.Null.And.Count.GreaterThan(10), "a turn was planned");
            turnLeft = turn.IsTurnLeft;
            return turn.TurnPath!;
        }

        /// <summary>Perpendicular distance from the north edge of the skewed field, positive inside.</summary>
        public static double DistanceToSkewedNorthEdge(Vec3 p)
        {
            // The edge from (-60, 40) to (60, 80): direction (120, 40), inward normal (40, -120)/|..|.
            double nx = 40, ny = -120, len = Math.Sqrt(nx * nx + ny * ny);
            return ((p.Easting + 60) * nx + (p.Northing - 40) * ny) / len;
        }

        private static List<Vec2> Densify(List<Vec2> ring, double spacing)
        {
            var points = new List<Vec2>();
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                double len = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
                int n = Math.Max(1, (int)(len / spacing));
                for (int k = 0; k < n; k++)
                    points.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * k / n, a.Northing + (b.Northing - a.Northing) * k / n));
            }
            return points;
        }
    }
}
