// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.Models.Pipeline;

/// <summary>
/// Cycle-worker-owned mirror of <see cref="State.YouTurnState"/>.
/// Plain POCO: no ObservableObject, no PropertyChanged, no UI thread awareness.
/// Single-writer — the cycle worker mutates freely. Never touched on the UI thread.
/// See <c>Plans/threading_model.svg</c> (yellow WorkingState box).
/// </summary>
public class YouTurnWorkingState
{
    // Enable/trigger
    public bool IsEnabled { get; set; }
    public bool IsTriggered { get; set; }
    public bool IsExecuting { get; set; }

    // Turn path
    public List<Vec3>? TurnPath { get; set; }
    public int PathIndex { get; set; }
    // Where TurnPath leaves the current pass (the arc's first point); null when the path has
    // no entry leg. The approach alarm and the too-close rule measure against it.
    public Vec3? ArcStart { get; set; }
    // The planned turn is made before an obstacle (an inner boundary chosen for a U-turn), not at
    // the field's edge: a turn missed there is not turned from where the tractor is.
    public bool TurnAtObstacle { get; set; }
    // With TurnAtObstacle, that obstacle's index in the inner boundaries; else -1.
    public int TurnAtObstacleIndex { get; set; } = -1;

    // Direction
    public bool IsTurnLeft { get; set; }
    public bool LastTurnWasLeft { get; set; }

    // Distance tracking
    public double DistanceToHeadland { get; set; }
    public double DistanceToTrigger { get; set; }

    // Next track after turn (unified Track model)
    public Track.Track? NextTrack { get; set; }

    // Completion tracking
    public Vec2? LastCompletionPosition { get; set; }
    public bool HasCompletedFirstTurn { get; set; }

    // Counter for stability
    public int YouTurnCounter { get; set; }

    // Heading direction captured at turn start, used to compute the post-turn pass offset.
    // Stored because IsHeadingSameWay on GuidanceState flips 180° once the turn completes.
    public bool WasHeadingSameWayAtTurnStart { get; set; }

    // Pre-computed perpendicular offset to the next track (meters, always positive).
    // Authoritative value for U-turn arc width — consumers should use this rather than recompute.
    public double NextTrackTurnOffset { get; set; }

    // When set, CompleteTurn jumps directly to this path number (used by snake / skip-worked modes).
    public int? ReturnPassTargetPath { get; set; }

    // Pre-computed path sequence for skip-and-fill (snake) mode. Null when not in snake mode.
    public List<int>? SnakeSequence { get; set; }

    public int SnakeIndex { get; set; } = -1;

    // Alternative skip mode (AgOpenGPS SkipMode.Alternative, #111). AltBaseWidth = passes
    // per skip (skip rows + 1); moves alternate AltBaseWidth-1 / AltBaseWidth and the side
    // flips each turn except every (2W-1)th, so the field is covered out-and-back.
    // AltSign 0 = pattern not started.
    public int AltSign { get; set; }
    public int AltBaseWidth { get; set; }
    public int AltWidth { get; set; }
    public int AltTurnSkips { get; set; }
    public bool AltPrevBig { get; set; }

    // Zone the tractor is in — source of truth for turn creation gating.
    public TractorZone CurrentZone { get; set; } = TractorZone.OutsideBoundary;

    /// <summary>
    /// Previous distance to turn end point. Used for closest-approach completion:
    /// when distance starts increasing after being close, the turn is complete.
    /// </summary>
    public double PreviousDistToTurnEnd { get; set; } = double.MaxValue;

    /// <summary>
    /// One-shot override for the direction of the *next* armed automatic turn.
    /// Set by the U-turn direction toggle while no turn is currently armed
    /// (<c>!IsTriggered &amp;&amp; !IsExecuting</c>). The state machine consults
    /// this when computing <see cref="IsTurnLeft"/> for the upcoming turn and
    /// clears it on consumption so it never leaks into a later turn.
    /// Mirrors legacy <c>FormGPS.SwapDirection</c> behavior.
    /// </summary>
    public bool? NextUTurnDirectionLeftOverride { get; set; }

    /// <summary>
    /// The side the operator picked with the U-turn arrow for the coming turn, kept from when
    /// the override is taken until a turn starts. A planned turn dropped before then (the skip
    /// changed, a nudge, a lateral move) is planned again on that side, as AgOpenGPS keeps
    /// <c>yt.isTurnLeft</c> when the skip changes (#577).
    /// </summary>
    public bool? HeldDirectionLeft { get; set; }

    /// <summary>
    /// The pass a normal (or skip-worked) turn was built to land on, set when its next track is
    /// planned. The turn completes onto it, so a skip changed while the turn is driven applies
    /// from the next turn on, not to the one already laid out (#585).
    /// </summary>
    public int? PlannedNextPath { get; set; }

    public void Reset()
    {
        IsTriggered = false;
        IsExecuting = false;
        TurnPath = null;
        ArcStart = null;
        TurnAtObstacle = false;
        TurnAtObstacleIndex = -1;
        PathIndex = 0;
        PreviousDistToTurnEnd = double.MaxValue;
        DistanceToHeadland = double.MaxValue;
        DistanceToTrigger = 0;
        NextTrack = null;
        HasCompletedFirstTurn = false;
        YouTurnCounter = 0;
        WasHeadingSameWayAtTurnStart = false;
        NextTrackTurnOffset = 0;
        ReturnPassTargetPath = null;
        SnakeSequence = null;
        SnakeIndex = -1;
        AltSign = 0;
        CurrentZone = TractorZone.OutsideBoundary;
        NextUTurnDirectionLeftOverride = null;
        HeldDirectionLeft = null;
        PlannedNextPath = null;
    }

    public void CompleteTurn()
    {
        IsExecuting = false;
        IsTriggered = false;
        TurnPath = null;
        ArcStart = null;
        TurnAtObstacle = false;
        TurnAtObstacleIndex = -1;
        LastTurnWasLeft = IsTurnLeft;
        HeldDirectionLeft = null;
        PlannedNextPath = null;
        HasCompletedFirstTurn = true;
        YouTurnCounter = 0;
    }
}
