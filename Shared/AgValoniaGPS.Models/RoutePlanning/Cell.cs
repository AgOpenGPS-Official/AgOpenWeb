// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// One convex sub-region produced by Boustrophedon Cellular Decomposition.
/// A cell is convex with respect to the sweep direction — within it, parallel
/// swaths are uninterrupted (no obstacles, no concavities). Inter-cell
/// transitions happen at critical points along the sweep line where the
/// polygon-with-holes topology changes (Open/Close/Split/Merge).
/// </summary>
public class Cell
{
    /// <summary>Unique cell id assigned during decomposition.</summary>
    public int Id { get; set; }

    /// <summary>Closed polygon defining the cell boundary, vertices in CCW order.</summary>
    public List<Vec2> Polygon { get; set; } = new();

    /// <summary>
    /// Local (non-topological) obstacles inside this cell. Each ring is a CW
    /// hole in the cell — vehicle must drive around or section-control over it
    /// during swath execution. Topological obstacles that disconnect the field
    /// are NOT inner rings; they cause the cell to be split during decomposition.
    /// </summary>
    public List<List<Vec2>> InnerRings { get; set; } = new();

    /// <summary>Sweep-direction coordinate at the cell's earliest extent.</summary>
    public double SweepStart { get; set; }

    /// <summary>Sweep-direction coordinate at the cell's latest extent.</summary>
    public double SweepEnd { get; set; }
}
