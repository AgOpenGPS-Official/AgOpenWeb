// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.YouTurn;

using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// Drives the YouTurn state machine: zone tracking, distance-to-headland, turn creation
/// gating, trigger detection, reset on overshoot, and completion. Mutates
/// <see cref="YouTurnState"/> and <see cref="GuidanceState"/> directly; returns an
/// <see cref="YouTurnEffects"/> describing the side-effects the caller must apply
/// (map updates, guidance state reset, status message).
/// </summary>
public sealed class YouTurnStateMachine
{
    // Turn-creation window: only consider creating a turn when the tractor is this far
    // from the headland (raycast distance to the turn line).
    //
    // Min stays a "too late to start a smooth turn" guard. Max is large so the turn is
    // plotted as soon as the upcoming turn-line crossing is ahead — i.e. right after the
    // previous turn completes and the tractor settles onto the new pass — matching current
    // AgOpenGPS (its distance-to-turn readout tracks a turn that is plotted across the whole
    // pass, not one built only near the boundary). This is safe because the turn ENTRY is the
    // track × turn-line crossing (fixed geometry; see YouTurnCreationService "find crossing"),
    // so an early plot yields the identical path. The raycast returns double.MaxValue when no
    // crossing lies ahead, so "no turn ahead" is still naturally excluded by this upper bound.
    // 2000 m comfortably exceeds any real field pass while staying below that no-hit sentinel.
    private const double MinDistanceToCreate = 10.0;
    private const double MaxDistanceToCreate = 2000.0;

    // Alignment tolerance around the AB line direction (forward or reverse) for turn gating.
    // ~20 degrees. Wider tolerances risk creating turns while mid-turn when heading swings.
    private static readonly double AlignmentTolerance = Math.PI / 9;

    // A tractor this close to the planned path, heading along it, is on the path and the
    // turn starts (#306). The entry leg is the current pass itself, so the tractor joins it
    // wherever it is when the turn is armed; an exact rendezvous with the path's first point
    // was never needed.
    private const double OnPathLateralMeters = 1.5;

    // Completion thresholds.
    // ClosestApproachThreshold: if the tractor was this close to the end
    // point and starts moving away, the turn is complete. This is more
    // robust than a fixed radius check because it works at any speed/GPS rate.
    private const double ClosestApproachThreshold = 5.0;
    private const double CompletionMinTraveledMeters = 5.0;

    private readonly YouTurnCreationService _creation;
    // U-turn sound latches (AgOpenGPS turnTooCloseTrigger / isBoundAlarming) (#110).
    // _missedSounded: a missed turn sounds once (#570). Not re-armed by a new plan, which a
    // missed turn is followed by at once: only when a turn starts or U-turn is switched off.
    private bool _creationFailSounded, _approachAlarmed, _missedSounded;
    private readonly YouTurnPathingService _pathing;
    private readonly PolygonIndexCache _headlandIndex = new(); // the zone test, every GPS cycle
    private readonly ILogger<YouTurnStateMachine> _logger;
    private readonly ConfigurationStore _configStore;

    // The snake's worked-pass test samples a pass across the whole field, so each pass is
    // decided once per plan and per turn, not on every cycle: a finished field plans no turn
    // and would otherwise test every pass again on each cycle. Cleared when the snake is
    // planned again, a turn completes, or the track, nudge or cultivated area changes.
    private readonly Dictionary<int, bool> _coveredPasses = new();
    private (List<int>? Plan, Models.Track.Track? Track, double Nudge, Boundary? Boundary, IReadOnlyList<Vec3>? Headland) _coveredPassesKey;

    // Which way the last turn stepped across the field (+1 to a higher pass, -1 lower) and on
    // which track (by name: a nudge or edit makes a new Track). A normal turn keeps stepping that
    // way, as AgOpenGPS does by flipping isTurnLeft after each turn, so the work goes on across
    // the field and a swapped turn (the U-turn arrow) carries on to the new side (#523).
    private int _lastStep;
    private string? _lastStepTrack;
    private int _lastStepPass; // the pass that turn landed on

    public YouTurnStateMachine(
        YouTurnCreationService creation,
        YouTurnPathingService pathing,
        ILogger<YouTurnStateMachine> logger,
        ConfigurationStore configStore)
    {
        _creation = creation;
        _pathing = pathing;
        _logger = logger;
        _configStore = configStore;
    }

    /// <summary>
    /// Per-cycle inputs for the state machine. Immutable snapshot — the state machine
    /// reads these but does not retain references beyond a single call.
    /// </summary>
    public readonly record struct TickContext(
        Position CurrentPosition,
        Models.Track.Track? SelectedTrack,
        Boundary? Boundary,
        IReadOnlyList<Vec3>? HeadlandLine,
        int UTurnSkipRows,
        bool IsSkipWorkedMode,
        double HeadlandCalculatedWidth,
        double HeadlandDistance,
        bool IsAlternateSkipMode = false, // AgOpenGPS SkipMode.Alternative (#111)
        Func<double, double, bool>? IsPointCovered = null); // the job's coverage: worked passes on the map

