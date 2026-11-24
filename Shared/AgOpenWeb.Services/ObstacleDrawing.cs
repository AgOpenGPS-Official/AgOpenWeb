// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using Clipper2Lib;

namespace AgOpenWeb.Services;

/// <summary>
/// An obstacle drawn on the map with "+ Polygon": the corners tapped, made into the obstacle's
/// outline.
/// </summary>
public static class ObstacleDrawing
{
    /// <summary>Two taps closer than this are one corner (a double tap on the touch screen).</summary>
    public const double CornerMinSpacing = 0.2;

    /// <summary>A piece of the drawing inside the field smaller than this (m²) is no obstacle.</summary>
    public const double MinArea = 1.0;

    /// <summary>
    /// A piece smaller than this share of the largest is left out: a sliver the outline catches
    /// where it is drawn close to a fence that bends in and out (Tapada da Casa's south fence: a
    /// 1.5 ha patch came with slivers of 29 and 13 m² along it, each a Hard obstacle of its own).
    /// </summary>
    public const double MinShareOfLargest = 0.1;

    // Clipper2 works in integers: millimetres.
    private const double Scale = 1000.0;

    /// <summary>
    /// The corners of an obstacle drawn by taps, in order: a tap within <see cref="CornerMinSpacing"/>
    /// of the corner before it is the same corner, and a last tap back on the first corner closes
    /// the outline instead of adding a corner.
    /// </summary>
    public static List<(double E, double N)> Corners(IReadOnlyList<(double E, double N)> taps)
    {
        var corners = new List<(double E, double N)>();
        foreach (var p in taps)
            if (corners.Count == 0 || GeometryMath.Distance(corners[^1].E, corners[^1].N, p.E, p.N) >= CornerMinSpacing)
                corners.Add(p);
        if (corners.Count > 1 && GeometryMath.Distance(corners[0].E, corners[0].N, corners[^1].E, corners[^1].N) < CornerMinSpacing)
            corners.RemoveAt(corners.Count - 1);
        return corners;
    }

    /// <summary>
    /// The part of a drawn outline that lies inside the field: the outline cut by the field's
    /// boundary, each piece of at least <see cref="MinArea"/> on its own, the largest first. An
    /// outline wholly inside comes back as it was drawn. Drawn round a patch at the field's edge
    /// (wet ground at the end of the passes), the corners can be tapped beyond the fence instead
    /// of along it inside, and only the field's part becomes the obstacle; drawn across a neck of
    /// the field, each side is a piece. A piece under <see cref="MinShareOfLargest"/> of the
    /// largest is left out and counted in <paramref name="leftOut"/>. An outline that crosses
    /// itself counts each loop it encloses (non-zero). Empty when nothing of it is in the field.
    /// </summary>
    public static List<List<(double E, double N)>> InField(
        IReadOnlyList<(double E, double N)> corners, IReadOnlyList<(double E, double N)> field, out int leftOut)
    {
        leftOut = 0;
        var pieces = new List<List<(double E, double N)>>();
        if (corners.Count < 3 || field.Count < 3) return pieces;

        var drawn = ToPath(corners);
        var fence = ToPath(field);
        if (AllInside(drawn, fence) && !EdgesCross(corners, field))
        {
            if (Math.Abs(Clipper.Area(drawn)) / (Scale * Scale) >= MinArea) pieces.Add(new List<(double E, double N)>(corners));
            return pieces;
        }

        var tree = new PolyTree64();
        var clipper = new Clipper64();
        clipper.AddSubject(new Paths64 { drawn });
        clipper.AddClip(new Paths64 { fence });
        clipper.Execute(ClipType.Intersection, FillRule.NonZero, tree);

        // Top-level polygons only: an obstacle has no hole (a loop drawn inside another is in it).
        var found = new List<(double Area, List<(double E, double N)> Ring)>();
        for (int i = 0; i < tree.Count; i++)
        {
            var poly = tree[i].Polygon;
            if (poly == null || poly.Count < 3) continue;
            double area = Math.Abs(Clipper.Area(poly)) / (Scale * Scale);
            if (area < MinArea) continue;
            var ring = new List<(double E, double N)>(poly.Count);
            foreach (var p in poly) ring.Add((p.X / Scale, p.Y / Scale));
            found.Add((area, ring));
        }
        found.Sort((a, b) => b.Area.CompareTo(a.Area));
        foreach (var f in found)
        {
            if (f.Area < MinShareOfLargest * found[0].Area) leftOut++;
            else pieces.Add(f.Ring);
        }
        return pieces;
    }

    private static Path64 ToPath(IReadOnlyList<(double E, double N)> ring)
    {
        var path = new Path64(ring.Count);
        foreach (var (e, n) in ring) path.Add(new Point64(Math.Round(e * Scale), Math.Round(n * Scale)));
        return path;
    }

    private static bool AllInside(Path64 drawn, Path64 fence)
    {
        foreach (var p in drawn)
            if (Clipper.PointInPolygon(p, fence) != PointInPolygonResult.IsInside) return false;
        return true;
    }

    // A concave fence can cross an outline whose corners are all inside (a corner of the field
    // reaching into the drawing): then it is cut too.
    private static bool EdgesCross(IReadOnlyList<(double E, double N)> a, IReadOnlyList<(double E, double N)> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            var (a0, a1) = (a[i], a[(i + 1) % a.Count]);
            for (int j = 0; j < b.Count; j++)
                if (SegmentsCross(a0, a1, b[j], b[(j + 1) % b.Count])) return true;
        }
        return false;
    }

    private static bool SegmentsCross((double E, double N) p, (double E, double N) p2, (double E, double N) q, (double E, double N) q2)
    {
        static double Cross((double E, double N) o, (double E, double N) a, (double E, double N) b) =>
            (a.E - o.E) * (b.N - o.N) - (a.N - o.N) * (b.E - o.E);
        double d1 = Cross(q, q2, p), d2 = Cross(q, q2, p2), d3 = Cross(p, p2, q), d4 = Cross(p, p2, q2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
