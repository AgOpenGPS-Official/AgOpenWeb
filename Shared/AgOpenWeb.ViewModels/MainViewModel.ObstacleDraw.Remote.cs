// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// A new obstacle drawn on the map ("+ Polygon" in the boundary menu): its corners in the
    /// order tapped, field metres; a repeated tap on a corner is one corner. Only the part inside
    /// the field is kept (<see cref="AgOpenWeb.Services.ObstacleDrawing.InField"/>): a wet patch at the end of the
    /// passes is drawn round from beyond the fence, not along it inside, and each piece inside
    /// becomes an obstacle of its own, but a sliver caught along a fence that bends in and out is
    /// left out (<see cref="AgOpenWeb.Services.ObstacleDrawing.MinShareOfLargest"/>). Nothing of it in the field is
    /// refused. Its choice is U-turn (what is drawn by hand is a ditch, a pond or a patch, not a
    /// tree) and it is Hard: the implement is kept out of it. The row of its (largest) piece is
    /// selected.
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
        if (Outline(corners).AreaSquareMeters < Services.ObstacleDrawing.MinArea)
        {
            ReportFailure("The corners drawn enclose no area");
            return;
        }

        var pieces = new List<List<(double E, double N)>> { corners };
        bool cut = false;
        int leftOut = 0;
        if (boundary.OuterBoundary is { IsValid: true } outer)
        {
            var field = new List<(double E, double N)>(outer.Points.Count);
            foreach (var p in outer.Points) field.Add((p.Easting, p.Northing));
            pieces = Services.ObstacleDrawing.InField(corners, field, out leftOut);
            if (pieces.Count == 0)
            {
                ReportFailure("The obstacle drawn is outside the field");
                return;
            }
            cut = pieces.Count > 1 || !SameRing(pieces[0], corners);
        }

        int first = boundary.InnerBoundaries.Count;
        foreach (var piece in pieces) boundary.AddNewObstacle(Outline(piece));
        int last = boundary.InnerBoundaries.Count - 1;
        SaveObstacleChoice(boundary, fieldPath, ListRowOfInner(boundary, first));
        string slivers = leftOut switch
        {
            0 => "",
            1 => " (a sliver along the fence left out)",
            _ => $" ({leftOut} slivers along the fence left out)",
        };
        StatusMessage = !cut
            ? $"Inner {first + 1}: drawn with {corners.Count} corners"
            : pieces.Count == 1
                ? $"Inner {first + 1}: drawn with {corners.Count} corners, kept to the field{slivers}"
                : $"Inner {first + 1} to {last + 1}: drawn with {corners.Count} corners, kept to the field in {pieces.Count} pieces{slivers}";
    }

    private static BoundaryPolygon Outline(IReadOnlyList<(double E, double N)> ring)
    {
        var polygon = new BoundaryPolygon { AtObstacle = ObstacleAction.Turn };
        foreach (var (e, n) in ring) polygon.Points.Add(new BoundaryPoint(e, n, 0));
        polygon.UpdateBounds();
        return polygon;
    }

    // InField gives the drawn corners back as they were when the outline is wholly inside.
    private static bool SameRing(List<(double E, double N)> piece, List<(double E, double N)> corners)
    {
        if (piece.Count != corners.Count) return false;
        for (int i = 0; i < piece.Count; i++)
            if (piece[i] != corners[i]) return false;
        return true;
    }
}
