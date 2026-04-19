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
    /// Create a YouTurn path when approaching headland.
    /// Uses a simplified direct approach that creates entry leg, semicircle, and exit leg.
    /// </summary>
    private void CreateYouTurnPath(AgValoniaGPS.Models.Position currentPosition, double headingRadians, double abHeading)
    {
        var track = SelectedTrack;
        if (track == null || _currentHeadlandLine == null) return;

        // Turn direction was already set before ComputeNextTrack was called
        bool turnLeft = State.YouTurn.IsTurnLeft;

        _logger.LogDebug($"[YouTurn] Creating turn with YouTurnCreationService: direction={(State.YouTurn.IsTurnLeft ? "LEFT" : "RIGHT")}, isHeadingSameWay={State.Guidance.IsHeadingSameWay}, pathsAway={State.Guidance.HowManyPathsAway}");

        // Build the YouTurnCreationInput with proper boundary wiring
        var input = BuildYouTurnCreationInput(currentPosition, headingRadians, abHeading, turnLeft);
        if (input == null)
        {
            _logger.LogWarning("[YouTurn] Failed to build creation input - no boundary available?");
            return;
        }

        // Use the YouTurnCreationService to create the path
        var output = _youTurnCreationService.CreateTurn(input);

        if (output.Success && output.TurnPath != null && output.TurnPath.Count > 10)
        {
            var path = output.TurnPath;

            // Check for spiral/pretzel pattern by measuring total heading change
            // A proper U-turn should have ~180° total heading change, not 360°+
            double totalHeadingChange = 0;
            for (int i = 1; i < path.Count; i++)
            {
                double delta = path[i].Heading - path[i - 1].Heading;
                // Normalize to -π to π
                while (delta > Math.PI) delta -= 2 * Math.PI;
                while (delta < -Math.PI) delta += 2 * Math.PI;
                totalHeadingChange += Math.Abs(delta);
            }

            // If total heading change exceeds 270° (π * 1.5), it's likely a spiral - use simple fallback
            if (totalHeadingChange > Math.PI * 1.5)
            {
                _logger.LogWarning($"[YouTurn] Service path has excessive heading change ({totalHeadingChange * 180 / Math.PI:F0}°) - using simple fallback");
                var fallbackPath = CreateSimpleUTurnPath(currentPosition, headingRadians, abHeading, turnLeft);
                if (fallbackPath != null && fallbackPath.Count > 10)
                {
                    State.YouTurn.TurnPath = fallbackPath;
                    State.YouTurn.YouTurnCounter = 0;
                    _mapService.SetYouTurnPath(State.YouTurn.TurnPath.Select(p => (p.Easting, p.Northing)).ToList());
                }
                return;
            }

            // Apply smoothing passes from config (1-50).
            TurnPathSmoothing.Smooth(path, Guidance.UTurnSmoothing);

            State.YouTurn.TurnPath = path;
            State.YouTurn.YouTurnCounter = 0;
            StatusMessage = $"YouTurn path created ({path.Count} points)";
            _logger.LogDebug($"[YouTurn] Path created with {path.Count} points");

            _mapService.SetYouTurnPath(State.YouTurn.TurnPath.Select(p => (p.Easting, p.Northing)).ToList());
        }
        else
        {
            _logger.LogWarning($"[YouTurn] Service failed: {output.FailureReason ?? "unknown"} - using simple fallback");
            var fallbackPath = CreateSimpleUTurnPath(currentPosition, headingRadians, abHeading, turnLeft);
            if (fallbackPath != null && fallbackPath.Count > 10)
            {
                State.YouTurn.TurnPath = fallbackPath;
                State.YouTurn.TurnPath = fallbackPath;
                State.YouTurn.YouTurnCounter = 0;
                _mapService.SetYouTurnPath(State.YouTurn.TurnPath.Select(p => (p.Easting, p.Northing)).ToList());
            }
        }
    }

    /// <summary>
    /// Find the track heading at the point where the CURRENT OFFSET TRACK crosses the headland.
    /// Uses the offset track, not the original, to get accurate heading for curves.
    /// </summary>
    private double FindTrackHeadingAtHeadland(Track track, Vec3 vehiclePos, bool headingSameWay)
    {
        if (_currentHeadlandLine == null || _currentHeadlandLine.Count < 3)
            return track.Points[0].Heading; // Fallback

        // Create the current offset track (same logic as guidance)
        double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
        double offsetDistance = State.Guidance.HowManyPathsAway * widthMinusOverlap;

        List<Vec3> searchPoints;
        if (Math.Abs(offsetDistance) < 0.01)
        {
            searchPoints = track.Points;
        }
        else
        {
            // Create clean offset curve that handles self-intersections on tight curves
            searchPoints = CurveProcessing.CreateOffsetCurve(track.Points, offsetDistance);
        }

        // Find nearest point to vehicle on the offset track
        int nearestIdx = 0;
        double minDistSq = double.MaxValue;
        for (int i = 0; i < searchPoints.Count; i++)
        {
            double dx = searchPoints[i].Easting - vehiclePos.Easting;
            double dy = searchPoints[i].Northing - vehiclePos.Northing;
            double distSq = dx * dx + dy * dy;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                nearestIdx = i;
            }
        }

        // Search from nearest point in direction of travel
        int step = headingSameWay ? 1 : -1;
        int endIdx = headingSameWay ? searchPoints.Count - 1 : 0;

        for (int i = nearestIdx; i != endIdx; i += step)
        {
            var p1 = searchPoints[i];
            var p2 = searchPoints[i + step];

            // Check if this segment crosses the headland
            for (int j = 0; j < _currentHeadlandLine.Count; j++)
            {
                var h1 = _currentHeadlandLine[j];
                var h2 = _currentHeadlandLine[(j + 1) % _currentHeadlandLine.Count];

                if (GeometryMath.SegmentsIntersect(
                        p1.Easting, p1.Northing, p2.Easting, p2.Northing,
                        h1.Easting, h1.Northing, h2.Easting, h2.Northing))
                {
                    // Found intersection - return the track heading at this segment
                    _logger.LogDebug($"[YouTurn] Found headland intersection on offset track (path {State.Guidance.HowManyPathsAway}) at index {i}, heading={p1.Heading * 180 / Math.PI:F1}°");
                    return p1.Heading;
                }
            }
        }

        // No intersection found, use heading at nearest point
        return searchPoints[nearestIdx].Heading;
    }

    /// <summary>
    /// Build the YouTurnCreationInput with proper boundary wiring.
    ///
    /// The IsPointInsideTurnArea delegate must return:
    /// - 0 = point is in the FIELD (safe to drive, inside headland boundary)
    /// - != 0 = point is in the TURN AREA (headland zone, where turn arc should be)
    ///
    /// We set this up with:
    /// - turnAreaPolygons[0] = outer field boundary (outer limit)
    /// - turnAreaPolygons[1] = headland boundary (inner limit, marks the field)
    ///
    /// So points between outer and headland return 0 (in outer but not in inner = headland zone... wait, that's wrong)
    /// Actually TurnAreaService returns 0 if in outer and NOT in any inner.
    /// So we need to INVERT the logic or structure it differently.
    ///
    /// Simpler approach: Create a custom delegate that directly tests:
    /// - If point is OUTSIDE outer boundary -> return 1 (out of bounds)
    /// - If point is INSIDE headland boundary (in the field) -> return 0 (safe)
    /// - Otherwise (in headland zone) -> return 1 (turn area)
    /// </summary>
    private YouTurnCreationInput? BuildYouTurnCreationInput(
        AgValoniaGPS.Models.Position currentPosition,
        double headingRadians,
        double abHeading,
        bool turnLeft)
    {
        // Need boundary to create turn boundaries
        if (_currentBoundary?.OuterBoundary == null || !_currentBoundary.OuterBoundary.IsValid)
        {
            _logger.LogDebug($"[YouTurn] No valid outer boundary available");
            return null;
        }

        var track = SelectedTrack;
        if (track == null)
        {
            _logger.LogDebug($"[YouTurn] No track selected");
            return null;
        }

        // Tool/implement width from configuration (use actual width from sections)
        double toolWidth = ConfigStore.ActualToolWidth;

        // Total headland width from the headland multiplier setting
        double totalHeadlandWidth = HeadlandCalculatedWidth;

        // Create outer boundary Vec3 list
        var outerPoints = _currentBoundary.OuterBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();
        var outerBoundaryVec3 = _polygonOffsetService.CalculatePointHeadings(outerPoints);

        // Create turn boundary: controls where the outermost point of the turn can reach
        // distanceFromBoundary = 0 means turn can reach the outer boundary
        // distanceFromBoundary > 0 means turn stays that far inside
        // distanceFromBoundary < 0 means turn can extend past the outer boundary
        double distanceFromBoundary = Guidance.UTurnDistanceFromBoundary;
        double turnBoundaryOffset = distanceFromBoundary;
        _logger.LogDebug($"[YouTurn] distanceFromBoundary={distanceFromBoundary:F1}m, turnBoundaryOffset={turnBoundaryOffset:F1}m");

        List<Vec2>? turnBoundaryVec2;
        if (turnBoundaryOffset > 0.1)
        {
            // Positive: offset inward
            turnBoundaryVec2 = _polygonOffsetService.CreateInwardOffset(outerPoints, turnBoundaryOffset);
        }
        else if (turnBoundaryOffset < -0.1)
        {
            // Negative: offset outward (turn starts outside boundary)
            turnBoundaryVec2 = _polygonOffsetService.CreateOutwardOffset(outerPoints, -turnBoundaryOffset);
        }
        else
        {
            // Near zero: use outer boundary directly
            turnBoundaryVec2 = outerPoints;
        }
        if (turnBoundaryVec2 == null || turnBoundaryVec2.Count < 3)
        {
            _logger.LogDebug($"[YouTurn] Offset failed, using outer boundary directly");
            turnBoundaryVec2 = outerPoints;
        }
        var turnBoundaryVec3 = _polygonOffsetService.CalculatePointHeadings(turnBoundaryVec2);

        // Create headland boundary: outer boundary offset inward by total headland width
        // This marks the inner edge of the turn zone (where the field starts)
        var headlandBoundaryVec2 = _polygonOffsetService.CreateInwardOffset(outerPoints, totalHeadlandWidth);
        if (headlandBoundaryVec2 == null || headlandBoundaryVec2.Count < 3)
        {
            _logger.LogDebug($"[YouTurn] Failed to create headland boundary");
            return null;
        }
        var headlandBoundaryVec3 = _polygonOffsetService.CalculatePointHeadings(headlandBoundaryVec2);

        // Create the BoundaryTurnLine for the target turn boundary (where turn tangents)
        var boundaryTurnLines = new List<BoundaryTurnLine>
        {
            new BoundaryTurnLine
            {
                Points = turnBoundaryVec3,
                BoundaryIndex = 0
            }
        };

        // HeadlandWidth = distance from headland boundary to turn boundary
        double headlandWidthForTurn = Math.Max(totalHeadlandWidth - toolWidth, toolWidth);

        // Create IsPointInsideTurnArea delegate
        // Returns: 0 = OK to place turn here, != 0 = out of allowed zone
        // The user controls how far into headland via distanceFromBoundary setting
        // We allow turns up to (or past) the configured boundary
        Func<Vec3, int> isPointInsideTurnArea = (point) =>
        {
            // Use the turn boundary (which accounts for distanceFromBoundary) as the limit
            // Points inside the turn boundary are OK (return 0)
            // Points outside the turn boundary are in the restricted zone (return 1)
            if (GeometryMath.IsPointInPolygon(turnBoundaryVec3, point))
            {
                return 0; // Inside allowed zone
            }

            // Point is outside the configured turn boundary
            return 1; // In restricted area
        };

        // Build the input
        var input = new YouTurnCreationInput
        {
            TurnType = YouTurnType.AlbinStyle,
            IsTurnLeft = turnLeft,
            GuidanceType = GuidanceLineType.ABLine,

            // Boundary data - the turn line the path should tangent
            BoundaryTurnLines = boundaryTurnLines,

            // Custom delegate for turn area testing
            IsPointInsideTurnArea = isPointInsideTurnArea,

            // AB line guidance data
            // For curves, use the track heading at the headland intersection, not at vehicle position
            ABHeading = track.Points.Count > 2
                ? FindTrackHeadingAtHeadland(track, new Vec3(currentPosition.Easting, currentPosition.Northing, headingRadians), State.Guidance.IsHeadingSameWay)
                : abHeading,
            // Calculate reference point on the CURRENT track near the vehicle position
            // (not at the extended endpoints which could be kilometers away)
            ABReferencePoint = CalculateCurrentTrackReferencePoint(track, toolWidth, abHeading,
                new Vec3(currentPosition.Easting, currentPosition.Northing, headingRadians)),
            IsHeadingSameWay = State.Guidance.IsHeadingSameWay,

            // Vehicle position and configuration
            PivotPosition = new Vec3(currentPosition.Easting, currentPosition.Northing, headingRadians),
            ToolWidth = toolWidth,
            ToolOverlap = Tool.Overlap,
            ToolOffset = Tool.Offset,
            TurnRadius = Guidance.UTurnRadius,

            // Turn parameters - use pre-calculated offset from ComputeNextTrack (matches cyan line exactly)
            TurnOffset = State.YouTurn.NextTrackTurnOffset,
            RowSkipsWidth = UTurnSkipRows, // Kept for fallback/logging
            TurnStartOffset = 0,
            HowManyPathsAway = State.Guidance.HowManyPathsAway,
            NudgeDistance = 0.0,
            TrackMode = 0, // Standard mode

            // State machine
            MakeUTurnCounter = State.YouTurn.YouTurnCounter + 10, // Ensure we pass the throttle check

            // Leg length - use user's UTurnExtension setting directly
            LegLength = Guidance.UTurnExtension,
            YouTurnLegExtensionMultiplier = 2.5, // Fallback if LegLength not set
            HeadlandWidth = headlandWidthForTurn
        };

        _logger.LogDebug($"[YouTurn] Input built: toolWidth={toolWidth:F1}m, totalHeadland={totalHeadlandWidth:F1}m, headlandWidthForTurn={headlandWidthForTurn:F1}m, turnBoundaryPoints={turnBoundaryVec3.Count}, headlandPoints={headlandBoundaryVec3.Count}");

        return input;
    }

    /// <summary>
    /// Calculate the reference point where the CURRENT OFFSET TRACK crosses the headland, ahead of the vehicle.
    /// This is the starting point for the U-turn path.
    ///
    /// Key insight: We create the offset track first, then find where IT crosses the headland.
    /// This is correct for curves where the offset track crosses at a different position than the base track.
    /// </summary>
    private Vec2 CalculateCurrentTrackReferencePoint(Track track, double toolWidth, double abHeading, Vec3 vehiclePosition)
    {
        if (track.Points.Count < 2)
            return new Vec2(vehiclePosition.Easting, vehiclePosition.Northing);

        // First, create the current offset track (same logic as in guidance)
        double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
        double offsetDistance = State.Guidance.HowManyPathsAway * widthMinusOverlap;

        Track currentOffsetTrack;
        if (Math.Abs(offsetDistance) < 0.01)
        {
            // No offset - use original track
            currentOffsetTrack = track;
        }
        else
        {
            // Create clean offset curve that handles self-intersections on tight curves
            var offsetPoints = CurveProcessing.CreateOffsetCurve(track.Points, offsetDistance);

            currentOffsetTrack = new Track
            {
                Name = $"Current path {State.Guidance.HowManyPathsAway}",
                Points = offsetPoints,
                Type = track.Type,
                IsVisible = false,
                IsActive = false
            };
        }

        // Now find where the OFFSET TRACK crosses the headland (no additional offset needed)
        var intersection = FindTrackHeadlandIntersectionAhead(currentOffsetTrack, vehiclePosition, State.Guidance.IsHeadingSameWay);
        if (intersection.HasValue)
        {
            _logger.LogDebug($"[YouTurn] Reference point: offset track (path {State.Guidance.HowManyPathsAway}) crosses headland at ({intersection.Value.Easting:F1}, {intersection.Value.Northing:F1})");
            return intersection.Value;
        }

        // Fallback: project vehicle position onto the offset track
        var ptA = currentOffsetTrack.Points[0];
        var ptB = currentOffsetTrack.Points[currentOffsetTrack.Points.Count - 1];

        // Vector from A to B
        double abE = ptB.Easting - ptA.Easting;
        double abN = ptB.Northing - ptA.Northing;
        double abLengthSq = abE * abE + abN * abN;

        // Vector from A to vehicle
        double avE = vehiclePosition.Easting - ptA.Easting;
        double avN = vehiclePosition.Northing - ptA.Northing;

        // Project vehicle onto track: t = (AV · AB) / |AB|²
        double t = (avE * abE + avN * abN) / abLengthSq;
        t = Math.Max(0, Math.Min(1, t));

        // Calculate the projected point on the offset track
        double projEasting = ptA.Easting + t * abE;
        double projNorthing = ptA.Northing + t * abN;

        _logger.LogDebug($"[YouTurn] Reference point: fallback to vehicle projection on offset track, path={State.Guidance.HowManyPathsAway}");

        return new Vec2(projEasting, projNorthing);
    }

    /// <summary>
    /// Find where the track crosses the headland ahead of the vehicle in the direction of travel.
    /// Returns the intersection point, or null if no intersection found ahead.
    /// </summary>
    private Vec2? FindTrackHeadlandIntersectionAhead(Track track, Vec3 vehiclePos, bool headingSameWay)
    {
        if (_currentHeadlandLine == null || _currentHeadlandLine.Count < 3)
            return null;

        if (track.Points.Count < 2)
            return null;

        // For curves, search along track points from vehicle position in direction of travel
        if (track.Points.Count > 2)
        {
            // Find nearest point to vehicle
            int nearestIdx = 0;
            double minDistSq = double.MaxValue;
            for (int i = 0; i < track.Points.Count; i++)
            {
                double pdx = track.Points[i].Easting - vehiclePos.Easting;
                double pdy = track.Points[i].Northing - vehiclePos.Northing;
                double distSq = pdx * pdx + pdy * pdy;
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    nearestIdx = i;
                }
            }

            // Search from nearest point in direction of travel
            int step = headingSameWay ? 1 : -1;
            int endIdx = headingSameWay ? track.Points.Count - 1 : 0;

            for (int i = nearestIdx; i != endIdx; i += step)
            {
                var p1 = track.Points[i];
                var p2 = track.Points[i + step];

                // Check if this segment crosses the headland
                for (int j = 0; j < _currentHeadlandLine.Count; j++)
                {
                    var h1 = _currentHeadlandLine[j];
                    var h2 = _currentHeadlandLine[(j + 1) % _currentHeadlandLine.Count];

                    var intersection = GeometryMath.TryGetSegmentIntersection(
                        p1.Easting, p1.Northing, p2.Easting, p2.Northing,
                        h1.Easting, h1.Northing, h2.Easting, h2.Northing);

                    if (intersection.HasValue)
                    {
                        return intersection;
                    }
                }
            }
            return null;
        }

        // For AB lines, extend the line and find intersection
        var ptA = track.Points[0];
        var ptB = track.Points[1];

        // Determine which direction is "ahead" based on heading
        Vec3 startPoint, endPoint;
        if (headingSameWay)
        {
            startPoint = ptA;
            endPoint = ptB;
        }
        else
        {
            startPoint = ptB;
            endPoint = ptA;
        }

        // Extend the line far beyond the track endpoints
        double dx = endPoint.Easting - startPoint.Easting;
        double dy = endPoint.Northing - startPoint.Northing;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return null;

        // Extend to 1000m past the endpoint (should cover any field)
        double extendedE = endPoint.Easting + (dx / len) * 1000;
        double extendedN = endPoint.Northing + (dy / len) * 1000;

        // Find where this extended line crosses the headland, starting from vehicle position
        Vec2? closestIntersection = null;
        double closestDist = double.MaxValue;

        for (int j = 0; j < _currentHeadlandLine.Count; j++)
        {
            var h1 = _currentHeadlandLine[j];
            var h2 = _currentHeadlandLine[(j + 1) % _currentHeadlandLine.Count];

            var intersection = GeometryMath.TryGetSegmentIntersection(
                vehiclePos.Easting, vehiclePos.Northing, extendedE, extendedN,
                h1.Easting, h1.Northing, h2.Easting, h2.Northing);

            if (intersection.HasValue)
            {
                double distSq = (intersection.Value.Easting - vehiclePos.Easting) * (intersection.Value.Easting - vehiclePos.Easting) +
                               (intersection.Value.Northing - vehiclePos.Northing) * (intersection.Value.Northing - vehiclePos.Northing);
                if (distSq < closestDist)
                {
                    closestDist = distSq;
                    closestIntersection = intersection;
                }
            }
        }

        return closestIntersection;
    }


    /// <summary>
    /// Create a simple geometric U-turn path with entry leg, semicircle arc, and exit leg.
    /// This is used as a fallback when the YouTurnCreationService produces an invalid path.
    /// </summary>
    private List<Vec3> CreateSimpleUTurnPath(AgValoniaGPS.Models.Position currentPosition, double headingRadians, double abHeading, bool turnLeft)
    {
        var path = new List<Vec3>();

        // Parameters - use the pre-calculated State.YouTurn.NextTrackTurnOffset which matches the cyan "next track" line
        double pointSpacing = 0.5; // meters between path points
        double turnOffset = State.YouTurn.NextTrackTurnOffset; // Use pre-calculated offset to match cyan line exactly

        // Fallback if State.YouTurn.NextTrackTurnOffset wasn't set
        if (turnOffset < 0.1)
        {
            int rowSkipWidth = UTurnSkipRows;
            double trackWidth = ConfigStore.ActualToolWidth - Tool.Overlap;
            turnOffset = trackWidth * (rowSkipWidth + 1);
            _logger.LogDebug($"[YouTurn] Using fallback turnOffset calculation: {turnOffset:F2}m");
        }

        // Turn radius from config, with fallback calculation
        double turnRadius = Guidance.UTurnRadius;

        // If config radius is too small for the track offset, use geometric minimum
        double geometricMinRadius = turnOffset / 2.0;
        if (turnRadius < geometricMinRadius)
        {
            turnRadius = geometricMinRadius;
        }

        // Absolute minimum turn radius constraint
        double minTurnRadius = 4.0;
        if (turnRadius < minTurnRadius)
        {
            turnRadius = minTurnRadius;
        }

        // Get the heading we're traveling (adjusted for same/opposite to AB)
        double travelHeading = abHeading;
        if (!State.Guidance.IsHeadingSameWay)
        {
            travelHeading += Math.PI;
            if (travelHeading >= Math.PI * 2) travelHeading -= Math.PI * 2;
        }

        // Exit heading is 180° opposite (going back toward field)
        double exitHeading = travelHeading + Math.PI;
        if (exitHeading >= Math.PI * 2) exitHeading -= Math.PI * 2;

        // Perpendicular direction (toward next track)
        double perpAngle = turnLeft ? (travelHeading - Math.PI / 2) : (travelHeading + Math.PI / 2);

        // Calculate the headland boundary point on CURRENT track
        double distToHeadland = State.YouTurn.DistanceToHeadland;
        double headlandBoundaryEasting = currentPosition.Easting + Math.Sin(travelHeading) * distToHeadland;
        double headlandBoundaryNorthing = currentPosition.Northing + Math.Cos(travelHeading) * distToHeadland;

        // Leg lengths
        double distanceFromBoundary = Guidance.UTurnDistanceFromBoundary;
        double headlandLegLength = HeadlandDistance - turnRadius - distanceFromBoundary;
        double fieldLegLength = Guidance.UTurnExtension;

        _logger.LogDebug($"[YouTurn] Simple path: turnOffset={turnOffset:F1}m, turnRadius={turnRadius:F1}m");
        _logger.LogDebug($"[YouTurn] HeadlandDistance={HeadlandDistance:F1}m, headlandLegLength={headlandLegLength:F1}m");

        // Calculate key waypoints
        double entryStartE = headlandBoundaryEasting - Math.Sin(travelHeading) * fieldLegLength;
        double entryStartN = headlandBoundaryNorthing - Math.Cos(travelHeading) * fieldLegLength;

        double arcStartE = headlandBoundaryEasting + Math.Sin(travelHeading) * headlandLegLength;
        double arcStartN = headlandBoundaryNorthing + Math.Cos(travelHeading) * headlandLegLength;

        double arcCenterE = arcStartE + Math.Sin(perpAngle) * turnRadius;
        double arcCenterN = arcStartN + Math.Cos(perpAngle) * turnRadius;

        double arcDiameter = 2.0 * turnRadius;

        // Note: Arc apex boundary check removed - it was too restrictive.
        // The arc extends into the headland zone which exists precisely for turns.
        // WouldNextLineBeInsideBoundary already validates the next track is valid.
        // The exit end check below ensures we end up on a valid track.

        double exitEndE = entryStartE + Math.Sin(perpAngle) * turnOffset;
        double exitEndN = entryStartN + Math.Cos(perpAngle) * turnOffset;

        if (!IsPointInsideBoundary(exitEndE, exitEndN))
        {
            _logger.LogDebug($"[YouTurn] Exit end is outside boundary - not creating U-turn");
            return path;
        }

        // Build entry leg
        double totalEntryLength = fieldLegLength + headlandLegLength;
        int totalEntryPoints = (int)(totalEntryLength / pointSpacing);

        for (int i = 0; i <= totalEntryPoints; i++)
        {
            double dist = i * pointSpacing;
            Vec3 pt = new Vec3
            {
                Easting = entryStartE + Math.Sin(travelHeading) * dist,
                Northing = entryStartN + Math.Cos(travelHeading) * dist,
                Heading = travelHeading
            };
            path.Add(pt);
        }

        // Build semicircle arc
        int arcPoints = Math.Max((int)(Math.PI * turnRadius / pointSpacing), 20);

        for (int i = 1; i <= arcPoints; i++)
        {
            double t = (double)i / arcPoints;
            double startAngle = Math.Atan2(arcStartE - arcCenterE, arcStartN - arcCenterN);
            double sweepAngle = turnLeft ? (-Math.PI * t) : (Math.PI * t);
            double currentAngle = startAngle + sweepAngle;

            double ptE = arcCenterE + Math.Sin(currentAngle) * turnRadius;
            double ptN = arcCenterN + Math.Cos(currentAngle) * turnRadius;

            double tangentHeading = currentAngle + (turnLeft ? -Math.PI / 2 : Math.PI / 2);
            if (tangentHeading < 0) tangentHeading += Math.PI * 2;
            if (tangentHeading >= Math.PI * 2) tangentHeading -= Math.PI * 2;

            Vec3 pt = new Vec3
            {
                Easting = ptE,
                Northing = ptN,
                Heading = tangentHeading
            };
            path.Add(pt);
        }

        // Build exit leg
        var lastArcPoint = path[path.Count - 1];
        double actualArcEndE = lastArcPoint.Easting;
        double actualArcEndN = lastArcPoint.Northing;

        double exitStartE = arcStartE + Math.Sin(perpAngle) * turnOffset;
        double exitStartN = arcStartN + Math.Cos(perpAngle) * turnOffset;

        double arcToExitDist = Math.Sqrt(Math.Pow(exitStartE - actualArcEndE, 2) + Math.Pow(exitStartN - actualArcEndN, 2));
        if (arcToExitDist > pointSpacing)
        {
            int connectPoints = (int)(arcToExitDist / pointSpacing);
            for (int i = 1; i <= connectPoints; i++)
            {
                double t = (double)i / (connectPoints + 1);
                Vec3 pt = new Vec3
                {
                    Easting = actualArcEndE + (exitStartE - actualArcEndE) * t,
                    Northing = actualArcEndN + (exitStartN - actualArcEndN) * t,
                    Heading = exitHeading
                };
                path.Add(pt);
            }
        }

        int totalExitPoints = (int)(totalEntryLength / pointSpacing);

        for (int i = 1; i <= totalExitPoints; i++)
        {
            double dist = i * pointSpacing;
            Vec3 pt = new Vec3
            {
                Easting = exitStartE + Math.Sin(exitHeading) * dist,
                Northing = exitStartN + Math.Cos(exitHeading) * dist,
                Heading = exitHeading
            };
            path.Add(pt);
        }

        _logger.LogDebug($"[YouTurn] Simple fallback path has {path.Count} points");

        TurnPathSmoothing.Smooth(path, Guidance.UTurnSmoothing);

        return path;
    }

    #endregion
}
