// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Coverage;

/// <summary>
/// Represents a single cell in the visual coverage grid.
/// Used for fast rendering - each cell is a simple rectangle.
/// </summary>
public readonly struct CoverageCell
{
    /// <summary>
    /// World X coordinate of cell center (easting)
    /// </summary>
    public double CenterX { get; init; }

    /// <summary>
    /// World Y coordinate of cell center (northing)
    /// </summary>
    public double CenterY { get; init; }

    /// <summary>
    /// Section index (0-15) that painted this cell, or 255 for default color
    /// </summary>
    public byte SectionIndex { get; init; }

    /// <summary>
    /// Color of this cell (RGB packed into Vec3 format like CoverageColor)
    /// </summary>
    public CoverageColor Color { get; init; }

    public CoverageCell(double centerX, double centerY, byte sectionIndex, CoverageColor color)
    {
        CenterX = centerX;
        CenterY = centerY;
        SectionIndex = sectionIndex;
        Color = color;
    }
}

/// <summary>
/// Parameters for querying visible coverage cells.
/// </summary>
public readonly struct CoverageViewport
{
    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinY { get; init; }
    public double MaxY { get; init; }

    public CoverageViewport(double minX, double maxX, double minY, double maxY)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
    }
}
