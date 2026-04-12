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
            State.RoutePlan.Mode = GuidanceMode.PreComputedRoute;
            State.RoutePlan.CurrentSegmentIndex = 0;
            State.RoutePlan.IsRouteComplete = false;
            OnPropertyChanged(nameof(IsRouteActive));
            OnPropertyChanged(nameof(RouteProgressText));
            RoutePlanStatus = "Route started — follow the planned path";
        });

        StopRouteCommand = new RelayCommand(() =>
        {
            State.RoutePlan.Mode = GuidanceMode.WhiteCane;
            OnPropertyChanged(nameof(IsRouteActive));
            OnPropertyChanged(nameof(RouteProgressText));
            RoutePlanStatus = "Route stopped — white-cane guidance active";
        });

        SkipSegmentCommand = new RelayCommand(() =>
        {
            var plan = State.RoutePlan.ActivePlan;
            if (plan == null || !State.RoutePlan.IsRouteActive) return;

            int nextIndex = State.RoutePlan.CurrentSegmentIndex + 1;
            if (nextIndex >= plan.Segments.Count)
            {
                State.RoutePlan.IsRouteComplete = true;
                State.RoutePlan.Mode = GuidanceMode.WhiteCane;
                RoutePlanStatus = "Route complete";
            }
            else
            {
                State.RoutePlan.CurrentSegmentIndex = nextIndex;
                RoutePlanStatus = RouteProgressText;
            }
            OnPropertyChanged(nameof(IsRouteActive));
            OnPropertyChanged(nameof(RouteProgressText));
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
