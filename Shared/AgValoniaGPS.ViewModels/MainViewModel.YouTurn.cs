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

using System;
using System.Linq;

using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.YouTurn;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial class containing YouTurn (U-turn) UI glue.
/// The state machine, pathing, path creation, and geometry all live in services;
/// this file exposes UI properties/commands and applies state-machine side effects
/// (map updates, guidance reset, status message).
/// </summary>
public partial class MainViewModel
{
    // YouTurn state, turn direction, zone tracking, snake sequence, pass offsets:
    // All live on State.YouTurn and State.Guidance. See YouTurnState.cs / GuidanceState.cs.
    // TractorZone enum is in AgValoniaGPS.Models.State.TractorZone.

    #region YouTurn UI Properties

    private bool _isYouTurnEnabled;
    public bool IsYouTurnEnabled
    {
        get => _isYouTurnEnabled;
        set => SetProperty(ref _isYouTurnEnabled, value);
    }

    private int _uTurnSkipRows;
    /// <summary>Number of rows to skip during U-turn (0–9).</summary>
    public int UTurnSkipRows
    {
        get => _uTurnSkipRows;
        set => SetProperty(ref _uTurnSkipRows, Math.Max(0, Math.Min(9, value)));
    }

    private bool _isUTurnSkipRowsEnabled;
    public bool IsUTurnSkipRowsEnabled
    {
        get => _isUTurnSkipRowsEnabled;
        set => SetProperty(ref _isUTurnSkipRowsEnabled, value);
    }

    private bool _isSkipWorkedMode;
    /// <summary>
    /// When true, U-turns skip over already-worked paths to find the next unworked one,
    /// enabling the skip-and-fill pattern (work every Nth row, then return to fill gaps).
    /// </summary>
    public bool IsSkipWorkedMode
    {
        get => _isSkipWorkedMode;
        set => SetProperty(ref _isSkipWorkedMode, value);
    }

    #endregion

    #region YouTurn Entry Points

    // Phase C C3 bridge: the state machine now takes YouTurnWorkingState (POCO),
    // but the tick still runs on the UI thread from this file. Until C4 moves
    // the tick into the cycle worker, we copy State.YouTurn (observable) into
    // a local working-state scratch, run the machine, and copy back. Removed
    // wholesale in C4 — don't generalize these bridge helpers.
    private readonly YouTurnWorkingState _youTurnBridge = new();

    /// <summary>Clear all U-turn state — called when closing a field.</summary>
    public void ClearYouTurnState()
    {
        BridgeStateToWorking(State.YouTurn, _youTurnBridge);
        YouTurnStateMachine.ClearState(_youTurnBridge);
        BridgeWorkingToState(_youTurnBridge, State.YouTurn);
        _mapService.SetYouTurnPath(null);
        _mapService.SetNextTrack(null);
        _mapService.SetIsInYouTurn(false);
    }

    /// <summary>Manually trigger a left U-turn.</summary>
    public void TriggerManualYouTurnLeft() => TriggerManualYouTurn(turnLeft: true);

    /// <summary>Manually trigger a right U-turn.</summary>
    public void TriggerManualYouTurnRight() => TriggerManualYouTurn(turnLeft: false);

    private void TriggerManualYouTurn(bool turnLeft)
    {
        BridgeStateToWorking(State.YouTurn, _youTurnBridge);
        var effects = _youTurnStateMachine.TriggerManual(
            turnLeft, IsAutoSteerEngaged, BuildTickContext(GetCurrentGpsPosition()),
            State.Guidance, _youTurnBridge);
        BridgeWorkingToState(_youTurnBridge, State.YouTurn);
        ApplyEffects(effects);
    }

    /// <summary>
    /// Drive the state machine one cycle. Called from the GPS handler while autosteer
    /// is engaged, YouTurn is enabled, and a headland line exists. Mutates state
    /// and applies the resulting side effects.
    /// </summary>
    internal void TickYouTurnStateMachine(AgValoniaGPS.Models.Position currentPosition)
    {
        BridgeStateToWorking(State.YouTurn, _youTurnBridge);
        var effects = _youTurnStateMachine.Tick(
            BuildTickContext(currentPosition), State.Guidance, _youTurnBridge);
        BridgeWorkingToState(_youTurnBridge, State.YouTurn);
        ApplyEffects(effects);
    }

    private static void BridgeStateToWorking(AgValoniaGPS.Models.State.YouTurnState src, YouTurnWorkingState dst)
    {
        dst.IsEnabled = src.IsEnabled;
        dst.IsTriggered = src.IsTriggered;
        dst.IsExecuting = src.IsExecuting;
        dst.TurnPath = src.TurnPath;
        dst.PathIndex = src.PathIndex;
        dst.IsTurnLeft = src.IsTurnLeft;
        dst.LastTurnWasLeft = src.LastTurnWasLeft;
        dst.DistanceToHeadland = src.DistanceToHeadland;
        dst.DistanceToTrigger = src.DistanceToTrigger;
        dst.NextTrack = src.NextTrack;
        dst.LastCompletionPosition = src.LastCompletionPosition;
        dst.HasCompletedFirstTurn = src.HasCompletedFirstTurn;
        dst.YouTurnCounter = src.YouTurnCounter;
        dst.WasHeadingSameWayAtTurnStart = src.WasHeadingSameWayAtTurnStart;
        dst.NextTrackTurnOffset = src.NextTrackTurnOffset;
        dst.ReturnPassTargetPath = src.ReturnPassTargetPath;
        dst.SnakeSequence = src.SnakeSequence;
        dst.SnakeIndex = src.SnakeIndex;
        dst.CurrentZone = src.CurrentZone;
    }

