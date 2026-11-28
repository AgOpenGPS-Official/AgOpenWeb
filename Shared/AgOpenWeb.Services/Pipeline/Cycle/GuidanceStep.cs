// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Contour;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The GPS cycle's guidance: the line shown (the pass, the tool's band, the guidelines), the
/// steering along the track, a U-turn, the contour or a recorded path, and in free drive the
/// nearest pass and where the steering would aim. One of the cycle's steps (Plans/
/// MAINVIEWMODEL_REFACTOR_PLAN.md item 8). Its state is its own: the guidance working state (the
/// pass, the nudge, the way along the track, this fix's results), the steering's memory between
/// fixes (nearest point, integral, last cross-track error), the simulator's steer angle and the
/// caches of the lines shown.
///
/// <para><b>Thread.</b> The GPS cycle's worker. <see cref="SimulatorSteerAngle"/> is read by the
/// host loop's simulator (a single double, Volatile). The host loop's track choice and AutoSteer
/// off reach it through the cycle's start (<see cref="TrackChosen"/>, <see cref="ForgetSteering"/>).</para>
///
/// <para><b>Shared with the U-turn, declared:</b> the U-turn's state machine reads and writes
/// <see cref="Working"/> (the way along the track, the pass, the nudge); the cycle hands it over
/// in its order, the U-turn's tick before <see cref="Run"/>. <see cref="Run"/> asks the U-turn
/// (an argument) for the pass a turn lands on and completes a turn it ran off the end of.</para>
///
/// <para><b>Reads from the cycle:</b> the fix and pivot, the track and pass, the contour's steer,
/// a recorded path, AutoSteer engaged, the U-turn's state, reversing, the roll and the cycle count,
/// as arguments. Tells the AutoSteer service the steer angle and cross-track error.</para>
/// </summary>
internal sealed class GuidanceStep
{
    private readonly ConfigurationStore _configStore;
    private readonly ITrackGuidanceService _trackGuidanceService;
    private readonly YouTurnGuidanceService _youTurnGuidanceService;
    private readonly IAutoSteerService _autoSteerService;
    private readonly ILogger _logger;

    public GuidanceStep(ConfigurationStore configStore, ITrackGuidanceService trackGuidanceService,
        YouTurnGuidanceService youTurnGuidanceService, IAutoSteerService autoSteerService, ILogger logger)
    {
        _configStore = configStore;
        _trackGuidanceService = trackGuidanceService;
        _youTurnGuidanceService = youTurnGuidanceService;
        _autoSteerService = autoSteerService;
        _logger = logger;
    }

    // POCO mirroring the subset of State.Guidance the cycle works out. Single-writer contract:
    // only mutated on the cycle worker thread, here and by the U-turn's state machine it is handed
    // to. ApplyGpsCycleResult mirrors it back to the observable State.Guidance on the UI thread.
    private readonly GuidanceWorkingState _guidanceWorking = new();
    private double _lastCrossTrackError; // last cycle's XTE, for the look-ahead (#110)
    // ── Pipeline-owned guidance state (only touched on background thread) ─
    private Models.Track.TrackGuidanceState? _trackGuidanceState;
    // Previous along-track travel direction; a change forces a global nearest
    // re-acquire so a stranded local index can recover (#422).
    private bool _lastHeadingSameWay = true;
    private double _simulatorSteerAngle;

    // Cached offset-display-track. UpdateDisplayTrack used to allocate a fresh
    // Models.Track.Track every GPS cycle while shifted off pass 0; the
    // ReferenceEquals gate in MainViewModel.ApplyResults then fired on every
    // cycle, defeating the VBO-rebuild gate downstream and re-uploading
    // boundary/track buffers at GPS rate. PERF-05 #403 sub-finding #5.
    // Cache key: (source track ref, its points list and count, distAway, turn radius). All must
    // match to reuse: a track whose points were replaced (extend A/B, Ref Nudge) keeps its object.
    private Models.Track.Track? _cachedOffsetTrack;
    private Models.Track.Track? _cachedOffsetSourceTrack;
    private List<Vec3>? _cachedOffsetSourcePoints;
    private int _cachedOffsetSourceCount;
    private double _cachedOffsetDistAway;
    private double _cachedOffsetTurnRadius;
    private readonly OffsetPassCache _offsetPasses = new();

    // ── Curve limit warning throttling ──────────────────────────────────
    private DateTime _lastCurveLimitWarning = DateTime.MinValue;
    private int _lastWarnedPathsAway = int.MinValue;

    /// <summary>The working state, handed to the U-turn's state machine (declared above) and read by tests.</summary>
    public GuidanceWorkingState Working => _guidanceWorking;
    /// <summary>The pass this cycle works on (the U-turn may have moved it).</summary>
    public int PassNumber => _guidanceWorking.HowManyPathsAway;
    public double NudgeOffset => _guidanceWorking.NudgeOffset;
    /// <summary>The steer angle the simulator follows, read by the host loop.</summary>
    public double SimulatorSteerAngle => Volatile.Read(ref _simulatorSteerAngle);
    /// <summary>The steering's memory, for tests: null means the next engage searches the line afresh.</summary>
    public Models.Track.TrackGuidanceState? SteeringMemory => _trackGuidanceState;
    /// <summary>The last cross-track error the look-ahead uses, for tests.</summary>
    public double LastCrossTrackError { get => _lastCrossTrackError; set => _lastCrossTrackError = value; }

    /// <summary>A track chosen on the host loop, taken at the cycle's start: its pass and nudge, and the steering starts afresh.</summary>
    public void TrackChosen(int passNumber, double nudgeOffset)
    {
        _guidanceWorking.HowManyPathsAway = passNumber;
        _guidanceWorking.NudgeOffset = nudgeOffset;
        // Reset guidance state when track changes so we do a global search
        _trackGuidanceState = null;
        _lastCrossTrackError = 0; // a new line has no previous XTE for the look-ahead (#110)
    }

    /// <summary>AutoSteer switched off on the host loop, taken at the cycle's start: the next engage searches the line afresh.</summary>
    public void ForgetSteering() => _trackGuidanceState = null;

    /// <summary>The track this cycle's pass and nudge belong to (#107), and this cycle's contour and reversing.</summary>
    public void Stamp(Models.Track.Track? track) => _guidanceWorking.ActiveTrack = track;
    public void SetContourMode(bool contourOn) => _guidanceWorking.IsContourMode = contourOn;
    public void SetReverse(bool isReverse) => _guidanceWorking.IsReverse = isReverse;

