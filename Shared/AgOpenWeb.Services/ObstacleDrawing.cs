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
    /// boundary (a Clipper2 intersection), each piece of at least <see cref="MinArea"/> on its own,
    /// the largest first. An outline wholly inside that does not cross itself comes back as it was
    /// drawn. Drawn round a patch at the field's edge (wet ground at the end of the passes), the
    /// corners can be tapped beyond the fence instead of along it inside, and only the field's part
    /// becomes the obstacle; drawn across a neck of the field, each side is a piece. A piece under
    /// <see cref="MinShareOfLargest"/> of the largest is left out and counted. An outline that
    /// crosses itself is each loop it encloses (non-zero): a figure of eight is two pieces, not one
    /// outline whose two halves cancel out. No pieces when nothing of it is in the field.
    /// </summary>
    public static FieldPart InField(IReadOnlyList<(double E, double N)> corners, IReadOnlyList<(double E, double N)> field)
    {
        var pieces = new List<List<(double E, double N)>>();
        if (corners.Count < 3 || field.Count < 3) return new FieldPart(pieces, 0, 0, 0, false);

        var drawn = ToPath(corners);
        double drawnArea = AreaOf(Clipper.Union(new Paths64 { drawn }, FillRule.NonZero));

        var tree = new PolyTree64();
        var clipper = new Clipper64();
        clipper.AddSubject(new Paths64 { drawn });
        clipper.AddClip(new Paths64 { ToPath(field) });
        clipper.Execute(ClipType.Intersection, FillRule.NonZero, tree);

        // Top-level polygons only: an obstacle has no hole (a loop drawn inside another is in it).
        var found = new List<(double Area, Path64 Ring)>();
        double inField = 0;
        for (int i = 0; i < tree.Count; i++)
        {
            var poly = tree[i].Polygon;
            if (poly == null || poly.Count < 3) continue;
            double area = Math.Abs(Clipper.Area(poly)) / (Scale * Scale);
            inField += area;
            if (area >= MinArea) found.Add((area, poly));
        }

        // Nothing cut off and no loops: the corners as tapped, not Clipper's copy of them.
        bool simple = Math.Abs(Math.Abs(Clipper.Area(drawn)) / (Scale * Scale) - drawnArea) <= AreaTolerance;
        if (simple && found.Count == 1 && Math.Abs(found[0].Area - drawnArea) <= AreaTolerance)
        {
            pieces.Add(new List<(double E, double N)>(corners));
            return new FieldPart(pieces, 0, drawnArea, inField, true);
        }

        found.Sort((a, b) => b.Area.CompareTo(a.Area));
        int leftOut = 0;
        foreach (var f in found)
        {
            if (f.Area < MinShareOfLargest * found[0].Area) { leftOut++; continue; }
            var ring = new List<(double E, double N)>(f.Ring.Count);
            foreach (var p in f.Ring) ring.Add((p.X / Scale, p.Y / Scale));
            pieces.Add(ring);
        }
        return new FieldPart(pieces, leftOut, drawnArea, inField, false);
    }

    // m²: what a millimetre's rounding can change.
    private const double AreaTolerance = 0.01;

    private static Path64 ToPath(IReadOnlyList<(double E, double N)> ring)
    {
        var path = new Path64(ring.Count);
        foreach (var (e, n) in ring) path.Add(new Point64(Math.Round(e * Scale), Math.Round(n * Scale)));
        return path;
    }

    private static double AreaOf(Paths64 paths)
    {
        double a = 0;
        foreach (var p in paths) a += Clipper.Area(p);
        return Math.Abs(a) / (Scale * Scale);
    }
}

/// <summary>What <see cref="ObstacleDrawing.InField"/> made of a drawn outline.</summary>
/// <param name="Pieces">The obstacles: each piece in the field, the largest first.</param>
/// <param name="LeftOut">Slivers left out (under <see cref="ObstacleDrawing.MinShareOfLargest"/> of the largest).</param>
/// <param name="DrawnArea">The outline's own area, m², each loop once.</param>
/// <param name="AreaInField">Its area inside the field, m², every bit counted.</param>
/// <param name="AsDrawn">The one piece is the outline as tapped: wholly inside, not crossing itself.</param>
public readonly record struct FieldPart(
    List<List<(double E, double N)>> Pieces, int LeftOut, double DrawnArea, double AreaInField, bool AsDrawn)
{
    /// <summary>Some of the outline was outside the field and is not in <see cref="Pieces"/>.</summary>
    public bool Cut => AreaInField < DrawnArea - 0.01;
}
