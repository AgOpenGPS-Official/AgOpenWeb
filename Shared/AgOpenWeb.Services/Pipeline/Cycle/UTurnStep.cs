// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The GPS cycle's U-turn: the turn planned, triggered and driven (<see cref="YouTurnStateMachine"/>
/// on the working state it keeps), the turn line it turns at, and the field a curve's opening
/// gives it. One of the cycle's steps (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 8); its working
/// state is its own, changed only through the methods here.
///
/// <para><b>Thread.</b> The GPS cycle's worker only.</para>
///
/// <para><b>Shared with the guidance, declared:</b> the state machine reads and writes the
/// guidance's working state (<see cref="GuidanceWorkingState"/>: the way along the track, the pass
/// and the nudge; a completed turn moves the pass). It gets it as an argument from the cycle, in
/// the cycle's order: <see cref="Tick"/> after the intents and before the guidance, and
/// <see cref="CompleteFromGuidance"/> from the guidance when it runs off the end of the turn.</para>
///
/// <para><b>Reads from the cycle:</b> the pivot, the track, the boundary, turn line and headland,
/// the skip settings, AutoSteer engaged, U-turns on, a manual turn asked for, contour and a
/// recorded path playing, as arguments; the coverage, for the skip-worked mode.</para>
/// </summary>
internal sealed class UTurnStep
{
    private readonly YouTurnStateMachine _youTurnStateMachine;
    private readonly IAudioService _audioService;
    private readonly ConfigurationStore _configStore;
    private readonly Func<double, double, bool> _isPointCovered;
    private readonly ILogger _logger;

    public UTurnStep(YouTurnStateMachine youTurnStateMachine, IAudioService audioService,
        ConfigurationStore configStore, Func<double, double, bool> isPointCovered, ILogger logger)
    {
        _youTurnStateMachine = youTurnStateMachine;
        _audioService = audioService;
        _configStore = configStore;
        _isPointCovered = isPointCovered;
        _logger = logger;
    }

    // POCO mirroring State.YouTurn. Single-writer contract — only mutated on the cycle worker
    // thread, here. ApplyGpsCycleResult mirrors it back to the observable State.YouTurn on the UI
    // thread, from the snapshot.
    private readonly YouTurnWorkingState _youTurn = new();
    private Models.Track.Track? _youTurnTrack; // the track _youTurn was planned on
    // Cycle-thread copy of the toggle from the previous cycle, to clear a turn
    // only on the on→off edge.
    private bool _youTurnWasEnabled;

    // Synthetic headland for the no-headland-but-has-boundary workflow.
    // Computed once per (boundary, UTurnRadius, UTurnDistanceFromBoundary)
    // tuple by insetting the outer boundary so an auto-uturn arc's apex
    // sits inside the outer boundary. Only consulted by ProcessCycle (worker
    // thread) so no lock is needed on the cache itself.
    private readonly PolygonOffsetService _syntheticHeadlandOffset = new();
    private List<Vec3>? _syntheticHeadlandLine;
    private Boundary? _syntheticHeadlandSourceBoundary;
    private double _syntheticHeadlandConfigKey;
    private double _syntheticHeadlandInsetUsed;

    /// <summary>The working state itself, for tests that set a turn up.</summary>
    public YouTurnWorkingState Working => _youTurn;

    public bool IsTriggered => _youTurn.IsTriggered;
    public bool IsExecuting => _youTurn.IsExecuting;
    public List<Vec3>? TurnPath => _youTurn.TurnPath;
    /// <summary>A turn planned, triggered or driven: something to clear.</summary>
    public bool HasTurn => _youTurn.IsTriggered || _youTurn.IsExecuting || _youTurn.TurnPath != null;

    /// <summary>Drop the turn, whatever it was doing.</summary>
    public void Clear() => YouTurnStateMachine.ClearState(_youTurn);

    /// <summary>Drop a planned turn so the next tick plans again.</summary>
    public void DiscardPlanned() => YouTurnStateMachine.DiscardPlannedTurn(_youTurn);

    /// <summary>The track of this cycle: a different one drops what was planned on the old.</summary>
    public void OnTrack(Models.Track.Track? track)
    {
        // Another track (or none: the field closed). The turn, the pass it lands on and the snake
        // order were the old track's; kept, they started a turn mid-field on the new one and
        // landed on the old pass number. AgOpenGPS resets the U-turn on a track change
        // (FormBuildTracks: yt.ResetYouTurn).
        if (!ReferenceEquals(track, _youTurnTrack))
        {
            _youTurnTrack = track;
            _youTurnStateMachine.ForgetTrack(_youTurn);
        }

    }

