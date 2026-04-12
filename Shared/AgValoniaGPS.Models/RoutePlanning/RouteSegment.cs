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
/// One segment of a route plan — either a working swath or a turn between swaths.
/// </summary>
public class RouteSegment
{
    /// <summary>Whether this segment is a swath or a turn.</summary>
    public RouteSegmentType Type { get; set; }

    /// <summary>Swath index in traversal order (meaningful for Swath segments).</summary>
    public int SwathIndex { get; set; }

    /// <summary>Dense waypoint list with headings.</summary>
    public List<Vec3> Waypoints { get; set; } = new();

    /// <summary>Segment length in meters.</summary>
    public double Length { get; set; }

    /// <summary>Travel direction — true if swath is driven in reverse heading.</summary>
    public bool IsReverse { get; set; }

    /// <summary>For turn segments: whether the turn path stays inside the boundary.</summary>
    public bool IsTurnValid { get; set; } = true;

    /// <summary>For turn segments: which Dubins path type was used (e.g. "RSR", "LSL").</summary>
    public string? TurnPathType { get; set; }
}
