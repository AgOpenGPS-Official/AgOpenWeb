// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using CommunityToolkit.Mvvm.ComponentModel;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Models.State;

/// <summary>
/// Runtime state for pre-computed route following.
/// Lives alongside VehicleState, GuidanceState, etc. in ApplicationState.
/// </summary>
public class RoutePlanState : ObservableObject
{
    private RoutePlan? _activePlan;
    public RoutePlan? ActivePlan
    {
        get => _activePlan;
        set => SetProperty(ref _activePlan, value);
    }

    private GuidanceMode _mode = GuidanceMode.WhiteCane;
    public GuidanceMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    private int _currentSegmentIndex;
    public int CurrentSegmentIndex
    {
        get => _currentSegmentIndex;
        set => SetProperty(ref _currentSegmentIndex, value);
    }

    private bool _isRouteComplete;
    public bool IsRouteComplete
    {
        get => _isRouteComplete;
        set => SetProperty(ref _isRouteComplete, value);
    }

    /// <summary>Set by UI to request skipping to the next segment. Pipeline reads and clears.</summary>
    public volatile bool SkipSegmentRequested;

    /// <summary>True when a route plan is loaded and guidance mode is PreComputedRoute.</summary>
    public bool IsRouteActive => ActivePlan != null && Mode == GuidanceMode.PreComputedRoute && !IsRouteComplete;

    /// <summary>Current segment, or null if no active route.</summary>
    public RouteSegment? CurrentSegment =>
        IsRouteActive && _currentSegmentIndex < ActivePlan!.Segments.Count
            ? ActivePlan.Segments[_currentSegmentIndex]
            : null;

    public void Reset()
    {
        ActivePlan = null;
        Mode = GuidanceMode.WhiteCane;
        CurrentSegmentIndex = 0;
        IsRouteComplete = false;
    }
}
