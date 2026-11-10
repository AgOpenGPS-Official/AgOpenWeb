// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Contour;

/// <summary>What the contour line ahead of the tractor holds.</summary>
public enum ContourAhead : byte
{
    /// <summary>Nothing in the way: the line can be followed.</summary>
    Clear = 0,
    /// <summary>The line turns back on itself (a hairpin, where the field narrows to less than
    /// two laps' depth): AutoSteer would turn the tractor round.</summary>
    TurnsBack = 1,
    /// <summary>The line runs over ground already sprayed: where there is no room for this lap,
    /// the line one width beside the last lap lands on that lap.</summary>
    OnCoverage = 2,
    /// <summary>The line stops short ahead: beside the last lap there is no room left for this
    /// one (the line is trimmed away where it would come within a width of the last lap's other
    /// side), and past its end AutoSteer has nothing to follow.</summary>
    Ends = 3,
}

/// <summary>
/// Looks along the contour line ahead of the tractor for a stretch it can't drive as a lap,
/// early enough for the operator to take over. Not in AgOpenGPS: its contour follows the last
/// strip wherever it goes, including into a neck of the field with no room for the lap, or round
/// the end of a lap that has narrowed to a thin loop.
/// </summary>
public static class ContourAheadCheck
{
    /// <summary>How far ahead the check looks, at least.</summary>
    public const double MinReachM = 40;
    /// <summary>How far ahead the check looks, at most.</summary>
    public const double MaxReachM = 60;
    /// <summary>Seconds of driving the check looks ahead (between the two reaches).</summary>
    public const double ReachSeconds = 15;
    /// <summary>A turn of more than this...</summary>
    public const double TurnBackDeg = 130;
    /// <summary>...tighter than the rig turns (within the length of line that turn takes at the
    /// U-turn radius, at most this) is a hairpin. Olival's corners, 72° to 105°, are not; nor is
    /// the last lap round a water pivot, a circle the rig can drive.</summary>
    public const double TurnBackWithinM = 30;
    /// <summary>The line's direction is taken over stretches this long, so a short spike in it
    /// (where two pieces of line are joined at a corner) is no turn.</summary>
    public const double ChordM = 5;
    /// <summary>Line under the tractor and the implement: never judged for coverage.</summary>
    public const double SkipNearM = 6;
    /// <summary>Line on sprayed ground at least this long counts.</summary>
    public const double CoveredRunM = 3;

    /// <summary>How far ahead to look at <paramref name="speedKmh"/>.</summary>
    public static double Reach(double speedKmh) =>
        Math.Clamp(Math.Abs(speedKmh) / 3.6 * ReachSeconds, MinReachM, MaxReachM);

    /// <summary>The length of line within which a turn of <see cref="TurnBackDeg"/> is
    /// tighter than <paramref name="turnRadiusM"/> (the rig's U-turn radius); 0 or less: any
    /// within <see cref="TurnBackWithinM"/>.</summary>
    public static double TurnBackWithin(double turnRadiusM) =>
        turnRadiusM <= 0 ? TurnBackWithinM
            : Math.Clamp(TurnBackDeg * Math.PI / 180 * turnRadiusM, 2 * ChordM + 1, TurnBackWithinM);

