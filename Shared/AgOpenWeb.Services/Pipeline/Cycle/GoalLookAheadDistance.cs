// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Pure Pursuit look-ahead distance (m), AgOpenGPS CVehicle.UpdateGoalPointDistance: the track
/// guidance and the contour each keep their own last cross-track error and ask here. A function
/// of the configuration, no state.
/// </summary>
internal static class GoalLookAheadDistance
{
    public static double For(ConfigurationStore configStore, double speedKmh, double lastXte)
    {
        var g = configStore.Guidance;
        double hold = g.GoalPointLookAheadHold;
        double acquire = hold * g.GoalPointAcquireFactor;
        double xte = Math.Abs(lastXte);
        double h = xte <= 0.1 ? hold
                 : xte >= 0.4 ? acquire
                 : acquire + (1 - (xte - 0.1) / 0.3) * (hold - acquire);
        double d = Math.Abs(speedKmh) * 0.05 * g.GoalPointLookAheadMult * h + h;
        return Math.Max(g.MinLookAheadDistance, d);
    }

}
