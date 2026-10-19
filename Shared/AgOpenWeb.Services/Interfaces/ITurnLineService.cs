// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for turn line (headland) geometry calculations.
    /// </summary>
    public interface ITurnLineService
    {
        /// <summary>
        /// Calculate headings for turn line points.
        /// Adds duplicate first/last points with forward-looking headings.
        /// </summary>
        List<Vec3> CalculateHeadings(List<Vec3> turnLine);

        /// <summary>
        /// Fix turn line spacing by removing points too close to fence line
        /// and optimizing spacing between remaining points.
        /// </summary>
        List<Vec3> FixSpacing(List<Vec3> turnLine, List<Vec3> fenceLine, double totalHeadWidth, double spacing);
    }
}
