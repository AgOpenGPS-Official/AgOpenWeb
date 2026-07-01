// Background broadcaster. The Scene + Tick feed runs on a 10 Hz PeriodicTimer;
// coverage is event-driven off CoverageUpdated (snapshot on (re)init, deltas
// after). PeriodicTimer (not DispatcherTimer) — off the UI thread, no Avalonia dep.
//
// Frames are encoded once (WireCodec) and fanned out to every client by the raw
// WebSocketHub. Newly-connected clients are seeded via SeedProvider below.

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteServer;

public sealed class MapBroadcaster : IAsyncDisposable
{
    private readonly WebSocketHub _ws;
    private readonly SceneProjector _projector;
    /// <summary>The Scene projector — exposed so the host can attach VM-coupled
    /// providers (e.g. the Field Builder headland-segment list) that ride the Scene frame.</summary>
    public SceneProjector Projector => _projector;
    private readonly ICoverageMapService _coverage;
    private readonly CoverageProjector _coverageProjector;
    private readonly ControlAuthority _authority;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private long _sceneVersion;
    private long _lastFingerprint = long.MinValue;
    private long _lastConfigFp = long.MinValue;
    private long _lastProfilesFp = long.MinValue;
    private long _lastNtripFp = long.MinValue;
    private long _lastFieldOpsFp = long.MinValue;
    private long _lastAgShareFp = long.MinValue;
    private long _lastAppInfoFp = long.MinValue;
    private long _lastFieldToolsFp = long.MinValue;
    private SceneDto _currentScene;
    private Task? _loop;

    private int _statusTick;
    private int _perimTick; // throttles the crisp-edge perimeter broadcast to ~2 Hz
    // Set by the host (App.axaml.cs) — projects the live SteerWizardViewModel to a
    // WizardDto, or null when the remote wizard isn't open. Sent every tick while open
    // (the calibration steps need live phase/angle updates).
    public Func<WizardDto?>? WizardProvider { get; set; }
    // Host-driven Recorded Path read-frame: the panel's UI state lives in the VM, so the
    // host projects it (like the wizard). Read every tick, re-sent on a fingerprint change.
    public Func<RecordedPathDto?>? RecordedPathProvider { get; set; }
    private long _lastRecPathFp = long.MinValue;
    // Host-driven Boundary read-frame (menu list + live drive-around recording state).
    public Func<BoundaryDto?>? BoundaryProvider { get; set; }
    private long _lastBoundaryFp = long.MinValue;
    // Host-supplied persisted web-camera view (pitch radians, zoom px/m). Read once
    // per connection and sent in the seed so the client restores its last tilt+zoom.
    public Func<(double Pitch, double Zoom)?>? ViewPrefsProvider { get; set; }
    private volatile bool _coverageInitSent;
    private double _lastCellSize;
    private volatile bool _coverageReload; // set by OnCoverageUpdated on a full reload

    public MapBroadcaster(WebSocketHub ws, SceneProjector projector,
        ICoverageMapService coverage, CoverageProjector coverageProjector,
        ControlAuthority authority)
    {
        _ws = ws;
        _projector = projector;
        _coverage = coverage;
        _coverageProjector = coverageProjector;
        _authority = authority;
        _currentScene = projector.BuildScene(0);
        _ws.SeedProvider = BuildSeed;
    }

