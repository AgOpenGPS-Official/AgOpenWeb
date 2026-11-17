// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The end of a pass: where it leaves the turn area ahead of the tractor, found once and the same
/// way by everything that needs it (<see cref="PassEnds.Find"/>). <see cref="OutsideIndex"/> is the
/// pass's first point outside the turn area after it was inside, <see cref="InsideIndex"/> the point
/// before it, <see cref="Crossing"/> where the segment between them crosses the turn line, and
/// <see cref="Heading"/> the pass's heading there, in the order of its points.
/// </summary>
public readonly record struct PassEnd(int OutsideIndex, int InsideIndex, Vec2 Crossing, double Heading);

/// <summary>
/// The one definition of where a pass ends, as AgOpenGPS finds it (CYouTurn.FindCurveTurnPoint:
/// walking the pass from the tractor's point, the first point outside the turn line). Walking the
/// pass itself, not a straight line along the heading: on the outside of a bend a straight line
/// leaves the field while the pass runs on (field job J10). The pass must have been inside first:
/// the tractor driving in over the turn line at the start of a job is outside at first, and the
/// crossing under it is where the pass begins, not where it ends (C11, C15, J12, J20). The state
/// machine's distance to the turn, the turn builder's arc start and the fallback turn are all
/// measured from this end.
/// <para>
/// A stretch outside the turn line that stays inside the fence, with the pass back inside the turn
/// line after it, is driven through, not turned at: the end of a pass is the turn line's crossing
/// that the fence's crossing follows. The turn line is the fence moved in by the U-turn distance,
/// a margin for turning, not for driving along an edge: on Olival (field job J19) pass -18 runs
/// along a slanted edge 1.2–2.1 m inside the fence for 26 m, with 180 m of field beyond, and every
/// search took the first point outside the turn line for its end, 49 m into the pass; the turn
/// there landed on a pass 11 m short of its own end, and the job stopped. AgOpenGPS turns at the
/// first point outside the turn line. A stretch that leaves the fence is an end, at the turn
/// line's crossing before it, as at any ordinary end of the field; so is one after which the pass
/// is back inside the turn line for less than <see cref="DriveOnLength"/> (J14).
/// </para>
/// </summary>
public static class PassEnds
{
    /// <summary>
    /// The end of <paramref name="pass"/> ahead of index <paramref name="fromIndex"/>, walking the
    /// points in their order (<paramref name="forward"/>) or against it. Null when the pass does not
    /// leave the turn area ahead (past its last crossing, or never inside). With
    /// <paramref name="insideField"/> (the fence) a stretch outside the turn line but inside the
    /// fence is driven through; without it every point outside the turn line is an end.
    /// </summary>
    public static PassEnd? Find(IReadOnlyList<Vec3> pass, int fromIndex, bool forward,
        Func<Vec3, bool> insideTurnArea, IReadOnlyList<Vec3>? turnLine = null, Func<Vec3, bool>? insideField = null,
        Action<string>? debug = null)
    {
        int step = forward ? 1 : -1;
        bool seenInside = false;
        for (int j = fromIndex; j >= 0 && j < pass.Count; j += step)
        {
            if (insideTurnArea(pass[j])) { seenInside = true; continue; }
            if (!seenInside) continue;
            if (insideField != null)
            {
                var why = IsClip(pass, j, step, insideTurnArea, insideField, out int back);
                if (back != j && RunInside(pass, back, step, insideTurnArea) is var run && run < DriveOnLength)
                {
                    why += FormattableString.Invariant($", but inside it for only {run:F1} m: the end of the pass");
                    back = j;
                }
                debug?.Invoke(FormattableString.Invariant($"point {j} ({pass[j].Easting:F1},{pass[j].Northing:F1}) is outside the turn line: {why}"));
                if (back != j)
                {
                    j = back; // back inside the turn line; the walk goes on from there
                    continue;
                }
            }
            int i = j - step;
            var a = pass[i];
            var b = pass[j];
            var crossing = turnLine != null ? CrossingOf(a, b, turnLine) : null;
            // The pass's heading at the end, in the order of its points.
            var (p, q) = forward ? (a, b) : (b, a);
            double heading = Math.Atan2(q.Easting - p.Easting, q.Northing - p.Northing);
            if (heading < 0) heading += 2 * Math.PI;
            return new PassEnd(j, i, crossing ?? new Vec2((a.Easting + b.Easting) / 2, (a.Northing + b.Northing) / 2), heading);
        }
        return null;
    }

    /// <summary>
    /// How much of the pass must lie inside the turn line after a stretch outside it for the stretch
    /// to be driven through: the landing length (<see cref="YouTurnPathingService.MinLandingLength"/>),
    /// room to work. A pass that dips out over a wiggle of the edge and is back inside for a few
    /// metres before it leaves for good ends at the first crossing: driven through, the end moved
    /// 10 m on into a corner, where the next pass had no room, and the job stopped at its first
    /// turn (field job J14 on Baetas, pass -23: back inside for 4 m, 2026-11-16).
    /// </summary>
    public const double DriveOnLength = YouTurnPathingService.MinLandingLength;

