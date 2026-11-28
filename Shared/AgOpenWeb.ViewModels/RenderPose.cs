// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// The map's pose between fixes: at 30 Hz (20 Hz on mobile) the vehicle, hitch and tool,
/// dead-reckoned to now from the estimator, into <c>State.Vehicle</c>'s <c>Render*</c> fields
/// (the web Tick reads them) and the map service. Moved out of the view model (Plans/
/// MAINVIEWMODEL_REFACTOR_PLAN.md item 7); started by the backend (<c>WebBackend.StartServices</c>,
/// before the view model's own start), and stopped with the host loop, as before.
///
/// <para><b>Thread.</b> The host loop: its timer fires there, and debug and test builds fail a
/// pull from another thread. It writes <c>State.Vehicle</c>'s <c>Render*</c> fields one by one;
/// the web Tick reads them on the map broadcaster's thread (<c>SceneProjector.BuildTick</c>, from
/// the thread pool) with no lock, so a Tick built while a pull is writing can mix two pulls'
/// values (a position at most one pull, ~7 cm at 25 km/h, apart). Written down here, not
/// changed in the move.</para>
///
/// <para><b>What it reads that another thread writes:</b> the pose snapshot (the GPS cycle,
/// swapped whole), the tool position's heading, tank position and readiness (the control tick,
/// behind a seqlock), the configuration (the host loop itself).</para>
/// </summary>
public sealed class RenderPose
{
    private readonly IToolPositionService _toolPositionService;
    private readonly IMapService _mapService;
    private readonly ApplicationState _appState;
    private readonly ConfigurationStore _configStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimerFactory _timerFactory;
    private readonly IPositionEstimator? _positionEstimator;
    private IUiTimer? _renderPullTimer;

    private ApplicationState State => _appState;
    private ConfigurationStore ConfigStore => _configStore;

    public RenderPose(
        IToolPositionService toolPositionService,
        IMapService mapService,
        ApplicationState appState,
        ConfigurationStore configStore,
        IUiDispatcher uiDispatcher,
        IUiTimerFactory uiTimerFactory,
        IPositionEstimator? positionEstimator = null)
    {
        _toolPositionService = toolPositionService;
        _mapService = mapService;
        _appState = appState;
        _configStore = configStore;
        _dispatcher = uiDispatcher;
        _timerFactory = uiTimerFactory;
        _positionEstimator = positionEstimator;

        // Renderer-pull timer (#313 commit 6): when a position estimator is
        // wired, push the latest dead-reckoned vehicle pose to the map at
        // ~30 Hz so the chevron moves smoothly between GPS samples instead
        // of stepping at the GPS rate (10 Hz). Pulled state, not pushed —
        // the estimator's pose at any tick reflects the latest GPS snapshot
        // dead-reckoned forward to that tick's timestamp.
        if (_positionEstimator is not null)
        {
            // Render-pull rate. Each tick clones 5 arrays, allocates an ~80-field
            // MapRenderState, and pushes it to the Avalonia composition thread —
            // which on iOS Metal stays saturated at 30 Hz on weaker hardware
            // (e.g. iPad Pro 2nd gen drops to 24 FPS during paint). Drop to
            // ~20 Hz on mobile to give the composition thread headroom; desktop
            // keeps 30 Hz for smoother vehicle interpolation.
            int intervalMs = (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid()) ? 50 : 33;
            _renderPullTimer = _timerFactory.Create();
            _renderPullTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
            _renderPullTimer.Tick += OnRenderPullTick;
        }
    }

    /// <summary>Start the pull (nothing without an estimator). Called once, on the host loop.</summary>
    public void Start() => _renderPullTimer?.Start();

