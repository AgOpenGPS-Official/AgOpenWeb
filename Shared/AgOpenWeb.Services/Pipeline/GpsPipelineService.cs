// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Contour;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Orchestrates the GPS processing pipeline on a background thread.
/// Subscribes to <see cref="IGpsService.GpsDataUpdated"/>, runs all heavy computation
/// off the UI thread, and fires <see cref="CycleCompleted"/> with an immutable result snapshot.
/// </summary>
public sealed class GpsPipelineService : IGpsPipelineService
{
    // Lookahead time (seconds) used for auto-track-select in free-drive mode.
    // Matches AgOpen's setAS_guidanceLookAheadTime default. See #261.

    // ── Dependencies ────────────────────────────────────────────────────
    private readonly IGpsService _gpsService;
    private readonly IToolPositionService _toolPositionService;
    private readonly ITrackGuidanceService _trackGuidanceService;
    private readonly ISectionControlService _sectionControlService;
    private readonly ICoverageMapService _coverageMapService;
    // The coverage test the U-turn planner reads worked passes from; one delegate, not one per cycle.
    private readonly Func<double, double, bool> _isPointCovered;
    private readonly IAutoSteerService _autoSteerService;
    private readonly YouTurnGuidanceService _youTurnGuidanceService;
    private readonly IAudioService _audioService;
    private readonly IPipelineIntents _intents;
    // Where the fix puts the vehicle: plane, heading, reversing, roll, pivot, with their own state (PoseStep).
    private readonly PoseStep _poseStep;
    private double _lastCrossTrackError; // last cycle's XTE, for the look-ahead (#110)
    // The contour (#110): its strips, line and steering, its own state (ContourStep).
    private readonly ContourStep _contourStep;
    // Out of bounds, the steering speed limits and the RTK alarm, with their own state (AutoSteerRules).
    private readonly AutoSteerRules _autoSteerRules;
    // The tool in the headland, the hydraulic lift and the headland distance, with their own state (HeadlandStep).
    private readonly HeadlandStep _headlandStep;
    private readonly ILogger<GpsPipelineService> _logger;
    private readonly ConfigurationStore _configStore;

    // ── Events ──────────────────────────────────────────────────────────
    public event Action<GpsCycleResult>? CycleCompleted;

    /// <summary>
    /// Fired inside ProcessCycle just after the canonical pose is published
    /// to <see cref="IPositionEstimator"/>, before the cycle reads section
    /// state to build <see cref="GpsCycleResult"/>. Argument is the timestamp
    /// of the publish (matches the snapshot's TimestampTicks).
    ///
    /// Production: no subscriber — the host control loop runs on its own
    /// thread and ticks at a fixed cadence regardless of GPS arrivals.
    /// Tests: subscribe a synchronous control-loop tick so GpsCycleResult.
    /// SectionStates reflects the section state at this GPS frame's pose
    /// (no one-frame lag).
    /// </summary>
    public event Action<long>? PoseEstimatorUpdated;

    // ── Re-entrancy guard ───────────────────────────────────────────────
    private int _processing; // 0 = idle, 1 = processing

    // ── Subscription tracking ───────────────────────────────────────────
    private bool _isStarted;

    // ── Operational state (written from UI thread, read from background) ─
    private readonly object _stateLock = new();

    private bool _autoSteerEngaged;
    private Models.Track.Track? _activeTrack;

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
    // Phase D D3: passNumber / nudgeOffset live on _guidanceWorking as the single
    // source of truth. The separate _passNumber / _nudgeOffset fields used to be
    // pushed here by SetActiveTrack; that path now writes directly to the
    // working state (still UI-thread under lock — retires fully in D4/D5/D6
    // when snap / nudge / set-active-track all become intents).

    private bool _youTurnEnabled;
    // One-shot direction override for the next armed automatic turn. The UI
    // toggle pre-flips this while idle; the cycle mirrors it into
    // _youTurn.NextUTurnDirectionLeftOverride and the state machine consumes
    // and clears it during turn creation. Mirrors legacy SwapDirection.
    private bool? _nextUTurnDirectionLeftOverride;
    private int _uTurnSkipRows;
    private bool _isSkipWorkedMode;
    private bool _isAlternateSkipMode; // AgOpenGPS SkipMode.Alternative (#111)
    private double _headlandCalculatedWidth;
    private double _headlandDistanceConfig;
    private List<Vec3>? _headlandLine;
    private Boundary? _boundary;
    private double _driftE;
    private double _driftN;
    private bool _hasActiveField;
    // The UI's committed LocalPlane (State.Field.LocalPlane), pushed by SetLocalPlane whenever
    // it changes: field open, the cycle's own first-fix / re-anchor commits, field close.
    private LocalPlane? _committedLocalPlane;
    private bool _simulatorEnabled;
    private bool _simulatorSteersModule;
    private bool _headlandOn;
    // Set by SetYouTurnConfig when the skip pattern changes, so the cycle drops the snake
    // sequence and the next turn rebuilds it for the new pattern.
    private bool _snakeResetPending;

    // ── Pipeline-owned guidance state (only touched on background thread) ─
    private Models.Track.TrackGuidanceState? _trackGuidanceState;
    // Previous along-track travel direction; a change forces a global nearest
    // re-acquire so a stranded local index can recover (#422).
    private bool _lastHeadingSameWay = true;
    private double _simulatorSteerAngle;

    // ── YouTurn + Guidance working state (Phase C) ──────────────────────
    // POCOs mirroring State.YouTurn and the subset of State.Guidance that
    // the YouTurn state machine reads/writes. Single-writer contract —
    // only mutated on the cycle worker thread, via state-machine Tick /
    // TriggerManual / ClearState. ApplyGpsCycleResult mirrors back to the
    // observable State.* on the UI thread.
    // The U-turn: its working state, the turn line and a curve's opening, its own (UTurnStep).
    private readonly UTurnStep _uTurnStep;
    private readonly GuidanceWorkingState _guidanceWorking = new();

    // ── Curve limit warning throttling ──────────────────────────────────
    private DateTime _lastCurveLimitWarning = DateTime.MinValue;
    private int _lastWarnedPathsAway = int.MinValue;

    // ── Logging throttle ────────────────────────────────────────────────
    private int _cycleCounter;

    private readonly IPositionEstimator? _positionEstimator;

