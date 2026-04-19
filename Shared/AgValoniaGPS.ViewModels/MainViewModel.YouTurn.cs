// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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
using System.Collections.Generic;
using System.Linq;

using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Guidance;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Models.YouTurn;
using AgValoniaGPS.Services.YouTurn;
using Microsoft.Extensions.Logging;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial class containing YouTurn (U-turn) logic.
/// Handles automatic U-turn path creation, guidance, and execution.
/// </summary>
public partial class MainViewModel
{
    // YouTurn state, turn direction, zone tracking, snake sequence, pass offsets:
    // All live on State.YouTurn and State.Guidance. See YouTurnState.cs / GuidanceState.cs.
    // TractorZone enum is in AgValoniaGPS.Models.State.TractorZone.

    #region YouTurn Properties

    private bool _isYouTurnEnabled; // YouTurn auto U-turn feature

    public bool IsYouTurnEnabled
    {
        get => _isYouTurnEnabled;
        set => SetProperty(ref _isYouTurnEnabled, value);
    }

    private int _uTurnSkipRows;

    /// <summary>
    /// Number of rows to skip during U-turn (0-9)
    /// </summary>
    public int UTurnSkipRows
    {
        get => _uTurnSkipRows;
        set => SetProperty(ref _uTurnSkipRows, Math.Max(0, Math.Min(9, value)));
    }

    private bool _isUTurnSkipRowsEnabled;

    /// <summary>
    /// When true, U-turn skip rows feature is enabled
    /// </summary>
    public bool IsUTurnSkipRowsEnabled
    {
        get => _isUTurnSkipRowsEnabled;
        set => SetProperty(ref _isUTurnSkipRowsEnabled, value);
    }

    private bool _isSkipWorkedMode;
    /// <summary>
    /// When true, U-turns skip over already-worked paths to find the next unworked one.
    /// This enables the skip-and-fill pattern: work every Nth row, then return to fill gaps.
    /// </summary>
    public bool IsSkipWorkedMode
    {
        get => _isSkipWorkedMode;
        set => SetProperty(ref _isSkipWorkedMode, value);
    }

    #endregion

    #region YouTurn Methods

    /// <summary>
    /// Clear all U-turn state - called when closing a field.
    /// </summary>
    public void ClearYouTurnState()
    {
        State.YouTurn.TurnPath = null;
        State.YouTurn.NextTrack = null;
        State.YouTurn.IsTriggered = false;
        State.YouTurn.IsExecuting = false;
        State.YouTurn.YouTurnCounter = 0;
        State.YouTurn.CurrentZone = TractorZone.OutsideBoundary;

        _mapService.SetYouTurnPath(null);
        _mapService.SetNextTrack(null);
        _mapService.SetIsInYouTurn(false);
    }

    /// <summary>
    /// Manually trigger a left U-turn. Used for tracks along boundaries where
    /// automatic headland detection doesn't work.
    /// </summary>
    public void TriggerManualYouTurnLeft()
    {
        TriggerManualYouTurn(turnLeft: true);
    }

    /// <summary>
    /// Manually trigger a right U-turn. Used for tracks along boundaries where
    /// automatic headland detection doesn't work.
    /// </summary>
    public void TriggerManualYouTurnRight()
    {
        TriggerManualYouTurn(turnLeft: false);
    }

