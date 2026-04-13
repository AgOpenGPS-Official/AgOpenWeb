// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using CommunityToolkit.Mvvm.Input;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.ViewModels;

public partial class MainViewModel
{
    // ── Route Following Commands ─────────────────────────────────────────

    public void InitializeRouteFollowingCommands()
    {
        StartRouteCommand = new RelayCommand(() =>
        {
            if (State.RoutePlan.ActivePlan == null) return;
            // Force pipeline to rebuild stitched waypoints from index 0
            // by bumping the plan reference (create a shallow copy)
            var plan = State.RoutePlan.ActivePlan;
            State.RoutePlan.ActivePlan = new Models.RoutePlanning.RoutePlan
            {
                Segments = plan.Segments,
                Pattern = plan.Pattern,
                CreatedAt = plan.CreatedAt,
            };
            State.RoutePlan.Mode = GuidanceMode.PreComputedRoute;
            State.RoutePlan.CurrentSegmentIndex = 0;
            State.RoutePlan.IsRouteComplete = false;
            OnPropertyChanged(nameof(IsRouteActive));
            OnPropertyChanged(nameof(RouteProgressText));
            RoutePlanStatus = "Route started — navigate to the green dot";
        });

        StopRouteCommand = new RelayCommand(() =>
        {
            State.RoutePlan.Mode = GuidanceMode.WhiteCane;
            State.RoutePlan.CurrentSegmentIndex = 0;
            OnPropertyChanged(nameof(IsRouteActive));
            OnPropertyChanged(nameof(RouteProgressText));
            RoutePlanStatus = "Route stopped — white-cane guidance active";
        });

        SkipSegmentCommand = new RelayCommand(() =>
        {
            if (!State.RoutePlan.IsRouteActive) return;
            State.RoutePlan.SkipSegmentRequested = true;
        });
    }

    // ── Route Following Properties ───────────────────────────────────────

    public bool IsRouteActive => State.RoutePlan.IsRouteActive;

    public string RouteProgressText
    {
        get
        {
            var plan = State.RoutePlan.ActivePlan;
            if (plan == null || !State.RoutePlan.IsRouteActive)
                return "";

            var segment = State.RoutePlan.CurrentSegment;
            if (segment == null) return "";

            int swathNum = 0;
            for (int i = 0; i <= State.RoutePlan.CurrentSegmentIndex && i < plan.Segments.Count; i++)
            {
                if (plan.Segments[i].Type == RouteSegmentType.Swath)
                    swathNum++;
            }

            string segLabel = segment.Type == RouteSegmentType.Swath
                ? $"Swath {swathNum}/{plan.SwathCount}"
                : $"Turn {swathNum}→{swathNum + 1}";

            int pct = (int)(100.0 * State.RoutePlan.CurrentSegmentIndex / plan.Segments.Count);
            return $"{segLabel} | {pct}%";
        }
    }
}