    public GpsPipelineService(
        IGpsService gpsService,
        IToolPositionService toolPositionService,
        ITrackGuidanceService trackGuidanceService,
        ISectionControlService sectionControlService,
        ICoverageMapService coverageMapService,
        IAutoSteerService autoSteerService,
        YouTurnGuidanceService youTurnGuidanceService,
        YouTurnStateMachine youTurnStateMachine,
        IAudioService audioService,
        IPipelineIntents intents,
        IGpsHeadingFusionService headingFusion,
        ILogger<GpsPipelineService> logger,
        ConfigurationStore configStore,
        IPositionEstimator? positionEstimator = null)
    {
        _gpsService = gpsService;
        _toolPositionService = toolPositionService;
        _trackGuidanceService = trackGuidanceService;
        _sectionControlService = sectionControlService;
        _coverageMapService = coverageMapService;
        _isPointCovered = coverageMapService.IsPointCovered;
        _autoSteerService = autoSteerService;
        _youTurnGuidanceService = youTurnGuidanceService;
        _uTurnStep = new UTurnStep(youTurnStateMachine, audioService, configStore, _isPointCovered, logger);
        _audioService = audioService;
        _contourStep = new ContourStep(configStore, sectionControlService, _isPointCovered, audioService);
        _autoSteerRules = new AutoSteerRules(configStore, audioService);
        _headlandStep = new HeadlandStep(configStore, sectionControlService);
        _intents = intents;
        _poseStep = new PoseStep(configStore, headingFusion);
        _logger = logger;
        _configStore = configStore;
        _positionEstimator = positionEstimator;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ══════════════════════════════════════════════════════════════════════

    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _gpsService.GpsDataUpdated += OnGpsDataUpdated;
        _logger.LogInformation("GpsPipelineService started");
    }

