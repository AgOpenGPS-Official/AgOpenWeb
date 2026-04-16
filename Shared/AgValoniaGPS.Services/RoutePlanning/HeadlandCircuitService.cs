// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.Geometry;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Generates perimeter passes (headland circuits) for route planning.
/// Outer passes work the field perimeter; inner passes create a buffer around
/// obstacles so swath routes can turn safely at the edge.
/// </summary>
public class HeadlandCircuitService
{
    private readonly IPolygonOffsetService _offsetService;

    public HeadlandCircuitService(IPolygonOffsetService offsetService)
    {
        _offsetService = offsetService;
    }

    /// <summary>
    /// Generate concentric passes following the outer boundary inward.
    /// Pass 0 is the outermost (closest to boundary), each subsequent pass
    /// is one tool-width further inside.
    /// </summary>
    /// <param name="outerBoundary">Outer field boundary.</param>
    /// <param name="toolWidth">Effective tool width (tool width minus overlap).</param>
    /// <param name="passCount">Number of perimeter passes to generate.</param>
    public List<HeadlandCircuitPass> GenerateOuterPasses(
        BoundaryPolygon outerBoundary,
        double toolWidth,
        int passCount)
    {
        var passes = new List<HeadlandCircuitPass>();
        if (outerBoundary.Points.Count < 3 || passCount < 1 || toolWidth <= 0)
            return passes;

        var boundaryPts = outerBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        // Offset each pass by (pass + 0.5) * toolWidth so the tool track sits
        // on the offset line and the outer tool edge touches the boundary on
        // the first pass.
        for (int i = 0; i < passCount; i++)
        {
            double offsetDist = (i + 0.5) * toolWidth;
            var offsetPoly = _offsetService.CreateInwardOffset(boundaryPts, offsetDist);
            if (offsetPoly == null || offsetPoly.Count < 3) break;

            // Close the polygon by repeating the first point so we can walk around
            var closed = new List<Vec2>(offsetPoly) { offsetPoly[0] };
            var pointsWithHeadings = _offsetService.CalculatePointHeadings(closed);

            passes.Add(new HeadlandCircuitPass
            {
                Points = pointsWithHeadings,
                PassNumber = i,
                OffsetDistance = offsetDist,
                IsInnerBoundary = false,
            });
        }

        return passes;
    }

    /// <summary>
    /// Generate a buffer zone around an inner boundary.
    /// Passes expand outward from the obstacle so swath routes can turn
    /// in this zone instead of being truncated mid-field.
    /// </summary>
    /// <param name="innerBoundary">Inner boundary (obstacle) polygon.</param>
    /// <param name="toolWidth">Effective tool width.</param>
    /// <param name="passCount">Number of passes around the obstacle.</param>
    public List<HeadlandCircuitPass> GenerateInnerPasses(
        BoundaryPolygon innerBoundary,
        double toolWidth,
        int passCount)
    {
        var passes = new List<HeadlandCircuitPass>();
        if (innerBoundary.Points.Count < 3 || passCount < 1 || toolWidth <= 0)
            return passes;

        var boundaryPts = innerBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        // Pass 0 hugs the inner boundary at 0.5 * toolWidth out; each subsequent
        // pass is one tool-width further from the obstacle.
        for (int i = 0; i < passCount; i++)
        {
            double offsetDist = (i + 0.5) * toolWidth;
            var offsetPoly = _offsetService.CreateOutwardOffset(boundaryPts, offsetDist);
            if (offsetPoly == null || offsetPoly.Count < 3) break;

            var closed = new List<Vec2>(offsetPoly) { offsetPoly[0] };
            var pointsWithHeadings = _offsetService.CalculatePointHeadings(closed);

            passes.Add(new HeadlandCircuitPass
            {
                Points = pointsWithHeadings,
                PassNumber = i,
                OffsetDistance = offsetDist,
                IsInnerBoundary = true,
            });
        }

        return passes;
    }
}
