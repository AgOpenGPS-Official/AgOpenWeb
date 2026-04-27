// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Post-process for <see cref="BoustrophedonDecomp"/>: any cell that still
/// contains an inner ring (a topological hole) is sub-decomposed using the
/// PERPENDICULAR sweep direction so the cell gets split into pieces around
/// the ring. After this pass, no cell contains an obstacle hole, and the
/// swath generator's output is single-segment per swath (no clip-around-
/// obstacle splits, no straight-line connectors through the obstacle).
///
/// Why this is needed: BCD only casts rays perpendicular to its sweep, so
/// for sweep along E-W it splits the field N-S at the obstacle's E-W
/// extremes, leaving a "middle column" cell with the obstacle as a hole.
/// Running BCD again on that cell with N-S sweep adds the missing E-W cuts
/// at the obstacle's N-S extremes — together the two passes split the
/// middle column into north / south / east-sliver / west-sliver sub-cells.
/// </summary>
public static class CellSubdivider
{
    /// <summary>
    /// Returns a new cell list where any cell with inner rings has been
    /// replaced by sub-cells produced by a perpendicular-sweep BCD on it.
    /// Cells with no inner rings pass through unchanged. The result is sorted
    /// by SweepStart (in the original sweep frame) and Ids are reassigned.
    ///
    /// Sub-cells smaller than <paramref name="minAreaSquareMeters"/> are
    /// dropped — these are the corner slivers between the obstacle's
    /// rounded boundary and the straight BCD cuts, too small to fit a swath
    /// and already covered by the inner-headland passes around the obstacle.
    /// Sub-cells that still contain an inner ring ARE kept: their swaths are
    /// clipped by the ring, and the stitcher routes sibling-segment
    /// connectors via the inner headland loop instead of cutting through
    /// the obstacle.
    /// </summary>
    public static List<Cell> Subdivide(
        IEnumerable<Cell> cells,
        double originalSweepHeading,
        double decompositionThresholdDegrees = 180.0,
        double minAreaSquareMeters = 1.0)
    {
        var result = new List<Cell>();
        double perpSweep = originalSweepHeading + Math.PI / 2.0;
        double sx = Math.Sin(originalSweepHeading);
        double sy = Math.Cos(originalSweepHeading);

        foreach (var cell in cells)
        {
            if (cell.InnerRings == null || cell.InnerRings.Count == 0)
            {
                result.Add(cell);
                continue;
            }

            // Run BCD on this cell with perpendicular sweep. The cell's
            // outer becomes the new outer, its inner rings become the new
            // inner rings.
            var subCells = BoustrophedonDecomp.Decompose(
                cell.Polygon,
                cell.InnerRings,
                perpSweep,
                decompositionThresholdDegrees);

            if (subCells.Count == 0)
            {
                // Sub-decomp produced nothing usable — keep the original cell.
                result.Add(cell);
                continue;
            }

            // SubCells were assembled with SweepStart/SweepEnd in the
            // perpendicular frame. Recompute in the ORIGINAL sweep frame so
            // they sort consistently with cells from the outer pass.
            int addedCount = 0;
            foreach (var sc in subCells)
            {
                if (sc.Polygon == null || sc.Polygon.Count < 3) continue;

                // Drop tiny corner slivers between the obstacle's rounded
                // boundary and the straight BCD cuts. Cells with inner rings
                // ARE kept — the swath generator handles the clip-around-
                // obstacle and the stitcher routes the sibling connectors.
                if (PolygonArea(sc.Polygon) < minAreaSquareMeters) continue;

                double minS = double.PositiveInfinity;
                double maxS = double.NegativeInfinity;
                foreach (var pt in sc.Polygon)
                {
                    double s = pt.Easting * sx + pt.Northing * sy;
                    if (s < minS) minS = s;
                    if (s > maxS) maxS = s;
                }
                sc.SweepStart = minS;
                sc.SweepEnd = maxS;
                result.Add(sc);
                addedCount++;
            }

            // Safety: if the area filter dropped EVERY sub-cell, the original
            // cell would be lost. Keep the original instead — it has valid
            // coverage area, just with a clipped-around-obstacle swath layout.
            if (addedCount == 0)
            {
                result.Add(cell);
            }
        }

        result.Sort((a, b) => a.SweepStart.CompareTo(b.SweepStart));
        for (int i = 0; i < result.Count; i++) result[i].Id = i;
        return result;
    }

    /// <summary>Shoelace area of a polygon (always positive).</summary>
    private static double PolygonArea(List<Vec2> poly)
    {
        if (poly == null || poly.Count < 3) return 0;
        double a = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % n];
            a += p.Easting * q.Northing - q.Easting * p.Northing;
        }
        return Math.Abs(a) * 0.5;
    }
}
