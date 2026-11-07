// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    // Two taps closer than this are one corner (a double tap on the touch screen).
    private const double DrawnCornerMinSpacing = 0.2;

    /// <summary>
    /// A new obstacle drawn on the map ("+ Polygon" in the boundary menu): its corners in the
    /// order tapped, field metres. Every corner must be inside the field; a repeated tap on a
    /// corner is one corner. Its choice is U-turn (what is drawn by hand is a ditch, a pond or a
    /// patch, not a tree) and it is Hard: the implement is kept out of it. Its row is selected.
    /// </summary>
    public void RemoteAddObstacleFromMapPoints(IReadOnlyList<(double E, double N)> points)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;

        var corners = new List<(double E, double N)>();
        foreach (var p in points)
            if (corners.Count == 0 || Distance(corners[^1], p) >= DrawnCornerMinSpacing) corners.Add(p);
        if (corners.Count > 1 && Distance(corners[0], corners[^1]) < DrawnCornerMinSpacing) corners.RemoveAt(corners.Count - 1);
        if (corners.Count < 3)
        {
            ReportFailure("An obstacle needs at least three corners");
            return;
        }
        if (boundary.OuterBoundary is { IsValid: true } outer)
            foreach (var (e, n) in corners)
                if (!outer.IsPointInside(e, n))
                {
                    ReportFailure("Every corner of an obstacle must be inside the field");
                    return;
                }

        var polygon = new BoundaryPolygon { AtObstacle = ObstacleAction.Turn };
        foreach (var (e, n) in corners) polygon.Points.Add(new BoundaryPoint(e, n, 0));
        polygon.UpdateBounds();
        if (polygon.AreaSquareMeters < 1.0)
        {
            ReportFailure("The corners drawn enclose no area");
            return;
        }

        boundary.AddNewObstacle(polygon);
        int inner = boundary.InnerBoundaries.Count - 1;
        SaveObstacleChoice(boundary, fieldPath, ListRowOfInner(boundary, inner));
        StatusMessage = $"Inner {inner + 1}: drawn with {corners.Count} corners";
    }

    private static double Distance((double E, double N) a, (double E, double N) b) =>
        Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
}
