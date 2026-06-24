// Projects the coverage display layer (RGB cells) into the wire contract.
//
// IMPORTANT (alongside mode): the app's own map control consumes the service's
// single-consumer delta drain (GetNewCoverageBitmapCells). The server must NOT
// touch it, or it steals the app's coverage. So here the delta is derived from
// the NON-draining GetCoverageBitmapCells diffed against a server-side "sent"
// bitset. In the END-STATE headless host there is no app renderer, so a real
// host can use the efficient drain directly — this diff is an alongside-only
// workaround. Detection layer never leaves the host (§6).

using AgOpenWeb.Models.Coverage;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteServer;

public sealed class CoverageProjector
{
    private readonly ICoverageMapService _cov;

    private bool[]? _sent;   // width*height; cells already broadcast to all clients
    private int _w, _h;

    public CoverageProjector(ICoverageMapService cov) => _cov = cov;

    public CoverageInitDto? BuildInit()
    {
        if (_cov.DisplayDimensions is not { } dim || _cov.DisplayBoundsWorld is not { } bnd)
            return null;
        return new CoverageInitDto(dim.CellSize, bnd.MinE, bnd.MinN, dim.Width, dim.Height);
    }

    /// <summary>Full current coverage — for seeding a newly-connected client. Non-draining; ignores the sent bitset.</summary>
    public CoverageCellsDto? Snapshot()
    {
        if (_cov.DisplayDimensions is not { } dim || _cov.DisplayBoundsWorld is not { } bnd)
            return null;
        var flat = new List<int>();
        foreach (var (x, y, c) in _cov.GetCoverageBitmapCells(dim.CellSize, bnd.MinE, bnd.MaxE, bnd.MinN, bnd.MaxN))
            Add(flat, x, y, c);
        return flat.Count == 0 ? null : new CoverageCellsDto(flat.ToArray());
    }

    /// <summary>
    /// Cells painted since the last broadcast — diffed from the NON-draining
    /// snapshot against a sent-bitset, so the app's own delta drain is untouched.
    /// </summary>
    public CoverageCellsDto? Delta()
    {
        if (_cov.DisplayDimensions is not { } dim || _cov.DisplayBoundsWorld is not { } bnd)
            return null;

        if (_sent is null || _w != dim.Width || _h != dim.Height)
        {
            _w = dim.Width; _h = dim.Height;
            _sent = new bool[(long)_w * _h];
        }

        var flat = new List<int>();
        foreach (var (x, y, c) in _cov.GetCoverageBitmapCells(dim.CellSize, bnd.MinE, bnd.MaxE, bnd.MinN, bnd.MaxN))
        {
            if (x < 0 || x >= _w || y < 0 || y >= _h) continue;
            long idx = (long)y * _w + x;
            if (_sent[idx]) continue;
            _sent[idx] = true;
            Add(flat, x, y, c);
        }
        return flat.Count == 0 ? null : new CoverageCellsDto(flat.ToArray());
    }

    /// <summary>
    /// Cells painted since the last call — drained from the service's SERVER-dedicated
    /// incremental stream (O(new cells), not an O(whole-grid) scan). Used for steady-state
    /// deltas so coverage keeps pace without the full-scan stall. Independent of the native
    /// map's drain.
    /// </summary>
    public CoverageCellsDto? IncrementalDelta()
    {
        if (_cov.DisplayDimensions is not { } dim) return null;
        var flat = new List<int>();
        foreach (var (x, y, c) in _cov.GetNewCoverageBitmapCellsServer(dim.CellSize))
            Add(flat, x, y, c);
        return flat.Count == 0 ? null : new CoverageCellsDto(flat.ToArray());
    }

    /// <summary>Drain + discard the server incremental stream (after a full Snapshot, so the
    /// next IncrementalDelta carries only post-snapshot cells).</summary>
    public void DiscardIncremental()
    {
        if (_cov.DisplayDimensions is { } dim)
            foreach (var _ in _cov.GetNewCoverageBitmapCellsServer(dim.CellSize)) { }
    }

    /// <summary>Forget what's been sent (on bounds/cell-size change → clients re-init).</summary>
    public void ResetSent() => _sent = null;

    private static void Add(List<int> flat, int x, int y, CoverageColor c)
    {
        flat.Add(x);
        flat.Add(y);
        flat.Add((c.R << 16) | (c.G << 8) | c.B);
    }
}
