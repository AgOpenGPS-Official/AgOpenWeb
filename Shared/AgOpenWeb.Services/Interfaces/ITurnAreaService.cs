// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for turn area polygon testing.
    /// </summary>
    public interface ITurnAreaService
    {
        /// <summary>
        /// Determine which turn area boundary a point is inside.
        /// </summary>
        /// <param name="turnLines">List of turn line polygons (outer boundary at index 0, inner boundaries at index > 0)</param>
        /// <param name="isDriveThru">List of drive-through flags for each boundary</param>
        /// <param name="point">Point to test</param>
        /// <returns>Boundary index (0 = outer, >0 = inner), or -1 if outside all boundaries</returns>
        int IsPointInsideTurnArea(List<List<Vec3>> turnLines, List<bool> isDriveThru, Vec3 point);
    }
}
