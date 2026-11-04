// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: round obstacles (trees) in the boundary list. A circle is placed by a
/// tap on its centre with a diameter, a drawn obstacle is made round (the smallest circle that
/// holds it), one at a time or every "Go around" one at once, and a circle's diameter or centre is
/// changed. The circle and its polygon are saved with the field (<see cref="ObstacleCircle"/>).
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// A new round obstacle at (<paramref name="easting"/>, <paramref name="northing"/>), field
    /// metres, <paramref name="diameter"/> metres across; its choice is Go around, as a tree's
    /// would be. Its row is selected.
    /// </summary>
    public void RemoteAddObstacleCircle(double easting, double northing, double diameter)
    {
        if (!CheckDiameter(diameter) || !TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        if (boundary.OuterBoundary is { IsValid: true } outer && !outer.IsPointInside(easting, northing))
        {
            ReportFailure("A circle's centre must be inside the field");
            return;
        }

        var polygon = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound };
        polygon.SetCircle(new ObstacleCircle(easting, northing, diameter));
        boundary.InnerBoundaries.Add(polygon);
        int inner = boundary.InnerBoundaries.Count - 1;
        SaveObstacleChoice(boundary, fieldPath, ListRowOfInner(boundary, inner));
        StatusMessage = $"Inner {inner + 1}: circle {Metres(diameter)} across";
    }

    /// <summary>The obstacle on list row <paramref name="listIndex"/> becomes a circle <paramref name="diameter"/> across, round its centre.</summary>
    public void RemoteSetObstacleCircleDiameter(int listIndex, double diameter)
    {
        if (!CheckDiameter(diameter) || !TryLoadInnerRow(listIndex, out var boundary, out var fieldPath, out int i)) return;
        var inner = boundary.InnerBoundaries[i];
        var circle = inner.Circle ?? ObstacleCircle.Covering(inner.Points);
        inner.SetCircle(circle with { Diameter = diameter });
        SaveObstacleChoice(boundary, fieldPath, listIndex);
        StatusMessage = $"Inner {i + 1}: circle {Metres(diameter)} across";
    }

    /// <summary>The obstacle on list row <paramref name="listIndex"/> becomes a circle centred at the point, its diameter kept.</summary>
    public void RemoteMoveObstacleCircle(int listIndex, double easting, double northing)
    {
        if (!TryLoadInnerRow(listIndex, out var boundary, out var fieldPath, out int i)) return;
        if (boundary.OuterBoundary is { IsValid: true } outer && !outer.IsPointInside(easting, northing))
        {
            ReportFailure("A circle's centre must be inside the field");
            return;
        }
        var inner = boundary.InnerBoundaries[i];
        var circle = inner.Circle ?? ObstacleCircle.Covering(inner.Points);
        inner.SetCircle(circle with { CenterEasting = easting, CenterNorthing = northing });
        SaveObstacleChoice(boundary, fieldPath, listIndex);
        StatusMessage = $"Inner {i + 1}: circle moved";
    }

    /// <summary>The obstacle on list row <paramref name="listIndex"/> becomes the smallest circle that holds it.</summary>
    public void RemoteMakeObstacleRound(int listIndex)
    {
        if (!TryLoadInnerRow(listIndex, out var boundary, out var fieldPath, out int i)) return;
        var inner = boundary.InnerBoundaries[i];
        if (inner.Circle != null)
        {
            ReportFailure($"Inner {i + 1} is already a circle");
            return;
        }
        var circle = ObstacleCircle.Covering(inner.Points);
        if (!ObstacleCircle.IsValidDiameter(circle.Diameter))
        {
            ReportFailure($"Inner {i + 1} is too big to make round ({Metres(circle.Diameter)} across)");
            return;
        }
        inner.SetCircle(circle);
        SaveObstacleChoice(boundary, fieldPath, listIndex);
        StatusMessage = $"Inner {i + 1}: circle {Metres(circle.Diameter)} across";
    }

    /// <summary>
    /// Every "Go around" obstacle that is not a circle yet becomes the smallest circle that holds
    /// it. One that is long and thin (its circle more than <see cref="ObstacleCircle.MaxAreaRatio"/>
    /// times its area) or too big for a circle keeps its shape.
    /// </summary>
    public void RemoteMakeGoRoundObstaclesRound()
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int made = 0, kept = 0;
        foreach (var inner in boundary.InnerBoundaries)
        {
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.GoRound || inner.Circle != null) continue;
            var circle = ObstacleCircle.Covering(inner.Points);
            double shapeArea = inner.AreaHectares * 10000.0;
            if (!ObstacleCircle.IsValidDiameter(circle.Diameter) || !(circle.Area <= ObstacleCircle.MaxAreaRatio * shapeArea))
            {
                kept++;
                continue;
            }
            inner.SetCircle(circle);
            made++;
        }
        if (made == 0)
        {
            ReportFailure(kept > 0 ? $"No obstacle made round: {kept} long and thin or too big, kept as drawn" : "No \"Go around\" obstacle to make round");
            return;
        }

        SaveObstacleChoice(boundary, fieldPath, SelectedBoundaryIndex);
        if (kept > 0) ReportFailure($"{made} obstacles made round; {kept} long and thin or too big, kept as drawn");
        else StatusMessage = $"{made} obstacles made round";
    }

    private bool CheckDiameter(double diameter)
    {
        if (ObstacleCircle.IsValidDiameter(diameter)) return true;
        ReportFailure($"A circle is {Metres(ObstacleCircle.MinDiameter)} to {Metres(ObstacleCircle.MaxDiameter)} across");
        return false;
    }

    private bool TryLoadInnerRow(int listIndex, out Boundary boundary, out string fieldPath, out int innerIndex)
    {
        innerIndex = -1;
        if (!TryLoadBoundaryForEdit(out boundary, out fieldPath)) return false;
        innerIndex = InnerIndexOfListRow(boundary, listIndex);
        if (innerIndex >= 0) return true;
        ReportFailure("Select an inner boundary");
        return false;
    }

    /// <summary>The boundary list row of inner boundary <paramref name="innerIndex"/> (the inverse of <see cref="InnerIndexOfListRow"/>).</summary>
    private static int ListRowOfInner(Boundary boundary, int innerIndex)
    {
        int row = boundary.OuterBoundary is { IsValid: true } ? 1 : 0;
        for (int i = 0; i < innerIndex; i++)
            if (boundary.InnerBoundaries[i].IsValid) row++;
        return row;
    }

    private static string Metres(double m) => m.ToString("0.0", CultureInfo.InvariantCulture) + " m";
}