    /// <summary>
    /// Run one cycle of the state machine. Precondition: autosteer engaged, track active,
    /// YouTurn enabled, headland line has ≥3 points. The caller should gate these before
    /// invoking Tick.
    /// </summary>
    public YouTurnEffects Tick(in TickContext ctx, GuidanceWorkingState guidance, YouTurnWorkingState turn)
    {
        var effects = new YouTurnEffects();
        var track = ctx.SelectedTrack;
        if (track == null || track.Points.Count < 2 || ctx.HeadlandLine == null)
            return effects;

        var currentPosition = ctx.CurrentPosition;
        double headingRadians = currentPosition.Heading * Math.PI / 180.0;
        bool isCurve = track.Points.Count > 2;

        // Local AB heading. For curves, use the heading at the nearest point;
        // for AB lines, derive from the two endpoints.
        double abHeading;
        if (isCurve)
        {
            abHeading = LocalCurveHeading(track, currentPosition);
        }
        else
        {
            var trackPointA = track.Points[0];
            var trackPointB = track.Points[1];
            double abDx = trackPointB.Easting - trackPointA.Easting;
            double abDy = trackPointB.Northing - trackPointA.Northing;
            abHeading = Math.Atan2(abDx, abDy);
            if (turn.YouTurnCounter % 30 == 0)
                _logger.LogDebug("[YouTurn] AB Line: abHeading={Deg:F1}°", abHeading * 180 / Math.PI);
        }

        // Is the vehicle heading the same way as the AB line (forward) or reverse?
        double headingDiff = headingRadians - abHeading;
        while (headingDiff > Math.PI) headingDiff -= 2 * Math.PI;
        while (headingDiff < -Math.PI) headingDiff += 2 * Math.PI;
        bool headingSameWay = Math.Abs(headingDiff) < Math.PI / 2;
        // Held while a turn is driven, as the pipeline holds it: the turn and the old pass's
        // line were placed for the old direction, and flipping it halfway round the arc moved
        // that line by twice the tool offset (#591). The first cycle after the turn takes the new one.
        if (!turn.IsExecuting) guidance.IsHeadingSameWay = headingSameWay;

        bool alignedForward = Math.Abs(headingDiff) < AlignmentTolerance;
        bool alignedReverse = Math.Abs(headingDiff) > (Math.PI - AlignmentTolerance);
        bool isAlignedWithABLine = alignedForward || alignedReverse;

        // Distance-to-headland raycast is valid only when the tractor is aligned with the track;
        // otherwise heading swings mid-turn create spurious proximity readings.
        double travelHeading = abHeading;
        if (!headingSameWay)
        {
            travelHeading += Math.PI;
            if (travelHeading >= Math.PI * 2) travelHeading -= Math.PI * 2;
        }

        // The pass is where the tool runs, so the turn is planned from the pass line: the pivot
        // moved by the tool offset (+ = right of the direction of travel), as AgOpenGPS plans from
        // the line (ABLine.rEastAB), not the pivot. On the edge pass a side-shift mower works the
        // cultivated area while the tractor runs in the headland, outside the headland line and
        // parallel to it: measured from the pivot the line was never ahead and no turn was ever
        // planned (field job J12). Without a tool offset it is the pivot. The tractor's own zone
        // (CurrentZone) stays the pivot's.
        double toolOffset = _configStore.Tool.Offset;
        var passPoint = toolOffset == 0 ? currentPosition : new Position
        {
            Easting = currentPosition.Easting + Math.Cos(travelHeading) * toolOffset,
            Northing = currentPosition.Northing - Math.Sin(travelHeading) * toolOffset,
            Heading = currentPosition.Heading,
            Speed = currentPosition.Speed,
        };

        turn.DistanceToHeadland = isAlignedWithABLine
            ? RaycastDistanceToHeadland(passPoint, travelHeading, ctx.HeadlandLine, headingSameWay, turn.YouTurnCounter)
            : double.MaxValue;

        turn.CurrentZone = DetermineZone(currentPosition.Easting, currentPosition.Northing, ctx.Boundary, ctx.HeadlandLine);
        var passZone = toolOffset == 0 ? turn.CurrentZone : DetermineZone(passPoint.Easting, passPoint.Northing, ctx.Boundary, ctx.HeadlandLine);

        bool isInCultivatedArea = passZone == TractorZone.InCultivatedArea;
        bool isInHeadlandZone = turn.CurrentZone == TractorZone.InHeadland;

        bool headlandInRange = turn.DistanceToHeadland > MinDistanceToCreate &&
                               turn.DistanceToHeadland < MaxDistanceToCreate;

        if (turn.TurnPath == null && !turn.IsExecuting && turn.DistanceToHeadland < 100)
        {
            _logger.LogDebug("[YouTurn] Zone={Zone}, dist={Dist:F1}m, aligned={Aligned}, inRange={InRange}",
                turn.CurrentZone, turn.DistanceToHeadland, isAlignedWithABLine, headlandInRange);
        }

        // ── TURN CREATION ───────────────────────────────────────────────
        bool canCreateTurn = isInCultivatedArea && headlandInRange;
        // On a curve the headland ahead is found by a straight ray along the heading, which
        // leaves the field early on the outside of a bend while the pass runs on. Whether the
        // next pass has room is judged at that end, so mid-pass a turn was refused and "End of
        // field reached" said, then the turn made at the real end (field job J10, a recorded
        // S-curve). The ray's direction is the reference curve's heading at its nearest point,
        // which on a pass far out from an S-shaped reference is not the pass's own, so the ray
        // can also cross the pass and meet the headland beside it. The pass itself is walked
        // ahead: when it goes on in the field past the ray's end, the end is further on and is
        // judged once the ray finds it. The walk stops at twice the margin past the ray (it is
        // counted from the nearest point of the offset curve, about a metre either way).
        if (canCreateTurn && turn.TurnPath == null && !turn.IsExecuting && track.Points.Count > 2)
        {
            const double RayShortBy = 5.0;
            double along = _pathing.LandingLength(track, abHeading, guidance.HowManyPathsAway, ctx.Boundary, ctx.HeadlandLine,
                passPoint, !headingSameWay, 0, stopAt: turn.DistanceToHeadland + 2 * RayShortBy);
            if (along > turn.DistanceToHeadland + RayShortBy)
            {
                canCreateTurn = false;
                _logger.LogDebug("[YouTurn] Curve: the pass runs on {Along:F1}m+ in the field past the headland the straight line meets at {Dist:F1}m - the end is further on",
                    along, turn.DistanceToHeadland);
            }
        }
        if (!canCreateTurn && ctx.IsSkipWorkedMode && isInHeadlandZone && isAlignedWithABLine
            && turn.SnakeSequence != null && turn.TurnPath == null && !turn.IsExecuting)
        {
            // In the headland at the far end of a field: the tractor reached the end of the
            // current pass; the snake sequence may still have more passes to work.
            canCreateTurn = _pathing.GetNextSnakePath(turn, WorkedPasses(in ctx, track, guidance, turn)).HasValue;
        }

        // Default: no armable trigger point — widget shows nothing meaningful.
        // Recomputed below when a precomputed turn path exists and execution hasn't started.
        turn.DistanceToTrigger = 0;

        // ── DIRECTION-OVERRIDE RE-ARM ───────────────────────────────────
        // If the operator pressed the swap-direction button while a turn
        // was already rendered (TurnPath != null) but execution hasn't
        // begun, drop the rendered path so the CREATE branch below
        // recomputes it with the new direction this same cycle. The
        // override flag itself is cleared by TakeDirectionSwap in the
        // Handle*Creation methods — leave it set here.
        // Mid-arc flips are unsafe and stay no-op.
        if (turn.NextUTurnDirectionLeftOverride.HasValue
            && turn.TurnPath != null
            && !turn.IsExecuting)
        {
            _logger.LogDebug("[YouTurn] Direction override set with rendered path — re-arming with {Dir}",
                turn.NextUTurnDirectionLeftOverride.Value ? "LEFT" : "RIGHT");
            DiscardPlannedTurn(turn);
            effects.SyncTurnPathToMap = true;
            effects.SyncNextTrackToMap = true;
        }

        if (!ctx.IsAlternateSkipMode) turn.AltSign = 0; // pattern restarts when re-entered

        if (turn.TurnPath == null && !turn.IsExecuting && canCreateTurn && isAlignedWithABLine)
        {
            if (ctx.IsSkipWorkedMode)
                HandleSnakeCreation(in ctx, track, abHeading, currentPosition, headingRadians, guidance, turn, effects);
            else if (ctx.IsAlternateSkipMode)
                HandleAlternateCreation(in ctx, track, abHeading, headingRadians, guidance, turn, effects);
            else
                HandleNormalCreation(in ctx, track, abHeading, currentPosition, headingRadians, guidance, turn, effects);
        }
        // ── NO TURN, HEADING OUT ────────────────────────────────────────
        // In the headland on the line, heading out of the field, with no turn planned or under
        // way and nothing said yet: no turn can be made from here, since the turn is laid out from
        // before the headland. AutoSteer was switched on again in the headland after a dropped
        // turn (field job T10), or the turn was never planned. AgOpenGPS can't turn there either
        // and sounds its creation failure (FailCreate, "too close"); this drove on out of the field
        // in silence. Not after an end of field or a refused turn: the operator was told then
        // (_creationFailSounded). Heading out is judged along the pass: the straight ray finding
        // no headland line ahead settles it on an AB line, but a curved pass can bend into the
        // field past where a straight line from here runs along the headland (field job J10, a
        // recorded curve, just after a turn), so the pass itself is walked ahead for cultivated
        // ground (the landing check's walk, the other way round).
        if (turn.TurnPath == null && !turn.IsExecuting && !_creationFailSounded && !_missedSounded
            && passZone == TractorZone.InHeadland && isAlignedWithABLine && turn.DistanceToHeadland == double.MaxValue
            && _pathing.LandingLength(track, abHeading, guidance.HowManyPathsAway, ctx.Boundary, ctx.HeadlandLine,
                passPoint, !headingSameWay, 0, stopAt: 1.0) == 0)
        {
            _logger.LogDebug("[YouTurn] In the headland heading out with no turn - turn missed");
            SoundMissed(effects);
            // Also for a pass that lies in the headland strip the whole way (along a side of the
            // field), where it comes mid-pass: worded for both, and it keeps "take over manually".
            effects.StatusMessage = "No U-turn can be made from the headland on this pass. Take over manually at the end of the field.";
        }

        // ── TURN TRIGGER ────────────────────────────────────────────────
        // The planned path begins with an entry leg that IS the current pass, so the tractor
        // is on the path as soon as it is on that leg and takes the turn from wherever it
        // joins (#306). The old rule, within 2 m of the path's first point, never fired when
        // the turn was armed late and the leg already began behind the tractor; the path then
        // stayed on screen while the tractor drove into the headland with no warning.
        if (turn.TurnPath != null && turn.TurnPath.Count > 2 && !turn.IsTriggered && !turn.IsExecuting)
        {
            var turnStart = turn.TurnPath[0];
            double distToTurnStart = Math.Sqrt(
                (currentPosition.Easting - turnStart.Easting) * (currentPosition.Easting - turnStart.Easting) +
                (currentPosition.Northing - turnStart.Northing) * (currentPosition.Northing - turnStart.Northing));
            var arcStart = turn.ArcStart ?? turnStart;
            double distToArcStart = Math.Sqrt(
                (currentPosition.Easting - arcStart.Easting) * (currentPosition.Easting - arcStart.Easting) +
                (currentPosition.Northing - arcStart.Northing) * (currentPosition.Northing - arcStart.Northing));

            // Publish the pivot→path-start distance for the UI countdown widget as ARC LENGTH
            // ALONG THE TRACK, not straight-line. On a curve that wraps a field end, the turn
            // start can be far to the east while the tractor drives west around the wrap toward
            // it — a Euclidean distance would COUNT UP as it drives away in a straight line, when
            // the operator expects it to COUNT DOWN as the pass is worked. Falls back to the
            // straight-line value for AB lines (2-point tracks) where the two are identical.
            turn.DistanceToTrigger = track.Points.Count > 2
                ? ArcLengthAlongTrack(track.Points, currentPosition, turnStart)
                : distToTurnStart;

            // Alarm once as the tractor comes within 20 m of the arc, where the path leaves
            // the pass. AgOpenGPS tests a 18–20 m band, which a fast approach steps straight
            // over between fixes — the alarm then sounded on roughly every other turn (#150).
            if (distToArcStart <= 20.0 && !_approachAlarmed)
            {
                _approachAlarmed = true;
                effects.ApproachAlarmSound = true;
            }

            if (IsOnPath(turn.TurnPath, currentPosition, headingRadians))
            {
                turn.IsTriggered = true;
                turn.IsExecuting = true;
                _missedSounded = false;
                ForgetDirectionChoice(turn);
                effects.StatusMessage = "YouTurn triggered!";
                _logger.LogDebug("[YouTurn] Triggered on the path, {Dist:F1}m from the arc", distToArcStart);
            }
            else if (isAlignedWithABLine && IsHeadingAlong(turn.TurnPath, headingRadians)
                     && IsBehind(arcStart, currentPosition, headingRadians)
                     && IsBehindAlongTrack(track, arcStart, currentPosition, guidance.IsHeadingSameWay))
            {
                // The arc already begins behind the tractor, so this path cannot be joined.
                // Turn from here instead, the way a manual turn does; when there is no room
                // for that either, say so, because the operator has to turn by hand now.
                var fromHere = _creation.CreateManualArcPath(
                    currentPosition, abHeading, turn.IsTurnLeft,
                    ctx.Boundary, guidance, ctx.UTurnSkipRows, turn.NextTrack);
                // Only a turn that stays in the field. The manual builder checks no more than
                // where the turn ends; started by the app itself, a turn must keep inside the
                // boundary and out of obstacles, or the operator takes over (#698's rule for the
                // fallback turn).
                if (fromHere.Count > 2 && !YouTurnCreationService.LeavesField(fromHere, ctx.Boundary))
                {
                    turn.TurnPath = fromHere;
                    turn.ArcStart = null;
                    turn.YouTurnCounter = 0;
                    turn.IsTriggered = true;
                    turn.IsExecuting = true;
                    _missedSounded = false;
                    ForgetDirectionChoice(turn);
                    effects.StatusMessage = "YouTurn started from here: the planned arc was already behind the tractor";
                    _logger.LogDebug("[YouTurn] Arc start {Dist:F1}m behind the tractor - turning from here ({Count} points)",
                        distToArcStart, fromHere.Count);
                }
                else
                {
                    _logger.LogDebug("[YouTurn] Arc start {Dist:F1}m behind the tractor and no room to turn from here - turn missed",
                        distToArcStart);
                    DiscardPlannedTurn(turn);
                    SoundMissed(effects);
                    effects.StatusMessage = "U-turn missed: too close to the headland to turn. Take over manually.";
                }
                effects.SyncTurnPathToMap = true;
                effects.SyncNextTrackToMap = true;
            }
            else if (isInHeadlandZone && IsPastStart(turn.TurnPath, currentPosition))
            {
                // In the headland, past where the path begins, and never on it: the turn is
                // missed. Not while the path's first point is still ahead: the planner lays
                // the turn out from the boundary, and where the field's headland line is
                // drawn further in than that, the path begins inside the headland. The
                // tractor then enters the headland on its pass before it reaches the path.
                _logger.LogDebug("[YouTurn] In the headland past the path's start without joining it - turn missed");
                DiscardPlannedTurn(turn);
                SoundMissed(effects);
                effects.StatusMessage = "U-turn missed: entered the headland off the turn path. Take over manually.";
                effects.SyncTurnPathToMap = true;
                effects.SyncNextTrackToMap = true;
            }
        }

        // ── TURN COMPLETION ─────────────────────────────────────────────
        // Two-stage completion:
        //   (1) Early-completion gated on remaining ARC LENGTH along the
        //       path (not Euclidean distance to endpoint). Switches
        //       guidance back to the regular track before the pure-
        //       pursuit goal can collapse to zero on the final segment.
        //   (2) Legacy closest-approach as a backstop for the case
        //       where (1) misses (e.g., a very short path where the
        //       arc-length never exceeds the lookahead).
        CheckCompletion(in ctx, guidance, turn, effects);

        return effects;
    }

