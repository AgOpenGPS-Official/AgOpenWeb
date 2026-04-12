// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// Selects between reactive white-cane guidance and pre-computed route following.
/// </summary>
public enum GuidanceMode
{
    /// <summary>Reactive offset-based guidance (current default).</summary>
    WhiteCane,

    /// <summary>Follow a pre-computed route plan segment by segment.</summary>
    PreComputedRoute
}