    /// <summary>
    /// Trigger a manual U-turn in the specified direction.
    /// Creates the turn path immediately without waiting for headland detection.
    /// </summary>
    private void TriggerManualYouTurn(bool turnLeft)
    {
        // Must have autosteer engaged and a track selected
        if (!IsAutoSteerEngaged || SelectedTrack == null)
        {
            StatusMessage = "Enable autosteer first";
            return;
        }

        // Don't create a new turn if already in one
        if (State.YouTurn.IsExecuting || State.YouTurn.TurnPath != null)
        {
            StatusMessage = "U-turn already in progress";
            return;
        }

        var track = SelectedTrack;
        if (track.Points.Count < 2)
        {
            StatusMessage = "Invalid track";
            return;
        }

        // Get current position from GPS
        var currentPosition = new AgValoniaGPS.Models.Position
        {
            Easting = Easting,
            Northing = Northing,
            Heading = Heading
        };

        double headingRadians = currentPosition.Heading * Math.PI / 180.0;

        // Calculate track heading
        var trackPointA = track.Points[0];
        var trackPointB = track.Points[track.Points.Count - 1];
        double abDx = trackPointB.Easting - trackPointA.Easting;
        double abDy = trackPointB.Northing - trackPointA.Northing;
        double abHeading = Math.Atan2(abDx, abDy);

        // Determine if vehicle is heading same way as AB line
        double headingDiff = headingRadians - abHeading;
        while (headingDiff > Math.PI) headingDiff -= 2 * Math.PI;
        while (headingDiff < -Math.PI) headingDiff += 2 * Math.PI;
        State.Guidance.IsHeadingSameWay = Math.Abs(headingDiff) < Math.PI / 2;

        // Set turn direction and save heading state for offset calculation
        State.YouTurn.IsTurnLeft = turnLeft;
        State.YouTurn.WasHeadingSameWayAtTurnStart = State.Guidance.IsHeadingSameWay;

        _logger.LogDebug($"[ManualYouTurn] Triggering {(turnLeft ? "LEFT" : "RIGHT")} turn, isHeadingSameWay={State.Guidance.IsHeadingSameWay}");

        // Compute next track and create turn path
        _youTurnPathingService.ComputeNextTrack(
            track, abHeading, State.Guidance, State.YouTurn,
            UTurnSkipRows, IsSkipWorkedMode, SelectedTrack);
        _mapService.SetNextTrack(State.YouTurn.NextTrack);
        _mapService.SetIsInYouTurn(true);
        CreateYouTurnPath(currentPosition, headingRadians, abHeading);

        if (State.YouTurn.TurnPath != null && State.YouTurn.TurnPath.Count > 2)
        {
            // Immediately trigger the turn (don't wait for proximity to start point)
            State.YouTurn.IsTriggered = true;
            State.YouTurn.IsExecuting = true;
            StatusMessage = $"Manual {(turnLeft ? "left" : "right")} U-turn started";
        }
        else
        {
            StatusMessage = "Failed to create U-turn path";
        }
    }

    #endregion

    #region YouTurn Processing

    /// <summary>
    /// Process YouTurn - check distance to headland, create turn path if needed, trigger turn.
    /// </summary>
    private void ProcessYouTurn(AgValoniaGPS.Models.Position currentPosition)
    {
        var track = SelectedTrack;
        if (track == null || track.Points.Count < 2 || _currentHeadlandLine == null) return;

        double headingRadians = currentPosition.Heading * Math.PI / 180.0;
        bool isCurve = track.Points.Count > 2;

        double abHeading;
        if (isCurve)
        {
            // For curves, find the nearest point and use its local heading
            double minDistSq = double.MaxValue;
            int nearestIdx = 0;
            for (int i = 0; i < track.Points.Count; i++)
            {
                double dx = track.Points[i].Easting - currentPosition.Easting;
                double dy = track.Points[i].Northing - currentPosition.Northing;
                double distSq = dx * dx + dy * dy;
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    nearestIdx = i;
                }
            }
            abHeading = track.Points[nearestIdx].Heading;
            _logger.LogDebug($"[YouTurn] Curve mode: nearest index={nearestIdx}, localHeading={abHeading * 180 / Math.PI:F1}°");
        }
        else
        {
            // For AB lines, calculate heading from first to last point
            var trackPointA = track.Points[0];
            var trackPointB = track.Points[1];
            double abDx = trackPointB.Easting - trackPointA.Easting;
            double abDy = trackPointB.Northing - trackPointA.Northing;
            abHeading = Math.Atan2(abDx, abDy);
            if (State.YouTurn.YouTurnCounter % 30 == 0)
                _logger.LogDebug($"[YouTurn] AB Line: abHeading={abHeading * 180 / Math.PI:F1}°");
        }

