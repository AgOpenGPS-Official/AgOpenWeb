// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Coverage;

/// <summary>
/// Result of segment-based coverage analysis.
/// </summary>
/// <param name="CoveragePercent">Coverage from 0.0 (none) to 1.0 (full).</param>
/// <param name="HasAnyOverlap">True if any overlap exists.</param>
/// <param name="IsFullyCovered">True if 100% covered (within tolerance).</param>
/// <param name="UncoveredLength">Total uncovered length in meters.</param>
public readonly record struct CoverageResult(
    double CoveragePercent,
    bool HasAnyOverlap,
    bool IsFullyCovered,
    double UncoveredLength)
{
    /// <summary>
    /// No coverage result.
    /// </summary>
    public static CoverageResult None => new(0, false, false, 0);
}
