// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// An immutable route plan: alternating swath and turn segments ready for guidance.
/// Produced by RouteStitchingService, consumed by RouteGuidanceService (Phase 3).
/// </summary>
public class RoutePlan
{
    /// <summary>Ordered segments: swath, turn, swath, turn, ... swath.</summary>
    public List<RouteSegment> Segments { get; set; } = new();

    /// <summary>Number of working swaths in this plan.</summary>
    public int SwathCount => Segments.Count(s => s.Type == RouteSegmentType.Swath);

    /// <summary>Number of turns in this plan.</summary>
    public int TurnCount => Segments.Count(s => s.Type == RouteSegmentType.Turn);

    /// <summary>Number of turns that failed boundary validation.</summary>
    public int InvalidTurnCount => Segments.Count(s => s.Type == RouteSegmentType.Turn && !s.IsTurnValid);

    /// <summary>Total working distance (swaths only) in meters.</summary>
    public double TotalSwathDistance => Segments.Where(s => s.Type == RouteSegmentType.Swath).Sum(s => s.Length);

    /// <summary>Total turning distance in meters.</summary>
    public double TotalTurnDistance => Segments.Where(s => s.Type == RouteSegmentType.Turn).Sum(s => s.Length);

    /// <summary>Total route distance in meters.</summary>
    public double TotalDistance => Segments.Sum(s => s.Length);

    /// <summary>Which ordering pattern was used (e.g. "Boustrophedon", "Snake", "Spiral").</summary>
    public string Pattern { get; set; } = "";

    /// <summary>When this plan was generated.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
