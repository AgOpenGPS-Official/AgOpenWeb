// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using AgValoniaGPS.Models;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial: web/remote-client entry point for importing a KML/KMZ boundary
/// into the open field. Mirrors the native ImportKmlBoundaryCommand → ParseKmlFile →
/// ImportKmlToExistingField sequence (which uses a native dialog) as a single host call so
/// the browser only sends the chosen file name. All KML parsing + boundary import is host-side.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Parse the named KML/KMZ from the Import folder and import its polygon(s) as the
    /// field boundary (first = outer, rest = inner), then save + recenter. The file name comes
    /// from the projected FieldOps KmlFiles list (matched by KmlFileItem.Name).</summary>
    public void RemoteImportKmlBoundary(string fileName)
    {
        if (!IsFieldOpen || string.IsNullOrEmpty(CurrentFieldName))
        {
            StatusMessage = "Open a field first before importing a boundary";
            return;
        }

        PopulateAvailableKmlFiles();
        var item = AvailableKmlFiles.FirstOrDefault(f => f.Name == fileName)
                   ?? AvailableKmlFiles.FirstOrDefault(f => f.FullPath == fileName);
        if (item == null)
        {
            StatusMessage = $"KML file not found: {fileName}";
            return;
        }

        _kmlImportToExistingField = true;
        PendingBoundaryType = BoundaryType.Outer;
        _kmlBoundaryPoints.Clear();
        _kmlParsedPolygons.Clear();

        // Setting SelectedKmlFile parses the file (ParseKmlFile) into _kmlParsedPolygons.
        SelectedKmlFile = item;
        if (_kmlParsedPolygons.Count == 0)
        {
            StatusMessage = "No boundary polygons found in KML";
            return;
        }

        ImportKmlToExistingField();
    }
}