    /// <summary>
    /// Run only the turn-completion checks for a turn that is already executing. The
    /// caller uses this when the full <see cref="Tick"/> is gated off (no headland line)
    /// but a manual turn is in progress — without it a manual turn in a field with no
    /// headland never completes (#163).
    /// </summary>
    public YouTurnEffects TickExecutingTurn(in TickContext ctx, GuidanceWorkingState guidance, YouTurnWorkingState turn)
    {
        var effects = new YouTurnEffects();
        if (ctx.SelectedTrack == null || ctx.SelectedTrack.Points.Count < 2) return effects;
        CheckCompletion(in ctx, guidance, turn, effects);
        return effects;
    }

    /// <summary>
    /// Complete the executing turn because U-turn guidance reached the end of the path
    /// (AgOpenGPS CYouTurn calls CompleteYouTurn from its guidance the same way). Backstop
    /// for the arc-length / closest-approach checks: guidance stops steering once it
    /// reports the path finished, so an uncompleted turn would leave the tractor with no
    /// steering at all. No-op when no turn is executing.
    /// </summary>
    public YouTurnEffects CompleteFromGuidance(in TickContext ctx, GuidanceWorkingState guidance, YouTurnWorkingState turn)
    {
        var effects = new YouTurnEffects();
        if (!turn.IsExecuting) return effects;
        if (guidance.IsReverse && _configStore.Guidance.UTurnStyle != 1) // a K-turn reverses as part of it
        {
            // Off the path while reversing: the operator backed out of the turn, so it didn't
            // happen. Stay on this pass; the turn is planned again from here (#588). AgOpenGPS
            // completes it (it changed pass when the turn started), leaving guidance on the
            // pass the turn was going to.
            _logger.LogDebug("[YouTurn] Left the turn path in reverse - turn cancelled, staying on path {Cur}",
                guidance.HowManyPathsAway);
            CancelTurn(turn, effects);
            return effects;
        }
        _logger.LogDebug("[YouTurn] Guidance reached end of turn path — completing turn");
        CompleteTurn(in ctx, guidance, turn, effects);
        return effects;
    }

    /// <summary>Drop the turn being driven without moving to another pass (#588).</summary>
    private static void CancelTurn(YouTurnWorkingState turn, YouTurnEffects effects)
    {
        DiscardPlannedTurn(turn);
        turn.IsExecuting = false;
        turn.YouTurnCounter = 0;
        effects.SyncTurnPathToMap = true;
        effects.SyncNextTrackToMap = true;
        effects.IsInYouTurnMapFlag = false;
        effects.StatusMessage = "U-turn cancelled: reversed out of the turn";
    }

    private void CheckCompletion(in TickContext ctx, GuidanceWorkingState guidance, YouTurnWorkingState turn, YouTurnEffects effects)
    {
        var currentPosition = ctx.CurrentPosition;
        if (turn.IsExecuting && turn.TurnPath != null && turn.TurnPath.Count > 2)
        {
            var startPoint = turn.TurnPath[0];
            var endPoint = turn.TurnPath[turn.TurnPath.Count - 1];

            double distToTurnStart = Math.Sqrt(
                (currentPosition.Easting - startPoint.Easting) * (currentPosition.Easting - startPoint.Easting) +
                (currentPosition.Northing - startPoint.Northing) * (currentPosition.Northing - startPoint.Northing));
            double distToTurnEnd = Math.Sqrt(
                (currentPosition.Easting - endPoint.Easting) * (currentPosition.Easting - endPoint.Easting) +
                (currentPosition.Northing - endPoint.Northing) * (currentPosition.Northing - endPoint.Northing));

            // Early-completion: ARC-LENGTH-based, NOT Euclidean. On omega
            // U-turns the path's start and end are physically close
            // (~3 m apart on the v12 production geometry), so a
            // Euclidean distToTurnEnd check fires the moment the path
            // is plotted — the v14 break. Arc-length to the end via the
            // pivot's closest path index correctly tracks actual
            // progress along the loop and only crosses the lookahead
            // threshold when the pivot really is in the last few
            // segments.
            //
            // Magic number 4.0 m matches
            // ConfigurationStore.Guidance.GoalPointLookAheadHold (the
            // pure-pursuit lookahead in YouTurnGuidanceService). Once
            // remaining arc-length falls below lookahead, the walk
            // can't satisfy the configured lookahead anyway — the goal
            // would collapse toward the path endpoint on subsequent
            // ticks. Defense-in-depth backstop: the
            // YouTurnGuidanceService collapsed-goal post-walk guard
            // (commit 61674b59) still catches the corner case if this
            // check misses.
            const double EarlyCompletionLookahead = 4.0;
            double remainingArc = ComputeRemainingArcLength(turn.TurnPath, currentPosition);
            double totalArc = ComputeTotalArcLength(turn.TurnPath);
            double traveledArc = totalArc - remainingArc;

            // Fire when:
            //   (a) less than lookahead of arc-length remains (the goal
            //       would collapse on the final segment), AND
            //   (b) at least lookahead of arc-length has been traversed
            //       (the tractor has genuinely progressed; protects
            //       against tick-0 fire on paths shorter than 2 ×
            //       lookahead AND against fresh-path-pivot-near-end
            //       cases where creation was wrong).
            // Together these require the path itself to be longer than
            // 2 × lookahead, which is true for any production U-turn
            // path (the v12 omega is ~42 m) but false for synthetic
            // test paths (~3-5 m). Short paths fall through to the
            // legacy closest-approach detector.
            if (remainingArc < EarlyCompletionLookahead
                && traveledArc > EarlyCompletionLookahead)
            {
                _logger.LogDebug("[YouTurn] Early-completion (remaining arc {Arc:F1}m < {L:F1}m, "
                    + "traveled {Tr:F1}m): distStart={DistStart:F2}m distEnd={DistEnd:F2}m",
                    remainingArc, EarlyCompletionLookahead, traveledArc,
                    distToTurnStart, distToTurnEnd);
                CompleteTurn(in ctx, guidance, turn, effects);
                return;
            }

            // Closest-approach backstop: complete when the tractor was
            // close to end, is now moving away, and has traveled far
            // enough from the start. Speed/GPS-rate independent — a
            // fixed radius check misses the end point at high speeds
            // because discrete GPS steps jump over it.
            bool wasClose = turn.PreviousDistToTurnEnd < ClosestApproachThreshold;
            bool movingAway = distToTurnEnd > turn.PreviousDistToTurnEnd;
            bool traveledEnough = distToTurnStart > CompletionMinTraveledMeters;
            // "Close to the end" must also hold along the path. A manual turn onto a pass
            // less than 5 m away ends beside its own first metres: the tractor drives past
            // the end point, a pass width away, on its way into the turn, and that read as
            // reaching the end and leaving it, with the whole loop still to drive (#272).
            bool onFinalStretch = remainingArc < ClosestApproachThreshold;

            if (wasClose && movingAway && traveledEnough && onFinalStretch
                && distToTurnEnd < distToTurnStart)
            {
                _logger.LogDebug("[YouTurn] Closest-approach completion: distEnd={DistEnd:F1}m prevDist={Prev:F1}m distStart={DistStart:F1}m",
                    distToTurnEnd, turn.PreviousDistToTurnEnd, distToTurnStart);
                CompleteTurn(in ctx, guidance, turn, effects);
            }

            turn.PreviousDistToTurnEnd = distToTurnEnd;
        }
    }