    /// <summary>
    /// Metres of the pass inside the turn area from <paramref name="from"/> on, up to
    /// <see cref="DriveOnLength"/>; a pass whose points end while inside counts as long enough.
    /// </summary>
    private static double RunInside(IReadOnlyList<Vec3> pass, int from, int step, Func<Vec3, bool> insideTurnArea)
    {
        double length = 0;
        for (int k = from; k + step >= 0 && k + step < pass.Count; k += step)
        {
            if (!insideTurnArea(pass[k + step])) return length;
            length += Distance(pass[k], pass[k + step]);
            if (length >= DriveOnLength) return length;
        }
        return DriveOnLength;
    }

    /// <summary>
    /// Whether the stretch outside the turn line from <paramref name="first"/> (the first point
    /// outside) is driven through: back inside the turn line at <paramref name="back"/> with no
    /// point of it outside the fence. Says what it decided, for the debug log.
    /// </summary>
    private static string IsClip(IReadOnlyList<Vec3> pass, int first, int step,
        Func<Vec3, bool> insideTurnArea, Func<Vec3, bool> insideField, out int back)
    {
        back = first;
        double length = 0;
        for (int k = first; k >= 0 && k < pass.Count; k += step)
        {
            if (!insideField(pass[k]))
                return FormattableString.Invariant($"the end of the pass, outside the fence at point {k} ({pass[k].Easting:F1},{pass[k].Northing:F1}), {length:F1} m on");
            if (k != first) length += Distance(pass[k - step], pass[k]);
            if (insideTurnArea(pass[k]))
            {
                back = k;
                return FormattableString.Invariant($"inside the fence, driven through: back inside the turn line at point {k} ({pass[k].Easting:F1},{pass[k].Northing:F1}), {length:F1} m on");
            }
        }
        return "the end of the pass, the pass ends outside the turn line";
    }

    /// <summary>Where the segment a–b crosses <paramref name="turnLine"/> (its closing segment included); null when it doesn't.</summary>
    private static Vec2? CrossingOf(Vec3 a, Vec3 b, IReadOnlyList<Vec3> turnLine)
    {
        int n = turnLine.Count;
        for (int k = 0; k < n; k++)
        {
            var h1 = turnLine[k];
            var h2 = turnLine[(k + 1) % n];
            var at = GeometryMath.TryGetSegmentIntersection(a.Easting, a.Northing, b.Easting, b.Northing,
                h1.Easting, h1.Northing, h2.Easting, h2.Northing);
            if (at.HasValue) return at;
        }
        return null;
    }

    /// <summary>The index of the pass point nearest <paramref name="e"/>, <paramref name="n"/>.</summary>
    public static int NearestIndex(IReadOnlyList<Vec3> pass, double e, double n)
    {
        int near = 0;
        double best = double.MaxValue;
        for (int i = 0; i < pass.Count; i++)
        {
            double de = pass[i].Easting - e, dn = pass[i].Northing - n;
            double d = de * de + dn * dn;
            if (d < best) { best = d; near = i; }
        }
        return near;
    }

    /// <summary>
    /// Metres along the pass from a point abreast of (<paramref name="e"/>, <paramref name="n"/>),
    /// whose nearest pass point is <paramref name="fromIndex"/>, to <paramref name="end"/>'s crossing;
    /// negative past it.
    /// </summary>
    public static double DistanceAlong(IReadOnlyList<Vec3> pass, int fromIndex, bool forward, double e, double n, PassEnd end)
    {
        int step = forward ? 1 : -1;
        double d = 0;
        for (int k = fromIndex; k != end.InsideIndex && k + step >= 0 && k + step < pass.Count; k += step)
            d += Distance(pass[k], pass[k + step]);
        var inside = pass[end.InsideIndex];
        double ce = end.Crossing.Easting - inside.Easting, cn = end.Crossing.Northing - inside.Northing;
        d += Math.Sqrt(ce * ce + cn * cn);
        // Less how far the point is along the pass past its nearest pass point.
        int next = fromIndex + step >= 0 && fromIndex + step < pass.Count ? fromIndex + step : fromIndex - step;
        if (next >= 0 && next < pass.Count && next != fromIndex)
        {
            var p = pass[fromIndex];
            double ue = pass[next].Easting - p.Easting, un = pass[next].Northing - p.Northing;
            double len = Math.Sqrt(ue * ue + un * un);
            if (len > 1e-9)
            {
                double along = ((e - p.Easting) * ue + (n - p.Northing) * un) / len;
                d -= next == fromIndex + step ? along : -along;
            }
        }
        return d;
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double de = a.Easting - b.Easting, dn = a.Northing - b.Northing;
        return Math.Sqrt(de * de + dn * dn);
    }
}
