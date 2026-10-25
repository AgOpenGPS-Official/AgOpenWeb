// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;

namespace AgOpenWeb.Models;

/// <summary>
/// Represents a single boundary polygon (outer or inner)
/// Points are in local coordinates (meters from field origin)
/// </summary>
public class BoundaryPolygon
{
    /// <summary>
    /// Boundary points (Easting, Northing, Heading in local coordinates)
    /// Minimum 3 points required for valid polygon
    /// </summary>
    public List<BoundaryPoint> Points { get; set; } = new List<BoundaryPoint>();

    // Bounding box, segment grid and point-in-polygon index, built together from Points and
    // published as one immutable object: the GPS cycle and the 100 Hz control tick read the
    // same polygon. Rebuilt when Points is replaced or changes length; call UpdateBounds after
    // moving points in place.
    private const double GRID_CELL_SIZE = 50.0; // meters per cell

    private sealed class Index
    {
        public required List<BoundaryPoint> Source { get; init; }
        public required int Count { get; init; }
        public required PolygonSlabIndex? Slabs { get; init; }
        public required double MinEasting { get; init; }
        public required double MaxEasting { get; init; }
        public required double MinNorthing { get; init; }
        public required double MaxNorthing { get; init; }
        public required Dictionary<(int, int), List<int>> Grid { get; init; } // cell -> segment indices
        public required int GridOffsetE { get; init; } // makes grid indices non-negative
        public required int GridOffsetN { get; init; }
    }

    private Index? _index;

    /// <summary>
    /// Call this after modifying Points in place to rebuild the bounding box and indexes
    /// </summary>
    public void UpdateBounds() => Volatile.Write(ref _index, Build(Points));

    private Index Current()
    {
        var index = Volatile.Read(ref _index);
        var points = Points;
        if (index != null && ReferenceEquals(index.Source, points) && index.Count == points.Count)
            return index;
        index = Build(points);
        Volatile.Write(ref _index, index);
        return index;
    }

    private static Index Build(List<BoundaryPoint> points)
    {
        double minE = 0, maxE = 0, minN = 0, maxN = 0;
        if (points.Count > 0)
        {
            minE = minN = double.MaxValue;
            maxE = maxN = double.MinValue;
            foreach (var pt in points)
            {
                if (pt.Easting < minE) minE = pt.Easting;
                if (pt.Easting > maxE) maxE = pt.Easting;
                if (pt.Northing < minN) minN = pt.Northing;
                if (pt.Northing > maxN) maxN = pt.Northing;
            }
        }

        // Grid-based spatial index for fast segment lookup
        var grid = new Dictionary<(int, int), List<int>>();
        int offsetE = (int)Math.Floor(minE / GRID_CELL_SIZE);
        int offsetN = (int)Math.Floor(minN / GRID_CELL_SIZE);
        for (int i = 0; i < points.Count; i++)
        {
            int j = (i + 1) % points.Count;
            var p1 = points[i];
            var p2 = points[j];

            // Find all cells this segment touches
            int cellMinE = (int)Math.Floor(Math.Min(p1.Easting, p2.Easting) / GRID_CELL_SIZE) - offsetE;
            int cellMaxE = (int)Math.Floor(Math.Max(p1.Easting, p2.Easting) / GRID_CELL_SIZE) - offsetE;
            int cellMinN = (int)Math.Floor(Math.Min(p1.Northing, p2.Northing) / GRID_CELL_SIZE) - offsetN;
            int cellMaxN = (int)Math.Floor(Math.Max(p1.Northing, p2.Northing) / GRID_CELL_SIZE) - offsetN;

            for (int ce = cellMinE; ce <= cellMaxE; ce++)
            {
                for (int cn = cellMinN; cn <= cellMaxN; cn++)
                {
                    if (!grid.TryGetValue((ce, cn), out var list))
                    {
                        list = new List<int>();
                        grid[(ce, cn)] = list;
                    }
                    list.Add(i);
                }
            }
        }

        return new Index
        {
            Source = points,
            Count = points.Count,
            Slabs = PolygonSlabIndex.Build(points),
            MinEasting = minE, MaxEasting = maxE, MinNorthing = minN, MaxNorthing = maxN,
            Grid = grid, GridOffsetE = offsetE, GridOffsetN = offsetN,
        };
    }

