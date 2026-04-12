// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Identifies whether a route segment is a working swath or a turn between swaths.
/// </summary>
public enum RouteSegmentType
{
    Swath,
    Turn
}