    // Frames sent to a client the instant it connects: the current Scene plus a
    // full coverage snapshot (non-draining, so it doesn't disturb the delta
    // sent-bitset the broadcast loop uses for everyone else).
    private IReadOnlyList<byte[]> BuildSeed()
    {
        var frames = new List<byte[]>
        {
            WireCodec.EncodeScene(_currentScene),
            WireCodec.EncodeStatus(_projector.BuildStatus()),
            WireCodec.EncodeConfig(_projector.BuildConfig()),
            WireCodec.EncodeProfiles(_projector.BuildProfiles()),
            WireCodec.EncodeNtripProfiles(_projector.BuildNtripProfiles()),
            WireCodec.EncodeFieldOps(_projector.BuildFieldOps()),
            WireCodec.EncodeAgShare(_projector.BuildAgShare()),
            WireCodec.EncodeAppInfo(_projector.BuildAppInfo()),
            WireCodec.EncodeFieldTools(_projector.BuildFieldTools()),
            WireCodec.EncodeRecordedPath(RecordedPathProvider?.Invoke() ?? EmptyRecordedPath),
            WireCodec.EncodeBoundary(BoundaryProvider?.Invoke() ?? EmptyBoundary),
            WireCodec.EncodeControlState(_authority.Snapshot()),
        };
        if (ViewPrefsProvider?.Invoke() is { } vp)
            frames.Add(WireCodec.EncodeViewPrefs(vp.Pitch, vp.Zoom));
        if (_coverageProjector.BuildInit() is { } init)
        {
            frames.Add(WireCodec.EncodeCoverageInit(init));
            if (_coverageProjector.Snapshot() is { } snap)
                frames.Add(WireCodec.EncodeCoverageCells(snap));
        }
        return frames;
    }

    public void Start()
    {
        _coverage.CoverageUpdated += OnCoverageUpdated;
        _coverage.BoundsExpanded += OnBoundsExpanded;
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    // Bounds growth / cell-size rescale changes the grid → clients must re-init.
    private void OnBoundsExpanded(object? sender, BoundsExpandedEventArgs e)
    {
        _coverageInitSent = false;
        _coverageProjector.ResetSent();
    }

    private void OnCoverageUpdated(object? sender, CoverageUpdatedEventArgs e)
    {
        // This fires on the 100 Hz control-loop thread (FlushCoverageUpdate). Keep it
        // CHEAP — only flag a full reload. The diff + broadcast happen on the broadcaster
        // thread (RunAsync). The old code ran the O(whole-grid) GetCoverageBitmapCells scan
        // HERE, which stalled real-time control (tool/section) in ~500 ms bursts and made
        // coverage step on the web. Steady-state new cells are drained in RunAsync via the
        // server-dedicated incremental stream.
        if (e.IsFullReload) _coverageReload = true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)); // 10 Hz
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                // Deadman: revoke a holder whose presence heartbeat has lapsed
                // (fires the failsafe). Cheap; runs every 100 ms tick.
                _authority.SweepStale();

                var fp = _projector.SceneFingerprint();
                if (fp != _lastFingerprint)
                {
                    _lastFingerprint = fp;
                    _currentScene = _projector.BuildScene(++_sceneVersion);
                    await _ws.BroadcastAsync(WireCodec.EncodeScene(_currentScene), ct).ConfigureAwait(false);
                }

                // Config read-frame: re-send only when an editable value changes.
                var cfp = _projector.ConfigFingerprint();
                if (cfp != _lastConfigFp)
                {
                    _lastConfigFp = cfp;
                    await _ws.BroadcastAsync(WireCodec.EncodeConfig(_projector.BuildConfig()), ct).ConfigureAwait(false);
                }

                // Profiles read-frame (picker hub): list / active pair / preview changes.
                var pfp = _projector.ProfilesFingerprint();
                if (pfp != _lastProfilesFp)
                {
                    _lastProfilesFp = pfp;
                    await _ws.BroadcastAsync(WireCodec.EncodeProfiles(_projector.BuildProfiles()), ct).ConfigureAwait(false);
                }

                // NTRIP profiles read-frame (Network IO): add / edit / delete / default.
                var nfp = _projector.NtripProfilesFingerprint();
                if (nfp != _lastNtripFp)
                {
                    _lastNtripFp = nfp;
                    await _ws.BroadcastAsync(WireCodec.EncodeNtripProfiles(_projector.BuildNtripProfiles()), ct).ConfigureAwait(false);
                }

                // Field Operations read-frame: field/job add / delete / open.
                var fofp = _projector.FieldOpsFingerprint();
                if (fofp != _lastFieldOpsFp)
                {
                    _lastFieldOpsFp = fofp;
                    await _ws.BroadcastAsync(WireCodec.EncodeFieldOps(_projector.BuildFieldOps()), ct).ConfigureAwait(false);
                }

