// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using AgOpenWeb.Models;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: the boundary list's choice per obstacle (what the tractor does at an
/// inner boundary, <see cref="ObstacleAction"/>), one row at a time or every obstacle at once
/// from a column's title. Saved with the field like the drive-through and hard flags.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// The obstacle on boundary list row <paramref name="listIndex"/> (the index the list and
    /// <c>boundary.select</c> use) gets <paramref name="action"/>. The row stays selected.
    /// </summary>
    public void RemoteSetObstacleAction(int listIndex, ObstacleAction action)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int i = InnerIndexOfListRow(boundary, listIndex);
        if (i < 0)
        {
            StatusMessage = "Only an inner boundary has a choice at the obstacle";
            return;
        }

        boundary.InnerBoundaries[i].AtObstacle = action;
        SaveObstacleChoice(boundary, fieldPath, listIndex);
        StatusMessage = $"Inner {i + 1}: {Describe(action)}";
    }

    /// <summary>Every inner boundary of the field gets <paramref name="action"/>.</summary>
    public void RemoteSetAllObstacleActions(ObstacleAction action)
    {
        if (!TryLoadBoundaryForEdit(out var boundary, out var fieldPath)) return;
        int count = 0;
        foreach (var inner in boundary.InnerBoundaries)
        {
            if (!inner.IsValid) continue;
            inner.AtObstacle = action;
            count++;
        }
        if (count == 0)
        {
            StatusMessage = "The field has no inner boundaries";
            return;
        }

        SaveObstacleChoice(boundary, fieldPath, SelectedBoundaryIndex);
        StatusMessage = $"All {count} inner boundaries: {Describe(action)}";
    }

    private bool TryLoadBoundaryForEdit(out Boundary boundary, out string fieldPath)
    {
        boundary = null!;
        fieldPath = "";
        if (string.IsNullOrEmpty(CurrentFieldName)) return false;
        fieldPath = Path.Combine(_settingsService.Settings.FieldsDirectory, CurrentFieldName);
        var loaded = LoadFieldBoundary(fieldPath);
        if (loaded == null) return false;
        boundary = loaded;
        return true;
    }

    private void SaveObstacleChoice(Boundary boundary, string fieldPath, int keepSelected)
    {
        SaveFieldBoundary(boundary, fieldPath);
        SetCurrentBoundary(boundary);
        RefreshBoundaryList(); // clears the selection
        if (keepSelected >= 0 && keepSelected < BoundaryItems.Count) SelectedBoundaryIndex = keepSelected;
    }

    /// <summary>
    /// The index in <see cref="Boundary.InnerBoundaries"/> of list row <paramref name="listIndex"/>,
    /// or -1 for the outer boundary or no row: the list counts the valid outer boundary first,
    /// then the valid inner ones in order (<see cref="RefreshBoundaryList"/>).
    /// </summary>
    private static int InnerIndexOfListRow(Boundary boundary, int listIndex)
    {
        int row = boundary.OuterBoundary is { IsValid: true } ? 1 : 0;
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            if (!boundary.InnerBoundaries[i].IsValid) continue;
            if (row == listIndex) return i;
            row++;
        }
        return -1;
    }

    private static string Describe(ObstacleAction action) => action switch
    {
        ObstacleAction.GoRound => "go around",
        ObstacleAction.DriveThrough => "drive through",
        _ => "U-turn",
    };
}
