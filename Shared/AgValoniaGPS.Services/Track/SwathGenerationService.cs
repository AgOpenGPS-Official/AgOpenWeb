// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Track;

/// <summary>
/// Generates parallel finite-endpoint swaths clipped to a field boundary.
/// Part of the pre-computed route planning system (#128).
///
/// Algorithm:
/// 1. From the reference AB line, compute heading and perpendicular offset direction
/// 2. Determine how many parallel tracks fit across the boundary
/// 3. For each track: offset from reference, clip to boundary polygon
/// 4. Apply ordering pattern (snake, spiral, boustrophedon)
/// 5. Optionally select "next N" from vehicle position
/// </summary>
public class SwathGenerationService : ISwathGenerationService
{
    public SwathPlan GenerateSwaths(SwathPlanInput input)
    {
        if (input.ReferenceTrack.Points.Count < 2)
            return new SwathPlan();

        double heading = input.ReferenceTrack.Heading;
        double swathWidth = input.ToolWidth - input.Overlap;
        if (swathWidth <= 0)
            return new SwathPlan();

        // Reference point on the AB line (midpoint)
        var refA = input.ReferenceTrack.Points[0];
        var refB = input.ReferenceTrack.Points[^1];
        double refEasting = (refA.Easting + refB.Easting) / 2.0;
        double refNorthing = (refA.Northing + refB.Northing) / 2.0;

        // Perpendicular direction (right of heading)
        double perpE = Math.Cos(heading);   // sin(heading + π/2) = cos(heading)
        double perpN = -Math.Sin(heading);  // cos(heading + π/2) = -sin(heading)

        // Convert boundary to Vec2 list for intersection tests
        var boundaryPoints = input.ClipBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        if (boundaryPoints.Count < 3)
            return new SwathPlan();

        // Determine the range of track offsets that intersect the boundary.
        // Project all boundary points onto the perpendicular axis to find the span.
        double minPerp = double.MaxValue;
        double maxPerp = double.MinValue;
        foreach (var pt in boundaryPoints)
        {
            double proj = (pt.Easting - refEasting) * perpE + (pt.Northing - refNorthing) * perpN;
            minPerp = Math.Min(minPerp, proj);
            maxPerp = Math.Max(maxPerp, proj);
        }

        // Track indices: 0 is the reference track, negative = left, positive = right
        int minIndex = (int)Math.Ceiling(minPerp / swathWidth);
        int maxIndex = (int)Math.Floor(maxPerp / swathWidth);
        int totalTracks = maxIndex - minIndex + 1;

        if (totalTracks <= 0)
            return new SwathPlan { TotalPossibleTracks = 0 };

        // Generate all track indices and apply ordering
        var orderedIndices = SwathOrderingService.GeneratePathSequence(
            minIndex, maxIndex, input.Pattern);

        // Apply skip width
        if (input.SkipWidth > 1)
        {
            orderedIndices = orderedIndices
                .Where((_, i) => i % input.SkipWidth == 0)
                .ToList();
        }

        // For "next N from vehicle": find nearest track and take next N in sequence.
        // Only applies to Boustrophedon — snake and spiral patterns have intentional
        // ordering that shouldn't be overridden by vehicle position.
        if (input.MaxTracks.HasValue && input.VehiclePosition.HasValue
            && input.Pattern == SwathPattern.Boustrophedon)
        {
            var vPos = input.VehiclePosition.Value;
            double vehiclePerp = (vPos.Easting - refEasting) * perpE +
                                 (vPos.Northing - refNorthing) * perpN;
            int nearestIndex = (int)Math.Round(vehiclePerp / swathWidth);

            // Find this index in the ordered sequence
            int seqPos = orderedIndices.IndexOf(nearestIndex);
            if (seqPos < 0)
            {
                // Find closest in sequence
                seqPos = 0;
                double minDist = double.MaxValue;
                for (int i = 0; i < orderedIndices.Count; i++)
                {
                    double dist = Math.Abs(orderedIndices[i] - nearestIndex);
                    if (dist < minDist) { minDist = dist; seqPos = i; }
                }
            }

            orderedIndices = orderedIndices
                .Skip(seqPos)
                .Take(input.MaxTracks.Value)
                .ToList();
        }
        else if (input.MaxTracks.HasValue)
        {
            orderedIndices = orderedIndices
                .Take(input.MaxTracks.Value)
                .ToList();
        }

        // Generate finite tracks by clipping each offset line to the boundary
        var swaths = new List<Models.Track.Track>();
        double totalDistance = 0;

        for (int i = 0; i < orderedIndices.Count; i++)
        {
            int trackIndex = orderedIndices[i];
            double offset = trackIndex * swathWidth;

            // Point on this track's line (offset from reference along perpendicular)
            double lineE = refEasting + offset * perpE;
            double lineN = refNorthing + offset * perpN;

            // Direction along the track
            double dirE = Math.Sin(heading);
            double dirN = Math.Cos(heading);

            // Clip this infinite line to the boundary polygon
            var segments = ClipLineToBoundary(lineE, lineN, dirE, dirN, boundaryPoints);

            foreach (var (startE, startN, endE, endN) in segments)
            {
                double length = Math.Sqrt((endE - startE) * (endE - startE) +
                                          (endN - startN) * (endN - startN));
                if (length < 0.5) continue; // Skip tiny segments

                var track = Models.Track.Track.FromCurve(
                    $"Swath {i + 1}",
                    new List<Vec3>
                    {
                        new Vec3(startE, startN, heading),
                        new Vec3(endE, endN, heading)
                    });
                track.Type = TrackType.Curve; // Finite, not infinite AB line

                swaths.Add(track);
                totalDistance += length;
            }
        }

        return new SwathPlan
        {
            Swaths = swaths,
            TotalPossibleTracks = totalTracks,
            TotalWorkingDistance = totalDistance,
        };
    }

