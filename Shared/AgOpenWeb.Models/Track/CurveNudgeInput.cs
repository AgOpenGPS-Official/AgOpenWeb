// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using System.Collections.Generic;

namespace AgOpenWeb.Models.Track
{
    /// <summary>
    /// Input for curve nudging calculation.
    /// </summary>
    public class CurveNudgeInput
    {
        /// <summary>
        /// Original curve points to nudge.
        /// </summary>
        public List<Vec3> CurvePoints { get; set; }

        /// <summary>
        /// Distance to nudge perpendicular to curve (positive = right, negative = left).
        /// </summary>
        public double Distance { get; set; }
    }
}
