// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services;

/// <summary>
/// The boundary list's edits on the field's obstacles (inner boundaries) that need geometry: which
/// obstacle a tap on the map means, and making the small ones round all at once. The view model
/// loads and saves the field and tells the operator; the page only sends where it was tapped.
/// </summary>
public static class ObstacleEditing
{
    /// <summary>
    /// The obstacle a tap on the map at (<paramref name="easting"/>, <paramref name="northing"/>)
    /// means, as an index in <see cref="Boundary.InnerBoundaries"/>, or -1: the smallest obstacle
    /// that holds the point, else the one whose edge is nearest, within <paramref name="reach"/>
    /// metres. A tree is a few pixels across with the map zoomed out, so the page sends how far a
    /// finger reaches at its zoom. Invalid obstacles (under 3 points) are not in the list and are
    /// left out.
    /// </summary>
    public static int ObstacleAt(Boundary boundary, double easting, double northing, double reach)
    {
        int best = -1;
        bool inside = false;
        double bestArea = double.PositiveInfinity, bestDistance = double.PositiveInfinity;
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid) continue;
            if (inner.IsPointInside(easting, northing))
            {
                double area = inner.AreaSquareMeters;
                if (!inside || area < bestArea) { inside = true; bestArea = area; best = i; }
                continue;
            }
            if (inside) continue;
            double d = DistanceToEdge(inner.Points, easting, northing);
            if (d <= reach && d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    private static double DistanceToEdge(List<BoundaryPoint> ring, double easting, double northing)
    {
        double best = double.PositiveInfinity;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            best = Math.Min(best, GeometryMath.PointToSegmentDistance(easting, northing,
                ring[j].Easting, ring[j].Northing, ring[i].Easting, ring[i].Northing));
        return best;
    }

    /// <summary>
    /// Every obstacle that is not a circle yet becomes the smallest circle that holds it, the
    /// trees drawn by hand. One that is long and thin (its circle more than
    /// <see cref="ObstacleCircle.MaxAreaRatio"/> times its area) or bigger than a tree (its circle
    /// over <see cref="ObstacleCircle.MaxRoundAllDiameter"/>, 10 m) keeps its shape. Returns how
    /// many were made round and how many kept their shape.
    /// </summary>
    public static (int Made, int Kept) MakeObstaclesRound(Boundary boundary)
    {
        int made = 0, kept = 0;
        foreach (var inner in boundary.InnerBoundaries)
        {
            if (!inner.IsValid || inner.Circle != null) continue;
            var circle = ObstacleCircle.Covering(inner.Points);
            double shapeArea = inner.AreaSquareMeters;
            if (!(circle.Diameter <= ObstacleCircle.MaxRoundAllDiameter) || !ObstacleCircle.IsValidDiameter(circle.Diameter)
                || !(circle.Area <= ObstacleCircle.MaxAreaRatio * shapeArea))
            {
                kept++;
                continue;
            }
            inner.SetCircle(circle);
            made++;
        }
        return (made, kept);
    }
}
