// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// One straight-line span of a swath in the original (un-rotated) frame.
/// Multiple segments together make up a swath that the inner rings of its
/// containing cell broke into pieces.
/// </summary>
public readonly record struct SwathSegment(Vec2 Start, Vec2 End)
{
    public double Length =>
        Math.Sqrt((End.Easting - Start.Easting) * (End.Easting - Start.Easting) +
                  (End.Northing - Start.Northing) * (End.Northing - Start.Northing));
}

/// <summary>
/// One parallel-swath line through a cell, expressed as 1+ disjoint segments
/// after clipping against the cell's outer boundary and any inner rings.
/// A swath split by an undrivable inner ring has &gt; 1 segments — sibling
/// segments that together cover the full swath line through the cell.
/// </summary>
public class GeneratedSwath
{
    /// <summary>0-based swath index inside the cell, ordered by perpendicular position.</summary>
    public int Index { get; init; }

    /// <summary>Swath direction (vehicle travel heading) in radians, our convention (from +N CW).</summary>
    public double SwathHeading { get; init; }

    /// <summary>
    /// Perpendicular coordinate of this swath in the cell's rotated frame.
    /// Useful for diagnostics and for ordering swaths during boustrophedon
    /// traversal.
    /// </summary>
    public double PerpCoord { get; init; }

    public required List<SwathSegment> Segments { get; init; }

    public bool IsSplit => Segments.Count > 1;
    public int SegmentCount => Segments.Count;
}