    /// <summary>
    /// The operator's lateral moves and nudges drained this cycle: they move the pass or the line,
    /// and drop a planned turn (the U-turn, an argument) built for the old one.
    /// </summary>
    public void ApplyIntents(PipelineIntentBatch intents, UTurnStep uTurn)
    {
        // #50. A snap mid-turn is IGNORED entirely: applying it would shift the
        // displayed track (HowManyPathsAway) while the tractor is committed to the
        // executing arc's already-computed target — so it lands on the OLD pass and
        // then laterally seeks across to the just-snapped one (operator-confirmed as
        // the confusing behavior). The arc can't be safely re-planned mid-swing, so
        // we drop the snap outright; the client also hints "finish the turn first".
        // The armed-but-not-executing case still re-plans cleanly below (#408).
        if (intents.GuidanceSnap.HasValue && !uTurn.IsExecuting)
        {
            // Phase D D4. IsHeadingSameWay is the cycle's view of whether the
            // tractor is aligned with the track direction. Snap "left" from
            // the driver's perspective means "decrement pass number when
            // aligned, increment when reversed" — matches AgOpenGPS.
            int delta = intents.GuidanceSnap.Value
                ? (_guidanceWorking.IsHeadingSameWay ? -1 : 1)
                : (_guidanceWorking.IsHeadingSameWay ? 1 : -1);
            int from = _guidanceWorking.HowManyPathsAway;
            _guidanceWorking.HowManyPathsAway += delta;
            // Logged: the line moves a whole pass with no turn, and a tap on the lateral button is
            // easy to miss. A bug report of the line on the next pass with the tractor standing still
            // had nothing in its log to show what moved it.
            _logger.LogInformation("[Guidance] Lateral move {Side}: pass {From} -> {To}",
                intents.GuidanceSnap.Value ? "left" : "right", PassName(from), PassName(from + delta));
            // The nudge stays: a lateral move is a pass, not a reset (AgOpenGPS BuildManualYouLateral).
            _trackGuidanceState = null;

            // #408. If a u-turn is plotted but not yet executing, drop it
            // so the state machine re-arms for the new pass direction on
            // the next tick. Without this the tractor enters the already-
            // armed arc and lands on the *old* NextTrack (the original
            // exit pass) instead of replanning for the just-snapped pass.
            // Mirrors the direction-override re-arm in YouTurnStateMachine.
            uTurn.DiscardPlanned();
        }
        else if (intents.GuidanceSnap.HasValue)
            _logger.LogInformation("[Guidance] Lateral move {Side} ignored: a U-turn is being driven",
                intents.GuidanceSnap.Value ? "left" : "right");
        // Phase D D5. Nudge accumulates (multiple clicks between drains sum).
        // Heading-same-way flips the sign so "left" always means left from the
        // driver's seat regardless of track direction. Reset wins if both
        // arrive in the same tick. A planned turn was built for the old line, so it is
        // dropped and re-planned (AgOpenGPS RebuildAfterNudge, #1173); like a snap (#50),
        // a turn already being driven is left alone.
        if (intents.GuidanceNudgeMeters != 0)
        {
            double adjusted = _guidanceWorking.IsHeadingSameWay
                ? intents.GuidanceNudgeMeters
                : -intents.GuidanceNudgeMeters;
            _guidanceWorking.NudgeOffset += adjusted;
            _trackGuidanceState = null;
            if (!uTurn.IsExecuting) uTurn.DiscardPlanned();
        }
        if (intents.GuidanceResetNudge)
        {
            _guidanceWorking.NudgeOffset = 0;
            _trackGuidanceState = null;
            if (!uTurn.IsExecuting) uTurn.DiscardPlanned();
        }
    }