        // Determine if vehicle is heading the same way as the AB line
        double headingDiff = headingRadians - abHeading;
        while (headingDiff > Math.PI) headingDiff -= 2 * Math.PI;
        while (headingDiff < -Math.PI) headingDiff += 2 * Math.PI;
        State.Guidance.IsHeadingSameWay = Math.Abs(headingDiff) < Math.PI / 2;

        // Check if vehicle is aligned with AB line (not mid-turn)
        // We need to be within ~20 degrees of the AB line direction (either forward or reverse)
        // Math.Abs(headingDiff) < PI/2 means heading same way, > PI/2 means opposite
        // We want to check alignment to either direction of the AB line
        double alignmentTolerance = Math.PI / 9;  // ~20 degrees
        bool alignedForward = Math.Abs(headingDiff) < alignmentTolerance;
        bool alignedReverse = Math.Abs(headingDiff) > (Math.PI - alignmentTolerance);
        bool isAlignedWithABLine = alignedForward || alignedReverse;

        // Only calculate distance to headland when aligned with the AB line
        // This prevents creating turns while mid-turn when heading changes rapidly
        double travelHeading = abHeading;
        if (!State.Guidance.IsHeadingSameWay)
        {
            travelHeading += Math.PI;
            if (travelHeading >= Math.PI * 2) travelHeading -= Math.PI * 2;
        }

        if (isAlignedWithABLine)
        {
            // Calculate distance to headland using raycast
            // Only triggers automatic U-turns when there's a headland line defined
            State.YouTurn.DistanceToHeadland = CalculateDistanceToHeadland(currentPosition, travelHeading);
        }
        else
        {
            State.YouTurn.DistanceToHeadland = double.MaxValue;  // Don't detect headland if not aligned
        }

        // Update zone tracking
        State.YouTurn.CurrentZone = DetermineCurrentZone(currentPosition.Easting, currentPosition.Northing);

        bool isInCultivatedArea = State.YouTurn.CurrentZone == TractorZone.InCultivatedArea;
        bool isInHeadlandZone = State.YouTurn.CurrentZone == TractorZone.InHeadland;

        // AgOpenGPS creates turns while in CULTIVATED AREA approaching headland
        // The turn path has a leg that extends back into the cultivated area
        // Distance-based creation window: 10-60m from headland
        double minDistanceToCreate = 10.0;
        double maxDistanceToCreate = 60.0;
        bool headlandInRange = State.YouTurn.DistanceToHeadland > minDistanceToCreate &&
                               State.YouTurn.DistanceToHeadland < maxDistanceToCreate;

        // Debug logging
        if (State.YouTurn.TurnPath == null && !State.YouTurn.IsExecuting && State.YouTurn.DistanceToHeadland < 100)
        {
            _logger.LogDebug($"[YouTurn] Zone={State.YouTurn.CurrentZone}, dist={State.YouTurn.DistanceToHeadland:F1}m, aligned={isAlignedWithABLine}, inRange={headlandInRange}");
        }