    /// <summary>
    /// Sum of segment lengths from the closest path point to
    /// <paramref name="pivot"/> through the path's last point. Returns
    /// arc-length remaining "along the path", which is topology-aware
    /// (handles omega U-turns where the path's start and end are
    /// physically close but separated by the full loop's arc).
    /// </summary>
    private static double ComputeRemainingArcLength(
        IReadOnlyList<Vec3> path, Position pivot)
    {
        if (path.Count < 2) return 0;

        // Closest path index.
        int closestIdx = 0;
        double minDistSq = double.MaxValue;
        for (int i = 0; i < path.Count; i++)
        {
            double dx = path[i].Easting - pivot.Easting;
            double dy = path[i].Northing - pivot.Northing;
            double d2 = dx * dx + dy * dy;
            if (d2 < minDistSq) { minDistSq = d2; closestIdx = i; }
        }

        // Sum segment lengths from closestIdx to end.
        double remaining = 0;
        for (int i = closestIdx; i < path.Count - 1; i++)
        {
            double dx = path[i + 1].Easting - path[i].Easting;
            double dy = path[i + 1].Northing - path[i].Northing;
            remaining += Math.Sqrt(dx * dx + dy * dy);
        }
        return remaining;
    }

    /// <summary>
    /// Arc length along <paramref name="track"/> between the point nearest
    /// <paramref name="pivot"/> and the point nearest <paramref name="turnStart"/>. Gives a
    /// "distance remaining as the pass is worked" that decreases as the tractor advances toward
    /// the turn, even when the turn start is straight-line far away (a curve wrapping a field
    /// end). Snaps both ends to the nearest track point, so the readout steps at roughly the
    /// track point spacing (~2 m) — fine for a distance display.
    /// </summary>
    private static double ArcLengthAlongTrack(
        IReadOnlyList<Vec3> track, Position pivot, Vec3 turnStart)
    {
        int NearestIdx(double e, double n)
        {
            int best = 0; double bestSq = double.MaxValue;
            for (int i = 0; i < track.Count; i++)
            {
                double de = track[i].Easting - e, dn = track[i].Northing - n;
                double d2 = de * de + dn * dn;
                if (d2 < bestSq) { bestSq = d2; best = i; }
            }
            return best;
        }

        int pivotIdx = NearestIdx(pivot.Easting, pivot.Northing);
        int turnIdx = NearestIdx(turnStart.Easting, turnStart.Northing);
        int lo = Math.Min(pivotIdx, turnIdx), hi = Math.Max(pivotIdx, turnIdx);

        double len = 0;
        for (int i = lo; i < hi; i++)
        {
            double de = track[i + 1].Easting - track[i].Easting;
            double dn = track[i + 1].Northing - track[i].Northing;
            len += Math.Sqrt(de * de + dn * dn);
        }
        return len;
    }