    /// <summary>The skip pattern changed (the host loop, taken at the cycle's start).</summary>
    public void ResetSnake()
    {
            // Rebuilt by the state machine on the next turn (HandleSnakeCreation).
            _youTurn.SnakeSequence = null;
            _youTurn.SnakeIndex = -1;
            // A planned turn aims at the pass the old skip chose: drop it so the next tick
            // plans for the new one (AgOpenGPS cboxpRowWidth: ResetCreatedYouTurn unless
            // triggered, #410). A turn already being driven is left alone.
            if (!_youTurn.IsExecuting) YouTurnStateMachine.DiscardPlannedTurn(_youTurn);
    }

    /// <summary>The U-turn switch and a direction asked for, into the working state.</summary>
    public void Mirror(bool youTurnEnabled, bool? nextUTurnDirectionOverride)
    {
        // YouTurn working state is cycle-owned — no cross-thread writers.
        // TriggerManual (manual U-turn) and ClearState (field close / track
        // deselect) run on the cycle thread via intents drained above.
        // Mirror the user's YouTurn-enabled toggle into the cycle-owned working
        // state so the YouTurnSnapshot.IsEnabled flag (and therefore
        // State.YouTurn.IsEnabled, used by the distance-to-trigger widget's
        // visibility predicate) tracks the toggle. Without this the snapshot
        // always reports IsEnabled=false because nothing else writes the
        // working-state flag, hiding the distance widget on every cycle.
        _youTurn.IsEnabled = youTurnEnabled;

        // Mirror the UI's direction override (one-shot) into the cycle's
        // working state. Only overwrite when the UI posted a new value —
        // otherwise an unconsumed override (set on a prior cycle, not yet
        // consumed because the tractor isn't in turn-creation range) survives.
        if (nextUTurnDirectionOverride.HasValue)
            _youTurn.NextUTurnDirectionLeftOverride = nextUTurnDirectionOverride;
    }

    /// <summary>
    /// This fix's U-turn: a manual turn asked for, the automatic tick, or the checks of a turn
    /// being driven, and their sounds. Before the guidance: completing a turn moves the pass the
    /// guidance steers to.
    /// </summary>
    public YouTurnEffects? Tick(bool? manualYouTurn, bool autoSteerEngaged, bool youTurnEnabled,
        Position pos, double driftedEasting, double driftedNorthing,
        Models.Track.Track? track, Boundary? boundary, List<Vec3>? turnLine, List<Vec3>? workHeadland,
        int uTurnSkipRows, bool isSkipWorkedMode, double headlandCalculatedWidth, double headlandDistanceConfig,
        bool isAlternateSkipMode, bool contourOn, PathPlanning.RecordedRouteGuide? recordedPath,
        GuidanceWorkingState guidance,
        out bool hasValidHeadlandLine, out bool hasTickableTrack, out YouTurnStateMachine.TickContext tickCtx)
    {
        // ── Phase C C4/C6: YouTurn state machine on the cycle worker ───
        // Two entry points, both running here on the background thread
        // against the cycle-owned _youTurn / _guidanceWorking POCOs:
        //   • Manual trigger via intent (C6 — drained above).
        //   • Auto tick, gated on autosteer + track + youturn-enabled +
        //     valid headland (matches pre-C4 MVM guards).
        // Snapshots built later in the cycle; the VM mirrors them back
        // via ApplyGpsCycleResult on the UI thread.
        YouTurnEffects? youTurnTickEffects = null;
        hasValidHeadlandLine = turnLine != null && turnLine.Count >= 3;
        // No U-turns in contour mode (AgOpenGPS DisableYouTurnButtons).
        hasTickableTrack = track != null && track.Points.Count >= 2 && !contourOn && recordedPath == null;

        var tickPosition = pos with { Easting = driftedEasting, Northing = driftedNorthing };
        // An open curve whose ends face each other turns at the opening between them as at a
        // headland: the U-turn gets the field with that opening as a virtual boundary (#694).
        var turnField = hasTickableTrack && hasValidHeadlandLine
            ? CurveEndTurnField(track!, boundary, turnLine, OpeningTurnRoom())
            : null;
        tickCtx = new YouTurnStateMachine.TickContext(
            tickPosition,
            track,
            turnField?.Boundary ?? boundary,
            turnField?.HeadlandLine ?? turnLine,
            uTurnSkipRows,
            isSkipWorkedMode,
            headlandCalculatedWidth,
            headlandDistanceConfig,
            isAlternateSkipMode,
            _isPointCovered,
            workHeadland);

        // Manual trigger — runs even when the auto gate would fail (e.g., YouTurn
        // toggle off). TriggerManual enforces its own preconditions (autosteer +
        // track + no turn already in progress) and sets a status message otherwise.
        if (manualYouTurn.HasValue && hasTickableTrack)
        {
            // IsHeadingSameWay is computed fresh by the state machine each tick;
            // HowManyPathsAway and NudgeOffset are already authoritative on
            // _guidanceWorking (D3 — SetActiveTrack writes directly).
            youTurnTickEffects = _youTurnStateMachine.TriggerManual(
                manualYouTurn.Value, autoSteerEngaged,
                in tickCtx, guidance, _youTurn);
        }

        if (autoSteerEngaged && hasTickableTrack
            && youTurnEnabled && hasValidHeadlandLine)
        {
            _youTurn.YouTurnCounter++;

            var autoEffects = _youTurnStateMachine.Tick(in tickCtx, guidance, _youTurn);
            // If a manual trigger already set effects this cycle, keep those —
            // state-machine branches make the auto tick a no-op mid-turn anyway.
            youTurnTickEffects ??= autoEffects;
        }
        else if (autoSteerEngaged && hasTickableTrack && _youTurn.IsExecuting)
        {
            // A manual turn is executing but the auto tick is gated off (no headland —
            // manual turns don't need one — or the YouTurn toggle off). Still run the
            // completion checks, or the turn never completes and the tractor is left
            // without steering (#163).
            youTurnTickEffects ??= _youTurnStateMachine.TickExecutingTurn(in tickCtx, guidance, _youTurn);
        }

        // U-turn sounds (#110); the audio service gates them on the U-turn sound setting.
        if (youTurnTickEffects?.TurnCreationFailedSound == true) _audioService.Play(SoundEffect.UTurnTooClose);
        if (youTurnTickEffects?.ApproachAlarmSound == true) _audioService.Play(SoundEffect.YouTurnApproach);

        return youTurnTickEffects;
    }

