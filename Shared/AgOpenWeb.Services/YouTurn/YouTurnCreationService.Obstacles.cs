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
/// obstacle, the same way it turns at the outer boundary's turn line. An obstacle chosen as Go
/// around that the pass steered now has no room to go round (<see cref="Track.GoAroundPasses"/>)
/// is promoted: it gets a turn line too, on this pass. A turn keeps out of the other Go around
/// obstacles as well: the tractor off them by its half track and the margin, the implement off a
/// hard one, since the pass goes round them and a turn through one would not.
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

    // Built once per boundary, choice, distances and promoted set: a turn is planned again every
    // cycle while it fails, and a field may have dozens of obstacles.
    private (Boundary? Boundary, double Distance, double HardExtra, long Key, long Promoted, List<TurnObstacle> List) _turnObstacles =
        (null, 0, 0, 0, 0, new List<TurnObstacle>());

    /// <summary>
    /// How much further a hard obstacle's turn line stands off the obstacle than a soft one's: the
    /// implement's reach beyond the pivot's line, half its width and its offset. The pass itself then
    /// keeps the implement, not only the tractor, the U-turn distance from a wall or a ditch; with a
    /// turn line laid for the tractor alone, a pass beside a ditch ran an 8 m boom a metre into it
    /// with no turn and no alarm. An obstacle the implement may pass over (soft) keeps the tractor's
    /// distance alone, as the field's edge does.
    /// </summary>
    internal static double HardReach(ConfigurationStore config) => config.Tool.Width / 2 + Math.Abs(config.Tool.Offset);

    /// <summary>
    /// How far a turn keeps the tractor's pivot from a Go around obstacle: half its track and the
    /// larger of the Go around margin (with 0.3 m for the rounding of the room) and the distance
    /// from boundary, the room it keeps from a ditch's fence. A turn is driven with a metre or
    /// more of tracking error in a loop (the scenario rig saw 1.3 m beside a tree in the
    /// headland), so the pass's own margin is not enough there.
    /// </summary>
    internal double GoAroundRoom(ConfigurationStore config) =>
        config.Vehicle.TrackWidth / 2 + Math.Max(0.3 + _goAround.SafetyMargin, Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary));

    /// <param name="promoted">Go around obstacles (inner indices) with no room on the pass steered
    /// now: they get a turn line on this pass.</param>
    /// <param name="hardExtra">Added to <paramref name="distance"/> for a hard obstacle (<see cref="HardReach"/>).</param>
    private List<TurnObstacle> TurnObstacles(Boundary boundary, double distance, IReadOnlyList<int>? promoted = null, double hardExtra = 0)
    {
        if (boundary.InnerBoundaries.Count == 0) return EmptyObstacles;
        long key = ObstacleStamp(boundary);
        long promotedKey = 0;
        if (promoted != null) foreach (int p in promoted) promotedKey = promotedKey * 31 + p + 1;
        var cached = _turnObstacles;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Distance == distance && cached.HardExtra == hardExtra && cached.Key == key
            && cached.Promoted == promotedKey)
            return cached.List;

        var list = new List<TurnObstacle>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid) continue;
            if (inner.AtObstacle != ObstacleAction.Turn && !(promoted != null && promoted.Contains(i))) continue;

            var fence = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            // > 0: the turn stays that far from the obstacle; < 0: it may reach that far into it.
            double off = distance + (inner.IsHard ? hardExtra : 0);
            List<Vec2>? line = off > 0.1 ? _polygonOffsetService.CreateOutwardOffset(fence, off)
                : off < -0.1 ? _polygonOffsetService.CreateInwardOffset(fence, -off)
                : fence;
            if (line == null || line.Count < 3) continue; // shrunk away: nothing left to turn at

            var withHeadings = _polygonOffsetService.CalculatePointHeadings(line);
            var area = PolygonSlabIndex.Build(withHeadings);
            if (area == null) continue;
            double tightDistance = off - ArcTolerance;
            List<Vec2>? tightLine = tightDistance > 0.1 ? _polygonOffsetService.CreateOutwardOffset(fence, tightDistance)
                : tightDistance < -0.1 ? _polygonOffsetService.CreateInwardOffset(fence, -tightDistance)
                : fence;
            var tight = (tightLine != null && tightLine.Count >= 3
                ? PolygonSlabIndex.Build(_polygonOffsetService.CalculatePointHeadings(tightLine)) : null) ?? area;
            list.Add(new TurnObstacle(i, withHeadings, area, fence, inner.IsHard, tight));
        }

        _turnObstacles = (boundary, distance, hardExtra, key, promotedKey, list);
        return list;
    }

    /// <summary>A Go around obstacle a turn must keep out of: the tractor outside the obstacle grown
    /// by half its track and the margin (<see cref="Clear"/>), the implement off a hard one.</summary>
    private sealed record RoundObstacle(int InnerIndex, List<Vec2> Fence, bool IsHard, PolygonSlabIndex Clear);

    private (Boundary? Boundary, double Grow, long Key, List<RoundObstacle> List) _roundObstacles = (null, 0, 0, new());

    private List<RoundObstacle> RoundObstacles(Boundary boundary, double grow)
    {
        if (boundary.InnerBoundaries.Count == 0) return EmptyRound;
        long key = ObstacleStamp(boundary);
        var cached = _roundObstacles;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Grow == grow && cached.Key == key) return cached.List;
        var list = new List<RoundObstacle>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.GoRound) continue;
            var fence = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            var grown = grow > 0.05 ? _polygonOffsetService.CreateOutwardOffset(fence, grow) : fence;
            if (grown == null || grown.Count < 3) continue;
            var area = PolygonSlabIndex.Build(_polygonOffsetService.CalculatePointHeadings(grown));
            if (area == null) continue;
            list.Add(new RoundObstacle(i, fence, inner.IsHard, area));
        }
        _roundObstacles = (boundary, grow, key, list);
        return list;
    }

    private static readonly List<RoundObstacle> EmptyRound = new();

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
    public int TurnObstacleAhead(Boundary? boundary, double easting, double northing, double heading, double distance,
        IReadOnlyList<int>? promoted = null)
    {
        if (boundary == null || boundary.InnerBoundaries.Count == 0) return -1;
        var obstacles = TurnObstacles(boundary, _configStore.Guidance.UTurnDistanceFromBoundary, promoted, HardReach(_configStore));
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
    /// past the end of the path, straight on along the next pass, and the implement keeps the
    /// Go around safety margin off every hard fence there: how close the next pass itself runs
    /// to a fence is the pass's business, but a turn never comes out with the implement in a
    /// hard obstacle (an 8 m sprayer turning at a tree beside a ditch did, 0.8 m, on a real field).
    /// The obstacle the turn is made at
    /// (<paramref name="turnedAt"/>, its inner index) is tested with <see cref="ArcTolerance"/>: the
    /// generator lays the arc along its turn line and the smoothing moves it a few centimetres;
    /// the exit leg it does not test, and a pass that grazes the turn line of a tree can have its
    /// neighbour run through the tree. The path also keeps out of every Go around obstacle: the
    /// tractor off it by <see cref="GoAroundRoom"/>, the implement off a hard one by the distance
    /// from boundary; and so does the exit while the rig settles on the next pass (8 m, plus twice
    /// a trailed implement's hitch), the implement by the safety margin: the next pass is bent
    /// round those obstacles, but the tractor comes out of the arc metres off that line, and a
    /// trailed implement swept 0.5 to 1.7 m into trees at the landing on two real fields.
    /// <paramref name="obstacles"/> are the turn obstacles of the plan (with its promoted ones);
    /// without them they are built from <paramref name="promoted"/>.
    /// </summary>
    private bool ClearsTurnObstacles(IReadOnlyList<Vec3> path, Boundary boundary, ConfigurationStore config,
        out int innerIndex, double exitReach = 0, int turnedAt = -1, List<TurnObstacle>? obstacles = null,
        IReadOnlyList<int>? promoted = null)
    {
        innerIndex = -1;
        obstacles ??= TurnObstacles(boundary, config.Guidance.UTurnDistanceFromBoundary, promoted, HardReach(config));
        double goAroundMargin = _goAround.SafetyMargin;
        var round = RoundObstacles(boundary, GoAroundRoom(config));
        if ((obstacles.Count == 0 && round.Count == 0) || path.Count < 2) return true;
        double hitch = config.Tool.IsToolTrailing ? Math.Abs(config.Tool.TrailingHitchLength)
            : config.Tool.IsToolTBT ? Math.Abs(config.Tool.TankTrailingHitchLength) + Math.Abs(config.Tool.TrailingHitchLength) : 0;
        double settle = Math.Min(exitReach, 8 + 2 * hitch);
        var pivot = Densified(path, 0.5).ToList();
        int pathPoints = pivot.Count;
        // Straight on along the next pass past the end of the path.
        var exit = new List<Vec3>();
        if (exitReach > 0)
        {
            Vec3 a = path[^2], b = path[^1];
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing, len = Math.Sqrt(de * de + dn * dn);
            if (len > 1e-6)
                for (double d = 0.5; d <= exitReach; d += 0.5)
                    exit.Add(new Vec3(b.Easting + de / len * d, b.Northing + dn / len * d, b.Heading));
            pivot.AddRange(exit);
        }
        // The implement's sweep over the path and the exit in one go: a trailed implement is
        // still angled when the arc ends.
        ImplementSweptPathResult? swept = null;
        IReadOnlyList<Vec3> sweptPath = exit.Count == 0 ? path : path.Concat(exit).ToList();
        double margin = Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);
        bool ImplementClear(IReadOnlyList<Vec2> fence, double pathMargin, double? exitMargin, double exitLength = double.PositiveInfinity)
        {
            swept ??= ImplementSweptPath.Compute(sweptPath, BuildToolGeometry(config));
            if (!TurnClearance.Evaluate(Corners(swept, 0, path.Count), fence, TurnClearance.KeepSide.Outside, pathMargin).IsClear)
                return false;
            int exitEnd = exitLength >= exitReach ? sweptPath.Count : Math.Min(sweptPath.Count, path.Count + (int)Math.Ceiling(exitLength / 0.5));
            return exitMargin == null || exit.Count == 0
                || TurnClearance.Evaluate(Corners(swept, path.Count, exitEnd), fence, TurnClearance.KeepSide.Outside, exitMargin.Value).IsClear;
        }
        foreach (var o in obstacles)
        {
            var area = o.InnerIndex == turnedAt ? o.Tight : o.Area;
            if (pivot.Any(p => area.Contains(p.Easting, p.Northing)))
            {
                innerIndex = o.InnerIndex;
                return false;
            }
            if (!o.IsHard) continue;
            if (!ImplementClear(o.Fence, margin, goAroundMargin))
            {
                innerIndex = o.InnerIndex;
                return false;
            }
        }
        foreach (var r in round)
        {
            if (obstacles.Any(o => o.InnerIndex == r.InnerIndex)) continue; // promoted: tested above
            int settleEnd = Math.Min(pivot.Count, pathPoints + (int)Math.Ceiling(settle / 0.5));
            for (int i = 0; i < settleEnd; i++)
                if (r.Clear.Contains(pivot[i].Easting, pivot[i].Northing))
                {
                    innerIndex = r.InnerIndex;
                    return false;
                }
            if (!r.IsHard) continue;
            if (!ImplementClear(r.Fence, margin, goAroundMargin, settle))
            {
                innerIndex = r.InnerIndex;
                return false;
            }
        }
        return true;
    }

    // The implement's four corner rails over the points [from, to) of a swept path.
    private static IEnumerable<Vec3> Corners(ImplementSweptPathResult s, int from, int to)
    {
        for (int i = from; i < to && i < s.FrontLeft.Count; i++)
        {
            yield return s.FrontLeft[i];
            yield return s.FrontRight[i];
            yield return s.RearLeft[i];
            yield return s.RearRight[i];
        }
    }

    /// <summary>
    /// The obstacle the rest of a turn being driven runs into, for when the obstacles changed
    /// under the turn: the path from the point nearest the tractor on, and the first metres of the
    /// next pass, tested as at planning (<see cref="ClearsTurnObstacles"/>), <paramref name="turnedAt"/>
    /// the obstacle the turn is made at (its inner index, or -1). Returns the obstacle's index in
    /// the inner boundaries, or -1 when the turn is still clear.
    /// </summary>
    public int RestOfTurnBlockedBy(IReadOnlyList<Vec3> path, Position tractor, Boundary boundary, int turnedAt,
        IReadOnlyList<int>? promoted = null)
    {
        if (path.Count < 2) return -1;
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < path.Count; i++)
        {
            double de = path[i].Easting - tractor.Easting, dn = path[i].Northing - tractor.Northing, d = de * de + dn * dn;
            if (d < best) { best = d; nearest = i; }
        }
        int start = Math.Min(nearest, path.Count - 2);
        IReadOnlyList<Vec3> rest = start == 0 ? path : path.Skip(start).ToList();
        return ClearsTurnObstacles(rest, boundary, _configStore, out int inner, ExitReach, turnedAt, promoted: promoted) ? -1 : inner;
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