    /// <summary>
    /// Total arc length of <paramref name="path"/>, computed as the sum of
    /// pairwise distances between consecutive points. Used to gate the
    /// early-completion check against short synthetic paths.
    /// </summary>
    private static double ComputeTotalArcLength(IReadOnlyList<Vec3> path)
    {
        double total = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            double dx = path[i + 1].Easting - path[i].Easting;
            double dy = path[i + 1].Northing - path[i].Northing;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    /// <summary>Heading of a curve track at its point nearest <paramref name="position"/>, radians.</summary>
    private static double LocalCurveHeading(Models.Track.Track track, Position position)
    {
        double minDistSq = double.MaxValue;
        int nearestIdx = 0;
        for (int i = 0; i < track.Points.Count; i++)
        {
            double dx = track.Points[i].Easting - position.Easting;
            double dy = track.Points[i].Northing - position.Northing;
            double distSq = dx * dx + dy * dy;
            if (distSq < minDistSq) { minDistSq = distSq; nearestIdx = i; }
        }
        return track.Points[nearestIdx].Heading;
    }

    /// <summary>
    /// Manually trigger a U-turn in the specified direction. Used for tracks along boundaries
    /// where automatic headland detection doesn't fire.
    /// </summary>
    public YouTurnEffects TriggerManual(
        bool turnLeft,
        bool isAutoSteerEngaged,
        in TickContext ctx,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn)
    {
        var effects = new YouTurnEffects();

        if (!isAutoSteerEngaged || ctx.SelectedTrack == null)
        {
            effects.StatusMessage = "Enable autosteer first";
            return effects;
        }

        if (turn.IsExecuting)
        {
            // Refuse to swap paths while the guidance service is actively following
            // one — doing so could whip the steering mid-arc.
            effects.StatusMessage = "U-turn already in progress";
            return effects;
        }

        // If an auto-trigger plotted a path but it hasn't engaged yet, discard it so
        // the manual trigger's immediate arc takes over. Also drops a snake / alternate
        // target pass, so the manual turn completes by its own direction.
        if (turn.TurnPath != null) effects.SyncTurnPathToMap = true;
        DiscardPlannedTurn(turn);

        var track = ctx.SelectedTrack;
        if (track.Points.Count < 2)
        {
            effects.StatusMessage = "Invalid track";
            return effects;
        }

        var currentPosition = ctx.CurrentPosition;
        double headingRadians = currentPosition.Heading * Math.PI / 180.0;

        // The line's heading where the tractor is: the curve's local heading (AgOpenGPS
        // curve.manualUturnHeading), or A→B for an AB line. A curve's first-to-last-point
        // heading can be far off its local one, which turned the arc away from the tractor
        // and ended it short of the next pass.
        double abHeading;
        if (track.Points.Count > 2)
        {
            abHeading = LocalCurveHeading(track, currentPosition);
        }
        else
        {
            var trackPointA = track.Points[0];
            var trackPointB = track.Points[1];
            abHeading = Math.Atan2(trackPointB.Easting - trackPointA.Easting, trackPointB.Northing - trackPointA.Northing);
        }

        double headingDiff = headingRadians - abHeading;
        while (headingDiff > Math.PI) headingDiff -= 2 * Math.PI;
        while (headingDiff < -Math.PI) headingDiff += 2 * Math.PI;
        guidance.IsHeadingSameWay = Math.Abs(headingDiff) < Math.PI / 2;

        turn.IsTurnLeft = turnLeft;
        turn.WasHeadingSameWayAtTurnStart = guidance.IsHeadingSameWay;

        _logger.LogDebug("[ManualYouTurn] Triggering {Dir} turn, isHeadingSameWay={Way}",
            turnLeft ? "LEFT" : "RIGHT", guidance.IsHeadingSameWay);

        _pathing.ComputeNextTrack(track, abHeading, guidance, turn,
            ctx.UTurnSkipRows, ctx.IsSkipWorkedMode, ctx.SelectedTrack);
        effects.SyncNextTrackToMap = true;
        if (turn.NextTrack is { Points.Count: < 2 })
        {
            // The curve's offset removed all of that pass (#694): there is nothing to turn onto.
            DiscardPlannedTurn(turn);
            effects.StatusMessage = "No pass there: the curve is too tight";
            return effects;
        }
        effects.IsInYouTurnMapFlag = true;

        // Manual triggers skip the boundary-anchored Dubins pipeline used by auto turns.
        // The arc starts at the tractor's current position so the turn begins the instant
        // the path renders — no headland traversal, no entry/exit legs (#260).
        var path = _creation.CreateManualArcPath(
            ctx.CurrentPosition, abHeading, turnLeft,
            ctx.Boundary, guidance, ctx.UTurnSkipRows, turn.NextTrack);

        if (path.Count > 2)
        {
            turn.TurnPath = path;
            turn.YouTurnCounter = 0;
            effects.SyncTurnPathToMap = true;
            // Manual turns trigger immediately — no proximity check against the turn start.
            turn.IsTriggered = true;
            turn.IsExecuting = true;
            _missedSounded = false;
            ForgetDirectionChoice(turn);
            effects.StatusMessage = $"Manual {(turnLeft ? "left" : "right")} U-turn started";
        }
        else
        {
            effects.StatusMessage = "Not enough room — turn would put the tool past the boundary";
        }

        return effects;
    }

    /// <summary>
    /// Clear all U-turn state. Called when closing a field. Safe to call at any time.
    /// </summary>
    public static void ClearState(YouTurnWorkingState turn)
    {
        ForgetDirectionChoice(turn);
        DiscardPlannedTurn(turn);
        turn.IsExecuting = false;
        turn.YouTurnCounter = 0;
        turn.CurrentZone = TractorZone.OutsideBoundary;
    }

    /// <summary>
    /// The active track changed: everything planned or driven on the old one goes (the turn,
    /// the pass it lands on, the snake order, the alternate pattern, the worked-pass cache).
    /// </summary>
    public void ForgetTrack(YouTurnWorkingState turn)
    {
        ClearState(turn);
        turn.SnakeSequence = null;
        turn.SnakeIndex = -1;
        turn.AltSign = 0;
        _coveredPasses.Clear();
    }

    /// <summary>
    /// Drop a planned turn that isn't being driven yet: the path, the next track and the
    /// snake / alternate target pass (#1203). The next tick plans a fresh one. Without the
    /// target reset, a later Normal or manual turn would complete onto the stale pass.
    /// </summary>
    public static void DiscardPlannedTurn(YouTurnWorkingState turn)
    {
        turn.TurnPath = null;
        turn.NextTrack = null;
        turn.IsTriggered = false;
        turn.ReturnPassTargetPath = null;
        turn.PlannedNextPath = null;
        // The operator's side from the U-turn arrow goes into the next plan too (#577).
        if (!turn.IsExecuting && turn.HeldDirectionLeft is { } held)
            turn.NextUTurnDirectionLeftOverride ??= held;
    }

    /// <summary>A turn has started: the arrow's side was for this turn, not the next.</summary>
    private static void ForgetDirectionChoice(YouTurnWorkingState turn)
    {
        turn.HeldDirectionLeft = null;
        turn.NextUTurnDirectionLeftOverride = null;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// True when the tractor is on the path: within <see cref="OnPathLateralMeters"/> of its
    /// nearest segment and heading along that segment (within <see cref="AlignmentTolerance"/>).
    /// The heading test keeps the exit leg, which runs the other way on the next pass, and an
    /// omega's crossing legs from counting. No allocation; the path is a few hundred points.
    /// </summary>
    internal static bool IsOnPath(List<Vec3> path, Position position, double headingRadians)
    {
        double px = position.Easting, py = position.Northing;
        double bestD2 = double.MaxValue;
        int best = -1;
        for (int i = 0; i < path.Count - 1; i++)
        {
            double ax = path[i].Easting, ay = path[i].Northing;
            double dx = path[i + 1].Easting - ax, dy = path[i + 1].Northing - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) continue;
            double t = ((px - ax) * dx + (py - ay) * dy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double ex = ax + t * dx - px, ey = ay + t * dy - py;
            double d2 = ex * ex + ey * ey;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }
        if (best < 0 || bestD2 > OnPathLateralMeters * OnPathLateralMeters) return false;

        double segHeading = Math.Atan2(path[best + 1].Easting - path[best].Easting,
                                       path[best + 1].Northing - path[best].Northing);
        double diff = headingRadians - segHeading;
        while (diff > Math.PI) diff -= 2 * Math.PI;
        while (diff < -Math.PI) diff += 2 * Math.PI;
        return Math.Abs(diff) < AlignmentTolerance;
    }

    /// <summary>True when the tractor drives the way the path sets off (its first segment), within 90°.
    /// A tractor going back down the pass is not a candidate for a turn planned at the far end.</summary>
    internal static bool IsHeadingAlong(List<Vec3> path, double headingRadians)
    {
        double segHeading = Math.Atan2(path[1].Easting - path[0].Easting, path[1].Northing - path[0].Northing);
        double diff = headingRadians - segHeading;
        while (diff > Math.PI) diff -= 2 * Math.PI;
        while (diff < -Math.PI) diff += 2 * Math.PI;
        return Math.Abs(diff) < Math.PI / 2;
    }

    /// <summary>True when the tractor is level with or beyond the path's first point, measured
    /// along the way the path sets off. Until then the path is still ahead of it.</summary>
    internal static bool IsPastStart(List<Vec3> path, Position position)
    {
        double dx = path[1].Easting - path[0].Easting, dy = path[1].Northing - path[0].Northing;
        return (position.Easting - path[0].Easting) * dx + (position.Northing - path[0].Northing) * dy >= 0;
    }

    /// <summary>
    /// On an open curve, true when <paramref name="point"/> is not ahead of the tractor along the
    /// track (the way it drives it): the nearest point of the track to it comes no later than the
    /// tractor's. Behind the tractor in a straight line is not enough on a curve that comes back
    /// past itself: a boundary curve made the long way round leaves the opening between its ends
    /// at A on the line it comes in on at B, the same way, so a turn planned at B lies straight
    /// behind a tractor setting off from A, a whole pass ahead along the track (#694). AB lines
    /// and closed loops: true (the straight-line test decides).
    /// </summary>
    internal static bool IsBehindAlongTrack(Models.Track.Track track, Vec3 point, Position position, bool headingSameWay)
    {
        var pts = track.Points;
        if (pts.Count <= 2 || track.IsClosed) return true;
        int pointIdx = 0, tractorIdx = 0;
        double bestP = double.MaxValue, bestT = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double pe = pts[i].Easting - point.Easting, pn = pts[i].Northing - point.Northing;
            double te = pts[i].Easting - position.Easting, tn = pts[i].Northing - position.Northing;
            double p2 = pe * pe + pn * pn, t2 = te * te + tn * tn;
            if (p2 < bestP) { bestP = p2; pointIdx = i; }
            if (t2 < bestT) { bestT = t2; tractorIdx = i; }
        }
        return headingSameWay ? pointIdx <= tractorIdx : pointIdx >= tractorIdx;
    }

    /// <summary>True when <paramref name="point"/> lies behind the tractor along its heading.</summary>
    internal static bool IsBehind(Vec3 point, Position position, double headingRadians)
    {
        double dx = point.Easting - position.Easting, dy = point.Northing - position.Northing;
        return dx * Math.Sin(headingRadians) + dy * Math.Cos(headingRadians) < 0;
    }

    private void HandleSnakeCreation(
        in TickContext ctx,
        Models.Track.Track track,
        double abHeading,
        Position currentPosition,
        double headingRadians,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects)
    {
        // Plan the snake from the pass the tractor is on: at the first turn, and again when the
        // tractor isn't on the pass the plan has it on: the operator moved to another pass
        // midway (snap, or drove there), or a swapped turn left the order. Going on from the
        // old pass sent the turn onto a worked pass, or back onto the one the tractor was on.
        if (!YouTurnPathingService.IsSnakeOnPass(turn, guidance.HowManyPathsAway))
        {
            _pathing.BuildSnakeSequence(track, abHeading, guidance, turn, ctx.Boundary, ctx.HeadlandLine);
        }

        // Never onto a worked pass: the next pass of the order that is still to do.
        var isWorked = WorkedPasses(in ctx, track, guidance, turn);
        int? nextPath = _pathing.GetNextSnakePath(turn, isWorked);
        // An order planned before the headland or boundary changed can go on to a pass that is
        // no longer in the field: plan it again for the field as it is.
        if (nextPath is { } stale && !_pathing.IsPathInsideCultivated(track, abHeading, stale, ctx.Boundary, ctx.HeadlandLine))
        {
            _logger.LogDebug("[YouTurn] Snake pass {Pass} is outside the field - planning the snake again", stale);
            _pathing.BuildSnakeSequence(track, abHeading, guidance, turn, ctx.Boundary, ctx.HeadlandLine);
            nextPath = _pathing.GetNextSnakePath(turn, isWorked);
        }
        if (nextPath == null)
        {
            _logger.LogDebug("[YouTurn] Snake sequence complete — field done");
            effects.StatusMessage = "Field complete — all tracks worked";
            SoundCreationFailed(effects);
            return;
        }

        // The operator swapped the direction: turn onto the nearest unworked pass on that side,
        // as AgOpenGPS's GetNextNotWorkedTrack does. CompleteTurn then plans the snake again
        // from that pass.
        int target = nextPath.Value;
        if (TakeDirectionSwap(turn, guidance, target))
        {
            bool positive = target < guidance.HowManyPathsAway;
            int skip = _pathing.GetNextUnworkedPathSkip(track, guidance.HowManyPathsAway, positive, ctx.UTurnSkipRows + 1, isWorked);
            int swapped = guidance.HowManyPathsAway + (positive ? skip : -skip);
            if (_pathing.IsPathLandable(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine, currentPosition, guidance.IsHeadingSameWay, turn.DistanceToHeadland, guidance.HowManyPathsAway))
                target = swapped;
            else
                RefuseDirectionSwap(swapped, effects);
        }

        // The order's next pass may be inside only at the far end (an hourglass field's other
        // lobe): a turn onto it here lands in the headland and drives out of the field.
        if (!_pathing.IsPathLandable(track, abHeading, target, ctx.Boundary, ctx.HeadlandLine, currentPosition, guidance.IsHeadingSameWay, turn.DistanceToHeadland, guidance.HowManyPathsAway))
        {
            _logger.LogDebug("[YouTurn] Snake pass {Pass} is too short inside the field at this end - no turn", target);
            effects.StatusMessage = "End of field reached - no room on the next pass at this end";
            SoundCreationFailed(effects);
            return;
        }

        CreateTurnToPath(in ctx, track, abHeading, headingRadians, guidance, turn, effects, target, "Snake");
    }

    /// <summary>
    /// The worked-pass test for the snake: marked worked, or covered on the job's map
    /// (<see cref="YouTurnPathingService.IsPassWorked"/>). The map test is remembered for the
    /// plan in hand (see <see cref="_coveredPasses"/>); the tractor's own pass, the only one
    /// being covered as it drives, is never the next pass of the plan.
    /// </summary>
    private Func<int, bool> WorkedPasses(in TickContext ctx, Models.Track.Track track, GuidanceWorkingState guidance,
        YouTurnWorkingState turn)
    {
        var boundary = ctx.Boundary;
        var headlandLine = ctx.HeadlandLine;
        var isPointCovered = ctx.IsPointCovered;
        double nudge = guidance.NudgeOffset;
        var plan = turn.SnakeSequence;
        if (!ReferenceEquals(plan, _coveredPassesKey.Plan) || !ReferenceEquals(track, _coveredPassesKey.Track)
            || nudge != _coveredPassesKey.Nudge || !ReferenceEquals(boundary, _coveredPassesKey.Boundary)
            || !ReferenceEquals(headlandLine, _coveredPassesKey.Headland))
        {
            _coveredPasses.Clear();
            _coveredPassesKey = (plan, track, nudge, boundary, headlandLine);
        }
        return pass =>
        {
            if (track.IsPathWorked(pass)) return true;
            if (!_coveredPasses.TryGetValue(pass, out bool covered))
                _coveredPasses[pass] = covered = _pathing.IsPassWorked(track, pass, nudge, boundary, headlandLine, isPointCovered);
            return covered;
        };
    }

    /// <summary>
    /// Turn onto a planned pass (snake sequence or alternative skip): the target path sets
    /// the turn side and width, and CompleteTurn jumps straight to it.
    /// </summary>
    private void CreateTurnToPath(
        in TickContext ctx,
        Models.Track.Track track,
        double abHeading,
        double headingRadians,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects,
        int nextPath,
        string mode)
    {
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        // The next pass is driven the other way: the tool shift changes sign (#446).
        double toolShift = Models.Track.PassGeometry.ToolShift(config.Tool.Offset, guidance.IsHeadingSameWay);
        // With the nudge, as the exit leg and the line steered after the turn (#591).
        double nextDistAway = Models.Track.PassGeometry.Offset(track, nextPath, widthMinusOverlap, guidance.NudgeOffset, -toolShift);
        int pathDiff = nextPath - guidance.HowManyPathsAway;

        // The planned path dictates pathDiff (snake / alternative), not the regular skip logic.
        // The callers have already turned it to the operator's side if the direction was swapped.
        bool positiveOffset = pathDiff > 0;
        turn.IsTurnLeft = positiveOffset ^ guidance.IsHeadingSameWay;
        turn.WasHeadingSameWayAtTurnStart = guidance.IsHeadingSameWay;
        // Signed in the turn direction, as in YouTurnPathingService.
        turn.NextTrackTurnOffset = (pathDiff * widthMinusOverlap + 2 * toolShift) * (positiveOffset ? 1 : -1);

        var refA = track.Points[0];
        var refB = track.Points[track.Points.Count - 1];
        double perpAngle = abHeading + Math.PI / 2;

        if (track.Points.Count == 2)
        {
            double offsetE = Math.Sin(perpAngle) * nextDistAway;
            double offsetN = Math.Cos(perpAngle) * nextDistAway;
            turn.NextTrack = Models.Track.Track.FromABLine(
                $"Path {nextPath}",
                new Vec3(refA.Easting + offsetE, refA.Northing + offsetN, abHeading),
                new Vec3(refB.Easting + offsetE, refB.Northing + offsetN, abHeading));
        }
        else
        {
            // Offset then EXTEND the ends so the cyan next-track curve matches the
            // exit leg and the post-turn magenta line (no gap at the exit handoff).
            // Mirrors YouTurnPathingService.ComputeNextTrack; no-op on closed loops.
            var offsetPoints = CurveProcessing.ExtendPass(CurveProcessing.CurvePass(track.Points, nextDistAway));
            turn.NextTrack = Models.Track.Track.FromCurve($"Path {nextPath}", offsetPoints, track.IsClosed);
        }
        turn.NextTrack.IsActive = false;

        // CompleteTurn will jump directly to this path number instead of computing a skip.
        turn.ReturnPassTargetPath = nextPath;

        _logger.LogDebug("[YouTurn] {Mode}: path {Cur} -> {Next} (diff={Diff}, offset={Off:F1}m, turnLeft={Left})",
            mode, guidance.HowManyPathsAway, nextPath, pathDiff, nextDistAway, turn.IsTurnLeft);

        effects.SyncNextTrackToMap = true;
        effects.IsInYouTurnMapFlag = true;

        CreatePathAndSync(in ctx, track, headingRadians, abHeading, guidance, turn, effects);
    }

    /// <summary>
    /// Alternative skip (AgOpenGPS SkipMode.Alternative, CYouTurn.YouTurnTrigger): plan the
    /// next pass from the pattern; it advances when the turn completes (#111).
    /// </summary>
    private void HandleAlternateCreation(
        in TickContext ctx,
        Models.Track.Track track,
        double abHeading,
        double headingRadians,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects)
    {
        int baseWidth = Math.Max(2, ctx.UTurnSkipRows + 1); // AgOpenGPS "at least 1" row skipped
        if (turn.AltSign == 0 || turn.AltBaseWidth != baseWidth)
        {
            // Start the pattern the way the turns have been going, away from the worked passes,
            // as normal turns do (#523) and AgOpenGPS's alternating isTurnLeft carries on. Left
            // to the pass number's sign it went back over worked passes (turn intervention T4).
            bool? keepPositive = _lastStep != 0 && _lastStepTrack == track.Name ? _lastStep > 0 : null;
            var (_, positive) = _pathing.WouldNextLineBeInsideBoundary(
                track, abHeading, guidance, ctx.Boundary, ctx.HeadlandLine, baseWidth - 1, keepPositive, ctx.CurrentPosition, turn.DistanceToHeadland);
            turn.AltSign = positive ? 1 : -1;
            turn.AltBaseWidth = turn.AltWidth = baseWidth;
            turn.AltTurnSkips = baseWidth * 2 - 1;
            turn.AltPrevBig = false;
        }

        int nextPath = guidance.HowManyPathsAway + turn.AltSign * turn.AltWidth;
        // The operator swapped the direction: this turn goes to the other side and the pattern
        // alternates on from there, as AgOpenGPS's flipped isTurnLeft does.
        if (TakeDirectionSwap(turn, guidance, nextPath))
        {
            int swapped = guidance.HowManyPathsAway - turn.AltSign * turn.AltWidth;
            if (_pathing.IsPathLandable(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine, ctx.CurrentPosition, guidance.IsHeadingSameWay, turn.DistanceToHeadland, guidance.HowManyPathsAway))
            {
                turn.AltSign = -turn.AltSign;
                nextPath = swapped;
            }
            else
            {
                RefuseDirectionSwap(swapped, effects);
            }
        }
        if (!_pathing.IsPathLandable(track, abHeading, nextPath, ctx.Boundary, ctx.HeadlandLine, ctx.CurrentPosition, guidance.IsHeadingSameWay, turn.DistanceToHeadland, guidance.HowManyPathsAway))
        {
            effects.StatusMessage = _pathing.IsPathInsideCultivated(track, abHeading, nextPath, ctx.Boundary, ctx.HeadlandLine)
                ? "End of field reached - no room on the next pass at this end"
                : "End of field reached";
            SoundCreationFailed(effects);
            return;
        }
        CreateTurnToPath(in ctx, track, abHeading, headingRadians, guidance, turn, effects, nextPath, "Alternative");
    }

    // After an alternative-skip turn: flip side and alternate the width, except every
    // (2W-1)th turn, which keeps the side (AgOpenGPS YouTurnTrigger).
    internal static void AdvanceAlternate(YouTurnWorkingState turn)
    {
        if (turn.AltSign == 0) return;
        if (--turn.AltTurnSkips == 0)
        {
            turn.AltTurnSkips = turn.AltBaseWidth * 2 - 1;
            return;
        }
        turn.AltSign = -turn.AltSign;
        turn.AltPrevBig = !turn.AltPrevBig;
        turn.AltWidth = turn.AltPrevBig ? turn.AltBaseWidth - 1 : turn.AltBaseWidth;
    }

    private void HandleNormalCreation(
        in TickContext ctx,
        Models.Track.Track track,
        double abHeading,
        Position currentPosition,
        double headingRadians,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects)
    {
        bool? keepPositive = _lastStep != 0 && _lastStepTrack == track.Name ? _lastStep > 0 : null;
        var (nextLineInside, positiveDirection) = _pathing.WouldNextLineBeInsideBoundary(
            track, abHeading, guidance, ctx.Boundary, ctx.HeadlandLine, ctx.UTurnSkipRows, keepPositive, currentPosition, turn.DistanceToHeadland);

        _logger.LogDebug("[YouTurn] Creating turn? nextLineInside={Inside} positiveDir={Dir}", nextLineInside, positiveDirection);
        // The turns step one way across the field (#523). When that side has no pass left, the
        // turn back lands on the pass it came from, just worked: the field is done, as AgOpenGPS
        // ends it (the next pass out of bounds: FailCreate). Before the first turn on this track
        // the inside side is the way to go, and the operator's arrow still turns either way.
        // Skipped passes are filled with the alternative or ignore-worked modes. Only on the pass
        // the last turn landed on: moved to another pass, or the field opened again, the tractor
        // may start from this edge.
        bool steppedBack = false;
        if (nextLineInside && keepPositive is { } stepping && guidance.HowManyPathsAway == _lastStepPass
            && positiveDirection != stepping && turn.NextUTurnDirectionLeftOverride is null)
        {
            nextLineInside = false;
            steppedBack = true;
        }
        if (!nextLineInside)
        {
            // A pass inside the field somewhere, but with no room to land on at this end (a
            // corner pass, the far lobe of an hourglass): the operator takes over here too.
            // Stepped back, that is the next pass on the way the turns go: the pass just worked
            // has room but is no pass to go to, and the next one may still run on through the
            // field (field job J3, a narrowing lobe with 40 % of the field left), which is not
            // the end of the field.
            bool roomSomewhere = steppedBack
                ? _pathing.IsPathInsideCultivated(track, abHeading,
                    guidance.HowManyPathsAway + (keepPositive == true ? 1 : -1) * (ctx.UTurnSkipRows + 1), ctx.Boundary, ctx.HeadlandLine)
                : _pathing.WouldNextLineBeInsideBoundary(
                    track, abHeading, guidance, ctx.Boundary, ctx.HeadlandLine, ctx.UTurnSkipRows, keepPositive).isInside;
            _logger.LogDebug("[YouTurn] Next line would be outside boundary{Room} - stopping U-turns (headland {Dist:F1}m ahead of ({E:F1},{N:F1}) heading {Hdg:F0}°, zone {Zone})",
                roomSomewhere ? " at this end" : "", turn.DistanceToHeadland, currentPosition.Easting, currentPosition.Northing, currentPosition.Heading, turn.CurrentZone);
            effects.StatusMessage = roomSomewhere ? "End of field reached - no room on the next pass at this end" : "End of field reached";
            SoundCreationFailed(effects);
            return;
        }

        _logger.LogDebug("[YouTurn] Creating turn path at {Dist:F1}m from headland", turn.DistanceToHeadland);
        // IsTurnLeft depends on which direction has cultivated area and the current heading.
        // positiveDirection ^ IsHeadingSameWay gives the correct turn direction.
        turn.IsTurnLeft = positiveDirection ^ guidance.IsHeadingSameWay;
        // The operator swapped the direction (the U-turn arrow, legacy FormGPS.SwapDirection):
        // turn the other way, when the pass on that side is inside the field.
        int pathsToMove = ctx.UTurnSkipRows + 1;
        int plannedPath = guidance.HowManyPathsAway + (positiveDirection ? pathsToMove : -pathsToMove);
        if (TakeDirectionSwap(turn, guidance, plannedPath))
        {
            int swapped = guidance.HowManyPathsAway + (positiveDirection ? -pathsToMove : pathsToMove);
            if (_pathing.IsPathLandable(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine, currentPosition, guidance.IsHeadingSameWay, turn.DistanceToHeadland, guidance.HowManyPathsAway))
            {
                turn.IsTurnLeft = !turn.IsTurnLeft;
                _logger.LogDebug("[YouTurn] Direction swapped: turnLeft={Left}", turn.IsTurnLeft);
            }
            else
            {
                RefuseDirectionSwap(swapped, effects);
            }
        }
        turn.WasHeadingSameWayAtTurnStart = guidance.IsHeadingSameWay;

        _pathing.ComputeNextTrack(
            track, abHeading, guidance, turn,
            ctx.UTurnSkipRows, ctx.IsSkipWorkedMode, ctx.SelectedTrack);
        effects.SyncNextTrackToMap = true;
        effects.IsInYouTurnMapFlag = true;

        CreatePathAndSync(in ctx, track, headingRadians, abHeading, guidance, turn, effects);
    }

    /// <summary>
    /// Take the operator's direction swap (the U-turn arrow) for a turn planned onto
    /// <paramref name="plannedPath"/>, and clear it so it never reaches a later turn. True
    /// when the operator asked for the other side; the caller then picks a pass there.
    /// </summary>
    private static bool TakeDirectionSwap(YouTurnWorkingState turn, GuidanceWorkingState guidance, int plannedPath)
    {
        if (turn.NextUTurnDirectionLeftOverride is not { } overrideLeft) return false;
        turn.NextUTurnDirectionLeftOverride = null;
        turn.HeldDirectionLeft = overrideLeft;
        // Same rule as CreateTurnToPath: a higher pass number lies right of the AB heading.
        bool plannedLeft = (plannedPath > guidance.HowManyPathsAway) ^ guidance.IsHeadingSameWay;
        return overrideLeft != plannedLeft;
    }

    /// <summary>The swapped side has no pass inside the field: keep the planned turn.</summary>
    /// <summary>The missed-turn sound, once until a turn starts or U-turn is switched off (#570).</summary>
    private void SoundMissed(YouTurnEffects effects)
    {
        if (_missedSounded) return;
        _missedSounded = true;
        effects.TurnCreationFailedSound = true;
    }

    /// <summary>U-turn switched off: the next missed or failed turn sounds again (AgOpenGPS
    /// resets turnTooCloseTrigger on the U-turn button).</summary>
    public void OnYouTurnSwitchedOff() => _missedSounded = _creationFailSounded = false;

    /// <summary>
    /// No turn can be made: the turn would not build, or there is no pass to turn onto (the end of
    /// the field, every pass worked). Sounds once until a turn is built or U-turn is switched off,
    /// as AgOpenGPS plays sndUTurnTooClose for every FailCreate, the next pass outside the field
    /// included. Before, the end of the field showed only a status line and the tractor drove into
    /// the headland in silence.
    /// </summary>
    private void SoundCreationFailed(YouTurnEffects effects)
    {
        if (_creationFailSounded) return;
        _creationFailSounded = true;
        effects.TurnCreationFailedSound = true;
    }

    private void RefuseDirectionSwap(int swappedPath, YouTurnEffects effects)
    {
        _logger.LogDebug("[YouTurn] Direction swap refused: path {Path} is outside the field", swappedPath);
        effects.StatusMessage = "U-turn direction not changed: no pass on that side inside the field";
    }

    private void CreatePathAndSync(
        in TickContext ctx,
        Models.Track.Track track,
        double headingRadians,
        double abHeading,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects)
    {
        var result = _creation.CreateTurnPath(
            ctx.CurrentPosition, track, headingRadians, abHeading,
            ctx.Boundary, ctx.HeadlandLine,
            guidance, turn,
            ctx.UTurnSkipRows, ctx.HeadlandCalculatedWidth, ctx.HeadlandDistance);

        if (result.ClearanceBlocked && effects.StatusMessage == null)
            effects.StatusMessage = "U-turn blocked: implement would swing into a hard boundary — take over manually.";
        // No real turn fits and the fallback would leave the field (#692): the pass is driven to its
        // end with no turn, so the operator is told, not only sounded at.
        if (result.Path == null && result.UsedFallback && effects.StatusMessage == null)
            effects.StatusMessage = "No U-turn fits inside the boundary at this end - take over manually";

        if (result.Path == null)
        {
            SoundCreationFailed(effects);
            return;
        }
        _creationFailSounded = false;
        _approachAlarmed = false;

        turn.TurnPath = result.Path;
        turn.ArcStart = result.ArcStart;
        turn.YouTurnCounter = 0;
        double startsInside = TurnStartInsideHeadlandLine(result.ArcStart, ctx.HeadlandLine);
        if (startsInside > NarrowHeadlandToleranceM && effects.StatusMessage == null)
            effects.StatusMessage = HeadlandTooNarrowMessage(startsInside);
        else if (!result.UsedFallback && effects.StatusMessage == null)
            effects.StatusMessage = $"YouTurn path created ({result.Path.Count} points)";
        effects.SyncTurnPathToMap = true;
    }

    /// <summary>Less than this inside the headland line is the turn line's own rounding.</summary>
    private const double NarrowHeadlandToleranceM = 0.5;

    /// <summary>
    /// The operator's warning for a turn that leaves its pass before the headland line: the
    /// headland is narrower than the turn needs at this radius and pass spacing, so the turn,
    /// moved in to stay inside the turn line (as AgOpenGPS), starts in the cultivated area with
    /// the sections still on: a wedge of the pass end is left unsprayed and the other side is
    /// sprayed twice (field job scenarios: a 16 m headland, an 8 m radius on 8 m passes, the
    /// turn starting 4.6 m inside).
    /// </summary>
    public static string HeadlandTooNarrowMessage(double insideM) => string.Format(CultureInfo.InvariantCulture,
        "Headland too narrow for this U-turn: it leaves the pass {0:0.0} m before the headland line, still working. Widen the headland or reduce the U-turn radius.", insideM);

    /// <summary>How far inside the headland line (in the cultivated area) the turn leaves its
    /// pass: the arc's start, before the entry leg. 0 when it starts in the headland or there
    /// is no headland line.</summary>
    public static double TurnStartInsideHeadlandLine(Vec3? arcStart, IReadOnlyList<Vec3>? headlandLine)
    {
        if (arcStart is not { } a || headlandLine == null || headlandLine.Count < 3) return 0;
        if (!GeometryMath.IsPointInPolygon(headlandLine, a)) return 0;
        double best = double.MaxValue;
        for (int i = 0, j = headlandLine.Count - 1; i < headlandLine.Count; j = i++)
        {
            var p = headlandLine[j]; var q = headlandLine[i];
            double dx = q.Easting - p.Easting, dy = q.Northing - p.Northing, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((a.Easting - p.Easting) * dx + (a.Northing - p.Northing) * dy) / l2, 0, 1);
            double ex = p.Easting + t * dx - a.Easting, ey = p.Northing + t * dy - a.Northing;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return Math.Sqrt(best);
    }

    private void CompleteTurn(
        in TickContext ctx,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        YouTurnEffects effects)
    {
        // Guard against double-call (turn completion can fire from both Tick and
        // eventual pipeline guidance completion).
        if (!turn.IsExecuting)
        {
            _logger.LogDebug("[YouTurn] CompleteTurn called but not in turn - ignoring");
            return;
        }

        var config = _configStore;
        int passBefore = guidance.HowManyPathsAway;

        if (turn.ReturnPassTargetPath.HasValue)
        {
            _logger.LogDebug("[YouTurn] Turn complete! Jumping to path {Target} (was {Cur})",
                turn.ReturnPassTargetPath.Value, guidance.HowManyPathsAway);
            guidance.HowManyPathsAway = turn.ReturnPassTargetPath.Value;
            turn.ReturnPassTargetPath = null;
            if (ctx.IsAlternateSkipMode) AdvanceAlternate(turn);
            else if (ctx.SelectedTrack != null)
            {
                // On to the pass the turn landed on. A swapped turn (the U-turn arrow) left the
                // planned order: the next turn plans the snake again from this pass, as a skip
                // change does (#410).
                _pathing.AdvanceSnakeSequence(turn, guidance.HowManyPathsAway,
                    WorkedPasses(in ctx, ctx.SelectedTrack, guidance, turn));
                _coveredPasses.Clear(); // decided again for the next turn
            }
        }
        else if (turn.PlannedNextPath is { } planned)
        {
            // Onto the pass the turn was built to reach. The skip may have changed while the
            // turn was driven; that applies from the next turn on (#585). AgOpenGPS lands there
            // too: it moves to the next pass when the turn starts (YouTurnTrigger).
            _logger.LogDebug("[YouTurn] Turn complete! Onto the planned path {Target} (was {Cur})",
                planned, guidance.HowManyPathsAway);
            guidance.HowManyPathsAway = planned;
        }
        else
        {
            int pathsToMove = ctx.UTurnSkipRows + 1;

            // WasHeadingSameWayAtTurnStart was saved at turn creation — IsHeadingSameWay has
            // since flipped (we just finished a 180° turn), so we need the pre-turn value.
            // CreateOffsetCurve offsets RIGHT of heading (positive perpAngle in the
            // Sin/Cos coordinate system). So positive pass = right of AB heading.
            // Turn left while heading same way = next pass is LEFT of heading = negative offset.
            bool positiveOffset = turn.IsTurnLeft ^ turn.WasHeadingSameWayAtTurnStart;

            if (ctx.IsSkipWorkedMode && ctx.SelectedTrack != null)
            {
                pathsToMove = _pathing.GetNextUnworkedPathSkip(
                    ctx.SelectedTrack, guidance.HowManyPathsAway, positiveOffset, pathsToMove);
            }

            int offsetChange = positiveOffset ? pathsToMove : -pathsToMove;
            guidance.HowManyPathsAway += offsetChange;

            _logger.LogDebug("[YouTurn] Turn complete! Normal: offset {Sign} by {Change}",
                positiveOffset ? "positive" : "negative", offsetChange);
        }

        _lastStep = Math.Sign(guidance.HowManyPathsAway - passBefore);
        _lastStepTrack = ctx.SelectedTrack?.Name;
        _lastStepPass = guidance.HowManyPathsAway;

        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        _logger.LogDebug("[YouTurn] Now on path {Path} ({Off:F1}m from reference)",
            guidance.HowManyPathsAway, widthMinusOverlap * guidance.HowManyPathsAway);

        turn.LastTurnWasLeft = turn.IsTurnLeft;
        turn.PlannedNextPath = null;
        ForgetDirectionChoice(turn);
        turn.HasCompletedFirstTurn = true;
        turn.IsTriggered = false;
        turn.IsExecuting = false;
        turn.TurnPath = null;
        turn.NextTrack = null;
        // Keep counter high so the next turn creation window opens immediately.
        turn.YouTurnCounter = 10;

        effects.SyncTurnPathToMap = true;
        effects.SyncNextTrackToMap = true;
        effects.IsInYouTurnMapFlag = false;
        effects.TurnCompleted = true;
        effects.StatusMessage = $"Following path {guidance.HowManyPathsAway} ({widthMinusOverlap * Math.Abs(guidance.HowManyPathsAway):F1}m offset)";
    }

    private double RaycastDistanceToHeadland(
        Position currentPosition,
        double headingRadians,
        IReadOnlyList<Vec3> headlandLine,
        bool isHeadingSameWay,
        int counter)
    {
        if (headlandLine.Count < 3) return double.MaxValue;

        double minDistance = double.MaxValue;
        var pos = new Vec2(currentPosition.Easting, currentPosition.Northing);
        var dir = new Vec2(Math.Sin(headingRadians), Math.Cos(headingRadians));

        int intersectionCount = 0;
        int n = headlandLine.Count;
        for (int i = 0; i < n; i++)
        {
            var p1 = headlandLine[i];
            var p2 = headlandLine[(i + 1) % n];

            var edge = new Vec2(p2.Easting - p1.Easting, p2.Northing - p1.Northing);
            var toP1 = new Vec2(p1.Easting - pos.Easting, p1.Northing - pos.Northing);

            double cross = dir.Easting * edge.Northing - dir.Northing * edge.Easting;
            if (Math.Abs(cross) < 1e-10) continue;

            double t = (toP1.Easting * edge.Northing - toP1.Northing * edge.Easting) / cross;
            double u = (toP1.Easting * dir.Northing - toP1.Northing * dir.Easting) / cross;

            if (t > 0 && u >= 0 && u <= 1)
            {
                intersectionCount++;
                if (t < minDistance) minDistance = t;
            }
        }

        if (counter % 120 == 0)
        {
            _logger.LogDebug("[Headland] Raycast: pos=({E:F1},{N:F1}), heading={Deg:F0}°, intersections={Hits}, minDist={Dist:F1}m, isHeadingSameWay={Way}",
                pos.Easting, pos.Northing, headingRadians * 180 / Math.PI, intersectionCount, minDistance, isHeadingSameWay);
        }

        return minDistance;
    }

    private TractorZone DetermineZone(
        double easting,
        double northing,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine)
    {
        // Cultivated area (inside the headland) — most common case, check first.
        if (headlandLine != null && headlandLine.Count >= 3)
        {
            if (_headlandIndex.Contains(headlandLine, easting, northing))
                return TractorZone.InCultivatedArea;
        }

        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            if (boundary.OuterBoundary.IsPointInside(easting, northing))
                return TractorZone.InHeadland;
        }

        return TractorZone.OutsideBoundary;
    }
}

