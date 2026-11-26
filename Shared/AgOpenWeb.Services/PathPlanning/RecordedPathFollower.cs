// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.PathPlanning;

/// <summary>
/// Pure Pursuit along a recorded route, or along the Dubins approach to it (AgOpenGPS
/// CRecordedPath.PurePursuitRecPath / PurePursuitDubins). The GPS pipeline runs it every cycle
/// while a route plays, so AutoSteer steers along it like any line (P7: it used to steer only
/// the built-in simulator's wheel). Positions are the pivot, headings compass radians.
/// </summary>
public sealed class RecordedPathFollower
{
    private readonly IReadOnlyList<Vec3> _path;
    private readonly bool _approach;
    private int _closest;

    /// <param name="path">The points to follow, in order.</param>
    /// <param name="startIndex">Where the search for the nearest point starts.</param>
    /// <param name="approach">The Dubins approach (a goal 5 points ahead) rather than the route (3).</param>
    public RecordedPathFollower(IReadOnlyList<Vec3> path, int startIndex, bool approach)
    {
        _path = path;
        _approach = approach;
        _closest = Math.Clamp(startIndex, 0, Math.Max(0, path.Count - 1));
    }

    public IReadOnlyList<Vec3> Path => _path;

    /// <summary>Steer angle (degrees, + right), cross-track error (m, + right of the path), the goal point.</summary>
    public readonly record struct Steer(double SteerAngleDegrees, double CrossTrackError, double GoalE, double GoalN);

    /// <summary>The steer for the pivot at (e, n) heading <paramref name="headingRad"/>; null at the
    /// end of the path, where there is nothing left ahead to steer to.</summary>
    public Steer? Follow(double e, double n, double headingRad, double wheelbase, double maxSteerDegrees)
    {
        int count = _path.Count;
        if (count < 2) return null;

        // The nearest point, searched forward from the last one (a little back, for tight curves).
        int from = Math.Max(0, _closest - (_approach ? 3 : 5));
        double best = double.MaxValue;
        for (int i = from; i < count; i++)
        {
            double dx = _path[i].Easting - e, dy = _path[i].Northing - n;
            double d = dx * dx + dy * dy;
            if (d < best) { best = d; _closest = i; }
        }
        if (_closest >= count - 2) return null;

        int ahead = _approach
            ? Math.Max(1, Math.Min(5, count - _closest - 1))
            : Math.Max(2, Math.Min(3, count - _closest - 1));
        var goal = _path[Math.Min(_closest + ahead, count - 1)];
        double gx = goal.Easting - e, gy = goal.Northing - n;
        double lookSq = gx * gx + gy * gy;

        double steerRad = 0;
        if (lookSq > 0.01)
        {
            double cosH = Math.Cos(headingRad), sinH = Math.Sin(headingRad);
            double right = gx * cosH - gy * sinH;   // across, + right
            double forward = gx * sinH + gy * cosH;
            double wb = Math.Max(wheelbase, 2.0);
            steerRad = forward > 0.1 ? Math.Atan2(2.0 * wb * right, lookSq) : (right > 0 ? 0.5 : -0.5);
            double max = maxSteerDegrees * Math.PI / 180.0;
            steerRad = Math.Clamp(steerRad, -max, max);
        }

        // Distance from the segment ahead of the nearest point, + when the pivot is right of it.
        var a = _path[_closest];
        var b = _path[_closest + 1];
        double sx = b.Easting - a.Easting, sy = b.Northing - a.Northing;
        double len = Math.Sqrt(sx * sx + sy * sy);
        double xte = len > 0.01 ? ((e - a.Easting) * sy - (n - a.Northing) * sx) / len : 0;

        return new Steer(steerRad * 180.0 / Math.PI, xte, goal.Easting, goal.Northing);
    }
}

/// <summary>
/// A played route as the GPS pipeline follows it: the Dubins approach, then the route from the
/// point the approach leads to, handed over in the same cycle (no gap where AutoSteer has no
/// line). The search starts there, not at the route's nearest point: a lap's two ends can lie
/// side by side.
/// </summary>
public sealed class RecordedRouteGuide
{
    private readonly IReadOnlyList<Vec3> _route;
    private readonly int _routeStart;
    private RecordedPathFollower _current;
    private bool _onRoute;

    public RecordedRouteGuide(IReadOnlyList<Vec3>? approach, IReadOnlyList<Vec3> route, int routeStartIndex)
    {
        _route = route;
        _routeStart = routeStartIndex;
        _onRoute = approach is not { Count: >= 2 };
        _current = _onRoute ? new RecordedPathFollower(route, routeStartIndex, approach: false) : new RecordedPathFollower(approach!, 0, approach: true);
    }

    /// <summary>The steer along the approach or the route; null at the route's end.</summary>
    public RecordedPathFollower.Steer? Follow(double e, double n, double headingRad, double wheelbase, double maxSteerDegrees)
    {
        if (_current.Follow(e, n, headingRad, wheelbase, maxSteerDegrees) is { } s) return s;
        if (_onRoute) return null;
        _onRoute = true;
        _current = new RecordedPathFollower(_route, _routeStart, approach: false);
        return _current.Follow(e, n, headingRad, wheelbase, maxSteerDegrees);
    }
}
