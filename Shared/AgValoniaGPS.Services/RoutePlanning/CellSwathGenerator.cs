// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Generates parallel swaths within a single BCD cell. Because cells are
/// monotone with respect to the sweep direction, each swath line crosses the
/// cell boundary exactly twice — no obstacles, no concavities, no splitting.
///
/// Swaths run perpendicular to the sweep direction (i.e., in the AB / swath
/// direction). The cell's sweep extent determines how many swaths fit; each
/// swath's two endpoints come from intersecting a sweep-coordinate constant
/// line with the cell polygon.
/// </summary>
public class CellSwathGenerator
{
    /// <summary>
    /// Generate swaths for a cell. Returns Track objects with two points each
    /// (start and end), in geometric (not boustrophedon) order — the
    /// stitcher decides traversal direction per swath.
    /// </summary>
    /// <param name="cell">Source cell (monotone polygon).</param>
    /// <param name="sweepHeading">Sweep direction in radians. Swath direction is sweep − π/2.</param>
    /// <param name="toolWidth">Tool width in meters.</param>
    /// <param name="overlap">Overlap between swaths in meters (0 if none).</param>
    public List<Models.Track.Track> Generate(Cell cell, double sweepHeading, double toolWidth, double overlap)
    {
        var swaths = new List<Models.Track.Track>();
        if (cell.Polygon.Count < 3) return swaths;

        double effective = toolWidth - overlap;
        if (effective <= 0) effective = 1.0;

        double extent = cell.SweepEnd - cell.SweepStart;
        if (extent < effective) return swaths;

        // Swath direction = perpendicular to sweep, rotated −π/2 (so that for
        // sweep heading π/2 = east, swath heading = 0 = north — the standard
        // case where swaths follow an N-S AB line).
        double swathHeading = sweepHeading - Math.PI / 2.0;

        int count = (int)Math.Floor(extent / effective);
        for (int i = 0; i < count; i++)
        {
            double s = cell.SweepStart + (i + 0.5) * effective;
            var endpoints = ClipSweepLineToPolygon(cell.Polygon, s, sweepHeading);
            if (endpoints.Count < 2) continue;

            var p0 = endpoints[0];
            var p1 = endpoints[endpoints.Count - 1];

            var track = Models.Track.Track.FromCurve(
                $"BCD-{cell.Id}-{i + 1}",
                new List<Vec3>
                {
                    new Vec3(p0.Easting, p0.Northing, swathHeading),
                    new Vec3(p1.Easting, p1.Northing, swathHeading),
                });
            track.Type = Models.Track.TrackType.Curve;
            swaths.Add(track);
        }

        return swaths;
    }

    /// <summary>
    /// Find all polygon-edge intersections with the sweep line at the given
    /// sweep coordinate. Result is sorted along the perpendicular (swath)
    /// direction so endpoints[0] is the "left" (lower perp) end of the swath
    /// and endpoints[^1] is the "right" end.
    /// </summary>
    private static List<Vec2> ClipSweepLineToPolygon(List<Vec2> poly, double sweepCoord, double sweepHeading)
    {
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);
        double px = -sy;
        double py = sx;

        var hits = new List<Vec2>();
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double sa = a.Easting * sx + a.Northing * sy;
            double sb = b.Easting * sx + b.Northing * sy;

            // Half-open interval avoids double-counting a vertex hit by both adjacent edges.
            bool crossesUp = sa <= sweepCoord && sb > sweepCoord;
            bool crossesDown = sb <= sweepCoord && sa > sweepCoord;
            if (!crossesUp && !crossesDown) continue;

            double range = sb - sa;
            if (Math.Abs(range) < 1e-12) continue;
            double t = (sweepCoord - sa) / range;
            hits.Add(new Vec2(
                a.Easting + t * (b.Easting - a.Easting),
                a.Northing + t * (b.Northing - a.Northing)));
        }

        hits.Sort((p, q) =>
            (p.Easting * px + p.Northing * py).CompareTo(q.Easting * px + q.Northing * py));
        return hits;
    }
}