/// <summary>
/// Side effects emitted by the <see cref="YouTurnStateMachine"/>. The caller applies
/// these after state mutation: syncs the map service, resets guidance state when a
/// turn completes, and updates the UI status message.
/// </summary>
public sealed class YouTurnEffects
{
    /// <summary>Set when a user-visible status message should be raised.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>True when the caller should push <c>YouTurnState.TurnPath</c> to the map.</summary>
    public bool SyncTurnPathToMap { get; set; }

    /// <summary>True when the caller should push <c>YouTurnState.NextTrack</c> to the map.</summary>
    public bool SyncNextTrackToMap { get; set; }

    /// <summary>
    /// When non-null, the caller should toggle the map's "in YouTurn" flag. This is the
    /// cyan-line / dotted-current-line render flag; it's independent of
    /// <c>YouTurnState.IsExecuting</c> — the map shows it from the moment a next track
    /// is drafted, and clears on completion.
    /// </summary>
    public bool? IsInYouTurnMapFlag { get; set; }

    /// <summary>
    /// True when a turn just completed. The caller should clear its
    /// TrackGuidanceState cache and re-sync the pipeline so guidance targets
    /// the newly-offset track from the start.
    /// </summary>
    public bool TurnCompleted { get; set; }

    /// <summary>U-turn sound (AgOpenGPS isTurnSoundOn, #110): the turn couldn't be created
    /// (sndUTurnTooClose), once per failure run.</summary>
    public bool TurnCreationFailedSound { get; set; }

    /// <summary>U-turn sound: 18–20 m before the turn starts (AgOpenGPS sndBoundaryAlarm).</summary>
    public bool ApproachAlarmSound { get; set; }
}
