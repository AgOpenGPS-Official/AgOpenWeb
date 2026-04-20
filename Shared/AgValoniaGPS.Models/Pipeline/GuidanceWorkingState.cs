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
/// Cycle-worker-owned mirror of <see cref="State.GuidanceState"/>.
/// Plain POCO: no ObservableObject, no PropertyChanged, no UI thread awareness.
/// Single-writer — the cycle worker mutates freely. Never touched on the UI thread.
/// See <c>Plans/threading_model.svg</c> (yellow WorkingState box).
/// </summary>
public class GuidanceWorkingState
{
    // Active track (unified Track model)
    public Track.Track? ActiveTrack { get; set; }
    public bool IsGuidanceActive { get; set; }

    // Cross-track error (meters, positive = right of line)
    public double CrossTrackError { get; set; }
    public double HeadingError { get; set; }

    // Steering output (degrees)
    public double SteerAngle { get; set; }

    // Raw values for UDP transmission
    public short SteerAngleRaw { get; set; }
    public short DistanceOffRaw { get; set; } // mm

    // Pure Pursuit state (persisted between frames)
    public double PpIntegral { get; set; }
    public double PpPivotDistanceError { get; set; }
    public double PpPivotDistanceErrorLast { get; set; }
    public int PpCounter { get; set; }

    // Visualization points
    public Vec2 GoalPoint { get; set; }
    public Vec2 RadiusPoint { get; set; }
    public double PurePursuitRadius { get; set; }

    // Direction relative to track.
    // Default matches GuidanceState — the backing field defaults to false at construction,
    // and only Reset() sets it to true (the "assume aligned" post-reset convention).
    public bool IsHeadingSameWay { get; set; }
    public bool IsReverse { get; set; }

    // Line offset (how many passes from original)
    public int HowManyPathsAway { get; set; }

    // Fine nudge offset in meters, added on top of the whole-pass offset from HowManyPathsAway.
    public double NudgeOffset { get; set; }

    public string CurrentLineLabel { get; set; } = "1L";

    // Contour mode
    public bool IsContourMode { get; set; }

    public void Reset()
    {
        ActiveTrack = null;
        IsGuidanceActive = false;
        CrossTrackError = HeadingError = SteerAngle = 0;
        SteerAngleRaw = DistanceOffRaw = 0;
        PpIntegral = PpPivotDistanceError = PpPivotDistanceErrorLast = 0;
        PpCounter = 0;
        GoalPoint = new Vec2();
        RadiusPoint = new Vec2();
        PurePursuitRadius = 0;
        IsHeadingSameWay = true;
        IsReverse = false;
        HowManyPathsAway = 0;
        NudgeOffset = 0;
        CurrentLineLabel = "1L";
        IsContourMode = false;
    }
}