    /// <summary>
    /// Checks the line ahead of <paramref name="pivot"/> (driven the way of
    /// <paramref name="headingRad"/>) up to <paramref name="reachM"/> along it. Returns what is
    /// there and how far along the line it starts (0 when clear).
    /// </summary>
    public static (ContourAhead What, double AtM) Check(IReadOnlyList<Vec3> line, Vec2 pivot, double headingRad,
        double reachM, Func<double, double, bool>? isCovered, double turnRadiusM = 0)
    {
        double within = TurnBackWithin(turnRadiusM);
        if (line.Count < 3) return (ContourAhead.Clear, 0);

        int at = 0;
        double best = double.MaxValue;
        for (int i = 0; i < line.Count; i++)
        {
            double d = Sq(line[i].Easting - pivot.Easting) + Sq(line[i].Northing - pivot.Northing);
            if (d < best) { best = d; at = i; }
        }
        // The way the tractor drives along the line.
        int j = Math.Min(at + 1, line.Count - 1), k = Math.Max(at - 1, 0);
        int step = (line[j].Easting - line[k].Easting) * Math.Sin(headingRad)
                 + (line[j].Northing - line[k].Northing) * Math.Cos(headingRad) < 0 ? -1 : 1;

        // The line ahead, with its length from the point beside the tractor; far enough past
        // the reach to judge a hairpin that starts at the reach.
        var pts = new List<Vec2>(64) { new(line[at].Easting, line[at].Northing) };
        var path = new List<double>(64) { 0 };
        for (int i = at + step; i >= 0 && i < line.Count && path[^1] < reachM + TurnBackWithinM; i += step)
        {
            var q = new Vec2(line[i].Easting, line[i].Northing);
            double l = Math.Sqrt(Sq(q.Easting - pts[^1].Easting) + Sq(q.Northing - pts[^1].Northing));
            if (l < 0.05) continue;
            pts.Add(q);
            path.Add(path[^1] + l);
        }

        // The nearest of the three.
        var found = (What: ContourAhead.Clear, AtM: double.MaxValue);
        double turnAt = TurnsBackAt(pts, path, reachM, within);
        if (!double.IsNaN(turnAt)) found = (ContourAhead.TurnsBack, turnAt);
        double coverAt = isCovered == null ? double.NaN : CoveredAt(pts, path, reachM, isCovered);
        if (!double.IsNaN(coverAt) && coverAt < found.AtM) found = (ContourAhead.OnCoverage, coverAt);
        if (path[^1] < reachM && path[^1] < found.AtM) found = (ContourAhead.Ends, path[^1]);
        return found.What == ContourAhead.Clear ? (ContourAhead.Clear, 0) : found;
    }

    /// <summary>Where (length along the line, within the reach) the line has turned more than
    /// <see cref="TurnBackDeg"/> from its direction at most <paramref name="within"/> before,
    /// or NaN.</summary>
    private static double TurnsBackAt(List<Vec2> pts, List<double> path, double reachM, double within)
    {
        double end = path[^1];
        if (end < 2 * ChordM) return double.NaN;
        double cosLimit = Math.Cos(TurnBackDeg * Math.PI / 180);
        for (double t = ChordM; t + ChordM <= end + 1e-9 && t <= reachM; t += 1)
        {
            var b = Dir(pts, path, t, t + ChordM);
            for (double s = Math.Max(0, t - (within - ChordM)); s <= t - ChordM; s += 1)
            {
                var a = Dir(pts, path, s, s + ChordM);
                if (a.Easting * b.Easting + a.Northing * b.Northing < cosLimit) return t;
            }
        }
        return double.NaN;
    }

    /// <summary>Where (length along the line) the line first runs <see cref="CoveredRunM"/> or
    /// more over sprayed ground, past <see cref="SkipNearM"/> and within the reach, or NaN.</summary>
    private static double CoveredAt(List<Vec2> pts, List<double> path, double reachM, Func<double, double, bool> isCovered)
    {
        double runStart = double.NaN;
        for (int i = 0; i < pts.Count && path[i] <= reachM + CoveredRunM; i++)
        {
            if (path[i] < SkipNearM) continue;
            if (isCovered(pts[i].Easting, pts[i].Northing))
            {
                if (double.IsNaN(runStart)) runStart = path[i];
                if (path[i] - runStart >= CoveredRunM && runStart <= reachM) return runStart;
            }
            else runStart = double.NaN;
        }
        return double.NaN;
    }

    private static Vec2 PointAt(List<Vec2> pts, List<double> path, double s)
    {
        int i = path.BinarySearch(s);
        if (i >= 0) return pts[i];
        i = ~i;
        if (i <= 0) return pts[0];
        if (i >= pts.Count) return pts[^1];
        double f = (s - path[i - 1]) / (path[i] - path[i - 1]);
        return new Vec2(pts[i - 1].Easting + f * (pts[i].Easting - pts[i - 1].Easting),
                        pts[i - 1].Northing + f * (pts[i].Northing - pts[i - 1].Northing));
    }

    private static Vec2 Dir(List<Vec2> pts, List<double> path, double s0, double s1)
    {
        var a = PointAt(pts, path, s0);
        var b = PointAt(pts, path, s1);
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? new Vec2(0, 0) : new Vec2(dx / l, dy / l);
    }

    private static double Sq(double x) => x * x;
}