    private static void BridgeWorkingToState(YouTurnWorkingState src, AgValoniaGPS.Models.State.YouTurnState dst)
    {
        dst.IsEnabled = src.IsEnabled;
        dst.IsTriggered = src.IsTriggered;
        dst.IsExecuting = src.IsExecuting;
        dst.TurnPath = src.TurnPath;
        dst.PathIndex = src.PathIndex;
        dst.IsTurnLeft = src.IsTurnLeft;
        dst.LastTurnWasLeft = src.LastTurnWasLeft;
        dst.DistanceToHeadland = src.DistanceToHeadland;
        dst.DistanceToTrigger = src.DistanceToTrigger;
        dst.NextTrack = src.NextTrack;
        dst.LastCompletionPosition = src.LastCompletionPosition;
        dst.HasCompletedFirstTurn = src.HasCompletedFirstTurn;
        dst.YouTurnCounter = src.YouTurnCounter;
        dst.WasHeadingSameWayAtTurnStart = src.WasHeadingSameWayAtTurnStart;
        dst.NextTrackTurnOffset = src.NextTrackTurnOffset;
        dst.ReturnPassTargetPath = src.ReturnPassTargetPath;
        dst.SnakeSequence = src.SnakeSequence;
        dst.SnakeIndex = src.SnakeIndex;
        dst.CurrentZone = src.CurrentZone;
    }

    #endregion

    #region VM utilities (used outside YouTurn too)

    /// <summary>
    /// Check if a track runs along the outer boundary (high percentage of points within
    /// <paramref name="threshold"/> meters). Tracks that do skip the boundary-disengage check
    /// on the first pass.
    /// </summary>
    private bool IsTrackOnBoundary(Track? track, double threshold = 5.0, double minOverlapPercent = 0.5)
    {
        if (track == null || track.Points.Count == 0) return false;
        if (_currentBoundary?.OuterBoundary == null || !_currentBoundary.OuterBoundary.IsValid) return false;

        int pointsNearBoundary = 0;
        foreach (var point in track.Points)
        {
            if (DistanceToBoundary(point.Easting, point.Northing) < threshold)
                pointsNearBoundary++;
        }

        return (double)pointsNearBoundary / track.Points.Count >= minOverlapPercent;
    }

    private double DistanceToBoundary(double easting, double northing)
    {
        if (_currentBoundary?.OuterBoundary == null || !_currentBoundary.OuterBoundary.IsValid)
            return double.MaxValue;

        var points = _currentBoundary.OuterBoundary.Points;
        double minDist = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Count];
            double dist = GeometryMath.PointToSegmentDistance(
                easting, northing, p1.Easting, p1.Northing, p2.Easting, p2.Northing);
            if (dist < minDist) minDist = dist;
        }
        return minDist;
    }

    #endregion

    #region Helpers

    private YouTurnStateMachine.TickContext BuildTickContext(AgValoniaGPS.Models.Position currentPosition)
        => new(
            currentPosition,
            SelectedTrack,
            _currentBoundary,
            _currentHeadlandLine,
            UTurnSkipRows,
            IsSkipWorkedMode,
            HeadlandCalculatedWidth,
            HeadlandDistance);

    private AgValoniaGPS.Models.Position GetCurrentGpsPosition() => new()
    {
        Easting = Easting,
        Northing = Northing,
        Heading = Heading,
    };

    private void ApplyEffects(YouTurnEffects effects)
    {
        if (effects.SyncTurnPathToMap)
        {
            _mapService.SetYouTurnPath(State.YouTurn.TurnPath?
                .Select(p => (p.Easting, p.Northing)).ToList());
        }
        if (effects.SyncNextTrackToMap)
        {
            _mapService.SetNextTrack(State.YouTurn.NextTrack);
        }
        if (effects.IsInYouTurnMapFlag.HasValue)
        {
            _mapService.SetIsInYouTurn(effects.IsInYouTurnMapFlag.Value);
        }
        if (effects.TurnCompleted)
        {
            // Force guidance to do a fresh global search on the new offset track instead of
            // resuming from the pre-turn CurrentLocationIndex (which points at the wrong track).
            _trackGuidanceState = null;
            SyncGuidanceStateToPipeline();
        }
        if (effects.StatusMessage != null)
        {
            StatusMessage = effects.StatusMessage;
        }
    }

    #endregion
}
