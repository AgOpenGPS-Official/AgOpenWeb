// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Guidance;

/// <summary>
/// Smoothing pass helpers for generated turn paths.
/// </summary>
public static class TurnPathSmoothing
{
    /// <summary>
    /// Apply an in-place 3-point averaging smooth to the interior of the path.
    /// Endpoints and the points adjacent to them are left untouched so the path
    /// still connects cleanly to its entry/exit segments. Heading is preserved
    /// per-point (only Easting/Northing are averaged).
    /// </summary>
    public static void Smooth(IList<Vec3> path, int passes)
    {
        if (passes <= 1 || path.Count <= 4) return;

        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 2; i < path.Count - 2; i++)
            {
                var prev = path[i - 1];
                var curr = path[i];
                var next = path[i + 1];

                path[i] = new Vec3
                {
                    Easting = (prev.Easting + curr.Easting + next.Easting) / 3.0,
                    Northing = (prev.Northing + curr.Northing + next.Northing) / 3.0,
                    Heading = curr.Heading,
                };
            }
        }
    }
}
