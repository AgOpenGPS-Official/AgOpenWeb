// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Identifies whether a route segment is a working swath, a turn between swaths,
/// or a transit path traced along a perimeter circuit to bypass an obstacle.
/// Transits move the vehicle between zones without working — section control
/// lifts automatically as the vehicle enters inner-boundary buffer zones.
/// </summary>
public enum RouteSegmentType
{
    Swath,
    Turn,
    Transit
}
