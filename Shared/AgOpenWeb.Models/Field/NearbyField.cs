// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.Models;

/// <summary>
/// One row in the Fields-list / nearby-fields views (e.g. the
/// StartWorkSession dialog left column, the InField shortcut).
/// </summary>
/// <param name="Name">Folder name under <c>FieldsRoot</c>.</param>
/// <param name="DirectoryPath">Absolute path to the field directory.</param>
/// <param name="DistanceKm">Great-circle distance from the query
/// coordinate to the field's origin, in kilometres. NaN when the
/// distance is unknown (no GPS fix, or the field origin couldn't be
/// read / is (0,0)).</param>
/// <param name="BoundaryAreaHectares">Area enclosed by the outer
/// boundary, or 0 if the field has no boundary on disk.</param>
public sealed record NearbyField(
    string Name,
    string DirectoryPath,
    double DistanceKm,
    double BoundaryAreaHectares)
{
    /// <summary>UI-friendly distance string. Renders "—" when
    /// <see cref="DistanceKm"/> is NaN so the operator isn't misled
    /// by a literal 0.0 that means "we don't know".</summary>
    public string DistanceKmDisplay =>
        double.IsNaN(DistanceKm)
            ? "—"
            : DistanceKm.ToString("F1", CultureInfo.CurrentCulture);
}