    /// <summary>
    /// The U-turn switch, this fix: switched off, a turn in progress is dropped. True when one was.
    /// </summary>
    public bool OnYouTurnSwitch(bool youTurnEnabled)
    {
        bool cleared = false;
        // Switching the YouTurn toggle OFF drops any turn in progress so a stale
        // arc doesn't linger on the map (the auto tick that would otherwise
        // advance or reset it is gated on youTurnEnabled). Only on the on→off
        // edge, like AgOpenGPS btnAutoYouTurn (ResetYouTurn when switched off):
        // manual turns stay available with the toggle off while autosteer is
        // engaged (AgOpenGPS draws/accepts them on isBtnAutoSteerOn ||
        // isYouTurnBtnOn), and a manual turn started then completes through
        // TickExecutingTurn above. Clearing every cycle while off used to kill
        // manual turns the moment they were created.
        if (!youTurnEnabled && _youTurnWasEnabled) _youTurnStateMachine.OnYouTurnSwitchedOff();
        if (!youTurnEnabled && _youTurnWasEnabled
            && (_youTurn.IsTriggered || _youTurn.IsExecuting || _youTurn.TurnPath != null))
        {
            YouTurnStateMachine.ClearState(_youTurn);
            cleared = true;
        }
        _youTurnWasEnabled = youTurnEnabled;

        return cleared;
    }

    /// <summary>The guidance ran off the end of the turn before the state machine's checks completed it.</summary>
    public YouTurnEffects CompleteFromGuidance(in YouTurnStateMachine.TickContext tickCtx, GuidanceWorkingState guidance) =>
        _youTurnStateMachine.CompleteFromGuidance(in tickCtx, guidance, _youTurn);

    /// <summary>The turn for the cycle's snapshot.</summary>
    public YouTurnSnapshot Snapshot(bool justCompleted) => BuildYouTurnSnapshot(_youTurn, justCompleted);

    private static YouTurnSnapshot BuildYouTurnSnapshot(YouTurnWorkingState src, bool justCompleted) => new()
    {
        IsEnabled = src.IsEnabled,
        IsTriggered = src.IsTriggered,
        IsExecuting = src.IsExecuting,
        TurnPath = src.TurnPath,
        PathIndex = src.PathIndex,
        ArcStart = src.ArcStart,
        IsTurnLeft = src.IsTurnLeft,
        LastTurnWasLeft = src.LastTurnWasLeft,
        DistanceToHeadland = src.DistanceToHeadland,
        DistanceToTrigger = src.DistanceToTrigger,
        NextTrack = src.NextTrack,
        LastCompletionPosition = src.LastCompletionPosition,
        HasCompletedFirstTurn = src.HasCompletedFirstTurn,
        YouTurnCounter = src.YouTurnCounter,
        WasHeadingSameWayAtTurnStart = src.WasHeadingSameWayAtTurnStart,
        NextTrackTurnOffset = src.NextTrackTurnOffset,
        ReturnPassTargetPath = src.ReturnPassTargetPath,
        SnakeSequence = src.SnakeSequence,
        SnakeIndex = src.SnakeIndex,
        AltSign = src.AltSign,
        AltBaseWidth = src.AltBaseWidth,
        AltWidth = src.AltWidth,
        AltTurnSkips = src.AltTurnSkips,
        AltPrevBig = src.AltPrevBig,
        CurrentZone = src.CurrentZone,
        NextUTurnDirectionLeftOverride = src.NextUTurnDirectionLeftOverride,
        JustCompleted = justCompleted,
    };

