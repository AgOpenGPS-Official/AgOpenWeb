// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Generates boundary-validated turn paths between consecutive swath endpoints.
/// Tries all 6 Dubins path types and selects the shortest that stays inside the boundary.
/// </summary>
public interface ITurnPathService
{
    /// <summary>
    /// Generate a turn path between two swath endpoints.
    /// Returns the shortest Dubins path that stays inside the boundary,
    /// or a best-effort path with IsValid=false if none fit.
    /// </summary>
    TurnPathResult GenerateTurn(TurnPathInput input);
}

/// <summary>
/// Input for turn path generation.
/// </summary>
public class TurnPathInput
{
    /// <summary>End point of the current swath.</summary>
    public required Vec3 ExitPoint { get; set; }

    /// <summary>Heading at exit (direction of travel when leaving swath).</summary>
    public double ExitHeading { get; set; }

    /// <summary>Start point of the next swath.</summary>
    public required Vec3 EntryPoint { get; set; }

    /// <summary>Heading at entry (direction of travel when entering next swath).</summary>
    public double EntryHeading { get; set; }

    /// <summary>Minimum turning radius in meters.</summary>
    public double TurningRadius { get; set; }

    /// <summary>Width of the headland zone (distance from cultivated area to outer boundary).</summary>
    public double HeadlandWidth { get; set; }

    /// <summary>Outer boundary polygon for containment checking.</summary>
    public required BoundaryPolygon Boundary { get; set; }

    /// <summary>Inner boundaries (obstacles) the turn path must NOT cross. May be empty.</summary>
    public List<BoundaryPolygon> InnerBoundaries { get; set; } = new();
}

/// <summary>
/// Result of turn path generation.
/// </summary>
public class TurnPathResult
{
    /// <summary>Full turn path waypoints: exit leg + Dubins arc + entry leg.</summary>
    public List<Vec3> Waypoints { get; set; } = new();

    /// <summary>Total path length in meters.</summary>
    public double Length { get; set; }

    /// <summary>Which Dubins path type was selected (e.g. "RSR", "LSL"), or "none" if no valid path.</summary>
    public string PathType { get; set; } = "none";

    /// <summary>True if all waypoints are inside the boundary.</summary>
    public bool IsValid { get; set; }
}