    /// <summary>
    /// Get segment indices near a point (from spatial index)
    /// </summary>
    private static IEnumerable<int> GetNearbySegments(Index index, double easting, double northing, double radius)
    {
        // Find cells within radius
        int cellE = (int)Math.Floor(easting / GRID_CELL_SIZE) - index.GridOffsetE;
        int cellN = (int)Math.Floor(northing / GRID_CELL_SIZE) - index.GridOffsetN;
        int cellRadius = (int)Math.Ceiling(radius / GRID_CELL_SIZE);

        var seen = new HashSet<int>();

        for (int ce = cellE - cellRadius; ce <= cellE + cellRadius; ce++)
        {
            for (int cn = cellN - cellRadius; cn <= cellN + cellRadius; cn++)
            {
                if (index.Grid.TryGetValue((ce, cn), out var segments))
                {
                    foreach (int idx in segments)
                    {
                        if (seen.Add(idx)) // Only yield each segment once
                            yield return idx;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check if point is definitely inside the bounding box with margin (potential fast accept)
    /// </summary>
    private static bool IsDeepInsideBounds(Index index, double easting, double northing, double margin) =>
        easting > index.MinEasting + margin && easting < index.MaxEasting - margin &&
        northing > index.MinNorthing + margin && northing < index.MaxNorthing - margin;

    /// <summary>
    /// Whether this is a drive-through boundary (true) or avoid boundary (false)
    /// </summary>
    public bool IsDriveThrough { get; set; } = false;

    /// <summary>
    /// Hard boundary: there are obstacles/objects (fence, building, trees) right
    /// at this edge, so the implement's swept path must not cross it during a
    /// U-turn. Soft boundaries (default) allow the implement to swing close, as
    /// today. Used by the U-turn clearance check to keep the implement clear.
    /// </summary>
    public bool IsHard { get; set; } = false;

    /// <summary>
    /// Area in square meters (calculated from points)
    /// </summary>
    public double AreaSquareMeters
    {
        get
        {
            if (Points.Count < 3) return 0;

            // Shoelace formula for polygon area
            double area = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                int j = (i + 1) % Points.Count;
                area += Points[i].Easting * Points[j].Northing;
                area -= Points[j].Easting * Points[i].Northing;
            }
            return Math.Abs(area) / 2.0;
        }
    }

    /// <summary>
    /// Area in hectares (10,000 square meters = 1 hectare)
    /// </summary>
    public double AreaHectares => AreaSquareMeters / 10000.0;

    /// <summary>
    /// Area in acres (4046.86 square meters = 1 acre)
    /// </summary>
    public double AreaAcres => AreaSquareMeters / 4046.86;

    /// <summary>
    /// Check if this polygon is valid
    /// </summary>
    public bool IsValid => Points.Count >= 3;

    /// <summary>
    /// Check if a point is inside this polygon (the crossing rule of GeometryMath.CrossesSouth,
    /// through the polygon's strip index: a dozen edges instead of all of them)
    /// </summary>
    /// <param name="easting">Point easting coordinate</param>
    /// <param name="northing">Point northing coordinate</param>
    /// <returns>True if point is inside the polygon</returns>
    public bool IsPointInside(double easting, double northing)
    {
        if (Points.Count < 3) return false;
        return Current().Slabs?.Contains(easting, northing) ?? false;
    }

    /// <summary>
    /// Check boundary status for a section segment using coordinate transform method.
    /// More accurate than point-based check as it detects when section edges cross boundary.
    /// </summary>
    /// <param name="sectionCenter">Center point of section in world coords.</param>
    /// <param name="heading">Section heading in radians.</param>
    /// <param name="halfWidth">Half the section width in meters.</param>
    /// <returns>Boundary result with inside percentage and crossing info.</returns>
    public BoundaryResult GetSegmentBoundaryStatus(Vec2 sectionCenter, double heading, double halfWidth)
    {
        if (Points.Count < 3)
            return BoundaryResult.FullyInside; // No boundary = always inside

        // FAST PATH: If section center is deep inside bounding box (50m+ from any edge),
        // skip expensive polygon intersection tests - we're definitely fully inside
        const double DEEP_INSIDE_MARGIN = 50.0; // meters from bounding box edge
        var index = Current();
        if (IsDeepInsideBounds(index, sectionCenter.Easting, sectionCenter.Northing, DEEP_INSIDE_MARGIN + halfWidth))
        {
            return BoundaryResult.FullyInside;
        }

        // Precompute transform
        double cos = Math.Cos(-heading);
        double sin = Math.Sin(-heading);

        // Find where boundary edges cross Y=0 (the section line) in local coords
        var crossings = new List<double>();

        // Use spatial index to only check nearby segments (within search radius)
        double searchRadius = halfWidth + GRID_CELL_SIZE; // Section width + one cell margin

        WorkCounters.PolygonTests++;
        foreach (int i in GetNearbySegments(index, sectionCenter.Easting, sectionCenter.Northing, searchRadius))
        {
            WorkCounters.PolygonEdges++;
            int j = (i + 1) % Points.Count;

            // Transform boundary points to local coordinates
            var p1 = GeometryMath.ToLocalCoords(
                new Vec2(Points[i].Easting, Points[i].Northing),
                sectionCenter, cos, sin);
            var p2 = GeometryMath.ToLocalCoords(
                new Vec2(Points[j].Easting, Points[j].Northing),
                sectionCenter, cos, sin);

            // Find X intercept where edge crosses Y=0
            var intercept = GeometryMath.GetXInterceptAtYZero(p1, p2);
            if (intercept.HasValue)
            {
                // Only count crossings within section width (with margin)
                if (intercept.Value >= -halfWidth - 1.0 && intercept.Value <= halfWidth + 1.0)
                {
                    crossings.Add(intercept.Value);
                }
            }
        }

        // Check endpoints: perpendicular to heading
        double perpHeading = heading + Math.PI / 2.0;
        bool leftEdgeInside = IsPointInside(
            sectionCenter.Easting + Math.Sin(perpHeading) * (-halfWidth),
            sectionCenter.Northing + Math.Cos(perpHeading) * (-halfWidth));
        bool rightEdgeInside = IsPointInside(
            sectionCenter.Easting + Math.Sin(perpHeading) * halfWidth,
            sectionCenter.Northing + Math.Cos(perpHeading) * halfWidth);

        // No crossings - entire segment is either inside or outside
        if (crossings.Count == 0)
        {
            // Check center point to determine
            bool centerInside = IsPointInside(sectionCenter.Easting, sectionCenter.Northing);
            if (centerInside)
                return BoundaryResult.FullyInside;
            else
                return BoundaryResult.FullyOutside;
        }

        // Sort crossings from left to right
        crossings.Sort();

        // Calculate inside percentage by walking along section
        // Start from left edge, toggle inside/outside at each crossing
        double totalWidth = halfWidth * 2;
        double insideLength = 0;

        // Determine starting state (is left edge inside?)
        bool currentlyInside = leftEdgeInside;
        double lastX = -halfWidth;

        foreach (double crossX in crossings)
        {
            // Clip to section bounds
            double clippedX = Math.Max(-halfWidth, Math.Min(halfWidth, crossX));

            if (currentlyInside)
            {
                insideLength += clippedX - lastX;
            }

            lastX = clippedX;
            currentlyInside = !currentlyInside;
        }

        // Handle final segment to right edge
        if (currentlyInside)
        {
            insideLength += halfWidth - lastX;
        }

        double insidePercent = Math.Max(0, Math.Min(1, insideLength / totalWidth));

        return new BoundaryResult(
            IsFullyInside: insidePercent > 0.99,
            IsFullyOutside: insidePercent < 0.01,
            CrossesBoundary: crossings.Count > 0,
            InsidePercent: insidePercent
        );
    }
}

/// <summary>
/// Represents a single point in a boundary polygon
/// Coordinates are in local system (meters from field origin)
/// </summary>
public class BoundaryPoint : IPlanarPoint
{
    /// <summary>
    /// Easting (X coordinate) in meters
    /// </summary>
    public double Easting { get; set; }

    /// <summary>
    /// Northing (Y coordinate) in meters
    /// </summary>
    public double Northing { get; set; }

    /// <summary>
    /// Heading/direction at this point in radians
    /// </summary>
    public double Heading { get; set; }

    public BoundaryPoint() { }

    public BoundaryPoint(double easting, double northing, double heading)
    {
        Easting = easting;
        Northing = northing;
        Heading = heading;
    }
}
