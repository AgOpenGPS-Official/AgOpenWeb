// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;

namespace AgValoniaGPS.Models.Pipeline;

/// <summary>
/// Immutable per-cycle snapshot of <see cref="GuidanceWorkingState"/>.
/// Built by the cycle worker at end-of-tick, consumed on the UI thread by
/// ApplyGpsCycleResult to mirror onto <see cref="State.GuidanceState"/>.
/// See <c>Plans/threading_model.svg</c> (cycle stage 3 — Emit snapshot).
/// </summary>
public record GuidanceSnapshot
{
    // Active track
    public Track.Track? ActiveTrack { get; init; }
    public bool IsGuidanceActive { get; init; }

    // Errors
    public double CrossTrackError { get; init; }
    public double HeadingError { get; init; }

    // Steering output
    public double SteerAngle { get; init; }
    public short SteerAngleRaw { get; init; }
    public short DistanceOffRaw { get; init; }

    // Pure Pursuit state
    public double PpIntegral { get; init; }
    public double PpPivotDistanceError { get; init; }
    public double PpPivotDistanceErrorLast { get; init; }
    public int PpCounter { get; init; }

    // Visualization points
    public Vec2 GoalPoint { get; init; }
    public Vec2 RadiusPoint { get; init; }
    public double PurePursuitRadius { get; init; }

    // Direction relative to track
    public bool IsHeadingSameWay { get; init; }
    public bool IsReverse { get; init; }

    // Line offset
    public int HowManyPathsAway { get; init; }
    public double NudgeOffset { get; init; }
    public string CurrentLineLabel { get; init; } = "1L";

    public bool IsContourMode { get; init; }
}