    /// <summary>
    /// 30 Hz UI-thread pull (#313 commit 6) of the dead-reckoned vehicle
    /// pose so the map chevron interpolates smoothly between GPS samples
    /// instead of stepping at the GPS rate. The estimator returns a pose
    /// dead-reckoned to "now" from the latest GPS snapshot using yaw rate
    /// and velocity, so position advances ~7 cm per 30 Hz frame at 25 km/h
    /// instead of a 28 cm jump every 100 ms.
    ///
    /// Tool/hitch are computed entirely from the current dead-reckoned
    /// vehicle pose (for hitch) and the Torriem-stable tool heading from
    /// the snapshot (for tool). Earlier attempts that translated the
    /// snapshot tool by a dead-reckoned hitch delta still mixed two
    /// estimator state bases when a GPS sample arrived between the last
    /// control-loop tick and the render tick — the snapshot hitch used
    /// the pre-arrival prediction while the new hitch used the post-
    /// arrival prediction, leaking a small snap into the implement at
    /// each sample. Computing both hitch and tool from the same single
    /// estimator pose eliminates that.
    /// </summary>
    private void OnRenderPullTick(object? sender, EventArgs e)
    {
#if DEBUG
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("The render pull runs on the host loop: it writes State.Vehicle, which the page's projectors read there.");
#endif
        if (_positionEstimator?.GetLatestSnapshot() is null)
            return;

        // PERF-05 #2: state-mirror cycle = one OnRenderPullTick after the
        // early-return. Captures GetPose + SetAllPositions + SendStateToHandler.
        bool sm = AgOpenWeb.Models.Diagnostics.DiagFlags.PerfStateMirror;
        long smT0 = sm ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long smA0 = sm ? GC.GetAllocatedBytesForCurrentThread() : 0;

        // Capture the instant this pose is dead-reckoned to, so the web Tick can stamp the
        // pose with ITS compute time (consistent value↔timestamp) rather than the later
        // broadcast time — see VehicleState.RenderPoseMs.
        long renderTs = Clock.Current.GetTimestamp();
        var p = _positionEstimator.GetPose(renderTs);

        var tool = ConfigStore.Tool;
        // The point the tool hangs on, as the tool position has it (RigGeometry).
        var hitch = AgOpenWeb.Models.Tool.RigGeometry.AlongTractor(new Vec3(p.Position.Easting, p.Position.Northing, p.Heading), p.Heading,
            AgOpenWeb.Models.Tool.RigGeometry.AttachX(ConfigStore.Vehicle, tool));
        double hitchE = hitch.Easting, hitchN = hitch.Northing;

        double toolE, toolN, toolHeading;
        if (AgOpenWeb.Models.Tool.RigGeometry.IsRigid(AgOpenWeb.Models.Tool.RigGeometry.Mount(tool)))
        {
            // Fixed tool follows the vehicle exactly — no Torriem state.
            toolHeading = p.Heading;
            toolE = hitchE;
            toolN = hitchN;
        }
        else
        {
            // Trailing / TBT — use the Torriem-tracked headings from the
            // snapshot (stable across ticks) and project the tool back from
            // the freshly-computed hitch (through the TBT tank) along them,
            // with the tool position's own geometry: the pivot distance's
            // sign as AgOpenGPS's, so "Behind" is drawn behind the wheels.
            toolHeading = _toolPositionService.ToolHeading;
            (toolE, toolN) = AgOpenWeb.Models.Tool.RigGeometry.TrailedToolCentre(
                hitchE, hitchN, _toolPositionService.TankPosition.Heading, toolHeading, tool);
        }

        // No lateral offset: the section spans the map draws from this point carry it,
        // as in ToolPositionService (#446).

        _mapService.SetAllPositions(
            p.Position.Easting, p.Position.Northing, p.Heading,
            toolE, toolN, toolHeading,
            ConfigStore.ActualToolWidth, hitchE, hitchN,
            _toolPositionService.IsToolPositionReady);

        // Mirror the dead-reckoned tool/hitch into ApplicationState so the web map
        // projector renders the SAME smooth implement the native map does (the
        // control-loop ToolPositionService snapshot the Tick used to read steps at the
        // GPS rate — freeze-then-jump on the web while the tractor glides).
        var vs = State.Vehicle;
        vs.RenderToolEasting = toolE;
        vs.RenderToolNorthing = toolN;
        vs.RenderToolHeading = toolHeading;
        vs.RenderHitchEasting = hitchE;
        vs.RenderHitchNorthing = hitchN;
        vs.RenderToolReady = _toolPositionService.IsToolPositionReady;
        // Dead-reckoned vehicle pose (same as the native map) for the web Tick.
        vs.RenderEasting = p.Position.Easting;
        vs.RenderNorthing = p.Position.Northing;
        vs.RenderHeadingRad = p.Heading;
        vs.RenderSpeed = p.SpeedMps;
        vs.RenderPoseValid = true;
        // Stopwatch-basis ms of this pose's compute instant (matches the web HostMs basis).
        vs.RenderPoseMs = renderTs * 1000.0 / Clock.Current.Frequency;

        if (sm)
        {
            _smCycleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - smT0;
            _smCycleAllocs += GC.GetAllocatedBytesForCurrentThread() - smA0;
            _smCycleCount++;
            var elapsed = (DateTime.UtcNow - _smWindowStart).TotalSeconds;
            if (elapsed >= 1.0 && _smCycleCount > 0)
            {
                double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
                Console.WriteLine(
                    $"[StateMirror-PERF] cycles={_smCycleCount}"
                    + $" us/cycle={(_smCycleTicks / ticksPerUs / _smCycleCount):F1}"
                    + $" alloc/cycle={(_smCycleAllocs / _smCycleCount)}B"
                    + $" total_us={(long)(_smCycleTicks / ticksPerUs)}"
                    + $" total_alloc={_smCycleAllocs}B"
                    + $" window={elapsed:F2}s");
                _smCycleTicks = 0;
                _smCycleAllocs = 0;
                _smCycleCount = 0;
                _smWindowStart = DateTime.UtcNow;
            }
        }
    }

    // PERF-05 #2: state-mirror accumulators. Gated by DiagFlags.PerfStateMirror.
    private long _smCycleTicks;
    private long _smCycleAllocs;
    private int _smCycleCount;
    private DateTime _smWindowStart = DateTime.UtcNow;
}