                // AgShare read-frame: settings change or a cloud action result.
                var afp = _projector.AgShareFingerprint();
                if (afp != _lastAgShareFp)
                {
                    _lastAgShareFp = afp;
                    await _ws.BroadcastAsync(WireCodec.EncodeAgShare(_projector.BuildAgShare()), ct).ConfigureAwait(false);
                }

                // App Info read-frame: language / hotkeys / logs / bug-report status.
                var aifp = _projector.AppInfoFingerprint();
                if (aifp != _lastAppInfoFp)
                {
                    _lastAppInfoFp = aifp;
                    await _ws.BroadcastAsync(WireCodec.EncodeAppInfo(_projector.BuildAppInfo()), ct).ConfigureAwait(false);
                }

                // Field Tools read-frame: import-track field list (field add/delete/open).
                var ftfp = _projector.FieldToolsFingerprint();
                if (ftfp != _lastFieldToolsFp)
                {
                    _lastFieldToolsFp = ftfp;
                    await _ws.BroadcastAsync(WireCodec.EncodeFieldTools(_projector.BuildFieldTools()), ct).ConfigureAwait(false);
                }

                // Recorded Path read-frame (host-driven): rec-file list + record/play state.
                if (RecordedPathProvider?.Invoke() is { } rpDto)
                {
                    var rpfp = RecPathFingerprint(rpDto);
                    if (rpfp != _lastRecPathFp)
                    {
                        _lastRecPathFp = rpfp;
                        await _ws.BroadcastAsync(WireCodec.EncodeRecordedPath(rpDto), ct).ConfigureAwait(false);
                    }
                }

                // Boundary read-frame (host-driven): menu list + drive-around recording.
                if (BoundaryProvider?.Invoke() is { } bDto)
                {
                    var bfp = BoundaryFingerprint(bDto);
                    if (bfp != _lastBoundaryFp)
                    {
                        _lastBoundaryFp = bfp;
                        await _ws.BroadcastAsync(WireCodec.EncodeBoundary(bDto), ct).ConfigureAwait(false);
                    }
                }

                await _ws.BroadcastAsync(WireCodec.EncodeTick(_projector.BuildTick(_sceneVersion)), ct)
                    .ConfigureAwait(false);

                // Coverage — diff + broadcast on THIS (broadcaster) thread, NEVER the 100 Hz
                // control loop. (Re)init on first coverage / cell-size change / full reload
                // sends a full snapshot; steady state drains the server-dedicated incremental
                // stream (O(new cells)). This is the 10 Hz coverage cadence.
                try
                {
                    if (_coverageProjector.BuildInit() is { } cvInit)
                    {
                        if (!_coverageInitSent || cvInit.CellSize != _lastCellSize || _coverageReload)
                        {
                            _coverageReload = false;
                            _coverageInitSent = true;
                            _lastCellSize = cvInit.CellSize;
                            _coverageProjector.ResetSent();
                            await _ws.BroadcastAsync(WireCodec.EncodeCoverageInit(cvInit), ct).ConfigureAwait(false);
                            if (_coverageProjector.Snapshot() is { } full)
                                await _ws.BroadcastAsync(WireCodec.EncodeCoverageCells(full), ct).ConfigureAwait(false);
                            _coverageProjector.DiscardIncremental(); // snapshot already covers everything so far
                        }
                        else if (_coverageProjector.IncrementalDelta() is { } delta)
                        {
                            await _ws.BroadcastAsync(WireCodec.EncodeCoverageCells(delta), ct).ConfigureAwait(false);
                        }
                    }
                }
                catch { /* tolerate transient coverage-layer races */ }

