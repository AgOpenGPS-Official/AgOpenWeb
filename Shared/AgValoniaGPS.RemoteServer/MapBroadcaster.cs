// Background broadcaster. The Scene + Tick feed runs on a 10 Hz PeriodicTimer;
// coverage is event-driven off CoverageUpdated (snapshot on (re)init, deltas
// after). PeriodicTimer (not DispatcherTimer) — off the UI thread, no Avalonia dep.

using Microsoft.AspNetCore.SignalR;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class MapBroadcaster : IAsyncDisposable
{
    private readonly IHubContext<MapHub> _hub;
    private readonly SceneProjector _projector;
    private readonly ICoverageMapService _coverage;
    private readonly CoverageProjector _coverageProjector;
    private readonly CancellationTokenSource _cts = new();

    private long _sceneVersion;
    private long _lastFingerprint = long.MinValue;
    private SceneDto _currentScene;
    private Task? _loop;

    private volatile bool _coverageInitSent;
    private double _lastCellSize;
    private long _lastCoverageTicks; // throttle the (O(total-cells)) diff scan

    public MapBroadcaster(IHubContext<MapHub> hub, SceneProjector projector,
        ICoverageMapService coverage, CoverageProjector coverageProjector)
    {
        _hub = hub;
        _projector = projector;
        _coverage = coverage;
        _coverageProjector = coverageProjector;
        _currentScene = projector.BuildScene(0);
    }

    /// <summary>Latest scene — sent to each client on connect.</summary>
    public SceneDto CurrentScene() => _currentScene;

    // For the hub to seed a newly-connected client.
    public CoverageInitDto? CurrentCoverageInit() => _coverageProjector.BuildInit();
    public CoverageCellsDto? CoverageSnapshot() => _coverageProjector.Snapshot();

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
                _ = _hub.Clients.All.SendAsync("coverageInit", init);
                if (_coverageProjector.Delta() is { } full) // first Delta after reset = everything
                    _ = _hub.Clients.All.SendAsync("coverageCells", full);
                _lastCoverageTicks = Environment.TickCount64;
                return;
            }

            // Steady state: throttle the diff scan (it enumerates all covered cells).
            long now = Environment.TickCount64;
            if (now - _lastCoverageTicks < 200) return;
            _lastCoverageTicks = now;
            if (_coverageProjector.Delta() is { } delta)
                _ = _hub.Clients.All.SendAsync("coverageCells", delta);
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
                    await _hub.Clients.All.SendAsync("scene", _currentScene, ct).ConfigureAwait(false);
                }

                await _hub.Clients.All.SendAsync("tick", _projector.BuildTick(_sceneVersion), ct).ConfigureAwait(false);
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