    public void Stop()
    {
        if (!_isStarted) return;
        _isStarted = false;
        _gpsService.GpsDataUpdated -= OnGpsDataUpdated;
        _logger.LogInformation("GpsPipelineService stopped");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Operational state setters (called from UI thread)
    // ══════════════════════════════════════════════════════════════════════

    public void SetAutoSteerEngaged(bool engaged)
    {
        lock (_stateLock)
        {
            _autoSteerEngaged = engaged;
            // Reset guidance state when disengaging so we do global search on next engage.
            // The cycle's own state: the cycle resets it at its next start (TakeHostChanges).
            if (!engaged) _steeringResetPending = true;
        }
    }

    public void SetActiveTrack(Models.Track.Track? track, int passNumber, double nudgeOffset)
    {
        lock (_stateLock)
        {
            _activeTrack = track;
            // The pass, the nudge and the guidance's memory are the cycle's: it takes them at its
            // next start (TakeHostChanges), so a cycle under way finishes on the track it began.
            _pendingTrack = (passNumber, nudgeOffset);
        }
    }

    // What the host loop changed in the cycle's own state, taken by the next cycle at its start.
    // Under _stateLock.
    private (int PassNumber, double NudgeOffset)? _pendingTrack;
    private bool _steeringResetPending;
    private bool _fieldOpenedPending;

    /// <summary>
    /// The host loop's changes to the cycle's own state, applied at the start of a cycle, before
    /// anything in it reads that state. Applied in the middle of a cycle, they were undone by it
    /// or mixed with it: a reset of the guidance's memory overwritten by that cycle's guidance,
    /// a track's pass reported with the previous track. Caller holds _stateLock.
    /// </summary>
    private void TakeHostChanges()
    {
        if (_pendingTrack is { } t)
        {
            _guidanceWorking.HowManyPathsAway = t.PassNumber;
            _guidanceWorking.NudgeOffset = t.NudgeOffset;
            // Reset guidance state when track changes so we do a global search
            _trackGuidanceState = null;
            _lastCrossTrackError = 0; // a new line has no previous XTE for the look-ahead (#110)
            _pendingTrack = null;
        }
        if (_steeringResetPending)
        {
            _trackGuidanceState = null;
            _steeringResetPending = false;
        }
        if (_fieldOpenedPending)
        {
            _poseStep.FieldOpened();
            _fieldOpenedPending = false;
        }
    }

    public void SetYouTurnEnabled(bool enabled)
    {
        lock (_stateLock) _youTurnEnabled = enabled;
    }

    /// <summary>
    /// One-shot direction override for the *next* armed automatic U-turn. The
    /// UI's direction toggle invokes this while idle; the cycle mirrors the
    /// flag into <see cref="YouTurnWorkingState.NextUTurnDirectionLeftOverride"/>,
    /// the state machine consumes it during the next turn-creation tick, and
    /// then clears it. <c>null</c> means no override.
    /// </summary>
    public void SetNextUTurnDirectionLeftOverride(bool? leftOverride)
    {
        lock (_stateLock) _nextUTurnDirectionLeftOverride = leftOverride;
    }

    /// <summary>
    /// Push YouTurn configuration values (skip-rows, skip-worked mode,
    /// headland geometry). Phase C C4 adds this so the cycle worker's
    /// YouTurn tick can build its own TickContext without reaching into
    /// the MVM.
    /// </summary>
    public void SetYouTurnConfig(int uTurnSkipRows, bool isSkipWorkedMode, double headlandCalculatedWidth, double headlandDistance,
        bool isAlternateSkipMode = false)
    {
        lock (_stateLock)
        {
            // A new skip pattern invalidates a snake sequence built for the old one.
            if (uTurnSkipRows != _uTurnSkipRows || isSkipWorkedMode != _isSkipWorkedMode
                || isAlternateSkipMode != _isAlternateSkipMode)
                _snakeResetPending = true;
            _uTurnSkipRows = uTurnSkipRows;
            _isSkipWorkedMode = isSkipWorkedMode;
            _isAlternateSkipMode = isAlternateSkipMode;
            _headlandCalculatedWidth = headlandCalculatedWidth;
            _headlandDistanceConfig = headlandDistance;
        }
    }

    public void SetHeadlandLine(IReadOnlyList<Vec3>? headlandLine)
    {
        lock (_stateLock)
        {
            // Copy to our own list so caller can mutate theirs safely
            _headlandLine = headlandLine != null ? new List<Vec3>(headlandLine) : null;
        }
    }

    public void SetBoundary(Boundary? boundary)
    {
        lock (_stateLock) _boundary = boundary;
    }

    public void SetDriftCompensation(double driftE, double driftN)
    {
        lock (_stateLock) { _driftE = driftE; _driftN = driftN; }
    }

    // Recorded route playback (P7): the line AutoSteer follows while a route plays, set from
    // the UI thread, followed by the cycle.
    private PathPlanning.RecordedRouteGuide? _recordedPath;

    public void SetRecordedPathGuidance(IReadOnlyList<Vec3>? approach, IReadOnlyList<Vec3>? route, int routeStartIndex) =>
        Volatile.Write(ref _recordedPath, route is { Count: >= 2 } ? new PathPlanning.RecordedRouteGuide(approach, route, routeStartIndex) : null);

    // The contour's switch, lock and strips, from the host loop (ContourStep).
    public void SetContourMode(bool on) => _contourStep.SetContourMode(on);
    public bool ToggleContourLock() => _contourStep.ToggleContourLock();
    public void LoadContours(IEnumerable<List<Vec3>> strips) => _contourStep.LoadContours(strips);
    public void ResetContours() => _contourStep.ResetContours();
    public List<List<Vec3>> TakeContoursToSave() => _contourStep.TakeContoursToSave();

    public void SetHasActiveField(bool hasActiveField)
    {
        lock (_stateLock)
        {
            // Reset the one-shot warning latch on the false->true transition so
            // re-opening (or opening a different) field gets a fresh shot at
            // the warning.
            if (hasActiveField && !_hasActiveField)
                _fieldOpenedPending = true; // the latch is the cycle's: reset at its next start
            _hasActiveField = hasActiveField;
        }
    }

    /// <summary>
    /// The UI's committed LocalPlane. The cycle converts WGS84 against it, drops its own
    /// first-fix cache once this matches it, and runs the origin guard on it.
    /// </summary>
    public void SetLocalPlane(LocalPlane? localPlane)
    {
        lock (_stateLock) _committedLocalPlane = localPlane;
    }

    /// <summary>Whether the internal simulator is the GPS source (no steer speed limits then, #106).
    /// It does not steer the real module (<see cref="HoldSteeringOnTheSimulator"/>).</summary>
    public void SetSimulatorEnabled(bool enabled)
    {
        lock (_stateLock) _simulatorEnabled = enabled;
        HoldSteeringOnTheSimulator();
    }

    /// <summary>The operator let the simulator move the real steering, for a bench test. Off, a
    /// simulated drive never asks the steer module to steer (the default).</summary>
    public void SetSimulatorSteersModule(bool steers)
    {
        lock (_stateLock) _simulatorSteersModule = steers;
        HoldSteeringOnTheSimulator();
    }

    /// <summary>
    /// The simulator does not steer the real module. The steer module stays on the network
    /// while the simulator runs; PGN 254 status 1 with the simulator's speed made it open the
    /// lock valve and drive the steering valve of a parked tractor. So the status stays 0
    /// while the simulator is the chosen source, from the moment it is chosen and not from the
    /// next fix (the receiver's first fixes arrive before the simulator's timer notices them
    /// and stops), unless the operator let it move the steering for a bench test. AutoSteer
    /// stays engaged in the app, so the simulator follows the line.
    /// </summary>
    private void HoldSteeringOnTheSimulator()
    {
        bool hold, simulator;
        lock (_stateLock) { simulator = _simulatorEnabled; hold = simulator && !_simulatorSteersModule; }
        _autoSteerService.SetSimulatorHoldsSteering(hold);
        _autoSteerService.SetSimulatorIsSource(simulator);
    }

    /// <summary>The headland toggle: the distance HUD and the hydraulic lift follow it (#106).</summary>
    public void SetHeadlandOn(bool on)
    {
        lock (_stateLock) _headlandOn = on;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Read-back properties
    // ══════════════════════════════════════════════════════════════════════

    public bool IsAutoSteerEngaged { get { lock (_stateLock) return _autoSteerEngaged; } }
    /// <summary>The U-turn's working state, for tests that set a turn up (UTurnStep).</summary>
    internal YouTurnWorkingState YouTurnWorking => _uTurnStep.Working;
    public double SimulatorSteerAngle => Volatile.Read(ref _simulatorSteerAngle);

    // ══════════════════════════════════════════════════════════════════════
    // GPS event handler
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When true, ProcessCycle runs synchronously on the calling thread instead
    /// of Task.Run. Eliminates async timing issues in tests: every GPS frame
    /// produces its result before the next frame is sent.
    /// </summary>
    public bool SynchronousMode { get; set; }

    // #169 stall diagnostics (see OnGpsDataUpdated).
    private static readonly TimeSpan StallLogThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowCycleLogThreshold = TimeSpan.FromMilliseconds(500);
    private long _lastGpsInputTicks;
    private int _droppedWhileBusy;

    private void OnGpsDataUpdated(object? sender, GpsData data)
    {
        if (SynchronousMode)
        {
            // Test mode: process inline, no back-pressure, no threading
            try { ProcessCycle(data); }
            catch (Exception ex) { _logger.LogError(ex, "GpsPipelineService.ProcessCycle failed"); }
            return;
        }

        // #169 stall diagnostics: a gap in GPS input (receiver, network or the UDP
        // thread) vs. a slow cycle (the pipeline itself) look the same on screen.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastGpsInputTicks, now);
        if (prev != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(prev, now) > StallLogThreshold)
            _logger.LogWarning("[Pipeline] No GPS input for {Ms:F0} ms", System.Diagnostics.Stopwatch.GetElapsedTime(prev, now).TotalMilliseconds);

        // Production mode: Task.Run with single-cycle-in-flight back-pressure
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            Interlocked.Increment(ref _droppedWhileBusy);
            return;
        }

        Task.Run(() =>
        {
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                ProcessCycle(data);
                var took = System.Diagnostics.Stopwatch.GetElapsedTime(c0);
                int dropped = Interlocked.Exchange(ref _droppedWhileBusy, 0);
                if (took > SlowCycleLogThreshold)
                    _logger.LogWarning("[Pipeline] Cycle took {Ms:F0} ms ({Dropped} fixes dropped while busy)",
                        took.TotalMilliseconds, dropped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GpsPipelineService.ProcessCycle failed");
            }
            finally
            {
                Volatile.Write(ref _processing, 0);
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Core cycle — runs on background thread
    // ══════════════════════════════════════════════════════════════════════

    private void ProcessCycle(GpsData data)
    {
        _cycleCounter++;

        // PERF-05 #3: GPS pipeline cycle = one full ProcessCycle invocation
        // (background thread, fires per GPS fix). Captures everything from
        // intent drain through the CycleCompleted event raise — guidance,
        // youturn, snapshot build, etc. Marker: .perf_gps_pipeline.
        bool perfGps = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfGpsPipeline;
        long perfT0 = perfGps ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long perfA0 = perfGps ? GC.GetAllocatedBytesForCurrentThread() : 0;

        var config = _configStore;

        // The host loop's changes to the cycle's state first: a lateral move drained below
        // applies to the pass of a track chosen since the last cycle, as when they were written
        // at once.
        lock (_stateLock) TakeHostChanges();

        // Stage 1: Drain intents — see Plans/threading_model.svg cycle worker lane.
        // Phase C consumes ManualYouTurn + ClearYouTurn here; Phase D extends
        // with Guidance writers (snap in D4, nudge in D5). Clear runs before
        // any state-machine call this cycle so a clear + manual pair resolves
        // cleanly (clear first, then manual triggers a fresh turn from the
        // empty state). Snap applies to HowManyPathsAway before the display-
        // track computation so this cycle's visuals reflect the new pass.
        var intents = _intents.Drain();
        if (intents.ClearYouTurn)
            _uTurnStep.Clear();
        // #50. A snap mid-turn is IGNORED entirely: applying it would shift the
        // displayed track (HowManyPathsAway) while the tractor is committed to the
        // executing arc's already-computed target — so it lands on the OLD pass and
        // then laterally seeks across to the just-snapped one (operator-confirmed as
        // the confusing behavior). The arc can't be safely re-planned mid-swing, so
        // we drop the snap outright; the client also hints "finish the turn first".
        // The armed-but-not-executing case still re-plans cleanly below (#408).
        if (intents.GuidanceSnap.HasValue && !_uTurnStep.IsExecuting)
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
            _uTurnStep.DiscardPlanned();
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
            if (!_uTurnStep.IsExecuting) _uTurnStep.DiscardPlanned();
        }
        if (intents.GuidanceResetNudge)
        {
            _guidanceWorking.NudgeOffset = 0;
            _trackGuidanceState = null;
            if (!_uTurnStep.IsExecuting) _uTurnStep.DiscardPlanned();
        }

        // Stage 2: Fix-quality status. The validator labels the fix; it does not
        // abort the cycle. Pre-Phase-B, NmeaParserService ran the same checks and
        // still fired GpsDataUpdated on rejection (with IsValid = false) so the
        // pipeline kept ticking and the UI — including latency display, PGN
        // heartbeat to modules, and fix-quality indicator — stayed live. An
        // earlier version of this gate early-returned here, which froze the
        // latency display and stopped PGN output whenever a real hardware fix
        // fell below MinFixQuality (default 4 = RTK Fixed). The rejection reason
        // propagates into the final GpsCycleResult's StatusMessage; downstream
        // consumers decide what to do with a low-quality fix by checking
        // data.IsValid or result.FixQuality.
        string? fixRejectionReason = null;
        if (!GpsFixQualityValidator.IsAcceptable(
                data.FixQuality, data.Hdop, data.DifferentialAge, out fixRejectionReason, _configStore))
        {
            data.IsValid = false;
        }

        // ── Snapshot operational state under lock ────────────────────────
        bool autoSteerEngaged;
        Models.Track.Track? track;
        int passNumber;
        double nudgeOffset;
        bool youTurnEnabled;
        int uTurnSkipRows;
        bool isSkipWorkedMode;
        bool isAlternateSkipMode;
        double headlandCalculatedWidth;
        double headlandDistanceConfig;
        List<Vec3>? headlandLine;
        Boundary? boundary;
        double driftE, driftN;
        bool hasActiveField;
        bool? nextUTurnDirectionOverride;
        LocalPlane? committedLocalPlane;
        bool simulatorEnabled;
        bool headlandOn;
        bool resetSnake;

        lock (_stateLock)
        {
            autoSteerEngaged = _autoSteerEngaged;
            track = _activeTrack;
            // Stamp which track this cycle's pass number / nudge belong to, so the VM can
            // tell a mirror of the current track's values from a stale one right after a
            // track switch (SaveTracksToFile, #107).
            _guidanceWorking.ActiveTrack = track;
            // Phase D D3: pass number / nudge offset live on _guidanceWorking as
            // the single source of truth. Still read under lock here because
            // SetActiveTrack (UI-thread) writes them under the same lock.
            passNumber = _guidanceWorking.HowManyPathsAway;
            nudgeOffset = _guidanceWorking.NudgeOffset;
            youTurnEnabled = _youTurnEnabled;
            uTurnSkipRows = _uTurnSkipRows;
            isSkipWorkedMode = _isSkipWorkedMode;
            isAlternateSkipMode = _isAlternateSkipMode;
            headlandCalculatedWidth = _headlandCalculatedWidth;
            headlandDistanceConfig = _headlandDistanceConfig;
            headlandLine = _headlandLine;
            boundary = _boundary;
            driftE = _driftE;
            driftN = _driftN;
            hasActiveField = _hasActiveField;
            committedLocalPlane = _committedLocalPlane;
            simulatorEnabled = _simulatorEnabled;
            headlandOn = _headlandOn;
            resetSnake = _snakeResetPending;
            _snakeResetPending = false;
            // Snapshot the pending direction override and clear it so the UI
            // can post a new one for the *next* turn even while this cycle
            // hasn't yet armed the current one — last-wins.
            nextUTurnDirectionOverride = _nextUTurnDirectionLeftOverride;
            _nextUTurnDirectionLeftOverride = null;
        }

        _uTurnStep.OnTrack(track);

        if (resetSnake)
        {
            _uTurnStep.ResetSnake();
        }

        // Boundary-follow curve (NoPassOffset): only SUPPRESS the free-drive nearest-pass snap
        // (below) so it stays where you put it — it starts on the boundary (pass 0, seeded from
        // NudgeDistance) instead of jumping to the pass nearest a parked tractor. Laterals,
        // nudges and U-turns still change the pass normally; we don't pin it here.
        bool noPassOffset = track != null && track.NoPassOffset;

        // No-headland workflow: substitute the outer-boundary TURN LINE for the
        // missing headland line — the AgOpen model (CTurn.BuildTurnLines): the
        // fence offset inward by just UTurnDistanceFromBoundary. This line is the
        // "in-turn-bounds" polygon the state machine gates arming on and raycasts
        // distance to; a fence-hugging boundary-follow pass (offset ~half a tool
        // inside the fence) sits INSIDE it, so it arms via the normal cultivated-
        // zone path. The turn's actual geometry is anchored on the turn boundary
        // built inside BuildCreationInput (also offset by UTurnDistanceFromBoundary)
        // and fitted by MoveTurnInsideTurnLine, so this line does not shape the arc.
        if (headlandLine == null && boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            var synth = _uTurnStep.GetOrComputeSyntheticHeadland(boundary);
            if (synth.Line != null && synth.Line.Count >= 3)
            {
                headlandLine = synth.Line;
                headlandCalculatedWidth = synth.Inset;
            }
        }

        // The U-turn turns at the turn line, as AgOpenGPS (CYouTurn uses bndList[].turnLine only,
        // CTurn.BuildTurnLines: the fence moved in by the U-turn distance from boundary): with a
        // headland too. The headland is for the sections, the hydraulic lift and the drawing; a
        // pass inside the headland band (a boundary curve's outer passes) turns at the turn line
        // like any other (#694, Graeme: "if I delete the headland I can make U-turns on the outer
        // passes"). The operator's headland is kept for the warning that a turn leaves its pass
        // before the headland line.
        List<Vec3>? turnLine = headlandLine;
        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            var tl = _uTurnStep.GetOrComputeSyntheticHeadland(boundary);
            if (tl.Line != null && tl.Line.Count >= 3) turnLine = tl.Line;
        }
        var workHeadland = headlandOn && _headlandLine != null && _headlandLine.Count >= 3 ? _headlandLine : null;

        _uTurnStep.Mirror(youTurnEnabled, nextUTurnDirectionOverride);
        bool isYouTurnTriggered = _uTurnStep.IsTriggered;
        bool isInYouTurn = _uTurnStep.IsExecuting;
        List<Vec3>? youTurnPath = _uTurnStep.TurnPath;
        bool contourOn;
        contourOn = _contourStep.IsOn;
        _guidanceWorking.IsContourMode = contourOn;
        var recordedPath = contourOn ? null : Volatile.Read(ref _recordedPath);

        var pos = data.CurrentPosition;
        bool hasTrack = track != null && track.Points.Count >= 2;

        _poseStep.Locate(data, ref pos, committedLocalPlane, hasActiveField, driftE, driftN,
            out LocalPlane? firstFixLocalPlane, out LocalPlane? replacementLocalPlane, out double replacementDistanceKm,
            out FarFromFieldWarning? farFromFieldWarning, out LocalPlane? headingPlane, out string? jumpNotice,
            out double antennaE, out double antennaN,
            out double driftedEasting, out double driftedNorthing, out double headingRad);
        _guidanceWorking.IsReverse = _poseStep.IsReverse;
        _autoSteerService.SetReverse(_poseStep.IsReverse); // deadzone is off in reverse (#110)
        _sectionControlService.IsReversing = _poseStep.IsReverse; // Auto sections off in reverse (#173)

        // ── (2b) Publish canonical pose to the position estimator ───────
        // The estimator is the bridge between GPS arrivals (10 Hz) and
        // the host control loop (100 Hz). Readers — control loop,
        // renderer — pull dead-reckoned pose between GPS samples.
        if (_positionEstimator is not null)
        {
            // ImuValid only says the IMU *heading* is usable; a $PAOGI fix leaves it
            // false (the antenna heading needs no fusion) but still carries the dual roll.
            bool hasAttitude = data.ImuValid || data.HasDualHeading;
            // The sentence's yaw rate field is not used: the estimator derives the rate
            // from the heading change (a zero here asks for that). What the field holds
            // depends on the firmware, a $PAOGI has no "no IMU" marker for it, and
            // AgOpenGPS only displays it. A board sending -103.8 deg/s while parked swung
            // the predicted heading 10 degrees and back at every fix.
            const double yawRateRadPerSec = 0.0;
            double rollRad = hasAttitude
                ? _poseStep.Roll * Math.PI / 180.0
                : 0.0;
            long ts = Clock.Current.GetTimestamp();
            _positionEstimator.UpdateFromGps(new PoseSnapshot(
                new Vec2(driftedEasting, driftedNorthing),
                headingRad,
                pos.Speed,
                yawRateRadPerSec,
                rollRad,
                ts));
            // Test hook: fire so a synchronous control-loop tick can advance
            // the section state machine before this cycle's GpsCycleResult
            // captures section bits. No-op in production (loop runs on its
            // own thread).
            PoseEstimatorUpdated?.Invoke(ts);
        }

        // ── The U-turn (UTurnStep): before the guidance, as completing a turn moves the pass ──
        var youTurnTickEffects = _uTurnStep.Tick(intents.ManualYouTurn, autoSteerEngaged, youTurnEnabled,
            pos, driftedEasting, driftedNorthing, track, boundary, turnLine, workHeadland,
            uTurnSkipRows, isSkipWorkedMode, headlandCalculatedWidth, headlandDistanceConfig,
            isAlternateSkipMode, contourOn, recordedPath, _guidanceWorking,
            out bool hasValidHeadlandLine, out bool hasTickableTrack, out var tickCtx);

        // Refresh the locals the downstream guidance branch reads — the tick
        // (or a drained intent above) may have updated them. CompleteTurn
        // advances HowManyPathsAway to the next pass on the same tick that
        // clears IsTriggered/TurnPath, so we MUST re-read passNumber and
        // nudgeOffset here. Without the refresh, track guidance on the
        // completion tick used the *previous* pass's offset — so pivot was
        // ~one-pass-width off the wrong AB line and the algorithm produced
        // a hard-side steer for one tick (visible front-wheel spike), which
        // showed up as a leftward coverage curve at U-turn exit (worse at
        // higher speed because lateral excursion scales with velocity).
        isYouTurnTriggered = _uTurnStep.IsTriggered;
        isInYouTurn = _uTurnStep.IsExecuting;
        youTurnPath = _uTurnStep.TurnPath;
        passNumber = _guidanceWorking.HowManyPathsAway;
        nudgeOffset = _guidanceWorking.NudgeOffset;

        if (_uTurnStep.OnYouTurnSwitch(youTurnEnabled))
        {
            isYouTurnTriggered = false;
            isInYouTurn = false;
            youTurnPath = null;
        }

        // ── (3) Tool position ───────────────────────────────────────────
        // ToolPositionService is updated by ControlLoopService at 100 Hz
        // (ControlTick). The pipeline used to call
        // Update here too, but that created a dual-writer race on Torriem
        // state — pipeline fed GPS-anchored pose at 10 Hz while the control
        // loop fed dead-reckoned pose at 100 Hz, causing the trailing-tool
        // atan2 baseline to thrash. Single writer now; we just read the
        // latest snapshot (lock-free, at most ~10 ms stale).
        var toolPos = _toolPositionService.ToolPosition;
        var hitchPos = _toolPositionService.HitchPosition;
        double toolHeading = _toolPositionService.ToolHeading;
        bool isToolReady = _toolPositionService.IsToolPositionReady;
        double toolWidth = config.ActualToolWidth;


        // Map positions are now sent via GpsCycleResult → ViewModel → MapRenderState.
        // The pipeline does NOT call _mapService directly.

        // ── (5) Out of bounds (#494) ────────────────────────────────────
        // Leaving the field never switches autosteer off. As AgOpenGPS (Position.designer.cs,
        // "Youturn"): the boundary shows it and PGN 239 carries geoStop for the machine module.
        bool autoSteerDisengaged = false;
        string? disengageReason = null;
        _autoSteerRules.UpdateOutOfBounds(boundary, turnLine, youTurnEnabled && hasValidHeadlandLine,
            _uTurnStep.IsTriggered || _uTurnStep.IsExecuting,
            driftedEasting, driftedNorthing);

        // (5a) A dual heading across the direction of travel: its offset doesn't match how
        // the antennas are mounted (front to back set up as side by side). Guidance would
        // steer for a tractor driving across its line, and the tractor leaves the pass.
        // Also on the simulator, which takes the offset too.
        if (autoSteerEngaged && _poseStep.IsDualHeadingOffTravel)
        {
            autoSteerEngaged = false;
            autoSteerDisengaged = true;
            disengageReason = "AutoSteer disengaged - " + DualHeadingOffTravelText(_poseStep.DualHeadingOffTravelDeg);
            lock (_stateLock) _autoSteerEngaged = false;
        }

        // (5b) Steering speed limits (#106) — like AgOpenGPS, and like it not on the simulator.
        if (autoSteerEngaged && !simulatorEnabled)
        {
            var speedReason = _autoSteerRules.CheckSteerSpeedLimits(pos.Speed * 3.6);
            if (speedReason != null)
            {
                autoSteerEngaged = false;
                autoSteerDisengaged = true;
                disengageReason = speedReason;
                lock (_stateLock) _autoSteerEngaged = false;
            }
        }
        else
        {
            _autoSteerRules.NotSteering();
        }

        // (5c) Reverse (#125), like AgOpenGPS: with Steer in reverse off, steering
        // stops while reversing but AutoSteer stays engaged; it also stops while a
        // single antenna with no IMU can't yet tell a direction change. PGN 254
        // status goes to 0 for those cycles.
        bool steerPaused = autoSteerEngaged
            && ((_poseStep.IsReverse && !_configStore.AutoSteer.SteerInReverse) || _poseStep.IsChangingDirection);
        _autoSteerService.SetSteerPaused(steerPaused);

        // U-turn lifecycle is bound to autosteer: when autosteer is not
        // engaged (user toggled off, boundary kickout, far-from-field guard,
        // or any other disengage path), the rendered turn path must clear so
        // the operator doesn't see a stale arc on the map. The state machine
        // tick is also gated on autoSteerEngaged, so without this clear the
        // working state would freeze with IsTriggered/IsExecuting=true and
        // the snapshot would keep emitting the old TurnPath every cycle —
        // ApplyGpsCycleResult would then keep pushing it back to the map.
        // Re-engaging autosteer rebuilds the turn from scratch via the auto
        // tick or a manual trigger.
        if (!autoSteerEngaged
            && _uTurnStep.HasTurn)
        {
            _uTurnStep.Clear();
            isYouTurnTriggered = false;
            isInYouTurn = false;
            youTurnPath = null;
        }

        // ── (5d) Contour (#110, AgOpenGPS AddContourPoints + DistanceFromContourLine) ─
        ContourSteer? contourSteer = null;
        Models.Track.Track? contourDisplay = null;
        if (hasActiveField || contourOn)
            contourSteer = _contourStep.ProcessContour(contourOn, hasActiveField, driftedEasting, driftedNorthing,
                headingRad, antennaE, antennaN, pos.Speed * 3.6, autoSteerEngaged, _poseStep.IsReverse, _poseStep.Roll, out contourDisplay);

        // ── (6) Guidance calculation ────────────────────────────────────
        double steerAngle = 0;
        double crossTrackError = 0;
        double goalE = 0, goalN = 0;
        bool hasGuidance = false;
        bool hasGoal = false;
        bool youTurnCompleted = false;
        string? statusMessage = null;
        Models.Track.Track? displayTrack = null;
        Models.Track.Track? baseTrack = null;
        IReadOnlyList<IReadOnlyList<Vec3>>? guideLines = null;
        (double Width, double Shift, bool Extend, int Count)? guideArgs = null;
        IReadOnlyList<Vec3>? toolPass = null;
        (double Width, bool Extend)? toolPassArgs = null;
        int? shownLandingPass = null; // the pass a U-turn being driven lands on, while it is the one shown

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
            if (isInYouTurn && _uTurnStep.TurnTargetPass() is { } landing)
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
                    config2.Display.ExtraGuidelinesCount, _uTurnStep.TurnTargetPass());
            }
        }

        int diagPathAnchorA = 0;
        int diagPathAnchorB = 0;
        int diagTurnPathCount = 0;
        bool diagAntiTangentGuardFired = false;

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
                disengageReason = RecordedPathEndReason;
                lock (_stateLock) _autoSteerEngaged = false;
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
                var ytResult = CalculateYouTurnGuidance(ytPos, youTurnPath);
                if (ytResult != null)
                {
                    steerAngle = ytResult.Value.steerAngle;
                    crossTrackError = ytResult.Value.xte;
                    goalE = ytResult.Value.goalE;
                    goalN = ytResult.Value.goalN;
                    youTurnCompleted = ytResult.Value.turnComplete;
                    hasGuidance = !youTurnCompleted;
                    if (youTurnCompleted && _uTurnStep.IsExecuting)
                    {
                        // Guidance ran off the end of the path before the state machine's
                        // own checks fired. Complete the turn here (AgOpenGPS does the same
                        // from its U-turn guidance) so the next cycle follows the new pass
                        // instead of sitting in a turn with no steering (#163).
                        var completion = _uTurnStep.CompleteFromGuidance(in tickCtx, _guidanceWorking);
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
                    isYouTurnTriggered);
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
                    disengageReason = PastEndOfCurveReason;
                    lock (_stateLock) _autoSteerEngaged = false;
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
                    guideLines = GuideLines(track!, nearestPass, ga.Width, nudgeOffset, ga.Shift, ga.Extend, ga.Count, _uTurnStep.TurnTargetPass());
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

        // ── (7) Section control + coverage painting ─────────────────────
        // SectionControlService.Update is now driven by the host control
        // loop at 100 Hz (#313 commit 5c) for sub-frame edge accuracy.
        // The pipeline only reads the latest section states here for its
        // PGN-build step below.
        var sectionStates = _sectionControlService.SectionStates;
        int numSections = _sectionControlService.NumSections;

        // ── (8b) Hydraulic lift state (PGN 239 input) ───────────────────
        // Phase B completion: this used to live on the UI-thread legacy
        // path (MainViewModel.UpdateToolPositionProperties). Moved here so
        // SetMachineState's three fields (section bits, U-turn state,
        // hyd-lift state) are all written by the cycle, before the PGN
        // build in (9) reads them. Between cycles the control loop keeps the
        // section bits current itself (SetSectionBits, #311).
        // The tool in the headland raises the lift and, as in AgOpenGPS, turns the tram lights off.
        bool toolInHeadland = _headlandStep.IsToolInHeadland(toolPos, toolHeading, pos.Speed, headlandLine, headlandOn);
        byte hydLiftState = _headlandStep.HydLiftFor(toolInHeadland, pos.Speed, headlandLine, boundary, headlandOn, _poseStep.IsReverse);
        _autoSteerService.SetMachineState(
            _sectionControlService.GetSectionBits64(),
            isInYouTurn,
            hydLiftState,
            _autoSteerRules.IsOutOfBounds,
            toolInHeadland);

        // ── (9) AutoSteer pipeline (PGN/latency) ────────────────────────
        _autoSteerService.ProcessSimulatedPosition(
            pos.Latitude, pos.Longitude, pos.Altitude,
            pos.Heading, pos.Speed, data.FixQuality,
            data.SatellitesInUse, data.Hdop,
            driftedEasting, driftedNorthing);

        // ── (9b) PGN 100 corrected position, PGN 214 GPS data ───────────
        // AgOpenGPS sends pn.fix: the antenna with antenna offset, roll and drift applied
        // but not moved back to the pivot. Undo the fore/aft step of the transform above.
        // PGN 214 is the fix as it came in, which AgIO sends after parsing each sentence;
        // GPS_Out takes speed, heading and fix quality from it (#364). AgIO only has real
        // receivers; it goes out for the simulator too, so GPS_Out works in sim.
        // Both off unless ticked on the Network IO card. PGN 214 also goes to an ISOBUS task
        // controller while one is heard, ticked or not: it tells the implements the fix
        // quality from it (DDI 514), and without it says plain GPS whatever the receiver has.
        _autoSteerService.SendGpsData(data, toLoopback: _configStore.Connections.SendCorrectedPosition);
        if (_configStore.Connections.SendCorrectedPosition && headingPlane != null && data.FixQuality > 0)
        {
            double fixE = driftedEasting + Math.Sin(headingRad) * _configStore.Vehicle.AntennaPivot;
            double fixN = driftedNorthing + Math.Cos(headingRad) * _configStore.Vehicle.AntennaPivot;
            var fix = headingPlane.ConvertGeoCoordToWgs84(new GeoCoord(fixN, fixE));
            _autoSteerService.SendCorrectedPosition(fix.Latitude, fix.Longitude, pos.Heading);
        }

        // ── (9c) Loopback copies of PGN 254, 239, 229 ───────────────────
        // AgOpenGPS sends every module PGN to AgIO's loopback port, where rate controllers
        // read speed and section states; once per fix, 239 and 229 only with a field open.
        _autoSteerService.SendLoopbackPgns(hasActiveField);

        // ── (10) Headland proximity ─────────────────────────────────────
        _headlandStep.Proximity(headlandLine, _toolPositionService.ToolPivotPosition, headlandOn,
            out double? headlandDist, out bool headlandWarning);

        // ── (11) RTK quality sounds ─────────────────────────────────────
        if (_autoSteerRules.CheckRtkQualityChange(data.FixQuality) && autoSteerEngaged)
        {
            // "Alarm stops AutoSteer" (#106) — same path as the boundary kickout.
            autoSteerEngaged = false;
            autoSteerDisengaged = true;
            disengageReason = "AutoSteer disengaged - RTK fix lost";
            lock (_stateLock) _autoSteerEngaged = false;
        }

        // ── (12) Build section state arrays for result ──────────────────
        bool[]? secStatesArr = null;
        int[]? secColorCodes = null;
        if (numSections > 0)
        {
            secStatesArr = new bool[numSections];
            secColorCodes = new int[numSections];
            for (int i = 0; i < numSections; i++)
            {
                secStatesArr[i] = sectionStates[i].IsOn;
                secColorCodes[i] = sectionStates[i].ColorCode;
            }
        }

        // ── (13) Build immutable result ─────────────────────────────────
        var result = new GpsCycleResult
        {
            // GPS position
            Latitude = pos.Latitude,
            Longitude = pos.Longitude,
            Altitude = pos.Altitude,
            Easting = driftedEasting,
            Northing = driftedNorthing,
            Heading = pos.Heading,
            Speed = pos.Speed,
            RollDegrees = _poseStep.Roll,
            SatelliteCount = data.SatellitesInUse,
            Hdop = data.Hdop,
            DifferentialAge = data.DifferentialAge,
            FixQuality = data.FixQuality,
            GpsValid = data.IsValid,
            SentenceType = data.SentenceType,
            SentenceHeading = _poseStep.SentenceHeading,
            ImuValid = data.ImuValid,
            IsReverse = _poseStep.IsReverse,
            IsDualHeadingMissing = _poseStep.IsDualHeadingMissing,
            IsDualHeadingOffTravel = _poseStep.IsDualHeadingOffTravel,
            DualHeadingOffTravelDeg = _poseStep.DualHeadingOffTravelDeg,

            // Tool position
            ToolEasting = toolPos.Easting,
            ToolNorthing = toolPos.Northing,
            ToolHeadingRadians = toolHeading,
            ToolWidth = toolWidth,
            HitchEasting = hitchPos.Easting,
            HitchNorthing = hitchPos.Northing,
            IsToolPositionReady = isToolReady,

            // Autosteer
            IsAutoSteerEngaged = autoSteerEngaged,
            AutoSteerDisengagedThisCycle = autoSteerDisengaged,
            HydLiftState = hydLiftState,
            IsOutOfBounds = _autoSteerRules.IsOutOfBounds,
            ContourRef = _contourStep.RefShown,
            IsContourLocked = _contourStep.LockedShown,
            HasContoursToSave = _contourStep.HasPending,
            DisengageReason = disengageReason,

            // Per-cycle snapshots for UI-thread mirror via ApplyGpsCycleResult.
            // JustCompleted on the YouTurn snapshot carries the one-cycle turn
            // completion signal previously on the flat YouTurnCompleted field.
            // Guidance snapshot emits every cycle — Phase D D3 made the cycle
            // the sole writer of _guidanceWorking.HowManyPathsAway, so the
            // snapshot can no longer fight an opposing UI-thread writer.
            YouTurn = _uTurnStep.Snapshot(
                justCompleted: youTurnCompleted || (youTurnTickEffects?.TurnCompleted ?? false)),
            Guidance = BuildGuidanceSnapshot(
                WithLineLabel(_guidanceWorking, shownLandingPass), displayTrack, baseTrack, hasGuidance,
                diagPathAnchorA, diagPathAnchorB, diagTurnPathCount, diagAntiTangentGuardFired) with { GuideLines = guideLines, ToolPass = toolPass },

            // Sections
            SectionStates = secStatesArr,
            SectionColorCodes = secColorCodes,

            // Headland proximity
            HeadlandProximityDistance = headlandDist,
            HeadlandProximityWarning = headlandWarning,

            // Contour: a stretch ahead that can't be driven as a lap
            ContourAhead = (byte)_contourStep.Ahead.What,
            ContourAheadM = _contourStep.Ahead.AtM,

            // Phase E: first-fix LocalPlane auto-create — non-null only on
            // the single cycle where the cycle bootstrapped the plane.
            FirstFixLocalPlane = firstFixLocalPlane,

            // Origin guard: replacement plane (silent re-anchor when no field
            // is loaded and the GPS source jumped > TempOriginReinitDistanceM)
            // or a far-from-field warning (field loaded, > FieldOriginWarnDistanceM).
            ReplacementLocalPlane = replacementLocalPlane,
            ReplacementDistanceKm = replacementDistanceKm,
            FarFromFieldWarning = farFromFieldWarning,

            // Status
            StatusMessage = jumpNotice ?? statusMessage ?? youTurnTickEffects?.StatusMessage ?? fixRejectionReason,
            YouTurnNotice = youTurnTickEffects is { TellOperator: true } ? youTurnTickEffects.StatusMessage : null,
        };

        CycleCompleted?.Invoke(result);

        if (perfGps)
        {
            _perfGpsCycleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfT0;
            _perfGpsCycleAllocs += GC.GetAllocatedBytesForCurrentThread() - perfA0;
            _perfGpsCycleCount++;
            var elapsed = (DateTime.UtcNow - _perfGpsWindowStart).TotalSeconds;
            if (elapsed >= 1.0 && _perfGpsCycleCount > 0)
            {
                double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
                Console.WriteLine(
                    $"[GpsPipeline-PERF] cycles={_perfGpsCycleCount}"
                    + $" us/cycle={(_perfGpsCycleTicks / ticksPerUs / _perfGpsCycleCount):F1}"
                    + $" alloc/cycle={(_perfGpsCycleAllocs / _perfGpsCycleCount)}B"
                    + $" total_us={(long)(_perfGpsCycleTicks / ticksPerUs)}"
                    + $" total_alloc={_perfGpsCycleAllocs}B"
                    + $" window={elapsed:F2}s");
                _perfGpsCycleTicks = 0;
                _perfGpsCycleAllocs = 0;
                _perfGpsCycleCount = 0;
                _perfGpsWindowStart = DateTime.UtcNow;
            }
        }
    }

    // PERF-05 #3: GPS pipeline accumulators. Gated by DiagFlags.PerfGpsPipeline.
    private long _perfGpsCycleTicks;
    private long _perfGpsCycleAllocs;
    private int _perfGpsCycleCount;
    private DateTime _perfGpsWindowStart = DateTime.UtcNow;

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

    /// <summary>Why AutoSteer went off at the end of an open curve (AgOpenGPS gsGuidanceStopped, gsPastEndOfCurve).</summary>
    public const string PastEndOfCurveReason = "AutoSteer disengaged - past end of curve";
    public const string RecordedPathEndReason = "AutoSteer disengaged - end of the recorded path";

    private (double steerAngle, double xte, double goalE, double goalN, string? statusMessage, bool hasLine, bool atEnd)?
        CalculateTrackGuidance(
            Position currentPosition, Models.Track.Track track, int passNumber, double nudgeOffset,
            double driftedEasting, double driftedNorthing, double headingRad,
            bool isYouTurnTriggered)
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

        if (_cycleCounter % 50 == 0)
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

        if (_cycleCounter % 50 == 0 && currentTrack.Points.Count >= 2)
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
            IsReverse = _poseStep.IsReverse,
            IsAutoSteerOn = true,
            IsYouTurnTriggered = isYouTurnTriggered,
            ImuRoll = _poseStep.Roll, // 0 with no roll source → no side-hill term (#110)
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
    private double GoalLookAhead(double speedKmh) => GoalLookAheadDistance.For(_configStore, speedKmh, _lastCrossTrackError);

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
        CalculateYouTurnGuidance(Position currentPosition, List<Vec3> turnPath)
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
            IsReverse = _poseStep.IsReverse,
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


    /// <summary>The lift byte for a tool at this pose (HeadlandStep), reversing as the cycle last saw it.</summary>
    private byte ComputeHydLiftState(Vec3 toolPosition, double toolHeading, double speed, List<Vec3>? headlandLine,
        Boundary? boundary, bool headlandOn)
        => _headlandStep.ComputeHydLiftState(toolPosition, toolHeading, speed, headlandLine, boundary, headlandOn, _poseStep.IsReverse);

    // Monotonic ms clock of the steering speed limits; tests substitute it (AutoSteerRules.NowMs).
    internal Func<long> NowMs { get => _autoSteerRules.NowMs; set => _autoSteerRules.NowMs = value; }
    /// <summary>Why AutoSteer won't steer with a dual heading that runs across the direction of
    /// travel (<see cref="IGpsHeadingFusionService.IsDualHeadingOffTravel"/>), for the toast.</summary>
    public static string DualHeadingOffTravelText(double offDeg) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "the dual antenna heading is {0:F0}° off the direction of travel: check its Heading offset (GPS tab)",
            double.IsFinite(offDeg) ? offDeg : 90);

}