        // TURN CREATION: Approaching headland, aligned with track
        // In snake mode, also trigger from headland zone (tractor may be in headland at far end of field)
        bool canCreateTurn = isInCultivatedArea && headlandInRange;
        if (!canCreateTurn && _isSkipWorkedMode && isInHeadlandZone && isAlignedWithABLine
            && State.YouTurn.SnakeSequence != null && State.YouTurn.TurnPath == null && !State.YouTurn.IsExecuting)
        {
            // In headland at far end: check if we have more paths in the sequence
            canCreateTurn = _youTurnPathingService.GetNextSnakePath(State.YouTurn).HasValue;
        }
        if (State.YouTurn.TurnPath == null && !State.YouTurn.IsExecuting && canCreateTurn && isAlignedWithABLine)
        {
            // SNAKE MODE: Use pre-computed sequence for skip-and-fill
            if (_isSkipWorkedMode)
            {
                // Build snake sequence on first turn
                if (State.YouTurn.SnakeSequence == null)
                {
                    _youTurnPathingService.BuildSnakeSequence(
                        track, abHeading, State.Guidance, State.YouTurn,
                        _currentBoundary, _currentHeadlandLine);
                }

                int? nextPath = _youTurnPathingService.GetNextSnakePath(State.YouTurn);
                if (nextPath.HasValue)
                {
                    double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
                    double nextDistAway = widthMinusOverlap * nextPath.Value;
                    int pathDiff = nextPath.Value - State.Guidance.HowManyPathsAway;

                    // Determine turn direction from path difference
                    bool positiveOffset = pathDiff > 0;
                    State.YouTurn.IsTurnLeft = positiveOffset ^ State.Guidance.IsHeadingSameWay;
                    State.YouTurn.WasHeadingSameWayAtTurnStart = State.Guidance.IsHeadingSameWay;
                    State.YouTurn.NextTrackTurnOffset = Math.Abs(pathDiff) * widthMinusOverlap;

                    // Build next track at exact offset
                    var refA = track.Points[0];
                    var refB = track.Points[track.Points.Count - 1];
                    double perpAngle = abHeading + Math.PI / 2;

                    if (track.Points.Count == 2)
                    {
                        double offsetE = Math.Sin(perpAngle) * nextDistAway;
                        double offsetN = Math.Cos(perpAngle) * nextDistAway;
                        State.YouTurn.NextTrack = Track.FromABLine($"Path {nextPath.Value}",
                            new Vec3(refA.Easting + offsetE, refA.Northing + offsetN, abHeading),
                            new Vec3(refB.Easting + offsetE, refB.Northing + offsetN, abHeading));
                    }
                    else
                    {
                        var offsetPoints = Models.Guidance.CurveProcessing.CreateOffsetCurve(track.Points, nextDistAway);
                        State.YouTurn.NextTrack = Track.FromCurve($"Path {nextPath.Value}", offsetPoints, track.IsClosed);
                    }
                    State.YouTurn.NextTrack.IsActive = false;

                    // Store target for CompleteYouTurn
                    State.YouTurn.ReturnPassTargetPath = nextPath.Value;

                    _logger.LogDebug($"[YouTurn] Snake: path {State.Guidance.HowManyPathsAway} -> {nextPath.Value} (diff={pathDiff}, offset={nextDistAway:F1}m, turnLeft={State.YouTurn.IsTurnLeft})");
                    _mapService.SetNextTrack(State.YouTurn.NextTrack);
                    _mapService.SetIsInYouTurn(true);
                    CreateYouTurnPath(currentPosition, headingRadians, abHeading);
                }
                else
                {
                    _logger.LogDebug("[YouTurn] Snake sequence complete — field done");
                    StatusMessage = "Field complete — all tracks worked";
                }
            }
            // NORMAL MODE: Standard skip logic
            else
            {
                bool nextLineInside = _youTurnPathingService.WouldNextLineBeInsideBoundary(
                    track, abHeading, State.Guidance,
                    _currentBoundary, _currentHeadlandLine,
                    UTurnSkipRows);
                _logger.LogDebug($"[YouTurn] Creating turn? nextLineInside={nextLineInside}");
                if (nextLineInside)
                {
                    _logger.LogDebug($"[YouTurn] Creating turn path at {State.YouTurn.DistanceToHeadland:F1}m from headland");
                    State.YouTurn.IsTurnLeft = State.Guidance.IsHeadingSameWay;
                    State.YouTurn.WasHeadingSameWayAtTurnStart = State.Guidance.IsHeadingSameWay;
                    _youTurnPathingService.ComputeNextTrack(
                        track, abHeading, State.Guidance, State.YouTurn,
                        UTurnSkipRows, IsSkipWorkedMode, SelectedTrack);
                    _mapService.SetNextTrack(State.YouTurn.NextTrack);
                    _mapService.SetIsInYouTurn(true);
                    CreateYouTurnPath(currentPosition, headingRadians, abHeading);
                }
                else
                {
                    _logger.LogDebug("[YouTurn] Next line would be outside boundary - stopping U-turns");
                    StatusMessage = "End of field reached";
                }
            }
        }
        // TURN TRIGGER: When path is ready, trigger when close to turn start point
        else if (State.YouTurn.TurnPath != null && State.YouTurn.TurnPath.Count > 2 && !State.YouTurn.IsTriggered && !State.YouTurn.IsExecuting)
        {
            var turnStart = State.YouTurn.TurnPath[0];
            double distToTurnStart = Math.Sqrt(
                Math.Pow(currentPosition.Easting - turnStart.Easting, 2) +
                Math.Pow(currentPosition.Northing - turnStart.Northing, 2));

            // Trigger when within 2 meters of turn start
            if (distToTurnStart <= 2.0)
            {
                State.YouTurn.IsTriggered = true;
                State.YouTurn.IsExecuting = true;
                StatusMessage = "YouTurn triggered!";
                _logger.LogDebug($"[YouTurn] Triggered at {distToTurnStart:F2}m from turn start");
            }
        }
        // RESET: If entered headland with untriggered turn, reset (drove past turn start)
        else if (State.YouTurn.TurnPath != null && !State.YouTurn.IsTriggered && isInHeadlandZone)
        {
            _logger.LogDebug("[YouTurn] Entered headland without triggering - resetting turn");
            State.YouTurn.TurnPath = null;
            State.YouTurn.NextTrack = null;
            _mapService.SetYouTurnPath(null);
            _mapService.SetNextTrack(null);
        }

