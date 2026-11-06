// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;

namespace AgOpenWeb.Models;

/// <summary>
/// Represents a field boundary with outer boundary and optional inner boundaries (holes)
/// Matches AgOpenGPS Boundary.txt format
/// </summary>
public class Boundary
{
    /// <summary>
    /// Outer boundary polygon (required)
    /// </summary>
    public BoundaryPolygon? OuterBoundary { get; set; }

    /// <summary>
    /// Inner boundary polygons (holes/exclusions like ponds)
    /// </summary>
    public List<BoundaryPolygon> InnerBoundaries { get; set; } = new List<BoundaryPolygon>();

    /// <summary>
    /// Adds an obstacle made or imported now (drawn, recorded, built from tracks, a circle, or
    /// from KML, ISO-XML or AgShare) as Hard: a U-turn keeps the implement out of it, not only
    /// the tractor. An obstacle read from a file (field.geojson, or an AgOpenGPS Boundary.txt)
    /// keeps the Hard it was saved with.
    /// </summary>
    public void AddNewObstacle(BoundaryPolygon obstacle)
    {
        obstacle.IsHard = true;
        InnerBoundaries.Add(obstacle);
    }

    /// <summary>
    /// Obstacles read from a file with no choice for them (an AgOpenGPS field, or a field saved
    /// before the choice existed) that are not drive-through, so they now get a U-turn before
    /// them, as in AgOpenGPS. The operator is told when the field opens. Not saved.
    /// </summary>
    public int ObstaclesNowUTurn { get; set; }

    /// <summary>
    /// Headland polygon - defines the inner working area boundary.
    /// Points INSIDE this polygon are the work area (sections ON).
    /// Points OUTSIDE (but inside outer boundary) are the headland zone (sections OFF).
    /// This is separate from InnerBoundaries which are holes to avoid.
    /// </summary>
    public BoundaryPolygon? HeadlandPolygon { get; set; }

    /// <summary>
    /// Whether this boundary is turned on for display/guidance
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Total area in hectares (calculated from outer boundary minus inner boundaries)
    /// </summary>
    public double AreaHectares
    {
        get
        {
            double area = OuterBoundary?.AreaHectares ?? 0;
            foreach (var inner in InnerBoundaries)
            {
                area -= inner.AreaHectares;
            }
            return area;
        }
    }

    /// <summary>
    /// Check if this boundary is valid (has a valid outer boundary)
    /// </summary>
    public bool IsValid => OuterBoundary?.IsValid ?? false;

    /// <summary>
    /// Check if a point is inside the boundary area (inside outer, outside inner holes)
    /// AgOpenGPS FenceAreaService.IsPointInsideFenceArea: every inner boundary is out, drive-through
    /// or not. The drive-through flag lets a U-turn pass (CTurn), nothing more; section control,
    /// which uses this, applies nothing on an inner boundary (checklist 4.5).
    /// </summary>
    /// <param name="easting">Point easting coordinate</param>
    /// <param name="northing">Point northing coordinate</param>
    /// <returns>True if point is inside the usable boundary area</returns>
    public bool IsPointInside(double easting, double northing)
    {
        // First check if inside outer boundary
        if (OuterBoundary == null || !OuterBoundary.IsPointInside(easting, northing))
        {
            return false;
        }

        // Check if inside any inner boundary (hole) - if so, it's outside the usable area
        foreach (var innerBoundary in InnerBoundaries)
        {
            if (innerBoundary.IsPointInside(easting, northing))
            {
                return false;
            }
        }

        // Inside outer, outside all inner holes
        return true;
    }

    /// <summary>
    /// Check if a position is inside the boundary
    /// </summary>
    public bool IsPointInside(Position position)
    {
        return IsPointInside(position.Easting, position.Northing);
    }

    /// <summary>
    /// Get segment-based boundary status for a section.
    /// Checks if section segment is inside outer boundary and outside inner holes.
    /// </summary>
    /// <param name="sectionCenter">Center point of section in world coords.</param>
    /// <param name="heading">Section heading in radians.</param>
    /// <param name="halfWidth">Half the section width in meters.</param>
    /// <returns>Boundary result with inside percentage.</returns>
    public BoundaryResult GetSegmentBoundaryStatus(Vec2 sectionCenter, double heading, double halfWidth)
    {
        // Check outer boundary first
        if (OuterBoundary == null || !OuterBoundary.IsValid)
            return BoundaryResult.FullyInside; // No boundary = always in

        var outerResult = OuterBoundary.GetSegmentBoundaryStatus(sectionCenter, heading, halfWidth);

        // If fully outside outer boundary, we're done
        if (outerResult.IsFullyOutside)
            return outerResult;

        // Check inner boundaries (holes) - subtract their inside portions. Drive-through ones
        // too: as in AgOpenGPS, the flag is for U-turns, and nothing is applied on an inner boundary.
        double effectiveInsidePercent = outerResult.InsidePercent;

        foreach (var innerBoundary in InnerBoundaries)
        {
            var innerResult = innerBoundary.GetSegmentBoundaryStatus(sectionCenter, heading, halfWidth);

            // Portion inside inner boundary is "outside" the usable area
            effectiveInsidePercent -= innerResult.InsidePercent;
        }

        effectiveInsidePercent = System.Math.Max(0, effectiveInsidePercent);

        return new BoundaryResult(
            IsFullyInside: effectiveInsidePercent > 0.99,
            IsFullyOutside: effectiveInsidePercent < 0.01,
            CrossesBoundary: outerResult.CrossesBoundary || effectiveInsidePercent != outerResult.InsidePercent,
            InsidePercent: effectiveInsidePercent
        );
    }
}
