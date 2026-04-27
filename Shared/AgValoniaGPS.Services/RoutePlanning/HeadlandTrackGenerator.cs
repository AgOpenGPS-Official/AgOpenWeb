// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.Geometry;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Generates the headland-track network: one closed-loop polyline per
/// headland pass per boundary. Outer boundary contributes <c>passes</c>
/// concentric loops shrinking inward; each inner ring contributes
/// <c>passes</c> concentric loops growing outward. Loops are returned in CCW
/// order (math convention) so downstream walkers don't have to second-guess
/// orientation.
/// </summary>
public class HeadlandTrackGenerator
{
    private readonly IPolygonOffsetService _offsetService;

    public HeadlandTrackGenerator(IPolygonOffsetService? offsetService = null)
    {
        _offsetService = offsetService ?? new PolygonOffsetService();
    }

    /// <summary>
    /// Build the headland-track network for one field.
    /// </summary>
    /// <param name="outerBoundary">Original outer field boundary (NOT inward-offset).</param>
    /// <param name="innerRings">Original inner-ring obstacles (NOT outward-offset).</param>
    /// <param name="opWidth">Operating width (one pass).</param>
    /// <param name="passes">Number of headland passes per boundary (≥ 1).</param>
    /// <returns>All headland loops, outer first then inner. Empty list if inputs are degenerate.</returns>
    public List<HeadlandLoop> Generate(
        List<Vec2> outerBoundary,
        IReadOnlyList<List<Vec2>> innerRings,
        double opWidth,
        int passes)
    {
        var result = new List<HeadlandLoop>();
        if (outerBoundary == null || outerBoundary.Count < 3 || passes < 1 || opWidth <= 0)
            return result;

        // Outer: pass i (i=0..passes-1) sits at distance (i+0.5)·opWidth INWARD
        // from the outer boundary. Pass 0 = outermost (closest to original outer).
        for (int i = 0; i < passes; i++)
        {
            double dist = (i + 0.5) * opWidth;
            var loop = _offsetService.CreateInwardOffset(outerBoundary, dist);
            if (loop == null || loop.Count < 3) continue;
            result.Add(new HeadlandLoop(EnsureCcw(loop), HeadlandLoopKind.Outer, ringIndex: 0, passIndex: i));
        }

        // Inner: pass i sits at distance (i+0.5)·opWidth OUTWARD from each ring.
        if (innerRings != null)
        {
            for (int r = 0; r < innerRings.Count; r++)
            {
                var ring = innerRings[r];
                if (ring == null || ring.Count < 3) continue;
                for (int i = 0; i < passes; i++)
                {
                    double dist = (i + 0.5) * opWidth;
                    var loop = _offsetService.CreateOutwardOffset(ring, dist);
                    if (loop == null || loop.Count < 3) continue;
                    result.Add(new HeadlandLoop(EnsureCcw(loop), HeadlandLoopKind.Inner, ringIndex: r, passIndex: i));
                }
            }
        }

        return result;
    }

    /// <summary>Reverse the polygon if it's wound CW so the caller always gets CCW.</summary>
    private static List<Vec2> EnsureCcw(List<Vec2> poly)
    {
        // Trapezoidal shoelace = -2 · signedArea. Positive sum here = CW polygon.
        double trap = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            trap += (b.Easting - a.Easting) * (b.Northing + a.Northing);
        }
        if (trap > 0)
        {
            var rev = new List<Vec2>(n);
            for (int i = n - 1; i >= 0; i--) rev.Add(poly[i]);
            return rev;
        }
        return poly;
    }
}
