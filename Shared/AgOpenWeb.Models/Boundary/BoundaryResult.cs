// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Headland;

/// <summary>
/// Result of segment-based boundary analysis.
/// </summary>
/// <param name="IsFullyInside">Entire segment is inside boundary.</param>
/// <param name="IsFullyOutside">Entire segment is outside boundary.</param>
/// <param name="CrossesBoundary">Segment crosses boundary edge.</param>
/// <param name="InsidePercent">Percentage of segment inside boundary (0.0 to 1.0).</param>
public readonly record struct BoundaryResult(
    bool IsFullyInside,
    bool IsFullyOutside,
    bool CrossesBoundary,
    double InsidePercent)
{
    /// <summary>
    /// Fully inside boundary result.
    /// </summary>
    public static BoundaryResult FullyInside => new(true, false, false, 1.0);

    /// <summary>
    /// Fully outside boundary result.
    /// </summary>
    public static BoundaryResult FullyOutside => new(false, true, false, 0.0);
}
