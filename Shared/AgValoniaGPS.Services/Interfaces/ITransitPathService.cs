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
/// Generates inter-zone transit paths along perimeter circuits.
/// Used when a Dubins turn between two swath endpoints would cross an obstacle —
/// e.g. swaths split by an inner boundary, a concave outer notch, or a non-convex
/// headland neck. The transit traces along the relevant circuit (outer perimeter
/// pass for outer-boundary obstacles; inner perimeter pass for inner-boundary
/// obstacles), connecting endpoints with short Dubins arcs.
/// </summary>
public interface ITransitPathService
{
    /// <summary>
    /// Generate a transit path between two endpoints by tracing along the best
    /// available circuit. Returns the shortest valid result, or a best-effort
    /// invalid result if no circuit yields a path that stays within the field.
    /// </summary>
    TransitPathResult GenerateTransit(TransitPathInput input);
}

/// <summary>
/// One closed perimeter circuit available for transit (already populated with headings).
/// </summary>
public class TransitCircuit
{
    /// <summary>Closed loop of points with tangent headings.</summary>
    public required List<Vec3> Points { get; set; }

    /// <summary>True if this circuit surrounds an obstacle (inner boundary), false for outer perimeter.</summary>
    public bool IsInnerBoundary { get; set; }
}

/// <summary>
/// Input for transit path generation.
/// </summary>
public class TransitPathInput
{
    /// <summary>End of the previous swath (where transit begins).</summary>
    public required Vec3 ExitPoint { get; set; }

    /// <summary>Travel direction at exit (radians).</summary>
    public double ExitHeading { get; set; }

    /// <summary>Start of the next swath (where transit ends).</summary>
    public required Vec3 EntryPoint { get; set; }

    /// <summary>Travel direction needed at entry (radians).</summary>
    public double EntryHeading { get; set; }

    /// <summary>Vehicle minimum turning radius in meters.</summary>
    public double TurningRadius { get; set; }

    /// <summary>Outer field boundary — transit must stay inside.</summary>
    public required BoundaryPolygon OuterBoundary { get; set; }

    /// <summary>Raw inner boundaries (obstacles) — transit must not enter them.
    /// We validate against the raw boundary, not the expanded buffer used for
    /// swath clipping, because the inner circuit lives inside that buffer by design.</summary>
    public List<BoundaryPolygon> InnerBoundaries { get; set; } = new();

    /// <summary>Available circuits to trace along (outer perimeter + each inner perimeter pass).</summary>
    public List<TransitCircuit> Circuits { get; set; } = new();
}

/// <summary>
/// Result of transit path generation.
/// </summary>
public class TransitPathResult
{
    /// <summary>Full transit waypoints from ExitPoint through circuit to EntryPoint.</summary>
    public List<Vec3> Waypoints { get; set; } = new();

    /// <summary>Total path length in meters.</summary>
    public double Length { get; set; }

    /// <summary>True if the path stays inside the outer boundary and outside all relevant buffers.</summary>
    public bool IsValid { get; set; }

    /// <summary>Which circuit was used: "outer" or "inner-{index}".</summary>
    public string CircuitUsed { get; set; } = "none";
}
