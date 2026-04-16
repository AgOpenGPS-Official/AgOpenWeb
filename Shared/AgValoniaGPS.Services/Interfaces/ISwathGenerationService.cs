// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.Track;
using System.Collections.Generic;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Generates parallel finite-endpoint swaths clipped to a field boundary.
/// Part of the pre-computed route planning system (#128).
/// </summary>
public interface ISwathGenerationService
{
    /// <summary>
    /// Generate parallel swaths from a reference track, clipped to boundary.
    /// </summary>
    SwathPlan GenerateSwaths(SwathPlanInput input);
}

/// <summary>
/// Input parameters for swath generation.
/// </summary>
public class SwathPlanInput
{
    /// <summary>AB line or curve that defines the driving direction.</summary>
    public required Models.Track.Track ReferenceTrack { get; set; }

    /// <summary>Boundary polygon to clip tracks to (headland or outer boundary).</summary>
    public required BoundaryPolygon ClipBoundary { get; set; }

    /// <summary>Tool/implement width in meters.</summary>
    public double ToolWidth { get; set; }

    /// <summary>Overlap between adjacent swaths in meters.</summary>
    public double Overlap { get; set; }

    /// <summary>Coverage pattern for traversal ordering.</summary>
    public SwathPattern Pattern { get; set; } = SwathPattern.Boustrophedon;

    /// <summary>Max tracks to generate (null = all that fit in the boundary).</summary>
    public int? MaxTracks { get; set; }

    /// <summary>Vehicle position for "next N" mode — start from nearest track.</summary>
    public Vec3? VehiclePosition { get; set; }

    /// <summary>Skip width: 1 = every track, 2 = skip one, etc.</summary>
    public int SkipWidth { get; set; } = 1;

    /// <summary>Distance from headland boundary to outer boundary (headland zone width).</summary>
    public double HeadlandWidth { get; set; }

    /// <summary>Inner boundaries (obstacles/holes) to exclude from swaths. May be empty.</summary>
    public List<BoundaryPolygon> InnerBoundaries { get; set; } = new();

    /// <summary>Number of tool-widths of buffer around inner boundaries.
    /// Swaths terminate at the buffer edge instead of the raw obstacle, leaving
    /// room for turns. 0 = no buffer (swaths hit the obstacle directly).</summary>
    public int InnerBoundaryBufferPasses { get; set; } = 1;
}

/// <summary>
/// Result of swath generation — ordered finite tracks ready for display or guidance.
/// </summary>
public class SwathPlan
{
    /// <summary>Ordered, finite-endpoint tracks clipped to boundary.</summary>
    public List<Models.Track.Track> Swaths { get; set; } = new();

    /// <summary>Source swath index for each track (parallel to Swaths).
    /// When a swath is split by an inner boundary, multiple tracks share the same index.
    /// Used by RouteStitchingService to avoid generating turns between split segments.</summary>
    public List<int> SourceSwathIndex { get; set; } = new();

    /// <summary>Total number of parallel tracks that fit across the field.</summary>
    public int TotalPossibleTracks { get; set; }

    /// <summary>Sum of all swath lengths in meters.</summary>
    public double TotalWorkingDistance { get; set; }

}