    /// <summary>
    /// This fix's guidance, in its three parts and their order: the line shown, the steering (the
    /// contour, a recorded path, a U-turn or the track), and in free drive the nearest pass. Before
    /// the headland and the PGNs; after the U-turn's tick and the AutoSteer rules.
    /// </summary>
    public void Run(Position pos, Models.Track.Track? track, bool hasTrack, bool noPassOffset,
        double driftedEasting, double driftedNorthing, double headingRad, bool isReverse, double roll, int cycleCounter,
        ref int passNumber, double nudgeOffset,
        bool contourOn, ContourSteer? contourSteer, Models.Track.Track? contourDisplay, PathPlanning.RecordedRouteGuide? recordedPath,
        ref bool autoSteerEngaged, ref bool autoSteerDisengaged, ref string? disengageReason, bool youTurnEnabled,
        bool isYouTurnTriggered, bool isInYouTurn, List<Vec3>? youTurnPath,
        UTurnStep uTurn, in YouTurnStateMachine.TickContext tickCtx, ref YouTurnEffects? youTurnTickEffects,
        out double steerAngle, out double crossTrackError, out double goalE, out double goalN,
        out bool hasGuidance, out bool hasGoal, out bool youTurnCompleted, out string? statusMessage,
        out Models.Track.Track? displayTrack, out Models.Track.Track? baseTrack,
        out IReadOnlyList<IReadOnlyList<Vec3>>? guideLines, out IReadOnlyList<Vec3>? toolPass, out int? shownLandingPass,
        out int diagPathAnchorA, out int diagPathAnchorB, out int diagTurnPathCount, out bool diagAntiTangentGuardFired)
    {
        // ── (6) Guidance calculation ────────────────────────────────────
        steerAngle = 0;
        crossTrackError = 0;
        goalE = 0; goalN = 0;
        hasGuidance = false;
        hasGoal = false;
        youTurnCompleted = false;
        statusMessage = null;
        displayTrack = null;
        baseTrack = null;
        guideLines = null;
        (double Width, double Shift, bool Extend, int Count)? guideArgs = null;
        toolPass = null;
        (double Width, bool Extend)? toolPassArgs = null;
        shownLandingPass = null; // the pass a U-turn being driven lands on, while it is the one shown

        // Always compute the display track when we have a track (for map visualization)
        if (hasTrack)
        {
            var config2 = _configStore;
            // Which way along the track we drive decides which side of the pass the tractor
            // steers with a tool offset (#446), so it is known before any line is placed. Held
            // through a U-turn: the turn was planned for the old direction, and the next cycle
            // after it completes takes the new one.
            if (!isInYouTurn)
                _guidanceWorking.IsHeadingSameWay = IsHeadingSameWayOn(track!, driftedEasting, driftedNorthing, headingRad);
            double toolShift = Models.Track.PassGeometry.ToolShift(config2.Tool.Offset, _guidanceWorking.IsHeadingSameWay);
            double widthMinusOverlap = config2.ActualToolWidth - config2.Tool.Overlap;
            // While a U-turn is driven, the pass shown is the one it lands on, as AgOpenGPS, which
            // moves to the next pass when the turn starts (CYouTurn.YouTurnTrigger): the line, the
            // tool's band, the guidelines and the pass label. The pass worked (HowManyPathsAway)
            // moves on when the turn completes, so a turn backed out of keeps its pass (#588).
            // The landing pass is driven the other way: its tool shift has the other sign (#446).
            int shownPass = passNumber;
            double shownShift = toolShift;
            if (isInYouTurn && uTurn.TurnTargetPass() is { } landing)
            {
                shownPass = landing;
                shownShift = -toolShift;
                shownLandingPass = landing;
            }
            double distAway = Models.Track.PassGeometry.Offset(track, shownPass, widthMinusOverlap, nudgeOffset, shownShift);

            // Curve tracks: extend the displayed (magenta) line straight to the U-turn so
            // there's no visible gap between the guidance line and the turn (the line the
            // tractor follows, CalculateTrackGuidance, is extended the same way). No-op for
            // closed loops.
            bool extendDisp = track!.Points.Count > 2 && !track.IsClosed;

            if (Math.Abs(distAway) < 0.01)
            {
                if (extendDisp)
                {
                    // Pass 0: extended curve IS the reference line — leave baseTrack null so
                    // it isn't drawn twice at the same position.
                    displayTrack = new Models.Track.Track
                    {
                        Name = $"{track.Name} (path {shownPass})",
                        Points = CurveProcessing.ExtendCurveEnds(track.Points),
                        Type = track.Type, IsVisible = true, IsActive = true, IsClosed = track.IsClosed
                    };
                }
                else
                {
                    displayTrack = track;
                }
            }
            else
            {
                // A pass the offset removed altogether has no points: nothing is drawn (#694).
                var pass = _offsetPasses.Get(track, distAway, _configStore.Guidance.UTurnRadius);
                var offsetPoints = extendDisp ? CurveProcessing.ExtendPass(pass) : new List<Vec3>(pass.Points);
                displayTrack = new Models.Track.Track
                {
                    Name = $"{track.Name} (path {shownPass})",
                    Points = offsetPoints,
                    Type = track.Type,
                    IsVisible = true,
                    IsActive = true
                };
                baseTrack = track;
            }

            toolPassArgs = (widthMinusOverlap, extendDisp);
            toolPass = ToolPass(track, shownPass, widthMinusOverlap, nudgeOffset, extendDisp);

            if (config2.Display.ExtraGuidelines)
            {
                guideArgs = (widthMinusOverlap, toolShift, extendDisp, config2.Display.ExtraGuidelinesCount);
                guideLines = GuideLines(track, shownPass, widthMinusOverlap, nudgeOffset, toolShift, extendDisp,
                    config2.Display.ExtraGuidelinesCount, uTurn.TurnTargetPass());
            }
        }

        diagPathAnchorA = 0;
        diagPathAnchorB = 0;
        diagTurnPathCount = 0;
        diagAntiTangentGuardFired = false;

        if (contourOn)
        {
            // Contour replaces the track: its line is the one shown and followed.
            displayTrack = contourDisplay;
            baseTrack = null;
            if (contourSteer is { } cs)
            {
                crossTrackError = cs.CrossTrackError;
                goalE = cs.GoalPoint.Easting;
                goalN = cs.GoalPoint.Northing;
                hasGoal = !_configStore.Guidance.IsStanley;
                if (autoSteerEngaged)
                {
                    steerAngle = cs.SteerAngle;
                    hasGuidance = true;
                    Volatile.Write(ref _simulatorSteerAngle, steerAngle);
                    _autoSteerService.UpdateGuidanceResults(steerAngle, crossTrackError);
                }
            }
        }
        else if (recordedPath != null)
        {
            // A recorded route playing replaces the track, as contour does: AutoSteer steers
            // along it (AgOpenGPS steers the module along it; here the operator engages AutoSteer).
            var cfgRp = _configStore.Vehicle;
            if (recordedPath.Follow(driftedEasting, driftedNorthing, headingRad, cfgRp.Wheelbase, cfgRp.MaxSteerAngle) is { } rp)
            {
                crossTrackError = rp.CrossTrackError;
                goalE = rp.GoalE;
                goalN = rp.GoalN;
                hasGoal = !_configStore.Guidance.IsStanley;
                if (autoSteerEngaged)
                {
                    steerAngle = rp.SteerAngleDegrees;
                    hasGuidance = true;
                    Volatile.Write(ref _simulatorSteerAngle, steerAngle);
                    _autoSteerService.UpdateGuidanceResults(steerAngle, crossTrackError);
                }
            }
            else if (autoSteerEngaged)
            {
                // The end of the route: nothing left ahead to steer to. AutoSteer off with the
                // reason, as past the end of an open curve (AgOpenGPS stops playback there).
                autoSteerEngaged = false;
                autoSteerDisengaged = true;
                disengageReason = GpsPipelineService.RecordedPathEndReason;
                _autoSteerService.UpdateGuidanceResults(0, NoLineDistance);
            }
        }
        else if (autoSteerEngaged && hasTrack)
        {
            if (isYouTurnTriggered && youTurnPath != null && youTurnPath.Count > 0)
            {
                // YouTurn guidance — steer along turn path
                // Use drifted local coordinates (not pos which has raw E=0,N=0)
                var ytPos = pos with { Easting = driftedEasting, Northing = driftedNorthing };
                var ytResult = CalculateYouTurnGuidance(ytPos, youTurnPath, isReverse);
                if (ytResult != null)
                {
                    steerAngle = ytResult.Value.steerAngle;
                    crossTrackError = ytResult.Value.xte;
                    goalE = ytResult.Value.goalE;
                    goalN = ytResult.Value.goalN;
                    youTurnCompleted = ytResult.Value.turnComplete;
                    hasGuidance = !youTurnCompleted;
                    if (youTurnCompleted && uTurn.IsExecuting)
                    {
                        // Guidance ran off the end of the path before the state machine's
                        // own checks fired. Complete the turn here (AgOpenGPS does the same
                        // from its U-turn guidance) so the next cycle follows the new pass
                        // instead of sitting in a turn with no steering (#163).
                        var completion = uTurn.CompleteFromGuidance(in tickCtx, _guidanceWorking);
                        if (youTurnTickEffects == null) youTurnTickEffects = completion;
                        else youTurnTickEffects.TurnCompleted |= completion.TurnCompleted;
                    }
                    diagPathAnchorA = ytResult.Value.pointA;
                    diagPathAnchorB = ytResult.Value.pointB;
                    diagTurnPathCount = ytResult.Value.pathPointCount;
                    diagAntiTangentGuardFired = ytResult.Value.antiTangentGuardFired;
                }
            }
            else
            {
                // Normal track guidance
                var guidanceResult = CalculateTrackGuidance(
                    pos, track!, passNumber, nudgeOffset, driftedEasting, driftedNorthing, headingRad,
                    isYouTurnTriggered, isReverse, roll, cycleCounter);
                if (guidanceResult is { hasLine: false } noLine)
                {
                    // No pass where the tractor is (#694): steer straight and tell the module
                    // there is no line (PGN 254's no-line distance), as AgOpenGPS does.
                    statusMessage = noLine.statusMessage;
                    _autoSteerService.UpdateGuidanceResults(0, NoLineDistance);
                }
                else if (guidanceResult is { atEnd: true } && !(youTurnEnabled && youTurnPath is { Count: > 0 }))
                {
                    // Past the end of an open curve, and no U-turn there to take over: AutoSteer
                    // off, as AgOpenGPS ("Guidance Stopped" / "Past end of curve"), rather than
                    // steering on into what the line leaves out (#694).
                    autoSteerEngaged = false;
                    autoSteerDisengaged = true;
                    disengageReason = GpsPipelineService.PastEndOfCurveReason;
                    _autoSteerService.UpdateGuidanceResults(0, NoLineDistance);
                }
                else if (guidanceResult != null)
                {
                    steerAngle = guidanceResult.Value.steerAngle;
                    crossTrackError = guidanceResult.Value.xte;
                    goalE = guidanceResult.Value.goalE;
                    goalN = guidanceResult.Value.goalN;
                    hasGuidance = true;
                    statusMessage = guidanceResult.Value.statusMessage;
                }
            }

            if (hasGuidance)
            {
                Volatile.Write(ref _simulatorSteerAngle, steerAngle);
                _autoSteerService.UpdateGuidanceResults(steerAngle, crossTrackError);
                _lastCrossTrackError = crossTrackError;
            }
            // Stanley has no look-ahead target, so no goal marker (#99).
            hasGoal = hasGuidance && !_configStore.Guidance.IsStanley;
        }
        if (!contourOn && !autoSteerEngaged && hasTrack && !noPassOffset)
        {
            // Display-only: auto-detect nearest pass and update visualization.
            // Phase D D3: write the detected pass directly into the cycle's
            // working state. Previously this was emitted as
            // GpsCycleResult.NearestPassNumber and the UI thread wrote it back
            // through SyncGuidanceStateToPipeline — two writers fighting each
            // other and causing a per-cycle oscillation (see commit 57920e0).
            // One writer now — the cycle.
            //
            // #261: match AgOpen — project a *lookahead* point (pivot + heading * lookDist)
            // onto the reference line instead of the raw pivot. Produces the "track jumps
            // ahead of the tractor" behavior operators expect in free-drive.
            double lookDist = Math.Max(
                _configStore.ActualToolWidth * 0.5,
                pos.Speed * _configStore.AutoSteer.NextGuidanceTime); // AgOpenGPS guidanceLookAheadTime (#110)
            double hRad = pos.Heading * Math.PI / 180.0;
            double lookE = driftedEasting + Math.Sin(hRad) * lookDist;
            double lookN = driftedNorthing + Math.Cos(hRad) * lookDist;
            var (nearestPass, nearestDisplayTrack, displayXte, onReference) = UpdateDisplayTrack(
                pos, track!, passNumber, nudgeOffset,
                driftedEasting, driftedNorthing,
                lookE, lookN);
            // The light bar shows the distance to the line in free-drive too, as AgOpenGPS
            // does (it computes the line's distance whether or not AutoSteer is on).
            crossTrackError = displayXte;
            if (nearestDisplayTrack != null)
            {
                displayTrack = nearestDisplayTrack;
                // Explicitly clear baseTrack when returning to pass 0 — otherwise a
                // stale baseTrack set by the earlier block (when incoming passNumber
                // was non-zero) stays pointing at the reference track, and the UI
                // renders baseTrack + displayTrack at the same position (overlap).
                baseTrack = onReference ? null : track;
                // The guidelines go either side of the pass shown.
                if (guideArgs is { } ga)
                    guideLines = GuideLines(track!, nearestPass, ga.Width, nudgeOffset, ga.Shift, ga.Extend, ga.Count, uTurn.TurnTargetPass());
                if (toolPassArgs is { } ta)
                    toolPass = ToolPass(track!, nearestPass, ta.Width, nudgeOffset, ta.Extend);

                // #95: show where Pure Pursuit would aim if engaged now, so the operator
                // can see the target (field vs. ditch) before engaging. The steer angle it
                // would command feeds the steer bar, as AgOpenGPS computes it whether or not
                // AutoSteer is on (#479); nothing steers from it here.
                var display = CalculateDisplayGuidance(
                    pos, nearestDisplayTrack, driftedEasting, driftedNorthing, headingRad);
                if (display is { } dg)
                {
                    steerAngle = dg.SteerAngle;
                    if (dg.Goal is { } goal)
                    {
                        goalE = goal.Easting;
                        goalN = goal.Northing;
                        hasGoal = true;
                    }
                }
            }
            _guidanceWorking.HowManyPathsAway = nearestPass;
            passNumber = nearestPass;
        }

        // Phase D D2: write cycle-local guidance outputs into _guidanceWorking
        // so BuildGuidanceSnapshot can read them uniformly. D3 will make the
        // working state the authoritative source (today the flat GpsCycleResult
        // fields are still populated from locals and consumed by ApplyResults).
        _guidanceWorking.SteerAngle = steerAngle;
        _guidanceWorking.CrossTrackError = crossTrackError;
        _guidanceWorking.GoalPoint = new Vec2(goalE, goalN);
        _guidanceWorking.HasGoalPoint = hasGoal;

    }

