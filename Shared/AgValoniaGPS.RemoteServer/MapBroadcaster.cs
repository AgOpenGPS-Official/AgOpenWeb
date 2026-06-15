// Background broadcaster. The Scene + Tick feed runs on a 10 Hz PeriodicTimer;
// coverage is event-driven off CoverageUpdated (snapshot on (re)init, deltas
// after). PeriodicTimer (not DispatcherTimer) — off the UI thread, no Avalonia dep.
//
// Frames are encoded once (WireCodec) and fanned out to every client by the raw
// WebSocketHub. Newly-connected clients are seeded via SeedProvider below.

using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class MapBroadcaster : IAsyncDisposable
{
    private readonly WebSocketHub _ws;
    private readonly SceneProjector _projector;
    private readonly ICoverageMapService _coverage;
    private readonly CoverageProjector _coverageProjector;
    private readonly CancellationTokenSource _cts = new();

    private long _sceneVersion;
    private long _lastFingerprint = long.MinValue;
    private SceneDto _currentScene;
    private Task? _loop;

    private int _statusTick;
    private volatile bool _coverageInitSent;
    private double _lastCellSize;
    private long _lastCoverageTicks; // throttle the (O(total-cells)) diff scan

    public MapBroadcaster(WebSocketHub ws, SceneProjector projector,
        ICoverageMapService coverage, CoverageProjector coverageProjector)
    {
        _ws = ws;
        _projector = projector;
        _coverage = coverage;
        _coverageProjector = coverageProjector;
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
        };
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
        try
        {
            var init = _coverageProjector.BuildInit();
            if (init is null) return;

            // Re-init on first coverage, a cell-size rescale, or a full reload
            // (field load/clear). A fresh coverageInit makes clients drop + rebuild.
            if (!_coverageInitSent || init.CellSize != _lastCellSize || e.IsFullReload)
            {
                _lastCellSize = init.CellSize;
                _coverageInitSent = true;
                _coverageProjector.ResetSent();
                _ = _ws.BroadcastAsync(WireCodec.EncodeCoverageInit(init));
                if (_coverageProjector.Delta() is { } full) // first Delta after reset = everything
                    _ = _ws.BroadcastAsync(WireCodec.EncodeCoverageCells(full));
                _lastCoverageTicks = Environment.TickCount64;
                return;
            }

            // Steady state: throttle the diff scan (it enumerates all covered
            // cells). 100 ms = the broadcast cadence, so coverage keeps pace with
            // the dead-reckoned tool instead of trailing in ~200 ms chunks.
            long now = Environment.TickCount64;
            if (now - _lastCoverageTicks < 100) return;
            _lastCoverageTicks = now;
            if (_coverageProjector.Delta() is { } delta)
                _ = _ws.BroadcastAsync(WireCodec.EncodeCoverageCells(delta));
        }
        catch { /* tolerate transient races on the coverage layer */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)); // 10 Hz
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var fp = _projector.SceneFingerprint();
                if (fp != _lastFingerprint)
                {
                    _lastFingerprint = fp;
                    _currentScene = _projector.BuildScene(++_sceneVersion);
                    await _ws.BroadcastAsync(WireCodec.EncodeScene(_currentScene), ct).ConfigureAwait(false);
                }

                await _ws.BroadcastAsync(WireCodec.EncodeTick(_projector.BuildTick(_sceneVersion)), ct)
                    .ConfigureAwait(false);

                // Status bar changes slowly (fix/age/modules) — send at ~2 Hz, not
                // every 10 Hz tick. Speed (which updates fast) rides the Tick.
                if (++_statusTick >= 5)
                {
                    _statusTick = 0;
                    await _ws.BroadcastAsync(WireCodec.EncodeStatus(_projector.BuildStatus()), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Tolerate transient read races: ApplicationState collections are
                // mutated on the UI thread; an occasional dropped frame is fine.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
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
