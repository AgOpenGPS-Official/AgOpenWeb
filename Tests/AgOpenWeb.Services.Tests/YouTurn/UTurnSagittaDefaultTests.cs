// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
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
/// Locks in the U-turn default = Sagitta (Twol parity). AlbinStyle uses Dubins
/// shortest-path, which loops ("np" shape) whenever the next row is closer than
/// 2× the turn radius — a clean 180° arc of radius R lands 2R sideways, so to
/// reach a nearer row it overshoots and adds a counter-loop. Sagitta is a single
/// offset-semicircle that lands exactly on the row with no loop. Brian removed
/// Dubins from Twol for this reason; these tests guard the default and the
/// loop-free property at the borderline rig (tool == 2R).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class UTurnSagittaDefaultTests
{
    [Test]
    public void DefaultUTurnStyle_IsSagitta()
    {
        var store = new ConfigurationStore();
        Assert.That(store.Guidance.UTurnStyle, Is.EqualTo((int)YouTurnType.SagittaStyle),
            "U-turn style must default to Sagitta — AlbinStyle (Dubins) loops on close rows.");
    }

    [Test]
    public void DefaultTurn_AtBorderlineRig_DoesNotLoop()
    {
        // Tool 16 m, radius 8 m => 2R == tool width: adjacent-row spacing sits right
        // at the Dubins loop threshold (and tips under it with any overlap). This is
        // the exact rig that produced the operator's looping turn on AlbinStyle.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.Wheelbase = 2.5;
        config.Tool.Width = 16;
        config.Tool.Overlap = 0.5;          // tips turnOffset just under 2R
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 1600);
        config.Guidance.UTurnRadius = 8.0;
        config.Guidance.UTurnExtension = 2.0;
        config.Guidance.UTurnSmoothing = 4;
        config.Guidance.UTurnDistanceFromBoundary = 0;
        // NB: do NOT set UTurnStyle — exercise the shipped default.

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
        var headlandLine = new List<Vec3>
        {
            new(-90, -90, 0), new(90, -90, 0), new(90, 90, 0), new(-90, 90, 0),
        };
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));

        double turnOffset = config.ActualToolWidth - config.Tool.Overlap;
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 1, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true, IsTurnLeft = false, NextTrackTurnOffset = turnOffset };
        var pos = new Position { Easting = turnOffset, Northing = 80, Heading = 0 };

        var result = creation.CreateTurnPath(
            pos, track, headingRadians: 0, abHeading: 0,
            boundary, headlandLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10.0, headlandDistance: 5.0);

        Assert.That(result.Path, Is.Not.Null);
        Assert.That(result.Path!.Count, Is.GreaterThan(10));

        double cumDeg = CumulativeTurnDegrees(result.Path);
        // A clean U-turn is ~180°. The Dubins RLR loop is ~320°. Anything past ~230°
        // means the path doubled back on itself — the regression we're guarding.
        Assert.That(cumDeg, Is.LessThan(230.0),
            $"Turn path accumulated {cumDeg:F0}° of turning — that's the Dubins loop, not a clean U-turn.");
        Assert.That(HasSelfIntersection(result.Path), Is.False,
            "Turn path self-intersects — looping turn regression.");
    }

    private static double CumulativeTurnDegrees(List<Vec3> path)
    {
        var dirs = new List<double>();
        for (int i = 1; i < path.Count; i++)
        {
            double de = path[i].Easting - path[i - 1].Easting;
            double dn = path[i].Northing - path[i - 1].Northing;
            if (de * de + dn * dn < 1e-6) continue;
            dirs.Add(Math.Atan2(de, dn));
        }
        double cum = 0;
        for (int i = 1; i < dirs.Count; i++)
        {
            double d = dirs[i] - dirs[i - 1];
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            cum += Math.Abs(d);
        }
        return cum * 180 / Math.PI;
    }

    private static bool HasSelfIntersection(List<Vec3> path)
    {
        for (int i = 0; i < path.Count - 1; i++)
            for (int j = i + 2; j < path.Count - 1; j++)
            {
                if (i == 0 && j == path.Count - 2) continue;
                if (SegmentsIntersect(path[i], path[i + 1], path[j], path[j + 1])) return true;
            }
        return false;
    }

    private static bool SegmentsIntersect(Vec3 p1, Vec3 p2, Vec3 p3, Vec3 p4)
    {
        static double D(Vec3 a, Vec3 b, Vec3 c) =>
            (b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting);
        double d1 = D(p3, p4, p1), d2 = D(p3, p4, p2), d3 = D(p1, p2, p3), d4 = D(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