    /// <summary>The guidance for the cycle's snapshot: the working state with the pass label, and this fix's lines.</summary>
    public GuidanceSnapshot Snapshot(int? shownLandingPass, Models.Track.Track? displayTrack, Models.Track.Track? baseTrack,
        bool hasGuidance, int diagPathAnchorA, int diagPathAnchorB, int diagTurnPathCount, bool diagAntiTangentGuardFired,
        IReadOnlyList<IReadOnlyList<Vec3>>? guideLines, IReadOnlyList<Vec3>? toolPass) =>
        BuildGuidanceSnapshot(
            WithLineLabel(_guidanceWorking, shownLandingPass), displayTrack, baseTrack, hasGuidance,
            diagPathAnchorA, diagPathAnchorB, diagTurnPathCount, diagAntiTangentGuardFired) with { GuideLines = guideLines, ToolPass = toolPass };

    // The pass label, as AgOpenGPS's DrawTrackInfo: an arrow for the way you drive (up = A→B, its
    // font's '{'; down = B→A, its '}') and the pass counted from the reference line, which is the
    // edge between passes: pass 0 is 1R, pass −1 is 1L. The map adds the nudge in its own units.
    // The label names the pass shown: during a U-turn the pass it lands on, driven the other way.
    private static GuidanceWorkingState WithLineLabel(GuidanceWorkingState g, int? landingPass = null)
    {
        int pass = landingPass ?? g.HowManyPathsAway;
        bool sameWay = landingPass.HasValue ? !g.IsHeadingSameWay : g.IsHeadingSameWay;
        g.CurrentLineLabel = (sameWay ? "↑" : "↓") + PassName(pass);
        return g;
    }

    /// <summary>The pass as its label names it: pass 0 is 1R, pass −1 is 1L.</summary>
    private static string PassName(int pass) => pass >= 0 ? $"{pass + 1}R" : $"{-pass}L";

