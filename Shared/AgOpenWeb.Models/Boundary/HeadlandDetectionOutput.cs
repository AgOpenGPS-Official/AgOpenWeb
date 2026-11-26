// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using System.Collections.Generic;

namespace AgOpenWeb.Models.Headland
{
    /// <summary>
    /// Output data from headland detection operations.
    /// </summary>
    public class HeadlandDetectionOutput
    {
        /// <summary>
        /// Whether the tool is in the headland area (based on outer points).
        /// </summary>
        public bool IsToolOuterPointsInHeadland { get; set; }

        /// <summary>
        /// Whether the left side of the tool is in headland.
        /// </summary>
        public bool IsLeftSideInHeadland { get; set; }

        /// <summary>
        /// Whether the right side of the tool is in headland.
        /// </summary>
        public bool IsRightSideInHeadland { get; set; }

        /// <summary>
        /// Headland status for each section (corner-based).
        /// </summary>
        public List<SectionHeadlandStatus> SectionStatus { get; set; } = new List<SectionHeadlandStatus>();

        /// <summary>
        /// Nearest point on headland boundary to vehicle.
        /// </summary>
        public Vec2? HeadlandNearestPoint { get; set; }

        /// <summary>
        /// Distance to nearest headland boundary point in meters.
        /// </summary>
        public double? HeadlandDistance { get; set; }

        /// <summary>
        /// Whether the proximity warning should be triggered.
        /// </summary>
        public bool ShouldTriggerWarning { get; set; }
    }

/// <summary>
/// Headland status for a single section.
/// </summary>
/// <param name="IsInHeadlandArea">Whether the section corners are in headland area.</param>
/// <param name="IsLookOnInHeadland">Whether the look-ahead points are in headland area.</param>
public record SectionHeadlandStatus(bool IsInHeadlandArea, bool IsLookOnInHeadland);
}