    /// <summary>
    /// Clip an infinite line to a polygon boundary. Returns segments inside the polygon.
    /// Handles concave polygons (may return multiple segments).
    /// </summary>
    internal static List<(double StartE, double StartN, double EndE, double EndN)> ClipLineToBoundary(
        double lineE, double lineN, double dirE, double dirN, List<Vec2> boundary)
    {
        // Find all intersections of the line with the polygon edges.
        // The line is parameterized as: P(t) = (lineE + t*dirE, lineN + t*dirN)
        var intersections = new List<double>(); // parameter t values

        for (int i = 0; i < boundary.Count; i++)
        {
            var segA = boundary[i];
            var segB = boundary[(i + 1) % boundary.Count];

            // Solve for t (line parameter) and s (segment parameter)
            // Line: (lineE + t*dirE, lineN + t*dirN)
            // Segment: segA + s*(segB - segA), s ∈ [0,1]
            double dxSeg = segB.Easting - segA.Easting;
            double dySeg = segB.Northing - segA.Northing;

            double det = dirE * dySeg - dirN * dxSeg;
            if (Math.Abs(det) < 1e-10) continue; // Parallel

            double dx = segA.Easting - lineE;
            double dy = segA.Northing - lineN;

            double t = (dx * dySeg - dy * dxSeg) / det;
            double s = (dx * dirN - dy * dirE) / det;

            if (s >= 0 && s <= 1)
            {
                intersections.Add(t);
            }
        }

        if (intersections.Count < 2)
            return new List<(double, double, double, double)>();

        // Sort by parameter t to get entry/exit pairs
        intersections.Sort();

        // Pair up: entry at index 0, exit at index 1, entry at 2, exit at 3, etc.
        var segments = new List<(double, double, double, double)>();
        for (int i = 0; i + 1 < intersections.Count; i += 2)
        {
            double t0 = intersections[i];
            double t1 = intersections[i + 1];

            segments.Add((
                lineE + t0 * dirE, lineN + t0 * dirN,
                lineE + t1 * dirE, lineN + t1 * dirN));
        }

        return segments;
    }
}
