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
/// Reeb graph derived from BCD output. Captures the topology of cell
/// adjacency: nodes are cells, edges are critical points where cells meet
/// along the sweep line.
/// </summary>
public class ReebGraph
{
    public List<Cell> Cells { get; set; } = new();

    public List<ReebEdge> Edges { get; set; } = new();
}

/// <summary>
/// One adjacency in the Reeb graph: two cells share a critical point on
/// the sweep line (e.g. Split connects parent → 2 children, Merge
/// connects 2 parents → child).
/// </summary>
public class ReebEdge
{
    public int FromCellId { get; set; }
    public int ToCellId { get; set; }

    /// <summary>The polygon vertex where the two cells meet on the sweep line.</summary>
    public Vec2 CriticalPoint { get; set; }
}
