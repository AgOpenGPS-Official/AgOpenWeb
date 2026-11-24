// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// A new obstacle drawn on the map ("+ Polygon" in the boundary menu): its corners in the order
    /// tapped, field metres; a repeated tap on a corner is one corner. Only the part inside the field
    /// is kept (<see cref="AgOpenWeb.Services.ObstacleDrawing.InField"/>): a wet patch at the end of
    /// the passes is drawn round from beyond the fence, not along it inside, and each piece inside
    /// becomes an obstacle of its own, but a sliver caught along a fence that bends in and out is left
    /// out (<see cref="AgOpenWeb.Services.ObstacleDrawing.MinShareOfLargest"/>). Refused with no outer
    /// boundary to keep it to, and with nothing of it in the field. Its choice is U-turn (what is drawn
    /// by hand is a ditch, a pond or a patch, not a tree) and it is Hard: the implement is kept out of
    /// it. The row of its (largest) piece is selected.
    /// </summary>
    public void RemoteAddObstacleFromMapPoints(IReadOnlyList<(double E, double N)> points)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;

        var corners = Services.ObstacleDrawing.Corners(points);
        if (corners.Count < 3)
        {
            ReportFailure("An obstacle needs at least three corners");
            return;
        }
        if (boundary.OuterBoundary is not { IsValid: true } outer)
        {
            ReportFailure("Record the field boundary first: an obstacle goes inside it");
            return;
        }

        var field = new List<(double E, double N)>(outer.Points.Count);
        foreach (var p in outer.Points) field.Add((p.Easting, p.Northing));
        var part = Services.ObstacleDrawing.InField(corners, field);
        if (part.DrawnArea < Services.ObstacleDrawing.MinArea)
        {
            ReportFailure("The corners drawn enclose no area");
            return;
        }
        if (part.Pieces.Count == 0)
        {
            ReportFailure(part.AreaInField > 0
                ? "Only a sliver of the obstacle drawn is inside the field"
                : "The obstacle drawn is outside the field");
            return;
        }

        int first = boundary.InnerBoundaries.Count;
        foreach (var piece in part.Pieces) boundary.AddNewObstacle(Outline(piece));
        int last = boundary.InnerBoundaries.Count - 1;
        SaveObstacleChoice(boundary, fieldPath, ListRowOfInner(boundary, first));
        StatusMessage = (first == last ? $"Inner {first + 1}" : $"Inner {first + 1} to {last + 1}")
            + $": drawn with {corners.Count} corners"
            + (part.Cut ? ", kept to the field" : "")
            + (part.Pieces.Count > 1 ? $" in {part.Pieces.Count} pieces" : "")
            + part.LeftOut switch
            {
                0 => "",
                1 => " (a sliver along the fence left out)",
                _ => $" ({part.LeftOut} slivers along the fence left out)",
            };
    }

    private static BoundaryPolygon Outline(IReadOnlyList<(double E, double N)> ring)
    {
        var polygon = new BoundaryPolygon { AtObstacle = ObstacleAction.Turn };
        foreach (var (e, n) in ring) polygon.Points.Add(new BoundaryPoint(e, n, 0));
        polygon.UpdateBounds();
        return polygon;
    }
}
