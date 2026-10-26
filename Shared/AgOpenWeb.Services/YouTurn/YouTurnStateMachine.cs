// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

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
    private bool _creationFailSounded, _approachAlarmed;
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
        guidance.IsHeadingSameWay = Math.Abs(headingDiff) < Math.PI / 2;

        bool alignedForward = Math.Abs(headingDiff) < AlignmentTolerance;
        bool alignedReverse = Math.Abs(headingDiff) > (Math.PI - AlignmentTolerance);
        bool isAlignedWithABLine = alignedForward || alignedReverse;

        // Distance-to-headland raycast is valid only when the tractor is aligned with the track;
        // otherwise heading swings mid-turn create spurious proximity readings.
        double travelHeading = abHeading;
        if (!guidance.IsHeadingSameWay)
        {
            travelHeading += Math.PI;
            if (travelHeading >= Math.PI * 2) travelHeading -= Math.PI * 2;
        }

        turn.DistanceToHeadland = isAlignedWithABLine
            ? RaycastDistanceToHeadland(currentPosition, travelHeading, ctx.HeadlandLine, guidance.IsHeadingSameWay, turn.YouTurnCounter)
            : double.MaxValue;

        turn.CurrentZone = DetermineZone(currentPosition.Easting, currentPosition.Northing, ctx.Boundary, ctx.HeadlandLine);

        bool isInCultivatedArea = turn.CurrentZone == TractorZone.InCultivatedArea;
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
                effects.StatusMessage = "YouTurn triggered!";
                _logger.LogDebug("[YouTurn] Triggered on the path, {Dist:F1}m from the arc", distToArcStart);
            }
            else if (isAlignedWithABLine && IsHeadingAlong(turn.TurnPath, headingRadians)
                     && IsBehind(arcStart, currentPosition, headingRadians))
            {
                // The arc already begins behind the tractor, so this path cannot be joined.
                // Turn from here instead, the way a manual turn does; when there is no room
                // for that either, say so, because the operator has to turn by hand now.
                var fromHere = _creation.CreateManualArcPath(
                    currentPosition, abHeading, turn.IsTurnLeft,
                    ctx.Boundary, guidance, ctx.UTurnSkipRows, turn.NextTrack);
                if (fromHere.Count > 2)
                {
                    turn.TurnPath = fromHere;
                    turn.ArcStart = null;
                    turn.YouTurnCounter = 0;
                    turn.IsTriggered = true;
                    turn.IsExecuting = true;
                    effects.StatusMessage = "YouTurn started from here: the planned arc was already behind the tractor";
                    _logger.LogDebug("[YouTurn] Arc start {Dist:F1}m behind the tractor - turning from here ({Count} points)",
                        distToArcStart, fromHere.Count);
                }
                else
                {
                    _logger.LogDebug("[YouTurn] Arc start {Dist:F1}m behind the tractor and no room to turn from here - turn missed",
                        distToArcStart);
                    DiscardPlannedTurn(turn);
                    effects.TurnCreationFailedSound = true;
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
                effects.TurnCreationFailedSound = true;
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
        _logger.LogDebug("[YouTurn] Guidance reached end of turn path — completing turn");
        CompleteTurn(in ctx, guidance, turn, effects);
        return effects;
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
        DiscardPlannedTurn(turn);
        turn.IsExecuting = false;
        turn.YouTurnCounter = 0;
        turn.CurrentZone = TractorZone.OutsideBoundary;
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
        if (nextPath == null)
        {
            _logger.LogDebug("[YouTurn] Snake sequence complete — field done");
            effects.StatusMessage = "Field complete — all tracks worked";
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
            if (_pathing.IsPathInsideCultivated(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine))
                target = swapped;
            else
                RefuseDirectionSwap(swapped, effects);
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
        double nextDistAway = Models.Track.PassGeometry.Offset(track, nextPath, widthMinusOverlap);
        int pathDiff = nextPath - guidance.HowManyPathsAway;

        // The planned path dictates pathDiff (snake / alternative), not the regular skip logic.
        // The callers have already turned it to the operator's side if the direction was swapped.
        bool positiveOffset = pathDiff > 0;
        turn.IsTurnLeft = positiveOffset ^ guidance.IsHeadingSameWay;
        turn.WasHeadingSameWayAtTurnStart = guidance.IsHeadingSameWay;
        turn.NextTrackTurnOffset = Math.Abs(pathDiff) * widthMinusOverlap;

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
            var offsetPoints = CurveProcessing.ExtendCurveEnds(
                CurveProcessing.CreateOffsetCurve(track.Points, nextDistAway));
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
            var (_, positive) = _pathing.WouldNextLineBeInsideBoundary(
                track, abHeading, guidance, ctx.Boundary, ctx.HeadlandLine, baseWidth - 1);
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
            if (_pathing.IsPathInsideCultivated(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine))
            {
                turn.AltSign = -turn.AltSign;
                nextPath = swapped;
            }
            else
            {
                RefuseDirectionSwap(swapped, effects);
            }
        }
        if (!_pathing.IsPathInsideCultivated(track, abHeading, nextPath, ctx.Boundary, ctx.HeadlandLine))
        {
            effects.StatusMessage = "End of field reached";
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
        var (nextLineInside, positiveDirection) = _pathing.WouldNextLineBeInsideBoundary(
            track, abHeading, guidance, ctx.Boundary, ctx.HeadlandLine, ctx.UTurnSkipRows);

        _logger.LogDebug("[YouTurn] Creating turn? nextLineInside={Inside} positiveDir={Dir}", nextLineInside, positiveDirection);
        if (!nextLineInside)
        {
            _logger.LogDebug("[YouTurn] Next line would be outside boundary - stopping U-turns");
            effects.StatusMessage = "End of field reached";
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
            if (_pathing.IsPathInsideCultivated(track, abHeading, swapped, ctx.Boundary, ctx.HeadlandLine))
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
        // Same rule as CreateTurnToPath: a higher pass number lies right of the AB heading.
        bool plannedLeft = (plannedPath > guidance.HowManyPathsAway) ^ guidance.IsHeadingSameWay;
        return overrideLeft != plannedLeft;
    }

    /// <summary>The swapped side has no pass inside the field: keep the planned turn.</summary>
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

        if (result.Path == null)
        {
            if (!_creationFailSounded) { _creationFailSounded = true; effects.TurnCreationFailedSound = true; }
            return;
        }
        _creationFailSounded = false;
        _approachAlarmed = false;

        turn.TurnPath = result.Path;
        turn.ArcStart = result.ArcStart;
        turn.YouTurnCounter = 0;
        if (!result.UsedFallback && effects.StatusMessage == null)
            effects.StatusMessage = $"YouTurn path created ({result.Path.Count} points)";
        effects.SyncTurnPathToMap = true;
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

        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        _logger.LogDebug("[YouTurn] Now on path {Path} ({Off:F1}m from reference)",
            guidance.HowManyPathsAway, widthMinusOverlap * guidance.HowManyPathsAway);

        turn.LastTurnWasLeft = turn.IsTurnLeft;
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
