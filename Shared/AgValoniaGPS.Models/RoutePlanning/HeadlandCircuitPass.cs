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
/// A single perimeter pass following a boundary at a specific offset.
/// </summary>
public class HeadlandCircuitPass
{
    /// <summary>Points along the pass with headings.</summary>
    public List<Vec3> Points { get; set; } = new();

    /// <summary>Pass number — 0 is outermost (closest to boundary).</summary>
    public int PassNumber { get; set; }

    /// <summary>Distance from the reference boundary in meters.</summary>
    public double OffsetDistance { get; set; }

    /// <summary>True if this pass loops around an inner boundary (obstacle).</summary>
    public bool IsInnerBoundary { get; set; }
}