        // Check if U-turn is complete (vehicle reached end of turn path)
        if (State.YouTurn.IsExecuting && State.YouTurn.TurnPath != null && State.YouTurn.TurnPath.Count > 2)
        {
            var startPoint = State.YouTurn.TurnPath[0];
            var endPoint = State.YouTurn.TurnPath[State.YouTurn.TurnPath.Count - 1];

            double distToTurnStart = Math.Sqrt(
                Math.Pow(currentPosition.Easting - startPoint.Easting, 2) +
                Math.Pow(currentPosition.Northing - startPoint.Northing, 2));
            double distToTurnEnd = Math.Sqrt(
                Math.Pow(currentPosition.Easting - endPoint.Easting, 2) +
                Math.Pow(currentPosition.Northing - endPoint.Northing, 2));

            // Complete turn when:
            // 1. Within 2 meters of turn end, AND
            // 2. Closer to end than to start (prevents immediate completion when start/end are close)
            // 3. At least 5 meters from start (ensures we've actually traveled into the turn)
            if (distToTurnEnd <= 2.0 && distToTurnEnd < distToTurnStart && distToTurnStart > 5.0)
            {
                CompleteYouTurn();
            }
        }
    }

    /// <summary>
    /// Check if a point is inside the outer boundary.
    /// </summary>
    private bool IsPointInsideBoundary(double easting, double northing)
    {
        if (_currentBoundary?.OuterBoundary == null || !_currentBoundary.OuterBoundary.IsValid)
            return true;

        return _currentBoundary.OuterBoundary.IsPointInside(easting, northing);
    }

    /// <summary>
    /// Minimum distance from a point to the outer boundary polygon.
    /// </summary>
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
                easting, northing,
                p1.Easting, p1.Northing,
                p2.Easting, p2.Northing);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }

    /// <summary>
    /// Check if a track runs along the boundary (high % of points near boundary).
    /// Returns true if the track should skip boundary disengage on first pass.
    /// </summary>
    private bool IsTrackOnBoundary(Track? track, double threshold = 5.0, double minOverlapPercent = 0.5)
    {
        if (track == null || track.Points.Count == 0)
            return false;

        if (_currentBoundary?.OuterBoundary == null || !_currentBoundary.OuterBoundary.IsValid)
            return false;

        int pointsNearBoundary = 0;
        foreach (var point in track.Points)
        {
            if (DistanceToBoundary(point.Easting, point.Northing) < threshold)
                pointsNearBoundary++;
        }

        double overlapPercent = (double)pointsNearBoundary / track.Points.Count;
        return overlapPercent >= minOverlapPercent;
    }

    /// <summary>
    /// Complete the U-turn: switch to the next line and reset state.
    /// </summary>
    private void CompleteYouTurn()
    {
        // Guard against double-calling (can be triggered from both ProcessYouTurn and CalculateYouTurnGuidance)
        if (!State.YouTurn.IsExecuting)
        {
            _logger.LogDebug("[YouTurn] CompleteYouTurn called but not in turn - ignoring");
            return;
        }

        // If target path was set (by snake sequence or boundary-hit block), jump directly to it
        if (State.YouTurn.ReturnPassTargetPath.HasValue)
        {
            _logger.LogDebug($"[YouTurn] Turn complete! Jumping to path {State.YouTurn.ReturnPassTargetPath.Value} (was {State.Guidance.HowManyPathsAway})");
            State.Guidance.HowManyPathsAway = State.YouTurn.ReturnPassTargetPath.Value;
            State.YouTurn.ReturnPassTargetPath = null;
            _youTurnPathingService.AdvanceSnakeSequence(State.YouTurn);
        }
        else
        {
            // Normal path: determine offset using XOR
            int rowSkipWidth = UTurnSkipRows;
            int pathsToMove = rowSkipWidth + 1;

            // IMPORTANT: Use State.YouTurn.WasHeadingSameWayAtTurnStart (saved at turn creation), NOT State.Guidance.IsHeadingSameWay
            // (which has now flipped because we completed a 180° turn)
            bool positiveOffset = State.YouTurn.IsTurnLeft ^ State.YouTurn.WasHeadingSameWayAtTurnStart;

            // Skip-worked mode: find next unworked path
            if (_isSkipWorkedMode && SelectedTrack != null)
            {
                pathsToMove = _youTurnPathingService.GetNextUnworkedPathSkip(
                    SelectedTrack, State.Guidance.HowManyPathsAway, positiveOffset, pathsToMove);
            }

            int offsetChange = positiveOffset ? pathsToMove : -pathsToMove;
            State.Guidance.HowManyPathsAway += offsetChange;

            _logger.LogDebug($"[YouTurn] Turn complete! Normal: offset {(positiveOffset ? "positive" : "negative")} by {offsetChange}");
        }

        _logger.LogDebug($"[YouTurn] Now on path {State.Guidance.HowManyPathsAway} ({(ConfigStore.ActualToolWidth - Tool.Overlap) * State.Guidance.HowManyPathsAway:F1}m from reference)");
        _logger.LogDebug($"[YouTurn] Total offset: {(ConfigStore.ActualToolWidth - Tool.Overlap) * State.Guidance.HowManyPathsAway:F1}m from reference line");

        // Remember this turn direction for alternating pattern.
        State.YouTurn.LastTurnWasLeft = State.YouTurn.IsTurnLeft;
        State.YouTurn.HasCompletedFirstTurn = true;
        State.YouTurn.IsTriggered = false;
        State.YouTurn.IsExecuting = false;
        State.YouTurn.TurnPath = null;
        State.YouTurn.NextTrack = null;
        State.YouTurn.YouTurnCounter = 10; // Keep high so next U-turn path is created when conditions are met

        // CRITICAL: Reset guidance state to force global search on new offset track
        // Without this, the guidance uses the old CurrentLocationIndex which points to
        // the wrong position on the new track, causing the tractor to loop back
        _trackGuidanceState = null;

        // Update map visualization - clear the old turn path and next line
        // The display track will be updated by the pipeline via GpsCycleResult
        _mapService.SetYouTurnPath(null);
        _mapService.SetNextTrack(null);
        _mapService.SetIsInYouTurn(false);

        // Sync updated pass number to pipeline so guidance targets the new track
        SyncGuidanceStateToPipeline();

        StatusMessage = $"Following path {State.Guidance.HowManyPathsAway} ({(ConfigStore.ActualToolWidth - Tool.Overlap) * Math.Abs(State.Guidance.HowManyPathsAway):F1}m offset)";
    }

    /// <summary>
    /// Calculate distance from current position to the headland boundary in the direction of travel.
    /// </summary>
    private double CalculateDistanceToHeadland(AgValoniaGPS.Models.Position currentPosition, double headingRadians)
    {
        if (_currentHeadlandLine == null || _currentHeadlandLine.Count < 3)
            return double.MaxValue;

        // Use a simple raycast approach
        double minDistance = double.MaxValue;
        Vec2 pos = new Vec2(currentPosition.Easting, currentPosition.Northing);
        Vec2 dir = new Vec2(Math.Sin(headingRadians), Math.Cos(headingRadians));

        int intersectionCount = 0;
        int n = _currentHeadlandLine.Count;
        for (int i = 0; i < n; i++)
        {
            var p1 = _currentHeadlandLine[i];
            var p2 = _currentHeadlandLine[(i + 1) % n];

            // Ray-segment intersection
            Vec2 edge = new Vec2(p2.Easting - p1.Easting, p2.Northing - p1.Northing);
            Vec2 toP1 = new Vec2(p1.Easting - pos.Easting, p1.Northing - pos.Northing);

            double cross = dir.Easting * edge.Northing - dir.Northing * edge.Easting;
            if (Math.Abs(cross) < 1e-10) continue; // Parallel

            double t = (toP1.Easting * edge.Northing - toP1.Northing * edge.Easting) / cross;
            double u = (toP1.Easting * dir.Northing - toP1.Northing * dir.Easting) / cross;

            if (t > 0 && u >= 0 && u <= 1)
            {
                intersectionCount++;
                if (t < minDistance)
                    minDistance = t;
            }
        }

        // Debug: Log periodically to see what's happening
        if (State.YouTurn.YouTurnCounter % 120 == 0)
        {
            double headingDeg = headingRadians * 180.0 / Math.PI;
            _logger.LogDebug($"[Headland] Raycast: pos=({pos.Easting:F1},{pos.Northing:F1}), heading={headingDeg:F0}°, intersections={intersectionCount}, minDist={minDistance:F1}m, isHeadingSameWay={State.Guidance.IsHeadingSameWay}");
        }

        return minDistance;
    }

    /// <summary>
    /// Determine which zone the tractor is currently in based on boundary polygons.
    /// Uses ray casting algorithm without allocations.
    /// </summary>
    private TractorZone DetermineCurrentZone(double easting, double northing)
    {
        // Check if inside headland (cultivated area) first - most common case
        if (_currentHeadlandLine != null && _currentHeadlandLine.Count >= 3)
        {
            if (GeometryMath.IsPointInPolygon(_currentHeadlandLine, new Vec2(easting, northing)))
                return TractorZone.InCultivatedArea;
        }

        // Check if inside outer boundary (headland zone)
        if (_currentBoundary?.OuterBoundary != null && _currentBoundary.OuterBoundary.IsValid)
        {
            if (_currentBoundary.OuterBoundary.IsPointInside(easting, northing))
                return TractorZone.InHeadland;
        }

        // Outside everything
        return TractorZone.OutsideBoundary;
    }

    #endregion

    #region YouTurn Path Creation

    /// <summary>
    /// Create a YouTurn path when approaching headland. All geometry lives in
    /// YouTurnCreationService; this method just adapts state → service → map update.
    /// </summary>
    private void CreateYouTurnPath(AgValoniaGPS.Models.Position currentPosition, double headingRadians, double abHeading)
    {
        var track = SelectedTrack;
        if (track == null || _currentHeadlandLine == null) return;

        var result = _youTurnCreationService.CreateTurnPath(
            currentPosition, track, headingRadians, abHeading,
            _currentBoundary, _currentHeadlandLine,
            State.Guidance, State.YouTurn,
            UTurnSkipRows, HeadlandCalculatedWidth, HeadlandDistance);

        if (result.Path == null) return;

        State.YouTurn.TurnPath = result.Path;
        State.YouTurn.YouTurnCounter = 0;
        if (!result.UsedFallback)
            StatusMessage = $"YouTurn path created ({result.Path.Count} points)";
        _mapService.SetYouTurnPath(result.Path.Select(p => (p.Easting, p.Northing)).ToList());
    }

    #endregion
}
