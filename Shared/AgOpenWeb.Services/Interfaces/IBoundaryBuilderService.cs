// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Builds a boundary polygon from a set of tracks by finding their
/// intersections and trimming to create a closed polygon.
/// Ported from AgOpenGPS BoundaryBuilder.
/// </summary>
public interface IBoundaryBuilderService
{
    /// <summary>
    /// Builds a boundary polygon from the given tracks.
    /// Tracks are extended, intersections found, segments trimmed,
    /// and a closed polygon is constructed.
    /// </summary>
    /// <param name="tracks">Tracks to build the boundary from (minimum 2)</param>
    /// <param name="extendMeters">Distance to extend track endpoints (helps find intersections)</param>
    /// <returns>A BoundaryPolygon if successful, null if insufficient tracks or no valid polygon</returns>
    BoundaryPolygon? BuildBoundaryFromTracks(List<Models.Track.Track> tracks, double extendMeters = 20.0);
}
