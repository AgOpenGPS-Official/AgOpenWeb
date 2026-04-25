// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Classification of a polygon vertex relative to the BCD sweep direction.
/// Determines how the active-cell list mutates when the sweep line reaches
/// that vertex.
/// </summary>
public enum CriticalPointType
{
    /// <summary>Vertex is interior on the sweep direction; no cell change.</summary>
    Regular,

    /// <summary>Both edges go *forward* in sweep direction — vertex is the
    /// near side of a region. Start a new cell.</summary>
    Open,

    /// <summary>Both edges go *backward* — vertex is the far side. Close a cell.</summary>
    Close,

    /// <summary>One edge forward, one backward; locally a peak that divides
    /// one cell into two going forward (top of an inner-boundary obstacle,
    /// or top of an outer-boundary notch viewed from inside).</summary>
    Split,

    /// <summary>Mirror of Split: a valley that joins two cells back into one
    /// (bottom of an inner-boundary obstacle, bottom of an outer notch).</summary>
    Merge,
}

/// <summary>
/// A polygon vertex annotated with its critical-point classification, ready
/// for the sweep-line decomposition.
/// </summary>
public class CriticalPoint
{
    public Vec2 Position { get; set; }
    public CriticalPointType Type { get; set; }

    /// <summary>The vertex's coordinate along the sweep direction.</summary>
    public double SweepCoordinate { get; set; }

    /// <summary>Index of the vertex in its source polygon.</summary>
    public int VertexIndex { get; set; }

    /// <summary>True if the vertex came from an inner-boundary polygon, false for outer.</summary>
    public bool IsOnInnerBoundary { get; set; }
}