    // How deep a strip past A and B the U-turn has in a curve's opening (CurveEndOpening): room for
    // the turn itself, twice its radius, and the distance from boundary it keeps from that virtual
    // fence; not the headland's width, since the turn is made at the turn line whatever the headland
    // (with none, the strip was the 4 m U-turn distance and no turn fitted there).
    private double OpeningTurnRoom()
    {
        var g = _configStore.Guidance;
        return 2 * g.UTurnRadius + Math.Max(0, g.UTurnDistanceFromBoundary);
    }

    // The U-turn's field for a curve whose ends face each other, kept until the track, its
    // points, the boundary, the headland or its width change (CurveEndOpening, #694).
    private (Models.Track.Track? Track, List<Vec3>? Points, int Count, Boundary? Boundary, List<Vec3>? Headland, double Width) _turnFieldKey;
    private CurveEndOpening.TurnField? _turnField;

    private CurveEndOpening.TurnField? CurveEndTurnField(Models.Track.Track track, Boundary? boundary,
        List<Vec3>? headlandLine, double headlandWidth)
    {
        var key = (track, track.Points, track.Points.Count, boundary, headlandLine, headlandWidth);
        if (!ReferenceEquals(key.track, _turnFieldKey.Track) || !ReferenceEquals(key.Points, _turnFieldKey.Points)
            || key.Count != _turnFieldKey.Count || !ReferenceEquals(boundary, _turnFieldKey.Boundary)
            || !ReferenceEquals(headlandLine, _turnFieldKey.Headland) || headlandWidth != _turnFieldKey.Width)
        {
            _turnFieldKey = key;
            _turnField = track.Points.Count > 2 && !track.IsClosed && !track.IsPivot
                ? CurveEndOpening.Build(track.Points, boundary, headlandLine, headlandWidth)
                : null;
        }
        return _turnField;
    }

    /// <summary>The pass a planned or driven U-turn lands on, if one is planned.</summary>
    public int? TurnTargetPass() =>
        _youTurn.TurnPath is { Count: > 0 } ? _youTurn.ReturnPassTargetPath ?? _youTurn.PlannedNextPath : null;

    // Returns the synthetic headland line (inset of the outer boundary) plus
    // the inset distance, recomputing only when the boundary reference or the
    // relevant config values change. Called only from the cycle worker thread.
    public (List<Vec3>? Line, double Inset) GetOrComputeSyntheticHeadland(Boundary boundary)
    {
        var guidanceConfig = _configStore.Guidance;
        // AgOpen CTurn.BuildTurnLines: turn line = fence inset by uturnDistanceFromBoundary
        // ONLY (no turn-radius inset). A deeper inset would push a fence-hugging boundary
        // pass outside the "in-turn-bounds" polygon and it would never arm; the turn radius
        // is accounted for by MoveTurnInsideTurnLine fitting the arc, not by pre-insetting
        // this line.
        double inset = guidanceConfig.UTurnDistanceFromBoundary;
        double configKey = inset;

        if (ReferenceEquals(boundary, _syntheticHeadlandSourceBoundary)
            && Math.Abs(configKey - _syntheticHeadlandConfigKey) < 1e-9)
        {
            return (_syntheticHeadlandLine, _syntheticHeadlandInsetUsed);
        }

        var outerPoints = boundary.OuterBoundary!.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();
        var inwardOffset = _syntheticHeadlandOffset.CreateInwardOffset(outerPoints, inset);
        _syntheticHeadlandLine = (inwardOffset != null && inwardOffset.Count >= 3)
            ? _syntheticHeadlandOffset.CalculatePointHeadings(inwardOffset)
            : null;
        _syntheticHeadlandSourceBoundary = boundary;
        _syntheticHeadlandConfigKey = configKey;
        _syntheticHeadlandInsetUsed = inset;
        _logger.LogDebug("[YouTurn] Synthesized turn line (no user headland): inset={I:F1}m (uturnDistanceFromBoundary), pts={P}",
            inset, _syntheticHeadlandLine?.Count ?? 0);
        return (_syntheticHeadlandLine, _syntheticHeadlandInsetUsed);
    }

}
