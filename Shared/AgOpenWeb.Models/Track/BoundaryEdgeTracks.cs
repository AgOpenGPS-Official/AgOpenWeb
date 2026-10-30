// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// "All Edges": one track per boundary edge. The edges run between the corners, the points
/// where the boundary turns sharply. A straight edge becomes an AB line; an edge that bends
/// (a curved field side) becomes an AB curve along the boundary points (#365). Like any line
/// drawn on the fence, the first pass lies half a width inside (<see cref="PassGeometry"/>).
/// </summary>
public static class BoundaryEdgeTracks
{
    /// <summary>Turn that makes a corner: ~20°, measured over <see cref="CornerSpan"/> either
    /// side of a point. Real corners turn ~90°; a smooth curved side turns a few degrees.</summary>
    public const double CornerTurnThreshold = 0.35;

    /// <summary>How far either side of a point its turn is measured. A driven boundary rounds
    /// its corners over several points, each turning only a little; over 6 m the whole corner
    /// counts, while a side curving at a radius above ~35 m stays one edge.</summary>
    public const double CornerSpan = 6.0;

    /// <summary>An edge whose points stray further than this from its straight line (fitted
    /// through them) is curved. Small enough to catch a gently bowed side, large enough to ignore the
    /// wobble of a driven boundary.</summary>
    public const double CurveDeviation = 1.0;

    /// <summary>Edges shorter than this are skipped.</summary>
    public const double MinEdgeLength = 5.0;

    /// <summary>How far an AB line runs past each corner.</summary>
    public const double LineExtension = 50.0;

    /// <summary>The edge tracks of a closed boundary, in boundary order. Empty when fewer
    /// than two corners are found (a round field: use Boundary Curve).</summary>
    public static List<Track> Build(IReadOnlyList<BoundaryPoint> pts)
    {
        var tracks = new List<Track>();
        int n = pts.Count;
        if (n < 3) return tracks;

        // Boundary points are densified along each edge, so one track per point would make
        // dozens on a 4-sided field: find the corners and make one track per edge between them.
        var turns = new double[n];
        for (int i = 0; i < n; i++)
        {
            var cur = pts[i];
            var back = Along(pts, i, -1);
            var ahead = Along(pts, i, 1);
            double hIn = Math.Atan2(cur.Easting - back.Easting, cur.Northing - back.Northing);
            double hOut = Math.Atan2(ahead.Easting - cur.Easting, ahead.Northing - cur.Northing);
            double turn = hOut - hIn;
            while (turn > Math.PI) turn -= 2 * Math.PI;
            while (turn < -Math.PI) turn += 2 * Math.PI;
            turns[i] = Math.Abs(turn);
        }
        // Points next to a corner see it in their span too: one corner per run, at its sharpest.
        var corners = new List<int>();
        int start = Array.FindIndex(turns, t => t <= CornerTurnThreshold);
        if (start < 0) return tracks;
        for (int k = 1, best = -1; k <= n; k++)
        {
            int i = (start + k) % n;
            if (turns[i] > CornerTurnThreshold) { if (best < 0 || turns[i] > turns[best]) best = i; }
            else if (best >= 0) { corners.Add(best); best = -1; }
        }
        corners.Sort();
        if (corners.Count < 2) return tracks;

        for (int k = 0; k < corners.Count; k++)
        {
            int from = corners[k], to = corners[(k + 1) % corners.Count];
            var edge = new List<Vec3>();
            for (int i = from; ; i = (i + 1) % n)
            {
                edge.Add(new Vec3(pts[i].Easting, pts[i].Northing, 0));
                if (i == to) break;
            }

            var pa = edge[0];
            var pb = edge[^1];
            double dx = pb.Easting - pa.Easting, dy = pb.Northing - pa.Northing;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < MinEdgeLength) continue;

            // The edge without its first and last span: a rounded corner bends away from the
            // side there, and would tilt a line or hook a curve's ends.
            var along = new double[edge.Count];
            for (int i = 1; i < edge.Count; i++) along[i] = along[i - 1] + GeometryMath.Distance(edge[i - 1], edge[i]);
            var inner = new List<Vec3>();
            for (int i = 0; i < edge.Count; i++)
                if (along[i] >= CornerSpan && along[^1] - along[i] >= CornerSpan) inner.Add(edge[i]);
            if (inner.Count < 2) inner = edge;

            // The side's own line: the principal axis of its points, pointing from pa to pb.
            double me = 0, mn = 0;
            foreach (var q in inner) { me += q.Easting; mn += q.Northing; }
            me /= inner.Count; mn /= inner.Count;
            double see = 0, snn = 0, sen = 0;
            foreach (var q in inner)
            {
                double de = q.Easting - me, dn = q.Northing - mn;
                see += de * de; snn += dn * dn; sen += de * dn;
            }
            double axis = 0.5 * Math.Atan2(2 * sen, snn - see); // heading convention: atan2(dE, dN)
            double ue = Math.Sin(axis), un = Math.Cos(axis);
            if (ue * dx + un * dy < 0) { ue = -ue; un = -un; }

            double maxDev = 0;
            foreach (var q in inner)
                maxDev = Math.Max(maxDev, Math.Abs((q.Easting - me) * un - (q.Northing - mn) * ue));

            if (maxDev > CurveDeviation)
            {
                // On along the end tangents past the trimmed tips, as far past the corners as an
                // AB line runs, so the ends cross the fence and U-turns anchor there (like the
                // drawn curve tool).
                tracks.Add(new Track
                {
                    Name = $"Edge {tracks.Count + 1} ({along[^1]:F0}m)",
                    Points = CurveProcessing.ExtendCurveEnds(CurveProcessing.CalculateHeadings(inner),
                        LineExtension + CornerSpan),
                    Type = TrackType.Curve,
                    IsVisible = true,
                });
            }
            else
            {
                // From abreast of one corner to abreast of the other, then on past both.
                double ta = (pa.Easting - me) * ue + (pa.Northing - mn) * un - LineExtension;
                double tb = (pb.Easting - me) * ue + (pb.Northing - mn) * un + LineExtension;
                double heading = Math.Atan2(ue, un);
                tracks.Add(new Track
                {
                    Name = $"Edge {tracks.Count + 1} ({chord:F0}m)",
                    Points = new List<Vec3>
                    {
                        new(me + ue * ta, mn + un * ta, heading),
                        new(me + ue * tb, mn + un * tb, heading),
                    },
                    Type = TrackType.ABLine,
                    IsVisible = true,
                });
            }
        }
        return tracks;
    }

    // The boundary point CornerSpan along the ring from point i (dir ±1), or the furthest
    // one before going half way round.
    private static BoundaryPoint Along(IReadOnlyList<BoundaryPoint> pts, int i, int dir)
    {
        int n = pts.Count;
        double dist = 0;
        int j = i;
        for (int step = 0; step < n / 2; step++)
        {
            int next = ((j + dir) % n + n) % n;
            dist += GeometryMath.Distance(pts[j].Easting, pts[j].Northing, pts[next].Easting, pts[next].Northing);
            j = next;
            if (dist >= CornerSpan) break;
        }
        return pts[j];
    }
}
