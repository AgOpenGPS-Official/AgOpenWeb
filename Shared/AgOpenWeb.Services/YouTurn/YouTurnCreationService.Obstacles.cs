// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The obstacles the automatic U-turn is made at: every valid inner boundary whose choice is
/// <see cref="ObstacleAction.Turn"/> gets a turn line, the obstacle offset outward by the U-turn
/// distance from boundary, as AgOpenGPS builds a turn line round every inner boundary that is
/// not drive-through (CTurn.BuildTurnLines). A pass that runs into one turns there, before the
/// obstacle, the same way it turns at the outer boundary's turn line.
/// </summary>
public partial class YouTurnCreationService
{
    /// <param name="InnerIndex">The obstacle's index in <see cref="Boundary.InnerBoundaries"/>.</param>
    /// <param name="TurnLine">The turn line, with headings (the generator walks it).</param>
    /// <param name="Area">Inside the turn line: no part of a turn may be there.</param>
    /// <param name="Fence">The obstacle itself, for the hard-boundary clearance.</param>
    /// <param name="Tight">Inside the turn line less <see cref="ArcTolerance"/>: what the turn made
    /// at this obstacle is held to, since the generator lays its arc along the turn line and the
    /// smoothing moves it a few centimetres either way.</param>
    private sealed record TurnObstacle(int InnerIndex, List<Vec3> TurnLine, PolygonSlabIndex Area,
        List<Vec2> Fence, bool IsHard, PolygonSlabIndex Tight);

    private const double ArcTolerance = 0.3;

    // Built once per boundary, choice and distance: a turn is planned again every cycle while it
    // fails, and a field may have dozens of obstacles.
    private (Boundary? Boundary, double Distance, long Key, List<TurnObstacle> List) _turnObstacles =
        (null, 0, 0, new List<TurnObstacle>());

    private List<TurnObstacle> TurnObstacles(Boundary boundary, double distance)
    {
        if (boundary.InnerBoundaries.Count == 0) return EmptyObstacles;
        long key = ObstacleStamp(boundary);
        var cached = _turnObstacles;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Distance == distance && cached.Key == key)
            return cached.List;

