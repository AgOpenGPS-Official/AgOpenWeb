// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.Track;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Assembles ordered swaths and boundary-validated turn paths into a RoutePlan.
/// </summary>
public interface IRouteStitchingService
{
    /// <summary>
    /// Stitch ordered swaths into a complete route plan with validated turns.
    /// </summary>
    RoutePlan StitchRoute(List<Models.Track.Track> swaths, RouteStitchConfig config);
}

/// <summary>
/// Configuration for route stitching — turn generation parameters.
/// </summary>
public class RouteStitchConfig
{
    /// <summary>Minimum turning radius in meters.</summary>
    public double TurningRadius { get; set; }

    /// <summary>Headland zone width in meters.</summary>
    public double HeadlandWidth { get; set; }

    /// <summary>Outer boundary for turn containment checking.</summary>
    public required BoundaryPolygon Boundary { get; set; }

    /// <summary>Reference heading from the AB line (radians).</summary>
    public double ReferenceHeading { get; set; }

    /// <summary>Which ordering pattern was used.</summary>
    public SwathPattern Pattern { get; set; }
}