    private static GuidanceSnapshot BuildGuidanceSnapshot(
        GuidanceWorkingState src,
        Models.Track.Track? displayTrack,
        Models.Track.Track? baseTrack,
        bool hasGuidance,
        int pathAnchorA,
        int pathAnchorB,
        int turnPathPointCount,
        bool antiTangentGuardFired) => new()
    {
        // GuidanceState-mirrored fields (all populated from the cycle's
        // working state — Phase D D2 writes them there at end-of-branch).
        ActiveTrack = src.ActiveTrack,
        IsGuidanceActive = src.IsGuidanceActive,
        CrossTrackError = src.CrossTrackError,
        HeadingError = src.HeadingError,
        SteerAngle = src.SteerAngle,
        SteerAngleRaw = src.SteerAngleRaw,
        DistanceOffRaw = src.DistanceOffRaw,
        PpIntegral = src.PpIntegral,
        PpPivotDistanceError = src.PpPivotDistanceError,
        PpPivotDistanceErrorLast = src.PpPivotDistanceErrorLast,
        PpCounter = src.PpCounter,
        GoalPoint = src.GoalPoint,
        HasGoalPoint = src.HasGoalPoint,
        RadiusPoint = src.RadiusPoint,
        PurePursuitRadius = src.PurePursuitRadius,
        IsHeadingSameWay = src.IsHeadingSameWay,
        IsReverse = src.IsReverse,
        HowManyPathsAway = src.HowManyPathsAway,
        NudgeOffset = src.NudgeOffset,
        CurrentLineLabel = src.CurrentLineLabel,
        IsContourMode = src.IsContourMode,

        // Cycle-only fields (not mirrored on GuidanceState) — passed in
        // from ProcessCycle's local computations.
        HasGuidance = hasGuidance,
        DisplayTrack = displayTrack,
        BaseTrack = baseTrack,
        PathAnchorA = pathAnchorA,
        PathAnchorB = pathAnchorB,
        TurnPathPointCount = turnPathPointCount,
        AntiTangentGuardFired = antiTangentGuardFired,
    };

    // ══════════════════════════════════════════════════════════════════════
    // Guidance helpers (run on background thread, no Avalonia types)
    // ══════════════════════════════════════════════════════════════════════

    // The cross-track error that tells the steer module there is no line (TrackGuidanceService's,
    // AgOpenGPS's 32000; PgnBuilder sends it as such).
    private const double NoLineDistance = 32000;


    private (double steerAngle, double xte, double goalE, double goalN, string? statusMessage, bool hasLine, bool atEnd)?
        CalculateTrackGuidance(
            Position currentPosition, Models.Track.Track track, int passNumber, double nudgeOffset,
            double driftedEasting, double driftedNorthing, double headingRad,
            bool isYouTurnTriggered, bool isReverse, double roll, int cycleCounter)
    {
        var config = _configStore;

        double speedKmh = currentPosition.Speed * 3.6;
        double lookAhead = GoalLookAhead(speedKmh);

        // Steer axle position
        double steerE = driftedEasting + Math.Sin(headingRad) * config.Vehicle.Wheelbase;
        double steerN = driftedNorthing + Math.Cos(headingRad) * config.Vehicle.Wheelbase;

        // Calculate parallel offset: the line that puts the tool on the pass (#446)
        bool isHeadingSameWay = _guidanceWorking.IsHeadingSameWay;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        double distAway = Models.Track.PassGeometry.Offset(track, passNumber, widthMinusOverlap, nudgeOffset,
            Models.Track.PassGeometry.ToolShift(config.Tool.Offset, isHeadingSameWay));

        if (cycleCounter % 50 == 0)
        {
            Console.WriteLine($"[Guidance] pass={passNumber} distAway={distAway:F1} pivot=({driftedEasting:F1},{driftedNorthing:F1}) h={headingRad*180/Math.PI:F1}");
        }

        Models.Track.Track currentTrack;
        string? statusMessage = null;

        // Curve tracks are extended along their (smoothed) end tangents so the STEERING
        // line is the SAME extended curve the U-turn is built from. Otherwise the offset
        // guidance line stops at the recorded curve's end and the tractor approaches at the
        // curve's local heading, while the U-turn entry leg sits on the straight tangent
        // extension — a heading step at the algorithm handoff (the entry "hunt"). Extending
        // the steering line lets the tractor commit to the extension direction during the
        // (smooth) approach instead. No-op for closed loops.
        bool extendCurve = track.Points.Count > 2 && !track.IsClosed;

        if (Math.Abs(distAway) < 0.01)
        {
            currentTrack = extendCurve
                ? new Models.Track.Track
                {
                    Name = $"{track.Name} (path {passNumber})",
                    Points = CurveProcessing.ExtendCurveEnds(track.Points),
                    Type = track.Type, IsVisible = true, IsActive = true, IsClosed = track.IsClosed
                }
                : track;
        }
        else
        {
            var cached = _offsetPasses.Get(track, distAway, _configStore.Guidance.UTurnRadius);
            double percentRemoved = cached.PercentRemoved;
            var offsetPoints = extendCurve ? CurveProcessing.ExtendPass(cached) : new List<Vec3>(cached.Points);

            // Warn on tight curves
            if (percentRemoved > 10 && passNumber != _lastWarnedPathsAway)
            {
                if ((DateTime.Now - _lastCurveLimitWarning).TotalSeconds > 10)
                {
                    _lastCurveLimitWarning = DateTime.Now;
                    _lastWarnedPathsAway = passNumber;

                    double minRadius = CurveProcessing.CalculateMinRadiusOfCurvature(track.Points);
                    int maxPasses = CurveProcessing.CalculateMaxInwardPasses(track.Points, widthMinusOverlap);
                    statusMessage = $"Curve too tight! {percentRemoved:F0}% removed. Max ~{maxPasses} inward passes (min radius: {minRadius:F1}m)";
                }
            }

            currentTrack = new Models.Track.Track
            {
                Name = $"{track.Name} (path {passNumber})",
                Points = offsetPoints,
                Type = track.Type,
                IsVisible = true,
                IsActive = true,
                // Preserve closed-loop flag so guidance wraps at the seam rather
                // than treating an offset boundary loop as an open polyline (#422).
                IsClosed = track.IsClosed
            };

            // No pass here: the offset removed all of it (#694). Nothing to steer to, as in
            // AgOpenGPS, whose empty pass sends the module its no-line distance (32000).
            if (offsetPoints.Count < 2)
                return (0, 0, 0, 0, statusMessage, false, false);
        }

        if (cycleCounter % 50 == 0 && currentTrack.Points.Count >= 2)
        {
            var p0 = currentTrack.Points[0];
            var pN = currentTrack.Points[^1];
            Console.WriteLine($"[Guidance] track: ({p0.Easting:F1},{p0.Northing:F1})->({pN.Easting:F1},{pN.Northing:F1})");
        }


        // Build guidance input
        var input = new Models.Track.TrackGuidanceInput
        {
            Track = currentTrack,
            PivotPosition = new Vec3(driftedEasting, driftedNorthing, headingRad),
            SteerPosition = new Vec3(steerE, steerN, headingRad),
            UseStanley = config.Guidance.IsStanley,
            StanleyHeadingErrorGain = config.Guidance.StanleyHeadingErrorGain,
            StanleyDistanceErrorGain = config.Guidance.StanleyDistanceErrorGain,
            StanleyIntegralGain = config.Guidance.StanleyIntegralGainAB,
            IsHeadingSameWay = isHeadingSameWay,
            Wheelbase = config.Vehicle.Wheelbase,
            MaxSteerAngle = config.Vehicle.MaxSteerAngle,
            GoalPointDistance = lookAhead,
            // Side-hill compensation (AgOpenGPS gyd.sideHillCompFactor): steer += roll × −factor.
            SideHillCompFactor = config.AutoSteer.SideHillCompensation,
            PurePursuitIntegralGain = config.Guidance.PurePursuitIntegralGain,
            FixHeading = headingRad,
            AvgSpeed = speedKmh,
            IsReverse = isReverse,
            IsAutoSteerOn = true,
            IsYouTurnTriggered = isYouTurnTriggered,
            ImuRoll = roll, // 0 with no roll source → no side-hill term (#110)
            PreviousState = _trackGuidanceState,
            // Re-acquire the nearest segment globally on engage AND whenever the
            // travel direction along the track flips — otherwise a stranded local
            // index can't recover and the vehicle keeps steering off the wrong
            // part of the loop (#422). Steady-state stays local.
            FindGlobalNearest = _trackGuidanceState == null || isHeadingSameWay != _lastHeadingSameWay,
            CurrentLocationIndex = _trackGuidanceState?.CurrentLocationIndex ?? 0
        };
        _lastHeadingSameWay = isHeadingSameWay;

        var output = _trackGuidanceService.CalculateGuidance(input);

        // Store state for next iteration
        _trackGuidanceState = output.State;
        if (_trackGuidanceState != null)
            _trackGuidanceState.CurrentLocationIndex = output.CurrentLocationIndex;

        return (output.SteerAngle, output.CrossTrackError, output.GoalPoint.Easting, output.GoalPoint.Northing,
                statusMessage, true, output.IsAtEndOfTrack);
    }