        var list = new List<TurnObstacle>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.Turn) continue;

            var fence = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            // > 0: the turn stays that far from the obstacle; < 0: it may reach that far into it.
            List<Vec2>? line = distance > 0.1 ? _polygonOffsetService.CreateOutwardOffset(fence, distance)
                : distance < -0.1 ? _polygonOffsetService.CreateInwardOffset(fence, -distance)
                : fence;
            if (line == null || line.Count < 3) continue; // shrunk away: nothing left to turn at

            var withHeadings = _polygonOffsetService.CalculatePointHeadings(line);
            var area = PolygonSlabIndex.Build(withHeadings);
            if (area == null) continue;
            double tightDistance = distance - ArcTolerance;
            List<Vec2>? tightLine = tightDistance > 0.1 ? _polygonOffsetService.CreateOutwardOffset(fence, tightDistance)
                : tightDistance < -0.1 ? _polygonOffsetService.CreateInwardOffset(fence, -tightDistance)
                : fence;
            var tight = (tightLine != null && tightLine.Count >= 3
                ? PolygonSlabIndex.Build(_polygonOffsetService.CalculatePointHeadings(tightLine)) : null) ?? area;
            list.Add(new TurnObstacle(i, withHeadings, area, fence, inner.IsHard, tight));
        }

        _turnObstacles = (boundary, distance, key, list);
        return list;
    }

    private static readonly List<TurnObstacle> EmptyObstacles = new();

    /// <summary>
    /// A fingerprint of the field's obstacles as a turn sees them: how many there are, each one's
    /// choice, Hard and outline (its point count and two of its points, so a circle that is moved
    /// or resized counts too). A planned turn is planned again when it changes, and the obstacle
    /// turn lines are built again. Every cycle while a turn is planned, so it reads two points of
    /// each obstacle, not its outline.
    /// </summary>
    public static long ObstacleStamp(Boundary? boundary)
    {
        if (boundary == null) return 0;
        long h = boundary.InnerBoundaries.Count;
        foreach (var inner in boundary.InnerBoundaries)
        {
            var pts = inner.Points;
            h = ((h * 31 + pts.Count) * 31 + (int)inner.AtObstacle) * 31 + (inner.IsHard ? 1 : 0);
            if (pts.Count == 0) continue;
            var a = pts[0];
            var b = pts[pts.Count / 2];
            h = h * 31 + Cm(a.Easting);
            h = h * 31 + Cm(a.Northing);
            h = h * 31 + Cm(b.Easting);
            h = h * 31 + Cm(b.Northing);
        }
        return h;
    }

    private static long Cm(double metres) => (long)Math.Round(metres * 100);

    /// <summary>
    /// The turn area test with the obstacles, AgOpenGPS's IsPointInsideTurnArea: -1 outside the
    /// outer turn line, 0 in the workable area, k inside obstacle <paramref name="obstacles"/>[k - 1]'s
    /// turn line (turn line k of the input, the outer one being 0).
    /// </summary>
    private static Func<Vec3, int> TurnAreaTest(PolygonSlabIndex? outerArea, List<TurnObstacle> obstacles)
    {
        if (obstacles.Count == 0)
            return point => outerArea != null && outerArea.Contains(point.Easting, point.Northing) ? 0 : -1;
        return point =>
        {
            if (outerArea == null || !outerArea.Contains(point.Easting, point.Northing)) return -1;
            for (int k = 0; k < obstacles.Count; k++)
                if (obstacles[k].Area.Contains(point.Easting, point.Northing)) return k + 1;
            return 0;
        };
    }

    /// <summary>
    /// The first obstacle chosen as U-turn (its index in <see cref="Boundary.InnerBoundaries"/>)
    /// whose turn line is within <paramref name="distance"/> straight ahead of the tractor, or
    /// -1. When the tractor is already inside a turn line, that obstacle counts only if the
    /// obstacle itself is ahead.
    /// </summary>
    public int TurnObstacleAhead(Boundary? boundary, double easting, double northing, double heading, double distance)
    {
        if (boundary == null || boundary.InnerBoundaries.Count == 0) return -1;
        var obstacles = TurnObstacles(boundary, _configStore.Guidance.UTurnDistanceFromBoundary);
        if (obstacles.Count == 0) return -1;
        double se = Math.Sin(heading), ce = Math.Cos(heading);
        int steps = Math.Max(1, (int)Math.Ceiling(distance / 0.5));
        for (int k = 0; k <= steps; k++)
        {
            double t = distance * k / steps, e = easting + se * t, n = northing + ce * t;
            foreach (var o in obstacles)
            {
                if (!o.Area.Contains(e, n)) continue;
                if (!o.Area.Contains(easting, northing)) return o.InnerIndex;
                if (GeometryMath.IsPointInPolygon(o.Fence, new Vec2(e, n))) return o.InnerIndex;
            }
        }
        return -1;
    }

    /// <summary>
    /// How far past the end of an automatic turn the next pass must be clear of every turn line
    /// (<see cref="ClearsTurnObstacles"/>): the pipeline takes AutoSteer off with an obstacle
    /// 10 m plus 2 s of travel ahead and no turn before it, 18 m at 15 km/h, so a turn whose
    /// exit lands closer than that to the next obstacle ends in that alarm with no room to stop.
    /// </summary>
    private const double ExitReach = 20.0;

    /// <summary>
    /// Whether a turn path keeps out of every obstacle chosen as U-turn: the tractor outside its
    /// turn line, and on a hard one the implement outside it by the distance from boundary. With
    /// <paramref name="exitReach"/>, the tractor also stays outside every turn line for that far
    /// past the end of the path, straight on along the next pass. The obstacle the turn is made at
    /// (<paramref name="turnedAt"/>, its inner index) is tested with <see cref="ArcTolerance"/>: the
    /// generator lays the arc along its turn line and the smoothing moves it a few centimetres;
    /// the exit leg it does not test, and a pass that grazes the turn line of a tree can have its
    /// neighbour run through the tree.
    /// </summary>
    private bool ClearsTurnObstacles(IReadOnlyList<Vec3> path, Boundary boundary, ConfigurationStore config,
        out int innerIndex, double exitReach = 0, int turnedAt = -1)
    {
        innerIndex = -1;
        var obstacles = TurnObstacles(boundary, config.Guidance.UTurnDistanceFromBoundary);
        if (obstacles.Count == 0 || path.Count < 2) return true;
        var pivot = Densified(path, 0.5).ToList();
        if (exitReach > 0)
        {
            Vec3 a = path[^2], b = path[^1];
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing, len = Math.Sqrt(de * de + dn * dn);
            if (len > 1e-6)
                for (double d = 0.5; d <= exitReach; d += 0.5)
                    pivot.Add(new Vec3(b.Easting + de / len * d, b.Northing + dn / len * d, b.Heading));
        }
        ImplementSweptPathResult? swept = null;
        double margin = Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);
        foreach (var o in obstacles)
        {
            var area = o.InnerIndex == turnedAt ? o.Tight : o.Area;
            if (pivot.Any(p => area.Contains(p.Easting, p.Northing)))
            {
                innerIndex = o.InnerIndex;
                return false;
            }
            if (!o.IsHard) continue;
            swept ??= ImplementSweptPath.Compute(path, BuildToolGeometry(config));
            if (!TurnClearance.Evaluate(swept, o.Fence, TurnClearance.KeepSide.Outside, margin).IsClear)
            {
                innerIndex = o.InnerIndex;
                return false;
            }
        }
        return true;
    }

    // The path's points with more between any two further apart than the step, so a turn line
    // is not stepped over.
    private static IEnumerable<Vec3> Densified(IReadOnlyList<Vec3> path, double step)
    {
        yield return path[0];
        for (int i = 1; i < path.Count; i++)
        {
            Vec3 a = path[i - 1], b = path[i];
            double d = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
            int n = (int)Math.Ceiling(d / step);
            for (int k = 1; k <= n; k++)
                yield return new Vec3(a.Easting + (b.Easting - a.Easting) * k / n, a.Northing + (b.Northing - a.Northing) * k / n, b.Heading);
        }
    }

    /// <summary>
    /// The obstacle the current pass runs into before anything else, walking it from the tractor
    /// the way the generator looks for the turn (FindCurveTurnPoint), or null when the first
    /// thing ahead is the outer turn line or nothing.
    /// </summary>
    private static TurnObstacle? ObstacleAhead(YouTurnCreationInput input, List<TurnObstacle> obstacles)
    {
        if (obstacles.Count == 0) return null;
        int step = input.IsHeadingSameWay ? 1 : -1;
        var points = input.GuidancePoints;
        for (int j = input.CurrentLocationIndex; j > 0 && j < points.Count; j += step)
        {
            int area = input.IsPointInsideTurnArea(points[j]);
            if (area == 0) continue;
            return area > 0 && area <= obstacles.Count ? obstacles[area - 1] : null;
        }
        return null;
    }
}