                // Crisp worked-area edge: the vector perimeter (bounded by perimeter length,
                // not area). ~2 Hz — it shifts slowly as passes are laid, and the client just
                // replaces its set. Only once a field/coverage grid exists.
                if (++_perimTick >= 5)
                {
                    _perimTick = 0;
                    if (_coverageInitSent)
                        await _ws.BroadcastAsync(WireCodec.EncodeCoverageEdge(_coverage.GetCoveragePerimeter()), ct)
                            .ConfigureAwait(false);
                }

                // Status bar changes slowly (fix/age/modules) — send at ~2 Hz, not
                // every 10 Hz tick. Speed (which updates fast) rides the Tick.
                if (++_statusTick >= 5)
                {
                    _statusTick = 0;
                    await _ws.BroadcastAsync(WireCodec.EncodeStatus(_projector.BuildStatus()), ct)
                        .ConfigureAwait(false);
                }

                // Steer Wizard (host-driven): while the remote wizard is open, project
                // the live VM state every tick so calibration phases/angles stay live.
                if (WizardProvider?.Invoke() is { } wiz)
                    await _ws.BroadcastAsync(WireCodec.EncodeWizard(wiz), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Tolerate transient read races: ApplicationState collections are
                // mutated on the UI thread; an occasional dropped frame is fine.
            }
        }
    }

    private static readonly RecordedPathDto EmptyRecordedPath =
        new(Array.Empty<string>(), false, false, false, "", "Start", "", Array.Empty<double>());

    private static long RecPathFingerprint(RecordedPathDto r)
    {
        long h = 17;
        foreach (var f in r.RecFiles) h = h * 31 + (f?.GetHashCode() ?? 0);
        h = h * 31 + (r.IsRecording ? 1 : 0);
        h = h * 31 + (r.IsPlaying ? 1 : 0);
        h = h * 31 + (r.HasUnsaved ? 1 : 0);
        h = h * 31 + (r.RecordedPathInfo?.GetHashCode() ?? 0);
        h = h * 31 + (r.ResumeModeLabel?.GetHashCode() ?? 0);
        h = h * 31 + (r.RecordedPathName?.GetHashCode() ?? 0);
        h = h * 31 + r.RecordingPoints.Count; // grows as the path is driven → re-send
        return h;
    }

    private static readonly BoundaryDto EmptyBoundary =
        new(Array.Empty<BoundaryItemDto>(), -1, false, false, false, 0, 0, 0, false, false, false, Array.Empty<double>());

    private static long BoundaryFingerprint(BoundaryDto b)
    {
        long h = 17;
        foreach (var it in b.Items)
        {
            h = h * 31 + it.Index;
            h = h * 31 + (it.BoundaryType?.GetHashCode() ?? 0);
            h = h * 31 + (it.AreaDisplay?.GetHashCode() ?? 0);
            h = h * 31 + (it.DriveThru ? 1 : 0);
            h = h * 31 + (it.Hard ? 1 : 0);
        }
        h = h * 31 + b.SelectedIndex;
        h = h * 31 + (b.PlayerVisible ? 1 : 0);
        h = h * 31 + (b.IsRecording ? 1 : 0) + (b.IsPaused ? 2 : 0);
        h = h * 31 + b.PointCount;
        h = h * 31 + b.AreaHa.GetHashCode();
        h = h * 31 + b.OffsetCm.GetHashCode();
        h = h * 31 + (b.DrawRightSide ? 1 : 0) + (b.DrawAtPivot ? 2 : 0) + (b.SectionControlOn ? 4 : 0);
        h = h * 31 + b.RecordingPoints.Count;
        return h;
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: RemoteServerHost.StopAsync disposes us explicitly, then the
        // web host's DI container disposes us again when _app.DisposeAsync() tears
        // down its singletons. Without this guard the second call hit Cancel() on an
        // already-disposed CTS (ObjectDisposedException). Affects the windowed path
        // too — there it was swallowed by a fire-and-forget StopAsync.
        if (_disposed) return;
        _disposed = true;
        _coverage.CoverageUpdated -= OnCoverageUpdated;
        _coverage.BoundsExpanded -= OnBoundsExpanded;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