    /// <summary>
    /// Pure Pursuit look-ahead distance (m), AgOpenGPS CVehicle.UpdateGoalPointDistance:
    /// speed × 0.05 × mult × H + H, where H is the hold look-ahead on the line
    /// (|XTE| ≤ 0.1 m), hold × acquire factor off it (≥ 0.4 m), blended in between;
    /// at least the min look-ahead (AgOpenGPS 2 m). XTE is last cycle's (#110).
    /// </summary>
    public double GoalLookAhead(double speedKmh) => GoalLookAheadDistance.For(_configStore, speedKmh, _lastCrossTrackError);

    /// <summary>
    /// Display-only guidance for free-drive: the steer angle the steering would command if
    /// engaged right now (the steer bar, #479) and the Pure Pursuit goal point it would chase
    /// (#95), on the pass the display line shows. Stateless — no PreviousState and a fresh
    /// global nearest search — so the integrals and the nearest-segment memory the engaged
    /// path keeps in _trackGuidanceState are untouched. Returns null when the track is
    /// degenerate; Goal is null under Stanley, which has no look-ahead target (#99).
    /// </summary>
    private (double SteerAngle, Vec2? Goal)? CalculateDisplayGuidance(
        Position pos, Models.Track.Track displayTrack,
        double pivotEasting, double pivotNorthing, double headingRad)
    {
        var config = _configStore;
        bool stanley = config.Guidance.IsStanley;
        double speedKmh = pos.Speed * 3.6;
        var output = _trackGuidanceService.CalculateGuidance(new Models.Track.TrackGuidanceInput
        {
            Track = displayTrack,
            PivotPosition = new Vec3(pivotEasting, pivotNorthing, headingRad),
            SteerPosition = new Vec3(
                pivotEasting + Math.Sin(headingRad) * config.Vehicle.Wheelbase,
                pivotNorthing + Math.Cos(headingRad) * config.Vehicle.Wheelbase,
                headingRad),
            UseStanley = stanley,
            StanleyHeadingErrorGain = config.Guidance.StanleyHeadingErrorGain,
            StanleyDistanceErrorGain = config.Guidance.StanleyDistanceErrorGain,
            Wheelbase = config.Vehicle.Wheelbase,
            MaxSteerAngle = config.Vehicle.MaxSteerAngle,
            GoalPointDistance = GoalLookAhead(speedKmh),
            FixHeading = headingRad,
            AvgSpeed = speedKmh,
            IsAutoSteerOn = false,
            ImuRoll = 88888,
            PreviousState = null,
            FindGlobalNearest = true,
        });
        // CalculateGuidance flags an unusable track/segment with a 32000 m distance.
        if (output.DistanceFromLinePivot >= 32000) return null;
        return (output.SteerAngle, stanley ? null : output.GoalPoint);
    }

    /// <summary>
    /// Display-only track update: finds nearest pass and updates map, without steering.
    /// Returns the detected nearest pass number, the offset display track and the pivot's
    /// cross-track error to it (m, the engaged guidance's sign) for the light bar.
    /// </summary>
    // The track's points are part of the key: a reference nudge or a smoothing gives the same track a new list.
    private (Models.Track.Track Track, List<Vec3> Points, int Pass, double Width, double Nudge, bool Extend, double Radius) _toolPassKey;
    private IReadOnlyList<Vec3>? _toolPass;

