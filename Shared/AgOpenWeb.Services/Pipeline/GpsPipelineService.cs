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
    private readonly ISectionControlService _sectionControlService;
    private readonly ICoverageMapService _coverageMapService;
    // The coverage test the U-turn planner reads worked passes from; one delegate, not one per cycle.
    private readonly Func<double, double, bool> _isPointCovered;
    private readonly IAutoSteerService _autoSteerService;
    private readonly IAudioService _audioService;
    private readonly IPipelineIntents _intents;
    // Where the fix puts the vehicle: plane, heading, reversing, roll, pivot, with their own state (PoseStep).
    private readonly PoseStep _poseStep;
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

    // ── YouTurn + Guidance working state (Phase C) ──────────────────────
    // POCOs mirroring State.YouTurn and the subset of State.Guidance that
    // the YouTurn state machine reads/writes. Single-writer contract —
    // only mutated on the cycle worker thread, via state-machine Tick /
    // TriggerManual / ClearState. ApplyGpsCycleResult mirrors back to the
    // observable State.* on the UI thread.
    // The U-turn: its working state, the turn line and a curve's opening, its own (UTurnStep).
    private readonly UTurnStep _uTurnStep;
    // The guidance: its working state, the steering's memory and the lines shown, its own (GuidanceStep).
    private readonly GuidanceStep _guidance;

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
        _sectionControlService = sectionControlService;
        _coverageMapService = coverageMapService;
        _isPointCovered = coverageMapService.IsPointCovered;
        _autoSteerService = autoSteerService;
        _guidance = new GuidanceStep(configStore, trackGuidanceService, youTurnGuidanceService, autoSteerService, logger);
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
            _guidance.TrackChosen(t.PassNumber, t.NudgeOffset);
            _pendingTrack = null;
        }
        if (_steeringResetPending)
        {
            _guidance.ForgetSteering();
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
    /// <summary>The guidance step, for tests that read its working state or steering memory.</summary>
    internal GuidanceStep Guidance => _guidance;
    public double SimulatorSteerAngle => _guidance.SimulatorSteerAngle;

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
        _guidance.ApplyIntents(intents, _uTurnStep);

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
            _guidance.Stamp(track);
            // Phase D D3: pass number / nudge offset live on _guidanceWorking as
            // the single source of truth. Still read under lock here because
            // SetActiveTrack (UI-thread) writes them under the same lock.
            passNumber = _guidance.PassNumber;
            nudgeOffset = _guidance.NudgeOffset;
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
        _guidance.SetContourMode(contourOn);
        var recordedPath = contourOn ? null : Volatile.Read(ref _recordedPath);

        var pos = data.CurrentPosition;
        bool hasTrack = track != null && track.Points.Count >= 2;

        _poseStep.Locate(data, ref pos, committedLocalPlane, hasActiveField, driftE, driftN,
            out LocalPlane? firstFixLocalPlane, out LocalPlane? replacementLocalPlane, out double replacementDistanceKm,
            out FarFromFieldWarning? farFromFieldWarning, out LocalPlane? headingPlane, out string? jumpNotice,
            out double antennaE, out double antennaN,
            out double driftedEasting, out double driftedNorthing, out double headingRad);
        _guidance.SetReverse(_poseStep.IsReverse);
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
            isAlternateSkipMode, contourOn, recordedPath, _guidance.Working,
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
        passNumber = _guidance.PassNumber;
        nudgeOffset = _guidance.NudgeOffset;

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

        // ── (6) Guidance (GuidanceStep): the line shown, the steering, free drive ──
        bool engagedBeforeGuidance = autoSteerEngaged;
        _guidance.Run(pos, track, hasTrack, noPassOffset, driftedEasting, driftedNorthing, headingRad,
            _poseStep.IsReverse, _poseStep.Roll, _cycleCounter,
            ref passNumber, nudgeOffset,
            contourOn, contourSteer, contourDisplay, recordedPath,
            ref autoSteerEngaged, ref autoSteerDisengaged, ref disengageReason, youTurnEnabled,
            isYouTurnTriggered, isInYouTurn, youTurnPath,
            _uTurnStep, in tickCtx, ref youTurnTickEffects,
            out double steerAngle, out double crossTrackError, out double goalE, out double goalN,
            out bool hasGuidance, out bool hasGoal, out bool youTurnCompleted, out string? statusMessage,
            out Models.Track.Track? displayTrack, out Models.Track.Track? baseTrack,
            out IReadOnlyList<IReadOnlyList<Vec3>>? guideLines, out IReadOnlyList<Vec3>? toolPass, out int? shownLandingPass,
            out int diagPathAnchorA, out int diagPathAnchorB, out int diagTurnPathCount, out bool diagAntiTangentGuardFired);
        // The guidance switched AutoSteer off (past the end of an open curve or a recorded path):
        // the host loop's flag too, as the rules above do. Only then: written again otherwise, an
        // engage on the host loop since the rules' write would be lost.
        if (engagedBeforeGuidance && !autoSteerEngaged) lock (_stateLock) _autoSteerEngaged = false;

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
            Guidance = _guidance.Snapshot(shownLandingPass, displayTrack, baseTrack, hasGuidance,
                diagPathAnchorA, diagPathAnchorB, diagTurnPathCount, diagAntiTangentGuardFired, guideLines, toolPass),

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

    /// <summary>Why AutoSteer went off at the end of an open curve (AgOpenGPS gsGuidanceStopped, gsPastEndOfCurve).</summary>
    public const string PastEndOfCurveReason = "AutoSteer disengaged - past end of curve";
    public const string RecordedPathEndReason = "AutoSteer disengaged - end of the recorded path";

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
