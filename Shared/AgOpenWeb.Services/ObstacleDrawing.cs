// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services;

/// <summary>
/// An obstacle drawn on the map with "+ Polygon": the corners tapped, made into the obstacle's
/// outline.
/// </summary>
public static class ObstacleDrawing
{
    /// <summary>Two taps closer than this are one corner (a double tap on the touch screen).</summary>
    public const double CornerMinSpacing = 0.2;

    /// <summary>
    /// The corners of an obstacle drawn by taps, in order: a tap within <see cref="CornerMinSpacing"/>
    /// of the corner before it is the same corner, and a last tap back on the first corner closes
    /// the outline instead of adding a corner.
    /// </summary>
    public static List<(double E, double N)> Corners(IReadOnlyList<(double E, double N)> taps)
    {
        var corners = new List<(double E, double N)>();
        foreach (var p in taps)
            if (corners.Count == 0 || GeometryMath.Distance(corners[^1].E, corners[^1].N, p.E, p.N) >= CornerMinSpacing)
                corners.Add(p);
        if (corners.Count > 1 && GeometryMath.Distance(corners[0].E, corners[0].N, corners[^1].E, corners[^1].N) < CornerMinSpacing)
            corners.RemoveAt(corners.Count - 1);
        return corners;
    }
}