    /// <summary>
    /// Where the tool runs on the pass shown: the pass's centre, without the tool shift the
    /// tractor's line has (PassGeometry). The map draws the tool's width along it, as AgOpenGPS's
    /// shadow on the current AB line shows where the tool will land; on a curve it follows the
    /// pass, its corners rounded as the tractor will drive them. The same list until the pass
    /// changes.
    /// </summary>
    private IReadOnlyList<Vec3> ToolPass(Models.Track.Track track, int pass, double widthMinusOverlap, double nudge, bool extend)
    {
        double radius = _configStore.Guidance.UTurnRadius;
        var key = (track, track.Points, pass, widthMinusOverlap, nudge, extend, radius);
        if (_toolPass != null && _toolPassKey.Equals(key)) return _toolPass;
        double dist = Models.Track.PassGeometry.Offset(track, pass, widthMinusOverlap, nudge);
        IReadOnlyList<Vec3> line;
        if (track.Points.Count == 2)
        {
            var a = track.Points[0];
            var b = track.Points[1];
            double right = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) + Math.PI / 2;
            double de = Math.Sin(right) * dist, dn = Math.Cos(right) * dist;
            line = new List<Vec3> { new(a.Easting + de, a.Northing + dn, a.Heading), new(b.Easting + de, b.Northing + dn, b.Heading) };
        }
        else
        {
            var made = Math.Abs(dist) < 0.01 ? CurveProcessing.ReferencePass(track.Points, track.IsClosed) : _offsetPasses.Get(track, dist, radius);
            line = extend ? CurveProcessing.ExtendPass(made) : made.Points;
        }
        _toolPassKey = key;
        return _toolPass = line;
    }

    private (Models.Track.Track Track, List<Vec3> Points, int Pass, double Width, double Nudge, double Shift, bool Extend, int Count, int? Target) _guideKey;
    private IReadOnlyList<IReadOnlyList<Vec3>>? _guideLines;

    /// <summary>
    /// The extra guidelines of a track: <paramref name="count"/> passes each side of
    /// <paramref name="pass"/>, offset from the reference with the collision test like the pass
    /// itself, so they round off at corners instead of pinching into spikes or folding over on a
    /// tight bend (#450). Adjacent passes are driven the other way, so the tool offset moves to
    /// the other side on every other one, as in AgOpenGPS (CABCurve.BuildCurveGuidelines). The
    /// same list is returned until the pass or the setting changes. With skip rows the pass a
    /// turn lands on can be driven either way, so while a turn is planned the alternation starts
    /// from its <paramref name="turnTarget"/>, driven the other way: the turn ends on a line (#591).
    /// A water pivot has none: AgOpenGPS builds side guidelines only for tracks that aren't a
    /// pivot (CABCurve.BuildCurveCurrentList), and rings round the centre were drawn (#590).
    /// </summary>
    private IReadOnlyList<IReadOnlyList<Vec3>> GuideLines(Models.Track.Track track, int pass, double widthMinusOverlap,
        double nudge, double toolShift, bool extend, int count, int? turnTarget)
    {
        if (track.IsPivot) return Array.Empty<IReadOnlyList<Vec3>>();
        var key = (track, track.Points, pass, widthMinusOverlap, nudge, toolShift, extend, count, turnTarget);
        if (_guideLines != null && _guideKey.Equals(key)) return _guideLines;

        var lines = new List<IReadOnlyList<Vec3>>(2 * count);
        for (int k = 1; k <= count; k++)
            foreach (int side in new[] { -1, 1 })
            {
                int q = pass + side * k;
                double shift = Models.Track.PassGeometry.IsDrivenOtherWay(q, pass, turnTarget) ? -toolShift : toolShift;
                double dist = Models.Track.PassGeometry.Offset(track, q, widthMinusOverlap, nudge, shift);
                if (track.Points.Count == 2)
                {
                    // An AB line: A and B moved sideways; the map draws it across the field.
                    var a = track.Points[0];
                    var b = track.Points[1];
                    double right = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) + Math.PI / 2;
                    double de = Math.Sin(right) * dist, dn = Math.Cos(right) * dist;
                    lines.Add(new List<Vec3> { new(a.Easting + de, a.Northing + dn, a.Heading), new(b.Easting + de, b.Northing + dn, b.Heading) });
                    continue;
                }
                var made = Math.Abs(dist) < 0.01 ? CurveProcessing.ReferencePass(track.Points, track.IsClosed) : _offsetPasses.Get(track, dist, _configStore.Guidance.UTurnRadius);
                if (made.Points.Count < 2) continue; // no pass there (#694)
                lines.Add(extend ? CurveProcessing.ExtendPass(made) : made.Points);
            }
        _guideKey = key;
        return _guideLines = lines;
    }

    private const double PassPickIntervalSeconds = 0.66;
    private Models.Track.Track? _passPickTrack;
    private long _lastPassPick;

    private (int nearestPass, Models.Track.Track? displayTrack, double xte, bool onReference) UpdateDisplayTrack(
        Position pos, Models.Track.Track track, int passNumber, double nudgeOffset,
        double pivotEasting, double pivotNorthing,
        double sampleEasting, double sampleNorthing)
    {
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        if (widthMinusOverlap < 0.1) widthMinusOverlap = 1.0;

        // Nearest-pass selection uses the *lookahead* sample (hysteresis: line jumps
        // ahead of the tractor in free-drive). XTE display uses the *pivot* (actual
        // cross-track error from the tractor position). See #261.
        double sampleDist = CalculatePerpendicularDistance(track, sampleEasting, sampleNorthing);

        // Match AgOpen CABLine.BuildCurrentABLineList — subtract the accumulated nudge (and
        // the half width between the reference line and pass 0) before rounding to the
        // nearest pass. Without the nudge, nudging the line perpendicular can cause the
        // auto-select to fight the nudge each cycle.
        // With a tool offset the pass is the one nearest the tool, and the line shown is the one
        // the tractor steers to put the tool on it (#446).
        double toolShift = Models.Track.PassGeometry.ToolShift(config.Tool.Offset, _guidanceWorking.IsHeadingSameWay);
        int nearestPass = Models.Track.PassGeometry.Nearest(track, sampleDist, widthMinusOverlap, nudgeOffset, toolShift);
        // The pass is picked again at most every 0.66 s, as AgOpenGPS (CABLine / CABCurve
        // BuildCurrentLine): between picks the line stays put, so it doesn't flick between two
        // passes when the tractor is halfway. A new track is picked at once.
        long now = Clock.Current.GetTimestamp();
        if (!ReferenceEquals(track, _passPickTrack) || Clock.Current.ElapsedSeconds(_lastPassPick, now) >= PassPickIntervalSeconds)
        {
            _passPickTrack = track;
            _lastPassPick = now;
        }
        else nearestPass = passNumber;
        double distAway = Models.Track.PassGeometry.Offset(track, nearestPass, widthMinusOverlap, nudgeOffset, toolShift);

        // An open curve's pass is extended straight past A and B like the line the tractor steers,
        // the guidelines and the line shown while guiding, whether AutoSteer is engaged or not (#492).
        bool extend = track.Points.Count > 2 && !track.IsClosed;
        bool onReference = Math.Abs(distAway) < 0.01;
        double turnRadius = _configStore.Guidance.UTurnRadius;

        Models.Track.Track resultTrack;
        if (onReference && !extend)
        {
            // On the base track — show reference directly
            resultTrack = track;
        }
        else if (ReferenceEquals(_cachedOffsetSourceTrack, track)
                 && ReferenceEquals(_cachedOffsetSourcePoints, track.Points)
                 && _cachedOffsetSourceCount == track.Points.Count
                 && _cachedOffsetDistAway == distAway
                 && _cachedOffsetTurnRadius == turnRadius
                 && _cachedOffsetTrack != null)
        {
            // Inputs unchanged — reuse last offset track so the downstream
            // ReferenceEquals gate in ApplyResults can actually skip rebuild.
            resultTrack = _cachedOffsetTrack;
        }
        else
        {
            // The extended reference is the line itself: the caller leaves baseTrack out so it
            // isn't drawn twice at the same position.
            // A pass the offset removed altogether has no points: nothing is drawn (#694).
            var pass = onReference ? CurveProcessing.ReferencePass(track.Points, track.IsClosed) : _offsetPasses.Get(track, distAway, turnRadius);
            var offsetPoints = extend ? CurveProcessing.ExtendPass(pass) : new List<Vec3>(pass.Points);
            resultTrack = new Models.Track.Track
            {
                Name = $"{track.Name} (pass {nearestPass})",
                Points = offsetPoints,
                Type = track.Type,
                IsVisible = true,
                IsActive = true
            };
            _cachedOffsetSourceTrack = track;
            _cachedOffsetSourcePoints = track.Points;
            _cachedOffsetSourceCount = track.Points.Count;
            _cachedOffsetDistAway = distAway;
            _cachedOffsetTurnRadius = turnRadius;
            _cachedOffsetTrack = resultTrack;
        }

        // Update XTE display — actual pivot distance to the selected pass line.
        double pivotPerp = CalculatePerpendicularDistance(track, pivotEasting, pivotNorthing);
        double xte = pivotPerp - distAway;
        if (!_guidanceWorking.IsHeadingSameWay) xte = -xte; // set for this cycle before any line (#172, #446)

        _autoSteerService.UpdateGuidanceResults(0, xte);
        return (nearestPass, resultTrack, xte, onReference);
    }

    /// <summary>
    /// Calculate YouTurn path-following guidance. Returns null if no path.
    /// Tuple includes diagnostic fields (anchor indices, anti-tangent guard
    /// firings) that surface on the GuidanceSnapshot so the debug recorder
    /// can correlate steering anomalies with the path-anchor advancement
    /// pattern.
    /// </summary>
    private (double steerAngle, double xte, double goalE, double goalN, bool turnComplete,
             int pointA, int pointB, int pathPointCount, bool antiTangentGuardFired)?
        CalculateYouTurnGuidance(Position currentPosition, List<Vec3> turnPath, bool isReverse)
    {
        if (turnPath.Count == 0) return null;

        var config = _configStore;
        double headingRad = currentPosition.Heading * Math.PI / 180.0;
        double speedKmh = currentPosition.Speed * 3.6;

        // Use the SAME speed-scaled look-ahead as track guidance (CalculateTrackGuidance).
        // A fixed hold value here is smaller than the track follower's dynamic look-ahead
        // while moving, so the goal point jumped CLOSER at the track→turn handoff — a sudden
        // sharper steer that made the tractor hunt for ~½ s on entry (exit was gentler
        // because the goal jumped farther). Matching them makes the handoff seamless.
        double lookAhead = GoalLookAhead(speedKmh); // same as track guidance (AgOpenGPS UpdateGoalPointDistance)

        var input = new YouTurnGuidanceInput
        {
            TurnPath = turnPath,
            PivotPosition = new Vec3(currentPosition.Easting, currentPosition.Northing, headingRad),
            // Front (steer) axle — Stanley measures its error there; Pure Pursuit ignores it.
            SteerPosition = new Vec3(
                currentPosition.Easting + Math.Sin(headingRad) * config.Vehicle.Wheelbase,
                currentPosition.Northing + Math.Cos(headingRad) * config.Vehicle.Wheelbase,
                headingRad),
            Wheelbase = config.Vehicle.Wheelbase,
            MaxSteerAngle = config.Vehicle.MaxSteerAngle,
            UseStanley = config.Guidance.IsStanley,
            StanleyHeadingErrorGain = config.Guidance.StanleyHeadingErrorGain,
            StanleyDistanceErrorGain = config.Guidance.StanleyDistanceErrorGain,
            GoalPointDistance = lookAhead,
            UTurnCompensation = config.Guidance.UTurnCompensation,
            FixHeading = headingRad,
            AvgSpeed = speedKmh,
            IsReverse = isReverse,
            UTurnStyle = config.Guidance.UTurnStyle
        };

        var output = _youTurnGuidanceService.CalculateGuidance(input);

        if (output.IsTurnComplete)
            return (0, 0, 0, 0, true, 0, 0, 0, false);

        return (output.SteerAngle, output.DistanceFromCurrentLine,
            output.GoalPoint.Easting, output.GoalPoint.Northing, false,
            output.PointA, output.PointB, turnPath.Count, output.AntiTangentGuardFired);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Geometry helpers (pure math, no UI)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Driving along the track's own direction (A→B) where the tractor is? An AB line by
    /// its A→B heading, a curve by its nearest segment.</summary>
    private static bool IsHeadingSameWayOn(Models.Track.Track track, double easting, double northing, double headingRad)
    {
        var pts = track.Points;
        if (pts.Count < 2) return true;
        double trackHeading = pts.Count == 2
            ? Math.Atan2(pts[1].Easting - pts[0].Easting, pts[1].Northing - pts[0].Northing)
            : FindNearestSegmentHeading(pts, easting, northing);
        double diff = headingRad - trackHeading;
        while (diff > Math.PI) diff -= 2 * Math.PI;
        while (diff < -Math.PI) diff += 2 * Math.PI;
        return Math.Abs(diff) < Math.PI / 2;
    }

    private static double FindNearestSegmentHeading(List<Vec3> points, double easting, double northing)
    {
        if (points.Count < 2) return 0;
        if (points.Count == 2) return points[0].Heading;

        double minDist = double.MaxValue;
        int nearestIdx = 0;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            double segDx = p2.Easting - p1.Easting;
            double segDy = p2.Northing - p1.Northing;
            double segLenSq = segDx * segDx + segDy * segDy;

            double dist;
            if (segLenSq < 0.0001)
            {
                dist = Math.Sqrt((easting - p1.Easting) * (easting - p1.Easting) +
                                 (northing - p1.Northing) * (northing - p1.Northing));
            }
            else
            {
                double t = Math.Clamp(
                    ((easting - p1.Easting) * segDx + (northing - p1.Northing) * segDy) / segLenSq,
                    0, 1);
                double projE = p1.Easting + t * segDx;
                double projN = p1.Northing + t * segDy;
                dist = Math.Sqrt((easting - projE) * (easting - projE) +
                                 (northing - projN) * (northing - projN));
            }

            if (dist < minDist)
            {
                minDist = dist;
                nearestIdx = i;
            }
        }

        return points[nearestIdx].Heading;
    }

    private static double CalculatePerpendicularDistance(Models.Track.Track track, double easting, double northing)
    {
        if (track.Points.Count == 2)
        {
            var a = track.Points[0];
            var b = track.Points[1];
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01) return 0;
            return ((easting - a.Easting) * dy - (northing - a.Northing) * dx) / len;
        }
        else
        {
            double minDist = double.MaxValue;
            double signedDist = 0;
            for (int i = 0; i < track.Points.Count - 1; i++)
            {
                var a = track.Points[i];
                var b = track.Points[i + 1];
                double dx = b.Easting - a.Easting;
                double dy = b.Northing - a.Northing;
                double segLen = Math.Sqrt(dx * dx + dy * dy);
                if (segLen < 0.01) continue;
                double t = Math.Clamp(
                    ((easting - a.Easting) * dx + (northing - a.Northing) * dy) / (segLen * segLen),
                    0, 1);
                double projE = a.Easting + t * dx;
                double projN = a.Northing + t * dy;
                double dist = Math.Sqrt((easting - projE) * (easting - projE) + (northing - projN) * (northing - projN));
                if (dist < minDist)
                {
                    minDist = dist;
                    signedDist = ((easting - a.Easting) * dy - (northing - a.Northing) * dx) / segLen;
                }
            }
            return signedDist;
        }
    }

}
