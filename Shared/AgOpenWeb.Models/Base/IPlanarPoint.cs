// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Base;

/// <summary>
/// A position on the field's local plane. Lets one point-in-polygon test
/// (<see cref="GeometryMath.IsPointInPolygon{T}"/>) serve every point type.
/// </summary>
public interface IPlanarPoint
{
    double Easting { get; }
    double Northing { get; }
}
