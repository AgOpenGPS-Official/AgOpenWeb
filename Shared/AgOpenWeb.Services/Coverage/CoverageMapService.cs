// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Coverage;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Coverage;

/// <summary>
/// Tracks and manages coverage (worked area) as the tool moves across the field.
///
/// Architecture:
/// - DETECTION LAYER: Bit array with 0.1m cells for O(1) coverage detection (~65MB for 520ha)
/// - DISPLAY LAYER: RGB565 pixel buffer rendered from bit array (handled by SkiaMapControl)
///
/// This replaces the old patch-based system which stored 50,000+ triangle strips
/// and iterated through them for coverage detection (280-330ms per check).
/// The new bitmap approach provides coverage detection in ~0.04ms.
/// </summary>
public class CoverageMapService : ICoverageMapService
{
    private readonly ConfigurationStore _configStore;

    public CoverageMapService(ConfigurationStore configStore)
    {
        _configStore = configStore;
    }

    // ========== DETECTION LAYER ==========
    // 1 bit per cell at fixed 0.1m resolution. Authoritative coverage data.
    // Powers IsPointCovered / GetSegmentCoverage / GetSegmentCoverageMulti.
    private const double BITMAP_CELL_SIZE = 0.1; // meters per cell (10cm = ~4in resolution)

    // Detection bitmap dimensions (at BITMAP_CELL_SIZE)
    private int _bitmapWidth;   // Number of cells in E direction
    private int _bitmapHeight;  // Number of cells in N direction
    private int _bitmapOriginE; // Cell coordinate of bitmap origin (E)
    private int _bitmapOriginN; // Cell coordinate of bitmap origin (N)

    // ========== DISPLAY LAYER ==========
    // RGB565 per cell at DISPLAY resolution (coarser than detection on large
    // fields / low-end devices). Owned by the service; read by the GL renderer
    // via GetDisplayPixels() / ConsumeDirtyRect() and uploaded to a GL texture
    // via glTexSubImage2D. The 2D control's old WriteableBitmap is gone.
    private ushort[]? _displayPixels;
    private int _displayWidth;
    private int _displayHeight;
    private double _displayCellSize = BITMAP_CELL_SIZE;

    // Absolute display-pixel index of local pixel (0,0): pixel X covers world
    // [X * cell, (X + 1) * cell). Snapped to the world grid so a display pixel sits on the
    // same ground whatever the field bounds, which is what lets display tiles be saved by
    // absolute key (see CoverageTileStore).
    private int _displayOriginX;
    private int _displayOriginY;

    // Display-resolution dirty rect since last ConsumeDirtyRect(), inclusive
    // bounds in local display coords. Maintained under _coverageLock.
    private int _dirtyMinX, _dirtyMinY, _dirtyMaxX, _dirtyMaxY;
    private bool _dirtyValid;

    // Cap detection-resolution bitmap at ~25M cells before scaling up display
    // resolution.
    private const long MAX_DISPLAY_PIXELS = 25_000_000;

    // Per-zone cell counters for acreage calculation (zone index -> cell count)
    private readonly Dictionary<int, long> _cellCountPerZone = new();

    // Bit array for fast detection - 1 bit per cell, fixed size regardless of coverage
    // 582ha @ 0.1m = 582M cells / 8 = 72MB (much better than HashSet at high coverage)
    private byte[]? _detectionBits;

    // Track newly added cells since last GetNewCoverageBitmapCells call
    // Still use HashSet for new cells (small, cleared frequently)
    private readonly HashSet<(int CellE, int CellN, int Zone)> _newCells = new();

    // Reusable buffers for GetNewCoverageBitmapCells to avoid allocations
    private readonly List<(int CellX, int CellY, CoverageColor Color)> _newCellsResult = new();
    private readonly HashSet<(int, int)> _newCellsDedup = new();

    // Parallel new-cell stream for a SECOND independent consumer (the remote/web
    // server's CoverageProjector). GetNewCoverageBitmapCells drains _newCells for the
    // native map control; the server needs its own drain so the two don't steal cells
    // from each other. Without this the server fell back to an O(whole-grid) scan that,
    // run on the 100 Hz control-loop thread, stalled real-time control in ~500 ms bursts.
    private readonly HashSet<(int CellE, int CellN, int Zone)> _newCellsServer = new();
    private readonly List<(int CellX, int CellY, CoverageColor Color)> _newCellsServerResult = new();
    private readonly HashSet<(int, int)> _newCellsServerDedup = new();

    // Track bounds of coverage for reporting
    private int _minCellE = int.MaxValue;
    private int _maxCellE = int.MinValue;
    private int _minCellN = int.MaxValue;
    private int _maxCellN = int.MinValue;
    private bool _boundsValid;

    // Fixed field bounds for stable bitmap coordinates (set when field is loaded)
    private double _fieldMinE;
    private double _fieldMaxE;
    private double _fieldMinN;
    private double _fieldMaxN;
    private bool _fieldBoundsSet;

    // ========== TRACKING STATE ==========
    // Thread-safety lock — coverage methods may be called from background thread
    // (simulator tick) while UI thread reads state via events
    private readonly object _coverageLock = new();

    // Track which sections are actively mapping
    private readonly HashSet<int> _activeSections = new();

    // Track last edges per section for area calculation and bitmap rasterization
    private readonly Dictionary<int, ((double E, double N) Left, (double E, double N) Right)> _lastEdgesPerSection = new();

    // Area totals (calculated incrementally)
    private double _totalWorkedArea;
    private double _totalWorkedAreaUser;

    // Dirty flag to track if coverage has changed since last flush
    private bool _coverageDirty;
    private double _pendingAreaAdded;

    public double TotalWorkedArea => _totalWorkedArea;
    public double TotalWorkedAreaUser => _totalWorkedAreaUser;
    public int PatchCount => (int)GetTotalCellCount(); // Total covered cells across all zones
    public bool IsAnyZoneMapping => _activeSections.Count > 0;
    public int ActiveSectionCount => _activeSections.Count;

    public event EventHandler<CoverageUpdatedEventArgs>? CoverageUpdated;

    // Detection-layer dimensions for coordinate calculations (0.1m cells).
    public (int Width, int Height, int OriginE, int OriginN)? BitmapDimensions =>
        _fieldBoundsSet ? (_bitmapWidth, _bitmapHeight, _bitmapOriginE, _bitmapOriginN) : null;

    /// <summary>
    /// Display-layer pixel buffer (RGB565, row-major, width-major). Returns null
    /// if field bounds aren't set yet. The GL renderer uses this for first-frame
    /// texture upload; incremental updates go through <see cref="ConsumeDirtyRect"/>.
    /// </summary>
    public ushort[]? GetDisplayPixels() => _displayPixels;

    /// <summary>
    /// Display-layer dimensions (variable per field size + DisplayResolutionMultiplier).
    /// Always coarser-or-equal to detection. Null if field bounds aren't set.
    /// </summary>
    public (int Width, int Height, double CellSize)? DisplayDimensions =>
        _fieldBoundsSet ? (_displayWidth, _displayHeight, _displayCellSize) : null;

    public (double MinE, double MinN, double MaxE, double MaxN)? DisplayBoundsWorld =>
        _fieldBoundsSet
            ? (_displayOriginX * _displayCellSize, _displayOriginY * _displayCellSize,
               (_displayOriginX + _displayWidth) * _displayCellSize,
               (_displayOriginY + _displayHeight) * _displayCellSize)
            : null;

    /// <summary>
    /// Consume the dirty rect since the last call. Returns (x, y, w, h) in display
    /// pixel coordinates, or null if nothing changed. Resets after return so the
    /// next call sees only new writes. Lock-protected.
    /// </summary>
    public (int X, int Y, int Width, int Height)? ConsumeDirtyRect()
    {
        lock (_coverageLock)
        {
            if (!_dirtyValid) return null;
            var result = (_dirtyMinX, _dirtyMinY,
                          _dirtyMaxX - _dirtyMinX + 1,
                          _dirtyMaxY - _dirtyMinY + 1);
            _dirtyValid = false;
            return result;
        }
    }

    // ---- Vector perimeter (server-fed crisp edge) ----
    // The swept tool-edge ribbon per mapping run, as distance-gated (left,right) pairs.
    // GetCoveragePerimeter() walks these and keeps only vertices whose OUTWARD side is
    // unworked — the true worked-area boundary — which self-prunes as adjacent passes fill
    // in, so it stays bounded by perimeter length, not worked area. Live-only: a reloaded
    // field's coverage has no ribbons and falls back to the alpha-AA feather until re-driven.
    // All of this is mutated/read under _coverageLock.
    private sealed class EdgeRun { public readonly List<Vec2> Left = new(); public readonly List<Vec2> Right = new(); }
    private readonly Dictionary<int, EdgeRun> _edgeRunByZone = new();
    private readonly List<EdgeRun> _edgeRuns = new();
    private readonly Dictionary<int, (double E, double N)> _edgeGateLast = new();
    private int _edgePointCount;
    private const double EDGE_GATE = 2.0;          // metres between committed edge vertex pairs
    private const int EDGE_MAX_POINTS = 200_000;   // safety cap on total accumulated points

    private void ClearEdges()
    {
        _edgeRunByZone.Clear();
        _edgeRuns.Clear();
        _edgeGateLast.Clear();
        _edgePointCount = 0;
    }

    public void StartMapping(int zoneIndex, Vec2 leftEdge, Vec2 rightEdge, CoverageColor? color = null)
    {
        lock (_coverageLock)
        {
            if (_activeSections.Contains(zoneIndex))
                return;

            _activeSections.Add(zoneIndex);
            _lastEdgesPerSection[zoneIndex] = (
                (leftEdge.Easting, leftEdge.Northing),
                (rightEdge.Easting, rightEdge.Northing));

            // Open a fresh edge ribbon for this run, seeded with the first pair.
            if (_edgePointCount < EDGE_MAX_POINTS)
            {
                var run = new EdgeRun();
                run.Left.Add(leftEdge); run.Right.Add(rightEdge);
                _edgeRunByZone[zoneIndex] = run;
                _edgeRuns.Add(run);
                _edgeGateLast[zoneIndex] =
                    ((leftEdge.Easting + rightEdge.Easting) * 0.5, (leftEdge.Northing + rightEdge.Northing) * 0.5);
                _edgePointCount++;
            }
        }
    }

    public void StopMapping(int zoneIndex)
    {
        lock (_coverageLock)
        {
            if (!_activeSections.Contains(zoneIndex))
                return;

            _activeSections.Remove(zoneIndex);
            _lastEdgesPerSection.Remove(zoneIndex);
            // Close the edge ribbon (it stays in _edgeRuns for the perimeter walk).
            _edgeRunByZone.Remove(zoneIndex);
            _edgeGateLast.Remove(zoneIndex);
        }
    }

    public event EventHandler<BoundsExpandedEventArgs>? BoundsExpanded;

    public void AddCoveragePoint(int zoneIndex, Vec2 leftEdge, Vec2 rightEdge)
    {
        lock (_coverageLock)
        {
            if (!_activeSections.Contains(zoneIndex))
                return;

            // PERF-05 #5. Cycle = one AddCoveragePoint that gets past the
            // activeSections gate (so unmapped sections don't pollute counts).
            // Marker: .perf_coverage. Wraps rasterization + pixel paint +
            // bounds expand. Up to N_sections × GPS_Hz calls/sec.
            if (!AgOpenWeb.Models.Diagnostics.DiagFlags.PerfCoverage)
            {
                AddCoveragePointCore(zoneIndex, leftEdge, rightEdge);
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            try { AddCoveragePointCore(zoneIndex, leftEdge, rightEdge); }
            finally
            {
                _perfCovTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _perfCovAllocs += GC.GetAllocatedBytesForCurrentThread() - a0;
                _perfCovCount++;
                var elapsed = (DateTime.UtcNow - _perfCovWindowStart).TotalSeconds;
                if (elapsed >= 1.0 && _perfCovCount > 0)
                {
                    double ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
                    Console.WriteLine(
                        $"[Coverage-PERF] cycles={_perfCovCount}"
                        + $" us/cycle={(_perfCovTicks / ticksPerUs / _perfCovCount):F1}"
                        + $" alloc/cycle={(_perfCovAllocs / _perfCovCount)}B"
                        + $" total_us={(long)(_perfCovTicks / ticksPerUs)}"
                        + $" total_alloc={_perfCovAllocs}B"
                        + $" window={elapsed:F2}s");
                    _perfCovTicks = 0;
                    _perfCovAllocs = 0;
                    _perfCovCount = 0;
                    _perfCovWindowStart = DateTime.UtcNow;
                }
            }
        } // lock
    }

    // PERF-05 #5 accumulators. Gated by DiagFlags.PerfCoverage. Mutated under
    // _coverageLock (same lock as AddCoveragePoint body).
    private long _perfCovTicks;
    private long _perfCovAllocs;
    private int _perfCovCount;
    private DateTime _perfCovWindowStart = DateTime.UtcNow;

    /// <summary>
    /// Core body of AddCoveragePoint. Caller holds _coverageLock and has
    /// already verified the section is active.
    /// </summary>
    private void AddCoveragePointCore(int zoneIndex, Vec2 leftEdge, Vec2 rightEdge)
    {
        // Check if we need to expand bounds (auto-initialized, vehicle near edge)
        if (_fieldBoundsSet)
        {
            CheckAndExpandBounds(leftEdge, rightEdge);
        }

        // Get last edges for this section (used for bitmap rasterization and area calc)
        if (!_lastEdgesPerSection.TryGetValue(zoneIndex, out var lastEdges))
        {
            // First point - just store edges
            _lastEdgesPerSection[zoneIndex] = (
                (leftEdge.Easting, leftEdge.Northing),
                (rightEdge.Easting, rightEdge.Northing));
            return;
        }

        // Rasterize the quad to the coverage bitmap for O(1) detection
        RasterizeQuadToBitmap(zoneIndex, leftEdge, rightEdge);

        // Vector perimeter: append this swept edge pair if the tool moved past the gate.
        AccumulateEdge(zoneIndex, leftEdge, rightEdge);

        // Calculate area of the quad (two triangles)
        double area = CalculateQuadArea(
            lastEdges.Left, lastEdges.Right,
            (rightEdge.Easting, rightEdge.Northing),
            (leftEdge.Easting, leftEdge.Northing));

        _totalWorkedArea += area;
        _totalWorkedAreaUser += area;
        _pendingAreaAdded += area;
        _coverageDirty = true;

        // Update last edges for next quad
        _lastEdgesPerSection[zoneIndex] = (
            (leftEdge.Easting, leftEdge.Northing),
            (rightEdge.Easting, rightEdge.Northing));
    }

    // Append a swept edge pair to the zone's ribbon, distance-gated. Caller holds _coverageLock.
    // Only reached for an actively-mapping zone (AddCoveragePoint gates on _activeSections).
    private void AccumulateEdge(int zoneIndex, Vec2 leftEdge, Vec2 rightEdge)
    {
        if (_edgePointCount >= EDGE_MAX_POINTS) return;
        if (!_edgeRunByZone.TryGetValue(zoneIndex, out var run))
        {
            // The zone is actively mapping but has no open ribbon. This happens when ClearEdges
            // ran while the section stayed on — notably the no-boundary path, where the coverage
            // grid auto-inits (SetFieldBounds → ClearEdges) AFTER mapping started, wiping the
            // ribbon that StartMapping opened. StartMapping won't re-open it (it only fires on the
            // off→on edge), so the crisp worked-area perimeter would silently never appear for the
            // rest of the run. Re-open the ribbon here so the perimeter keeps building.
            run = new EdgeRun();
            run.Left.Add(leftEdge); run.Right.Add(rightEdge);
            _edgeRunByZone[zoneIndex] = run;
            _edgeRuns.Add(run);
            _edgeGateLast[zoneIndex] = ((leftEdge.Easting + rightEdge.Easting) * 0.5,
                                        (leftEdge.Northing + rightEdge.Northing) * 0.5);
            _edgePointCount++;
            return;
        }
        double cx = (leftEdge.Easting + rightEdge.Easting) * 0.5;
        double cy = (leftEdge.Northing + rightEdge.Northing) * 0.5;
        if (_edgeGateLast.TryGetValue(zoneIndex, out var last))
        {
            double dx = cx - last.E, dy = cy - last.N;
            if (dx * dx + dy * dy < EDGE_GATE * EDGE_GATE) return;
        }
        run.Left.Add(leftEdge); run.Right.Add(rightEdge);
        _edgeGateLast[zoneIndex] = (cx, cy);
        _edgePointCount++;
    }

    /// <summary>
    /// Worked-area perimeter as polylines for the crisp vector edge. Walks the accumulated
    /// swept tool-edge ribbons and keeps only vertices whose OUTWARD side is unworked (probed
    /// against the detection grid), so interior pass-to-pass seams are dropped and only the
    /// true boundary remains — bounded by perimeter length, not worked area. Live-only (a
    /// reloaded field has no ribbons). Cheap O(1) probes; safe to call off the control thread.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Vec2>> GetCoveragePerimeter()
    {
        var result = new List<IReadOnlyList<Vec2>>();
        lock (_coverageLock)
        {
            foreach (var run in _edgeRuns)
            {
                EmitPerimeterSide(run.Left, run.Right, result);
                EmitPerimeterSide(run.Right, run.Left, result);
            }
        }
        return result;
    }

    // Emit contiguous runs of `edge` vertices whose outward side (away from `other`) is
    // unworked — those are on the worked-area boundary. 0.2 m probe ≈ 2 detection cells out.
    private void EmitPerimeterSide(List<Vec2> edge, List<Vec2> other, List<IReadOnlyList<Vec2>> result)
    {
        List<Vec2>? cur = null;
        int n = Math.Min(edge.Count, other.Count);
        for (int i = 0; i < n; i++)
        {
            double ox = edge[i].Easting - other[i].Easting;
            double oy = edge[i].Northing - other[i].Northing;
            double len = Math.Sqrt(ox * ox + oy * oy);
            bool perim = len > 1e-6 &&
                !IsPointCovered(edge[i].Easting + ox / len * 0.2, edge[i].Northing + oy / len * 0.2);
            if (perim) { (cur ??= new List<Vec2>()).Add(edge[i]); }
            else if (cur != null) { if (cur.Count >= 2) result.Add(cur); cur = null; }
        }
        if (cur != null && cur.Count >= 2) result.Add(cur);
    }

    /// <summary>
    /// Calculate the area of a quad using the shoelace formula.
    /// Points are in order: p0 -> p1 -> p2 -> p3 -> back to p0
    /// </summary>
    private static double CalculateQuadArea(
        (double E, double N) p0, (double E, double N) p1,
        (double E, double N) p2, (double E, double N) p3)
    {
        // Shoelace formula for quadrilateral area
        double area = Math.Abs(
            (p0.E * p1.N - p1.E * p0.N) +
            (p1.E * p2.N - p2.E * p1.N) +
            (p2.E * p3.N - p3.E * p2.N) +
            (p3.E * p0.N - p0.E * p3.N)) / 2.0;
        return area;
    }


    /// <summary>
    /// Rasterize a quad (from previous to current edges) to the coverage bitmap.
    /// This provides O(1) coverage lookup similar to AgOpenGPS GPU pixel readback.
    /// </summary>
    private void RasterizeQuadToBitmap(int zoneIndex, Vec2 leftEdge, Vec2 rightEdge)
    {
        var currLeft = (E: leftEdge.Easting, N: leftEdge.Northing);
        var currRight = (E: rightEdge.Easting, N: rightEdge.Northing);

        // Need previous edges to form a quad
        if (!_lastEdgesPerSection.TryGetValue(zoneIndex, out var lastEdges))
        {
            _lastEdgesPerSection[zoneIndex] = (currLeft, currRight);
            return;
        }

        // Form quad: prevLeft -> prevRight -> currRight -> currLeft
        var p0 = lastEdges.Left;
        var p1 = lastEdges.Right;
        var p2 = currRight;
        var p3 = currLeft;

        // Find bounding box
        double minE = Math.Min(Math.Min(p0.E, p1.E), Math.Min(p2.E, p3.E));
        double maxE = Math.Max(Math.Max(p0.E, p1.E), Math.Max(p2.E, p3.E));
        double minN = Math.Min(Math.Min(p0.N, p1.N), Math.Min(p2.N, p3.N));
        double maxN = Math.Max(Math.Max(p0.N, p1.N), Math.Max(p2.N, p3.N));

        // Convert to cell coordinates
        int cellMinE = (int)Math.Floor(minE / BITMAP_CELL_SIZE);
        int cellMaxE = (int)Math.Floor(maxE / BITMAP_CELL_SIZE);
        int cellMinN = (int)Math.Floor(minN / BITMAP_CELL_SIZE);
        int cellMaxN = (int)Math.Floor(maxN / BITMAP_CELL_SIZE);

        // Mark all cells in bounding box that are inside the quad
        for (int ce = cellMinE; ce <= cellMaxE; ce++)
        {
            for (int cn = cellMinN; cn <= cellMaxN; cn++)
            {
                // Cell center
                double cellCenterE = (ce + 0.5) * BITMAP_CELL_SIZE;
                double cellCenterN = (cn + 0.5) * BITMAP_CELL_SIZE;

                // Two-triangle decomposition: handles self-intersecting bowties
                // that occur when inner section edges reverse during sharp turns.
                if (IsPointInTriangle(cellCenterE, cellCenterN, p0, p1, p2)
                    || IsPointInTriangle(cellCenterE, cellCenterN, p0, p2, p3))
                {
                    if (MarkCellCovered(ce, cn, zoneIndex))
                    {
                        // New cell - track it for incremental display update
                        _newCells.Add((ce, cn, zoneIndex));
                        UpdateBounds(ce, cn);
                    }
                }
            }
        }

        // Update last edges for next quad
        _lastEdgesPerSection[zoneIndex] = (currLeft, currRight);
    }

    /// <summary>
    /// Check if a point is inside a quad (4-point polygon).
    /// Uses cross product sign test - point is inside if all cross products have same sign.
    /// </summary>
    private static bool IsPointInQuad(double px, double py,
        (double E, double N) p0, (double E, double N) p1,
        (double E, double N) p2, (double E, double N) p3)
    {
        // Check each edge - point should be on same side of all edges
        double d0 = CrossProductSign(px, py, p0.E, p0.N, p1.E, p1.N);
        double d1 = CrossProductSign(px, py, p1.E, p1.N, p2.E, p2.N);
        double d2 = CrossProductSign(px, py, p2.E, p2.N, p3.E, p3.N);
        double d3 = CrossProductSign(px, py, p3.E, p3.N, p0.E, p0.N);

        bool hasNeg = (d0 < 0) || (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool hasPos = (d0 > 0) || (d1 > 0) || (d2 > 0) || (d3 > 0);

        // Inside if all same sign (all positive or all negative)
        return !(hasNeg && hasPos);
    }

    /// <summary>
    /// Check if a point is inside a triangle using cross product sign test.
    /// Unlike a quad, a triangle can never self-intersect.
    /// </summary>
    private static bool IsPointInTriangle(double px, double py,
        (double E, double N) a, (double E, double N) b, (double E, double N) c)
    {
        double d0 = CrossProductSign(px, py, a.E, a.N, b.E, b.N);
        double d1 = CrossProductSign(px, py, b.E, b.N, c.E, c.N);
        double d2 = CrossProductSign(px, py, c.E, c.N, a.E, a.N);

        bool hasNeg = (d0 < 0) || (d1 < 0) || (d2 < 0);
        bool hasPos = (d0 > 0) || (d1 > 0) || (d2 > 0);

        return !(hasNeg && hasPos);
    }

    /// <summary>
    /// Cross product sign for point vs edge.
    /// </summary>
    private static double CrossProductSign(double px, double py,
        double x1, double y1, double x2, double y2)
    {
        return (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2);
    }

    /// <summary>
    /// Fire the CoverageUpdated event if coverage has changed since last flush.
    /// Call this once per GPS update cycle to avoid firing 16 events for 16 sections.
    /// </summary>
    public void FlushCoverageUpdate()
    {
        CoverageUpdatedEventArgs? args = null;
        lock (_coverageLock)
        {
            if (!_coverageDirty) return;
            args = new CoverageUpdatedEventArgs
            {
                TotalArea = _totalWorkedArea,
                PatchCount = (int)GetTotalCellCount(),
                AreaAdded = _pendingAreaAdded
            };
            _coverageDirty = false;
            _pendingAreaAdded = 0;
        } // lock

        // Fire event outside lock to avoid deadlocks
        if (args != null)
            CoverageUpdated?.Invoke(this, args);
    }

    public bool IsZoneMapping(int zoneIndex)
    {
        lock (_coverageLock)
            return _activeSections.Contains(zoneIndex);
    }

    public bool IsPointCovered(double easting, double northing)
    {
        // O(1) bit array lookup - convert to cell coordinates and check if covered
        int cellE = (int)Math.Floor(easting / BITMAP_CELL_SIZE);
        int cellN = (int)Math.Floor(northing / BITMAP_CELL_SIZE);
        return IsCellCovered(cellE, cellN);
    }

    public CoverageResult GetSegmentCoverage(Vec2 sectionCenter, double heading, double halfWidth, double lookAheadDistance = 0)
    {
        // Adjust center for look-ahead
        Vec2 checkCenter = lookAheadDistance == 0
            ? sectionCenter
            : new Vec2(
                sectionCenter.Easting + Math.Sin(heading) * lookAheadDistance,
                sectionCenter.Northing + Math.Cos(heading) * lookAheadDistance);

        return GetSegmentCoverageBitmap(checkCenter, heading, halfWidth);
    }

    /// <summary>
    /// Check segment coverage using bitmap - O(width/cellSize) lookups.
    /// Sample points along the section width perpendicular to heading.
    /// </summary>
    private CoverageResult GetSegmentCoverageBitmap(Vec2 center, double heading, double halfWidth)
    {
        // Perpendicular direction (90 degrees to heading)
        double perpSin = Math.Cos(heading);  // sin(heading + 90) = cos(heading)
        double perpCos = -Math.Sin(heading); // cos(heading + 90) = -sin(heading)

        // Sample points along the section width at cell-size intervals
        int numSamples = Math.Max(3, (int)Math.Ceiling(halfWidth * 2 / BITMAP_CELL_SIZE));
        double step = halfWidth * 2 / (numSamples - 1);

        int coveredCount = 0;
        for (int i = 0; i < numSamples; i++)
        {
            double offset = -halfWidth + i * step;
            double sampleE = center.Easting + perpSin * offset;
            double sampleN = center.Northing + perpCos * offset;

            int cellE = (int)Math.Floor(sampleE / BITMAP_CELL_SIZE);
            int cellN = (int)Math.Floor(sampleN / BITMAP_CELL_SIZE);

            if (IsCellCovered(cellE, cellN))
                coveredCount++;
        }

        double coveragePercent = (double)coveredCount / numSamples;
        double uncoveredLength = (numSamples - coveredCount) * (halfWidth * 2 / numSamples);
        return new CoverageResult(
            coveragePercent,
            coveredCount > 0,           // HasAnyOverlap
            coveragePercent >= 0.95,    // IsFullyCovered (95%+ threshold)
            uncoveredLength);
    }

    public (CoverageResult Current, CoverageResult LookOn, CoverageResult LookOff) GetSegmentCoverageMulti(
        Vec2 sectionCenter, double heading, double halfWidth,
        double lookOnDistance, double lookOffDistance)
    {
        // Calculate all three check centers
        Vec2 currentCenter = sectionCenter;
        Vec2 lookOnCenter = new Vec2(
            sectionCenter.Easting + Math.Sin(heading) * lookOnDistance,
            sectionCenter.Northing + Math.Cos(heading) * lookOnDistance);
        Vec2 lookOffCenter = new Vec2(
            sectionCenter.Easting + Math.Sin(heading) * lookOffDistance,
            sectionCenter.Northing + Math.Cos(heading) * lookOffDistance);

        // Use bitmap-based coverage detection - O(width/cellSize) per position
        return (
            GetSegmentCoverageBitmap(currentCenter, heading, halfWidth),
            GetSegmentCoverageBitmap(lookOnCenter, heading, halfWidth),
            GetSegmentCoverageBitmap(lookOffCenter, heading, halfWidth)
        );
    }

    /// <summary>
    /// Update coverage bounds when a new cell is added.
    /// </summary>
    private void UpdateBounds(int cellE, int cellN)
    {
        if (cellE < _minCellE) _minCellE = cellE;
        if (cellE > _maxCellE) _maxCellE = cellE;
        if (cellN < _minCellN) _minCellN = cellN;
        if (cellN > _maxCellN) _maxCellN = cellN;
        _boundsValid = true;
    }

    /// <summary>
    /// Mark a cell as covered. Returns true if cell was newly covered.
    /// Uses bit array for fast detection; paints into the display pixel buffer
    /// (one display pixel may receive paint from many detection cells — the
    /// last writer wins, which matches the prior 2D-control behavior).
    /// </summary>
    private bool MarkCellCovered(int cellE, int cellN, int zone)
    {
        if (_detectionBits == null || !_fieldBoundsSet)
            return false;

        // Convert to local coordinates
        int localE = cellE - _bitmapOriginE;
        int localN = cellN - _bitmapOriginN;

        // Bounds check
        if (localE < 0 || localE >= _bitmapWidth || localN < 0 || localN >= _bitmapHeight)
            return false;

        // Calculate bit position in detection array
        long bitIndex = (long)localN * _bitmapWidth + localE;
        int byteIndex = (int)(bitIndex / 8);
        int bitOffset = (int)(bitIndex % 8);
        byte mask = (byte)(1 << bitOffset);

        // Check if already covered using bit array (O(1), no bitmap lock)
        bool wasAlreadyCovered = (_detectionBits[byteIndex] & mask) != 0;

        // Always paint the display pixel — even if the detection bit was set
        // by an earlier section pass, we want the most recent section color to
        // win. (The 2D control behaved the same way.)
        PaintDisplayPixel(cellE, cellN, zone);

        if (wasAlreadyCovered)
            return false;

        // Mark as covered in detection array
        _detectionBits[byteIndex] |= mask;

        long tileKey = CoverageTileStore.Key(cellE >> CoverageTileStore.DetectTileShift, cellN >> CoverageTileStore.DetectTileShift);
        if (tileKey != _lastDetectTileKey)
        {
            _lastDetectTileKey = tileKey;
            _dirtyDetectTiles.Add(tileKey);
        }

        // Track for batched write by map control (via GetNewCoverageBitmapCells)
        _newCells.Add((cellE, cellN, zone));
        _newCellsServer.Add((cellE, cellN, zone)); // parallel drain for the web server

        // Update per-zone counter
        if (!_cellCountPerZone.TryGetValue(zone, out long count))
            count = 0;
        _cellCountPerZone[zone] = count + 1;

        return true;
    }

    /// <summary>
    /// Map a detection cell (at 0.1m) to its display-resolution pixel and
    /// paint the zone's RGB565 color there. Updates the dirty rect.
    /// Caller must hold _coverageLock.
    /// </summary>
    private void PaintDisplayPixel(int cellE, int cellN, int zone)
    {
        if (_displayPixels == null) return;

        // World-coord center of this detection cell
        double worldX = (cellE + 0.5) * BITMAP_CELL_SIZE;
        double worldY = (cellN + 0.5) * BITMAP_CELL_SIZE;

        // Map to display-resolution pixel coords
        int px = (int)Math.Floor(worldX / _displayCellSize);
        int py = (int)Math.Floor(worldY / _displayCellSize);
        int dx = px - _displayOriginX;
        int dy = py - _displayOriginY;
        if (dx < 0 || dx >= _displayWidth || dy < 0 || dy >= _displayHeight) return;

        ushort rgb565 = GetZoneColorRgb565(zone);
        _displayPixels[(long)dy * _displayWidth + dx] = rgb565;
        ExpandDirty(dx, dy);

        long tileKey = CoverageTileStore.Key(px >> CoverageTileStore.DisplayTileShift, py >> CoverageTileStore.DisplayTileShift);
        if (tileKey != _lastDisplayTileKey)
        {
            _lastDisplayTileKey = tileKey;
            _dirtyDisplayTiles.Add(tileKey);
        }
    }

    /// <summary>Expand the dirty rect to include the given display pixel. Caller holds lock.</summary>
    private void ExpandDirty(int x, int y)
    {
        if (!_dirtyValid)
        {
            _dirtyMinX = _dirtyMaxX = x;
            _dirtyMinY = _dirtyMaxY = y;
            _dirtyValid = true;
            return;
        }
        if (x < _dirtyMinX) _dirtyMinX = x;
        if (x > _dirtyMaxX) _dirtyMaxX = x;
        if (y < _dirtyMinY) _dirtyMinY = y;
        if (y > _dirtyMaxY) _dirtyMaxY = y;
    }

    /// <summary>Mark the entire display buffer dirty (used after load/expand). Caller holds lock.</summary>
    private void ExpandDirtyAll()
    {
        _dirtyMinX = 0;
        _dirtyMinY = 0;
        _dirtyMaxX = Math.Max(0, _displayWidth - 1);
        _dirtyMaxY = Math.Max(0, _displayHeight - 1);
        _dirtyValid = _displayWidth > 0 && _displayHeight > 0;
    }

    /// <summary>Compute RGB565 for a zone using the same policy as GetZoneColor (RGB888 → 565).</summary>
    private ushort GetZoneColorRgb565(int zoneIndex)
    {
        var tool = _configStore.Tool;
        uint rgb888 = tool.IsMultiColoredSections
            ? tool.GetSectionColor(zoneIndex)
            : tool.SingleCoverageColor;
        return Rgb888ToRgb565(rgb888);
    }

    /// <summary>
    /// Check if a cell is covered.
    /// </summary>
    private bool IsCellCovered(int cellE, int cellN)
    {
        if (_detectionBits == null || !_fieldBoundsSet)
            return false;

        // Convert to local coordinates
        int localE = cellE - _bitmapOriginE;
        int localN = cellN - _bitmapOriginN;

        // Bounds check
        if (localE < 0 || localE >= _bitmapWidth || localN < 0 || localN >= _bitmapHeight)
            return false;

        // Calculate bit position
        long bitIndex = (long)localN * _bitmapWidth + localE;
        int byteIndex = (int)(bitIndex / 8);
        int bitOffset = (int)(bitIndex % 8);
        byte mask = (byte)(1 << bitOffset);

        return (_detectionBits[byteIndex] & mask) != 0;
    }

    /// <summary>
    /// Coverage fraction (0..255) of a display cell: the share of its underlying 0.1 m
    /// detection cells that are covered. 255 = fully covered (interior, opaque); a partial
    /// value is an edge cell the client draws with that alpha, so the boundary feathers
    /// instead of stair-stepping. Reads detection bits lock-free (same as the emit scans);
    /// a race only yields a slightly stale fraction, which is harmless.
    /// </summary>
    public int GetDisplayCellAlpha255(int displayX, int displayY)
    {
        if (_detectionBits == null || !_fieldBoundsSet) return 255;
        int span = (int)Math.Round(_displayCellSize / BITMAP_CELL_SIZE);
        if (span < 1) span = 1;
        // Display cell's world origin → its first underlying detection cell.
        int ce0 = (int)Math.Floor((_displayOriginX + displayX) * _displayCellSize / BITMAP_CELL_SIZE);
        int cn0 = (int)Math.Floor((_displayOriginY + displayY) * _displayCellSize / BITMAP_CELL_SIZE);
        int covered = 0, total = span * span;
        for (int j = 0; j < span; j++)
            for (int i = 0; i < span; i++)
                if (IsCellCovered(ce0 + i, cn0 + j)) covered++;
        // Near-full cells snap to opaque: the rasterizer center-samples, so interior cells
        // occasionally miss a stray detection cell at quad seams. A literal fraction would
        // let the (often dark) map fleck through those — the old binary "any → opaque" rule
        // hid it. Only cells well below FULL (genuine worked-area edge) feather.
        const double FULL = 0.75;
        double frac = (double)covered / total;
        return frac >= FULL ? 255 : (int)(frac / FULL * 255.0);
    }

    /// <summary>
    /// Mark a rectangular area as covered. Useful for tests that need pre-applied coverage
    /// without driving through the area.
    /// </summary>
    /// <param name="minE">Minimum easting in meters</param>
    /// <param name="maxE">Maximum easting in meters</param>
    /// <param name="minN">Minimum northing in meters</param>
    /// <param name="maxN">Maximum northing in meters</param>
    /// <param name="zone">Zone index (default 0)</param>
    /// <returns>Number of cells marked</returns>
    public int MarkRectangleCovered(double minE, double maxE, double minN, double maxN, int zone = 0)
    {
        int count = 0;
        int cellMinE = (int)Math.Floor(minE / BITMAP_CELL_SIZE);
        int cellMaxE = (int)Math.Ceiling(maxE / BITMAP_CELL_SIZE);
        int cellMinN = (int)Math.Floor(minN / BITMAP_CELL_SIZE);
        int cellMaxN = (int)Math.Ceiling(maxN / BITMAP_CELL_SIZE);

        for (int cn = cellMinN; cn <= cellMaxN; cn++)
        {
            for (int ce = cellMinE; ce <= cellMaxE; ce++)
            {
                if (MarkCellCovered(ce, cn, zone))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Get area covered by a specific zone in hectares.
    /// </summary>
    public double GetZoneArea(int zone)
    {
        if (!_cellCountPerZone.TryGetValue(zone, out long count))
            return 0;
        // Each cell is BITMAP_CELL_SIZE x BITMAP_CELL_SIZE meters
        double cellAreaM2 = BITMAP_CELL_SIZE * BITMAP_CELL_SIZE;
        return count * cellAreaM2 / 10000.0; // Convert to hectares
    }

    /// <summary>
    /// Get total cell count across all zones (for statistics).
    /// </summary>
    public long GetTotalCellCount()
    {
        long total = 0;
        foreach (var count in _cellCountPerZone.Values)
            total += count;
        return total;
    }


    /// <summary>
    /// Get coverage bitmap bounds in world coordinates.
    /// Returns fixed field bounds if set (eagerly — even with zero coverage
    /// cells), otherwise coverage bounds. Returns null only when neither
    /// is available.
    ///
    /// Returning field bounds eagerly is critical: it lets the bitmap get
    /// allocated and the background composited at field load (where the
    /// busy spinner already masks the ~550 ms cost). The previous behavior
    /// gated allocation on the first painted cell, so the pause was paid
    /// when the vehicle crossed from the headland into the cultivated
    /// zone — visible as a stop-the-world freeze the first time sections
    /// turned on. (#stop-the-world-pause-on-first-coverage-cell)
    /// </summary>
    public (double MinE, double MaxE, double MinN, double MaxN)? GetCoverageBounds()
    {
        // Use fixed field bounds if set (stable coordinate system, allows
        // pre-allocation before any coverage cells exist).
        if (_fieldBoundsSet)
            return (_fieldMinE, _fieldMaxE, _fieldMinN, _fieldMaxN);

        // No field bounds set — fall back to coverage bounds, which require
        // at least one painted cell to be valid.
        if (GetTotalCellCount() == 0)
            return null;

        if (!_boundsValid)
            return null;

        // Convert cell coordinates to world coordinates.
        // Cell (x,y) covers from x*cellSize to (x+1)*cellSize.
        double minE = _minCellE * BITMAP_CELL_SIZE;
        double maxE = (_maxCellE + 1) * BITMAP_CELL_SIZE;
        double minN = _minCellN * BITMAP_CELL_SIZE;
        double maxN = (_maxCellN + 1) * BITMAP_CELL_SIZE;

        return (minE, maxE, minN, maxN);
    }

    /// <summary>
    /// Get coverage cells within viewport bounds for bitmap rendering.
    /// Only iterates cells within the specified world coordinate bounds.
    /// Time complexity: O(viewport area), not O(total coverage).
    /// </summary>
    public IEnumerable<(int CellX, int CellY, CoverageColor Color)> GetCoverageBitmapCells(
        double cellSize, double viewMinE, double viewMaxE, double viewMinN, double viewMaxN)
    {
        if (GetTotalCellCount() == 0)
            yield break;

        // Determine origin for coordinate calculations
        double originE, originN;
        if (_fieldBoundsSet)
        {
            originE = _fieldMinE;
            originN = _fieldMinN;
        }
        else
        {
            if (!_boundsValid) yield break;
            originE = _minCellE * BITMAP_CELL_SIZE;
            originN = _minCellN * BITMAP_CELL_SIZE;
        }

        // Default color for legacy compatibility (actual colors are in the bitmap)
        var defaultColor = GetZoneColor(0);

        // Convert viewport bounds to internal cell coordinates
        int internalMinCellE = (int)Math.Floor(viewMinE / BITMAP_CELL_SIZE);
        int internalMaxCellE = (int)Math.Ceiling(viewMaxE / BITMAP_CELL_SIZE);
        int internalMinCellN = (int)Math.Floor(viewMinN / BITMAP_CELL_SIZE);
        int internalMaxCellN = (int)Math.Ceiling(viewMaxN / BITMAP_CELL_SIZE);

        // Track output cells to avoid duplicates when downsampling
        var outputCells = new HashSet<(int, int)>();

        // Iterate only over cells within viewport bounds - O(viewport) not O(coverage)
        for (int cellE = internalMinCellE; cellE <= internalMaxCellE; cellE++)
        {
            for (int cellN = internalMinCellN; cellN <= internalMaxCellN; cellN++)
            {
                // O(1) HashSet lookup
                if (IsCellCovered(cellE, cellN))
                {
                    // Convert to output cell coordinates
                    double worldE = (cellE + 0.5) * BITMAP_CELL_SIZE;
                    double worldN = (cellN + 0.5) * BITMAP_CELL_SIZE;
                    int outCellX = (int)Math.Floor((worldE - originE) / cellSize);
                    int outCellY = (int)Math.Floor((worldN - originN) / cellSize);

                    if (outputCells.Add((outCellX, outCellY)))
                    {
                        yield return (outCellX, outCellY, defaultColor);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Enumerate ONLY the painted display cells, for the full-coverage snapshot (server
    /// re-init / new-client seed). Reads the RGB565 display buffer directly, walking just the
    /// tracked painted bounding box (_minCellE.._maxCellN) — so cost is O(painted area), never
    /// the O(whole-field 0.1 m) walk that <see cref="GetCoverageBitmapCells"/> does. That scan
    /// touched every cell in the field (tens of millions on a large field) even to find a tiny
    /// worked strip, which stalled the coverage rebuild; this touches only ground that has
    /// actually been worked. Output cells are in display coordinates (x = col, y = row from the
    /// SW origin), matching the CoverageInit grid the client renders into. Lock-free read (same
    /// as the other emit scans); a race yields a slightly stale cell, which is harmless.
    /// </summary>
    public IReadOnlyList<(int X, int Y, CoverageColor Color, int Alpha)> GetPaintedDisplayCells()
    {
        var result = new List<(int, int, CoverageColor, int)>();
        // MUST hold _coverageLock: a bounds-expansion on the control thread clears the display
        // buffer in SetFieldBounds and then spends tens of ms resampling the old pixels back in
        // (CheckAndExpandBounds) — all under this lock. A lock-free read here could catch the
        // buffer cleared-but-not-yet-refilled and return an EMPTY snapshot, which (with the client
        // tearing down on a reset init and re-anchor no longer resending) permanently dropped the
        // pre-expansion coverage on the first cell-size-change expansion. Locking waits for the
        // fully-resampled buffer. Alpha is folded in here too so it's consistent with the cells.
        lock (_coverageLock)
        {
            var buf = _displayPixels;
            int w = _displayWidth, h = _displayHeight;
            if (buf == null || !_fieldBoundsSet || !_boundsValid || w <= 0 || h <= 0)
                return result;
            double cell = _displayCellSize, fMinE = _displayOriginX * cell, fMinN = _displayOriginY * cell;

            // Painted bounding box (detection cells at 0.1 m) → display-pixel index window, clamped.
            int dxMin = ClampIndex((int)Math.Floor((_minCellE * BITMAP_CELL_SIZE - fMinE) / cell), w);
            int dxMax = ClampIndex((int)Math.Floor(((_maxCellE + 1) * BITMAP_CELL_SIZE - fMinE) / cell), w);
            int dyMin = ClampIndex((int)Math.Floor((_minCellN * BITMAP_CELL_SIZE - fMinN) / cell), h);
            int dyMax = ClampIndex((int)Math.Floor(((_maxCellN + 1) * BITMAP_CELL_SIZE - fMinN) / cell), h);

            for (int dy = dyMin; dy <= dyMax; dy++)
            {
                long row = (long)dy * w;
                for (int dx = dxMin; dx <= dxMax; dx++)
                {
                    ushort px = buf[row + dx];
                    if (px == 0) continue; // unpainted display pixel
                    result.Add((dx, dy, Rgb565ToColor(px), GetDisplayCellAlpha255(dx, dy)));
                }
            }
        }
        return result;
    }

    private static int ClampIndex(int v, int size) => v < 0 ? 0 : (v >= size ? size - 1 : v);

    /// <summary>
    /// Get newly added coverage cells since last call.
    /// Clears the pending list after returning.
    /// Uses fixed field bounds if set, otherwise coverage bounds.
    /// </summary>
    public IEnumerable<(int CellX, int CellY, CoverageColor Color)> GetNewCoverageBitmapCells(double cellSize)
    {
        lock (_coverageLock)
        {
            if (_newCells.Count == 0)
                return Array.Empty<(int, int, CoverageColor)>();

            // Determine origin for coordinate calculations
            double minE, minN;
            if (_fieldBoundsSet)
            {
                minE = _fieldMinE;
                minN = _fieldMinN;
            }
            else
            {
                if (!_boundsValid)
                {
                    _newCells.Clear();
                    return Array.Empty<(int, int, CoverageColor)>();
                }
                minE = _minCellE * BITMAP_CELL_SIZE;
                minN = _minCellN * BITMAP_CELL_SIZE;
            }

            _newCellsDedup.Clear();
            _newCellsResult.Clear();

            foreach (var (cellE, cellN, zone) in _newCells)
            {
                double worldE = (cellE + 0.5) * BITMAP_CELL_SIZE;
                double worldN = (cellN + 0.5) * BITMAP_CELL_SIZE;

                int outCellX = (int)Math.Floor((worldE - minE) / cellSize);
                int outCellY = (int)Math.Floor((worldN - minN) / cellSize);

                if (_newCellsDedup.Add((outCellX, outCellY)))
                {
                    var color = GetZoneColor(zone);
                    _newCellsResult.Add((outCellX, outCellY, color));
                }
            }

            _newCells.Clear();

            // Return a copy since _newCellsResult is reused
            return _newCellsResult.ToArray();
        }
    }

    /// <summary>
    /// Second, independent incremental drain — newly-covered cells since the LAST
    /// call to THIS method (parallel to GetNewCoverageBitmapCells, which serves the
    /// native map). For the remote/web server's CoverageProjector so it gets O(new
    /// cells) deltas instead of an O(whole-grid) scan. Same downsample logic.
    /// </summary>
    public IEnumerable<(int CellX, int CellY, CoverageColor Color)> GetNewCoverageBitmapCellsServer(double cellSize)
    {
        lock (_coverageLock)
        {
            if (_newCellsServer.Count == 0)
                return Array.Empty<(int, int, CoverageColor)>();

            double minE, minN;
            if (_fieldBoundsSet)
            {
                minE = _fieldMinE;
                minN = _fieldMinN;
            }
            else
            {
                if (!_boundsValid)
                {
                    _newCellsServer.Clear();
                    return Array.Empty<(int, int, CoverageColor)>();
                }
                minE = _minCellE * BITMAP_CELL_SIZE;
                minN = _minCellN * BITMAP_CELL_SIZE;
            }

            _newCellsServerDedup.Clear();
            _newCellsServerResult.Clear();

            foreach (var (cellE, cellN, zone) in _newCellsServer)
            {
                double worldE = (cellE + 0.5) * BITMAP_CELL_SIZE;
                double worldN = (cellN + 0.5) * BITMAP_CELL_SIZE;
                int outCellX = (int)Math.Floor((worldE - minE) / cellSize);
                int outCellY = (int)Math.Floor((worldN - minN) / cellSize);
                if (_newCellsServerDedup.Add((outCellX, outCellY)))
                    _newCellsServerResult.Add((outCellX, outCellY, GetZoneColor(zone)));
            }

            _newCellsServer.Clear();
            return _newCellsServerResult.ToArray();
        }
    }

    public IReadOnlyList<CoveragePatch> GetPatches()
    {
        // Legacy compatibility - patches no longer used, return empty list
        return Array.Empty<CoveragePatch>();
    }

    public IReadOnlyList<CoveragePatch> GetPatchesForZone(int zoneIndex)
    {
        // Legacy compatibility - patches no longer used, return empty list
        return Array.Empty<CoveragePatch>();
    }

    /// <summary>
    /// Clear all painted coverage. Zones that are mapping stay mapping: the section service
    /// only calls <see cref="StartMapping"/> on a section's off→on edge, so dropping them here
    /// would make every later point of a section that stays on be discarded (deleting the
    /// applied area while painting stopped the painting until each section cycled off/on).
    /// Their next point re-seeds the strip and the edge ribbon.
    /// </summary>
    public void ClearAll()
    {
        lock (_coverageLock) // the cycle thread paints concurrently
        {
            RequireFullSave(detection: true, display: true);
            // Clear display pixel buffer and detection bits in lockstep
            if (_displayPixels != null)
                Array.Clear(_displayPixels, 0, _displayPixels.Length);
            ExpandDirtyAll();
            _newCells.Clear();
            _newCellsServer.Clear();
            if (_detectionBits != null)
                Array.Clear(_detectionBits, 0, _detectionBits.Length);
            _cellCountPerZone.Clear();

            // Reset bounds
            _minCellE = int.MaxValue;
            _maxCellE = int.MinValue;
            _minCellN = int.MaxValue;
            _maxCellN = int.MinValue;
            _boundsValid = false;

            // Clear tracking state (not _activeSections, see above)
            _lastEdgesPerSection.Clear();
            ClearEdges();

            // Reset totals
            _totalWorkedArea = 0;
            _totalWorkedAreaUser = 0;
            _coverageDirty = false;
            _pendingAreaAdded = 0;
        }

        // IsFullReload tells the 2D map control to drop its SKBitmap and
        // repaint from the (now empty) service. Without this, ClearAll wipes
        // the service-owned display buffer + detection bits, but the 2D
        // control still shows the old paint until something else triggers a
        // full rebuild.
        CoverageUpdated?.Invoke(this, new CoverageUpdatedEventArgs
        {
            TotalArea = 0,
            PatchCount = 0,
            AreaAdded = 0,
            IsFullReload = true
        });
    }

    /// <summary>
    /// Set fixed field bounds for stable bitmap coordinate calculations.
    /// Allocates the bit array for memory-efficient coverage detection.
    /// </summary>
    public bool IsFieldBoundsSet => _fieldBoundsSet;

    public void SetFieldBoundsFromPosition(double easting, double northing, double halfSize = 250.0)
    {
        SetFieldBounds(easting - halfSize, easting + halfSize, northing - halfSize, northing + halfSize);
        Console.WriteLine($"[Coverage] Auto-initialized bounds from position ({easting:F1}, {northing:F1}), {halfSize * 2}m x {halfSize * 2}m");
    }

    private bool _inExpansion; // set by CheckAndExpandBounds so SetFieldBounds keeps the edge ribbons

    public void SetFieldBounds(double minE, double maxE, double minN, double maxN)
    {
        // Under the lock so a coverage save never snapshots half-updated geometry.
        // Re-entrant from CheckAndExpandBounds, which already holds it.
        lock (_coverageLock)
            SetFieldBoundsCore(minE, maxE, minN, maxN);
    }

    private void SetFieldBoundsCore(double minE, double maxE, double minN, double maxN)
    {
        // Skip if bounds unchanged
        if (_fieldBoundsSet &&
            Math.Abs(_fieldMinE - minE) < 0.01 &&
            Math.Abs(_fieldMaxE - maxE) < 0.01 &&
            Math.Abs(_fieldMinN - minN) < 0.01 &&
            Math.Abs(_fieldMaxN - maxN) < 0.01)
        {
            Console.WriteLine($"[Coverage] SetFieldBounds: bounds unchanged");
            return;
        }

        _fieldMinE = minE;
        _fieldMaxE = maxE;
        _fieldMinN = minN;
        _fieldMaxN = maxN;
        _fieldBoundsSet = true;
        double oldDisplayCell = _displayCellSize;

        // Detection grid in absolute 0.1 m cells. Origin and width are rounded out to multiples
        // of 8 so every row starts on a byte: a detection tile's rows are then plain byte copies.
        _bitmapOriginE = (int)Math.Floor(minE / BITMAP_CELL_SIZE) & ~7;
        _bitmapOriginN = (int)Math.Floor(minN / BITMAP_CELL_SIZE);
        _bitmapWidth = ((int)Math.Ceiling(maxE / BITMAP_CELL_SIZE) - _bitmapOriginE + 7) & ~7;
        _bitmapHeight = (int)Math.Ceiling(maxN / BITMAP_CELL_SIZE) - _bitmapOriginN;

        long totalDetectionCells = (long)_bitmapWidth * _bitmapHeight;

        // Allocate bit array for detection: 1 bit per cell.
        // REUSE when size matches — these arrays are ~44 MB on LOH for a
        // 367 ha field; allocating fresh on every field open and abandoning
        // the previous one builds up enough garbage that Gen2 collection
        // eventually fires for 1-3 s when ~3 close/open cycles accumulate.
        // User repro: close + reopen field 3x → beach ball.
        long totalBytes = (totalDetectionCells + 7) / 8;
        if (_detectionBits != null && _detectionBits.LongLength == totalBytes)
            Array.Clear(_detectionBits, 0, _detectionBits.Length);
        else
            _detectionBits = new byte[totalBytes];
        // New field → drop stale ribbons. Expansion keeps them: edges are absolute-coordinate
        // and CheckAndExpandBounds copies the detection bits across, so the perimeter stays valid.
        if (!_inExpansion) ClearEdges();

        // Compute display resolution + dimensions (pillared on
        // DisplayConfig.DisplayResolutionMultiplier and a 25M-pixel cap)
        // then allocate (or reuse) the RGB565 display buffer.
        _displayCellSize = ComputeDisplayCellSize(maxE - minE, maxN - minN);
        SetDisplayGrid(_displayCellSize);
        long totalDisplayPixels = (long)_displayWidth * _displayHeight;
        if (_displayPixels != null && _displayPixels.LongLength == totalDisplayPixels)
            Array.Clear(_displayPixels, 0, _displayPixels.Length);
        else
            _displayPixels = new ushort[totalDisplayPixels];
        _dirtyValid = false;

        // A new field (or reopen) replaces the coverage: the next save rewrites every tile.
        // Growing the bounds keeps tile keys (they're absolute) unless the display cell changed.
        if (!_inExpansion)
            RequireFullSave(detection: true, display: true);
        else if (Math.Abs(oldDisplayCell - _displayCellSize) > 1e-12)
            RequireFullSave(detection: false, display: true);

        double areaMSq = (maxE - minE) * (maxN - minN);
        double areaHa = areaMSq / 10000.0;
        double displayMB = totalDisplayPixels * 2 / (1024.0 * 1024.0);
        double detectionMB = totalBytes / (1024.0 * 1024.0);
        Console.WriteLine($"[Coverage] Field bounds set: E[{minE:F1}, {maxE:F1}] N[{minN:F1}, {maxN:F1}] {areaHa:F0}ha");
        Console.WriteLine($"[Coverage] Detection {_bitmapWidth}x{_bitmapHeight} @ {BITMAP_CELL_SIZE}m = {totalDetectionCells:N0} cells / {detectionMB:F1}MB");
        Console.WriteLine($"[Coverage] Display   {_displayWidth}x{_displayHeight} @ {_displayCellSize:F2}m = {totalDisplayPixels:N0} pixels / {displayMB:F1}MB");
    }

    /// <summary>Display grid for a cell size over the current field bounds, snapped to the world grid.</summary>
    private void SetDisplayGrid(double cellSize)
    {
        _displayOriginX = (int)Math.Floor(_fieldMinE / cellSize);
        _displayOriginY = (int)Math.Floor(_fieldMinN / cellSize);
        _displayWidth = (int)Math.Ceiling(_fieldMaxE / cellSize) - _displayOriginX;
        _displayHeight = (int)Math.Ceiling(_fieldMaxN / cellSize) - _displayOriginY;
    }

    /// <summary>
    /// Pick a display-layer cell size. Detection is fixed at 0.1m; display
    /// scales coarser when needed to keep the RGB565 buffer within budget:
    /// cap at ~25M pixels, then apply the user's DisplayResolutionMultiplier
    /// (Ultra=1.0, High=1.5, Med=2.5, Low=4.0, Min=6.0).
    /// </summary>
    private double ComputeDisplayCellSize(double worldWidthM, double worldHeightM)
    {
        double cellSize = BITMAP_CELL_SIZE;

        long pixelsAtFullRes =
            (long)Math.Ceiling(worldWidthM / BITMAP_CELL_SIZE) *
            (long)Math.Ceiling(worldHeightM / BITMAP_CELL_SIZE);
        if (pixelsAtFullRes > MAX_DISPLAY_PIXELS)
        {
            double scaleFactor = Math.Sqrt((double)pixelsAtFullRes / MAX_DISPLAY_PIXELS);
            cellSize = BITMAP_CELL_SIZE * scaleFactor;
            // Snap to discrete steps so save/load files share predictable cell sizes
            if (cellSize <= 0.2) cellSize = 0.2;
            else if (cellSize <= 0.25) cellSize = 0.25;
            else if (cellSize <= 0.35) cellSize = 0.35;
            else if (cellSize <= 0.5) cellSize = 0.5;
            else if (cellSize <= 0.75) cellSize = 0.75;
            else cellSize = Math.Ceiling(cellSize);
        }

        double multiplier = _configStore.Display.DisplayResolutionMultiplier;
        if (multiplier > 1.0) cellSize *= multiplier;

        return cellSize;
    }

    /// <summary>
    /// Recompute the display layer for a DisplayResolutionMultiplier change at the CURRENT
    /// field bounds (NOT a field reopen): pick the new display cell size, resize the RGB565
    /// display buffer, and repaint it from the resolution-independent detection bits. This is
    /// what makes a live "quality" change take effect on the remote/web coverage feed (which
    /// reads <see cref="DisplayDimensions"/>.CellSize and re-snapshots from detection) and keeps
    /// _displayPixels consistent for save. No-op if no field bounds, or the multiplier maps to
    /// the same pixel grid. The native map control rebuilds its own bitmap separately.
    /// </summary>
    public void RebuildDisplayForResolutionChange()
    {
        lock (_coverageLock)
        {
            if (!_fieldBoundsSet) return;
            double worldW = _fieldMaxE - _fieldMinE, worldH = _fieldMaxN - _fieldMinN;
            if (worldW <= 0 || worldH <= 0) return;

            double newCell = ComputeDisplayCellSize(worldW, worldH);
            if (Math.Abs(newCell - _displayCellSize) < 1e-12) return; // same pixel grid → nothing to do

            _displayCellSize = newCell;
            SetDisplayGrid(newCell);
            if (_displayWidth <= 0 || _displayHeight <= 0) return;
            RequireFullSave(detection: false, display: true);
            long total = (long)_displayWidth * _displayHeight;
            if (_displayPixels != null && _displayPixels.LongLength == total)
                Array.Clear(_displayPixels, 0, _displayPixels.Length);
            else
                _displayPixels = new ushort[total];
            _dirtyValid = false;
            RepaintDisplayFromDetection();
        }
    }

    /// <summary>
    /// Paint the display layer from the detection bits (0.1 m, resolution-independent). The
    /// 1-bit detection layer carries no per-cell zone, so this paints zone 0 — the remote
    /// coverage projection is single-colour too (GetCoverageBitmapCells yields the default
    /// zone colour). Caller holds _coverageLock.
    /// </summary>
    private void RepaintDisplayFromDetection()
    {
        if (_detectionBits == null) return;
        for (int byteIdx = 0; byteIdx < _detectionBits.Length; byteIdx++)
        {
            byte bits = _detectionBits[byteIdx];
            if (bits == 0) continue; // 8 uncovered cells at once
            long baseBitIdx = (long)byteIdx * 8;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((bits & (1 << bit)) == 0) continue;
                long bitIdx = baseBitIdx + bit;
                // PaintDisplayPixel takes ABSOLUTE detection cells (as MarkCellCovered
                // passes them); bitIdx is local to the bitmap. Without the origin every
                // repainted pixel was shifted by the field's min corner, so the fill
                // vanished and only the edge strokes were left after a quality change (#175).
                PaintDisplayPixel(_bitmapOriginE + (int)(bitIdx % _bitmapWidth),
                                  _bitmapOriginN + (int)(bitIdx / _bitmapWidth), 0);
            }
        }
    }

    private const double EXPAND_MARGIN = 50.0; // Expand when within 50m of edge
    private const double EXPAND_AMOUNT = 250.0; // Add 250m in the needed direction

    /// <summary>
    /// Check if coverage points are near the bounds edge and expand if needed.
    /// Copies existing detection bits to the new larger array.
    /// </summary>
    private void CheckAndExpandBounds(Vec2 leftEdge, Vec2 rightEdge)
    {
        double minE = Math.Min(leftEdge.Easting, rightEdge.Easting);
        double maxE = Math.Max(leftEdge.Easting, rightEdge.Easting);
        double minN = Math.Min(leftEdge.Northing, rightEdge.Northing);
        double maxN = Math.Max(leftEdge.Northing, rightEdge.Northing);

        bool needsExpand = false;
        double newMinE = _fieldMinE, newMaxE = _fieldMaxE;
        double newMinN = _fieldMinN, newMaxN = _fieldMaxN;

        if (minE < _fieldMinE + EXPAND_MARGIN) { newMinE = _fieldMinE - EXPAND_AMOUNT; needsExpand = true; }
        if (maxE > _fieldMaxE - EXPAND_MARGIN) { newMaxE = _fieldMaxE + EXPAND_AMOUNT; needsExpand = true; }
        if (minN < _fieldMinN + EXPAND_MARGIN) { newMinN = _fieldMinN - EXPAND_AMOUNT; needsExpand = true; }
        if (maxN > _fieldMaxN - EXPAND_MARGIN) { newMaxN = _fieldMaxN + EXPAND_AMOUNT; needsExpand = true; }

        if (!needsExpand) return;

        // Save old state — both detection (always at 0.1m) and display (at
        // _displayCellSize). Detection origin is in 0.1m cells; display origin
        // derives from world bounds since the policy could change cell size.
        var oldBits = _detectionBits;
        int oldWidth = _bitmapWidth;
        int oldHeight = _bitmapHeight;
        int oldOriginE = _bitmapOriginE;
        int oldOriginN = _bitmapOriginN;

        var oldPixels = _displayPixels;
        int oldDispWidth = _displayWidth;
        int oldDispHeight = _displayHeight;
        double oldDispCell = _displayCellSize;
        int oldDispOriginX = _displayOriginX;
        int oldDispOriginY = _displayOriginY;

        // Reallocate with new bounds (keep the edge ribbons — detection is copied below)
        _inExpansion = true;
        try { SetFieldBounds(newMinE, newMaxE, newMinN, newMaxN); }
        finally { _inExpansion = false; }

        // Copy old detection bits to new array
        if (oldBits != null)
            CopyDetectionBitsIn(oldBits, oldWidth, oldHeight, oldOriginE, oldOriginN);

        // Copy old display pixels to new buffer. Same cell size: the grids share the world
        // snap, so it's a row-by-row copy at the origin offset. A changed cell size (rare —
        // only when the new bounds cross a policy threshold) resamples through world coordinates.
        if (oldPixels != null && _displayPixels != null)
        {
            bool sameCell = Math.Abs(oldDispCell - _displayCellSize) < 1e-12;
            int offX = oldDispOriginX - _displayOriginX, offY = oldDispOriginY - _displayOriginY;
            for (int oy = 0; oy < oldDispHeight; oy++)
            {
                if (sameCell)
                {
                    int ny = oy + offY;
                    if (ny < 0 || ny >= _displayHeight) continue;
                    int x0 = Math.Max(0, -offX), x1 = Math.Min(oldDispWidth, _displayWidth - offX);
                    if (x1 > x0)
                        oldPixels.AsSpan(oy * oldDispWidth + x0, x1 - x0)
                                 .CopyTo(_displayPixels.AsSpan(ny * _displayWidth + x0 + offX));
                    continue;
                }
                for (int ox = 0; ox < oldDispWidth; ox++)
                {
                    ushort px = oldPixels[(long)oy * oldDispWidth + ox];
                    if (px == 0) continue;
                    double worldX = (oldDispOriginX + ox + 0.5) * oldDispCell;
                    double worldY = (oldDispOriginY + oy + 0.5) * oldDispCell;
                    int nx = (int)Math.Floor(worldX / _displayCellSize) - _displayOriginX;
                    int ny = (int)Math.Floor(worldY / _displayCellSize) - _displayOriginY;
                    if (nx < 0 || nx >= _displayWidth || ny < 0 || ny >= _displayHeight) continue;
                    _displayPixels[(long)ny * _displayWidth + nx] = px;
                }
            }
            // Whole buffer is potentially changed after a resize.
            ExpandDirtyAll();
        }

        Console.WriteLine($"[Coverage] Bounds expanded: detection {oldWidth}x{oldHeight} -> {_bitmapWidth}x{_bitmapHeight}, display {oldDispWidth}x{oldDispHeight} -> {_displayWidth}x{_displayHeight}");

        // Notify listeners to resize their bitmaps
        BoundsExpanded?.Invoke(this, new BoundsExpandedEventArgs
        {
            MinE = newMinE, MaxE = newMaxE, MinN = newMinN, MaxN = newMaxN
        });
    }

    /// <summary>
    /// OR a detection grid with its own origin and size (in absolute 0.1 m cells) into
    /// _detectionBits. Cells outside the current grid are dropped.
    /// </summary>
    private void CopyDetectionBitsIn(byte[] srcBits, int srcWidth, int srcHeight, int srcOriginE, int srcOriginN)
    {
        if (_detectionBits == null) return;
        int offsetE = srcOriginE - _bitmapOriginE;
        int offsetN = srcOriginN - _bitmapOriginN;
        long srcCells = (long)srcWidth * srcHeight;

        for (long byteIdx = 0; byteIdx < srcBits.LongLength; byteIdx++)
        {
            byte bits = srcBits[byteIdx];
            if (bits == 0) continue; // 8 uncovered cells at once
            for (int bit = 0; bit < 8; bit++)
            {
                if ((bits & (1 << bit)) == 0) continue;
                long srcIdx = byteIdx * 8 + bit;
                if (srcIdx >= srcCells) break;
                int newX = (int)(srcIdx % srcWidth) + offsetE;
                int newY = (int)(srcIdx / srcWidth) + offsetN;
                if (newX >= 0 && newX < _bitmapWidth && newY >= 0 && newY < _bitmapHeight)
                {
                    long newIdx = (long)newY * _bitmapWidth + newX;
                    _detectionBits[newIdx / 8] |= (byte)(1 << (int)(newIdx % 8));
                }
            }
        }
    }

    /// <summary>
    /// Clear field bounds (when field is closed).
    /// </summary>
    public void ClearFieldBounds()
    {
        lock (_coverageLock)
            ClearFieldBoundsCore();
    }

    private void ClearFieldBoundsCore()
    {
        _fieldBoundsSet = false;
        RequireFullSave(detection: true, display: true);
        _savedDirectory = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
        _displayWidth = 0;
        _displayHeight = 0;
        _cellCountPerZone.Clear();
        // Keep _detectionBits and _displayPixels alive — SetFieldBounds
        // reuses them when LongLength matches (the reuse fix at lines
        // 981-996). Nulling here silently defeats the reuse on every
        // close+reopen and forces a fresh ~73 MB LOH alloc.
        _dirtyValid = false;
        ClearEdges();
        Console.WriteLine("[Coverage] Field bounds cleared");
    }

    public void ResetUserArea()
    {
        _totalWorkedAreaUser = 0;
    }

    // ========== PERSISTENCE: world-anchored tiles (CoverageTileStore) ==========

    // Tiles painted since the last save, keyed CoverageTileStore.Key of absolute tile indices.
    // A third dirty stream, independent of the renderer's dirty rect and the web projector's
    // new-cell drain, so a save steals from neither. Mutated under _coverageLock; a save swaps
    // the sets out and puts them back if it fails.
    private HashSet<long> _dirtyDetectTiles = new();
    private HashSet<long> _dirtyDisplayTiles = new();
    private long _lastDetectTileKey = long.MinValue;  // skips the set probe for runs in one tile
    private long _lastDisplayTileKey = long.MinValue;

    // Rewrite every tile on the next save, and delete the rest: the coverage was cleared,
    // replaced (new field, import) or regridded (display cell size).
    private bool _detectFullSave = true;
    private bool _displayFullSave = true;

    private string? _savedDirectory;        // job folder the tiles on disk match (null: unknown)
    private string? _legacyFilesDirectory;  // job imported from coverage_*.bin: delete them after its first tiled save

    // Serialises saves: the autosave can still be running when the field-close save starts,
    // and both write the same files. The scratch buffers below are only used under it.
    private readonly object _saveLock = new();
    private readonly CoverageTileStore.RunBuffer _runBuffer = new();
    private readonly byte[] _detectTileScratch = new byte[CoverageTileStore.DetectTileBytes];
    private readonly ushort[] _displayTileScratch = new ushort[CoverageTileStore.DisplayTileLength];

    /// <summary>Caller holds _coverageLock.</summary>
    private void RequireFullSave(bool detection, bool display)
    {
        if (detection)
        {
            _detectFullSave = true;
            _dirtyDetectTiles.Clear();
            _lastDetectTileKey = long.MinValue;
        }
        if (display)
        {
            _displayFullSave = true;
            _dirtyDisplayTiles.Clear();
            _lastDisplayTileKey = long.MinValue;
        }
    }

    // Everything a save needs, captured under _coverageLock. The arrays are the live ones:
    // tiles are encoded from them outside the lock. A cell painted meanwhile may or may not
    // make it into this save, but it re-dirties its tile, so the next save has it. Expansion
    // allocates new arrays, so these references stay consistent with these dimensions.
    private sealed record TileSave(
        byte[]? DetectionBits, int BitmapOriginE, int BitmapOriginN, int BitmapWidth, int BitmapHeight,
        ushort[]? DisplayPixels, int DisplayOriginX, int DisplayOriginY, int DisplayWidth, int DisplayHeight,
        double DisplayCellSize, double TotalWorkedArea,
        double MinE, double MaxE, double MinN, double MaxN,
        bool DetectFull, bool DisplayFull, HashSet<long> DetectKeys, HashSet<long> DisplayKeys);

    public void SaveToFile(string fieldDirectory)
    {
        lock (_saveLock)
        {
            string dir = Path.GetFullPath(fieldDirectory);
            var store = new CoverageTileStore(dir);
            TileSave save;
            lock (_coverageLock)
            {
                if (!_fieldBoundsSet)
                    return;
                // A different job folder, or one whose tiles we didn't write: write all of it.
                bool newTarget = dir != _savedDirectory || !store.ManifestExists;
                bool detectFull = _detectFullSave || newTarget;
                bool displayFull = _displayFullSave || newTarget;
                if (!detectFull && !displayFull && _dirtyDetectTiles.Count == 0 && _dirtyDisplayTiles.Count == 0)
                    return; // nothing painted since the last save here

                save = new TileSave(
                    _detectionBits, _bitmapOriginE, _bitmapOriginN, _bitmapWidth, _bitmapHeight,
                    _displayPixels, _displayOriginX, _displayOriginY, _displayWidth, _displayHeight,
                    _displayCellSize, _totalWorkedArea,
                    _fieldMinE, _fieldMaxE, _fieldMinN, _fieldMaxN,
                    detectFull, displayFull, _dirtyDetectTiles, _dirtyDisplayTiles);
                _dirtyDetectTiles = new HashSet<long>();
                _dirtyDisplayTiles = new HashSet<long>();
                _lastDetectTileKey = _lastDisplayTileKey = long.MinValue;
                _detectFullSave = _displayFullSave = false;
            }

            try
            {
                WriteTiles(store, save);
            }
            catch
            {
                // Leave the work for the next save (a failed autosave is a warning, not fatal).
                lock (_coverageLock)
                {
                    _dirtyDetectTiles.UnionWith(save.DetectKeys);
                    _dirtyDisplayTiles.UnionWith(save.DisplayKeys);
                    _detectFullSave |= save.DetectFull;
                    _displayFullSave |= save.DisplayFull;
                }
                throw;
            }

            _savedDirectory = dir;
            if (_legacyFilesDirectory == dir)
            {
                // One-way import: the job's coverage now lives in its tiles.
                foreach (var name in new[] { "coverage_detect.bin", "coverage_disp.bin" })
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                _legacyFilesDirectory = null;
                Console.WriteLine($"[Coverage] Imported coverage_*.bin into tiles and removed them: {dir}");
            }
        }
    }

    private void WriteTiles(CoverageTileStore store, TileSave s)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long bytes = 0;
        int written = 0, removed = 0;

        // Detection tiles. Full: every tile over the grid (empty ones get no file), then
        // anything else on disk is stale. Incremental: the dirty tiles only.
        var keepDetect = s.DetectFull ? new HashSet<long>() : null;
        foreach (long key in s.DetectFull
                     ? TilesOver(s.BitmapOriginE, s.BitmapOriginN, s.BitmapWidth, s.BitmapHeight, CoverageTileStore.DetectTileShift)
                     : s.DetectKeys)
        {
            int tx = CoverageTileStore.KeyX(key), ty = CoverageTileStore.KeyY(key);
            if (ExtractDetectionTile(s, tx, ty, _detectTileScratch))
            {
                bytes += store.WriteDetectionTile(tx, ty, _detectTileScratch, _runBuffer);
                written++;
                keepDetect?.Add(key);
            }
            else if (keepDetect == null)
            {
                CoverageTileStore.DeleteTile(store.DetectionDir, tx, ty);
                removed++;
            }
        }

        // Display tiles, in the folder for this cell size.
        string displayDir = store.DisplayDir(s.DisplayCellSize);
        var keepDisplay = s.DisplayFull ? new HashSet<long>() : null;
        foreach (long key in s.DisplayFull
                     ? TilesOver(s.DisplayOriginX, s.DisplayOriginY, s.DisplayWidth, s.DisplayHeight, CoverageTileStore.DisplayTileShift)
                     : s.DisplayKeys)
        {
            int tx = CoverageTileStore.KeyX(key), ty = CoverageTileStore.KeyY(key);
            if (ExtractDisplayTile(s, tx, ty, _displayTileScratch))
            {
                bytes += store.WriteDisplayTile(tx, ty, s.DisplayCellSize, _displayTileScratch, _runBuffer);
                written++;
                keepDisplay?.Add(key);
            }
            else if (keepDisplay == null)
            {
                CoverageTileStore.DeleteTile(displayDir, tx, ty);
                removed++;
            }
        }

        // Tiles land before the manifest, which switches the display folder and carries the area.
        store.CommitTiles();
        store.WriteManifest(new CoverageTileStore.Manifest
        {
            DisplayCellSize = s.DisplayCellSize,
            TotalWorkedArea = s.TotalWorkedArea,
            MinE = s.MinE, MaxE = s.MaxE, MinN = s.MinN, MaxN = s.MaxN,
        });

        if (keepDetect != null)
            foreach (long key in CoverageTileStore.ListTiles(store.DetectionDir))
                if (!keepDetect.Contains(key))
                {
                    CoverageTileStore.DeleteTile(store.DetectionDir, CoverageTileStore.KeyX(key), CoverageTileStore.KeyY(key));
                    removed++;
                }
        if (keepDisplay != null)
        {
            foreach (long key in CoverageTileStore.ListTiles(displayDir))
                if (!keepDisplay.Contains(key))
                {
                    CoverageTileStore.DeleteTile(displayDir, CoverageTileStore.KeyX(key), CoverageTileStore.KeyY(key));
                    removed++;
                }
            store.DeleteOtherDisplayDirs(s.DisplayCellSize);
        }

        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine(
            $"[Coverage] Saved {(s.DetectFull || s.DisplayFull ? "all" : "changed")} tiles: " +
            $"{written} written ({bytes / 1024}KB), {removed} removed, in {ms:F0}ms");
    }

    /// <summary>Keys of the tiles (2^shift cells per side) over a grid of absolute indices.</summary>
    private static IEnumerable<long> TilesOver(int originX, int originY, int width, int height, int shift)
    {
        if (width <= 0 || height <= 0) yield break;
        for (int ty = originY >> shift; ty <= (originY + height - 1) >> shift; ty++)
            for (int tx = originX >> shift; tx <= (originX + width - 1) >> shift; tx++)
                yield return CoverageTileStore.Key(tx, ty);
    }

    /// <summary>
    /// Copy one detection tile out of the grid into <paramref name="tile"/>. Rows are byte
    /// copies: the grid's origin and width are multiples of 8, as is a tile's first cell.
    /// False when the tile holds no coverage.
    /// </summary>
    private static bool ExtractDetectionTile(TileSave s, int tx, int ty, byte[] tile)
    {
        Array.Clear(tile);
        var bits = s.DetectionBits;
        if (bits == null) return false;
        const int n = CoverageTileStore.DetectTileCells, rowBytes = n / 8;
        int lx0 = tx * n - s.BitmapOriginE, ly0 = ty * n - s.BitmapOriginN;
        int cx0 = Math.Max(lx0, 0), cx1 = Math.Min(lx0 + n, s.BitmapWidth);
        if (cx0 >= cx1) return false;
        int stride = s.BitmapWidth / 8, len = (cx1 - cx0) / 8;
        bool any = false;
        for (int r = Math.Max(0, -ly0); r < n && ly0 + r < s.BitmapHeight; r++)
        {
            var src = bits.AsSpan((ly0 + r) * stride + cx0 / 8, len);
            src.CopyTo(tile.AsSpan(r * rowBytes + (cx0 - lx0) / 8));
            any = any || src.IndexOfAnyExcept((byte)0) >= 0;
        }
        return any;
    }

    /// <summary>Copy one display tile out of the grid. False when it holds no coverage.</summary>
    private static bool ExtractDisplayTile(TileSave s, int tx, int ty, ushort[] tile)
    {
        Array.Clear(tile);
        var pixels = s.DisplayPixels;
        if (pixels == null) return false;
        const int n = CoverageTileStore.DisplayTilePixels;
        int lx0 = tx * n - s.DisplayOriginX, ly0 = ty * n - s.DisplayOriginY;
        int cx0 = Math.Max(lx0, 0), cx1 = Math.Min(lx0 + n, s.DisplayWidth);
        if (cx0 >= cx1) return false;
        bool any = false;
        for (int r = Math.Max(0, -ly0); r < n && ly0 + r < s.DisplayHeight; r++)
        {
            var src = pixels.AsSpan((ly0 + r) * s.DisplayWidth + cx0, cx1 - cx0);
            src.CopyTo(tile.AsSpan(r * n + (cx0 - lx0)));
            any = any || src.IndexOfAnyExcept((ushort)0) >= 0;
        }
        return any;
    }

    public void LoadFromFile(string fieldDirectory)
    {
        string dir = Path.GetFullPath(fieldDirectory);
        var store = new CoverageTileStore(dir);
        bool hasDetectionBits, hasSectionDisplay, fromTiles = false, fromLegacyBins = false;

        var manifest = store.ManifestExists || Directory.Exists(store.DetectionDir)
            ? store.ReadManifest() ?? RecoverManifest(store)
            : null;
        if (manifest != null)
        {
            // The tiles carry their own extent: the bounds may have grown while painting, or not
            // be set at all yet (no boundary: they normally come from the first GPS fix).
            EnsureBoundsHold(manifest.MinE, manifest.MaxE, manifest.MinN, manifest.MaxN);
            (hasDetectionBits, hasSectionDisplay) = LoadTiles(store, manifest);
            fromTiles = hasDetectionBits;
        }
        else
        {
            // Pre-tile job: one file per layer. Imported once; the first save writes tiles.
            EnsureBoundsHoldLegacyFiles(fieldDirectory);
            hasDetectionBits = LoadDetectionBits(fieldDirectory);
            hasSectionDisplay = LoadSectionDisplay(fieldDirectory);
            fromLegacyBins = hasDetectionBits || hasSectionDisplay;

            // Fallback: try legacy AgOpenGPS Sections.txt format
            if (!hasDetectionBits && !hasSectionDisplay)
                hasDetectionBits = LoadLegacySections(fieldDirectory);
        }

        lock (_coverageLock)
        {
            // Without display tiles (lost, or never written) draw the worked area from detection.
            if (hasDetectionBits && !hasSectionDisplay)
            {
                RepaintDisplayFromDetection();
                ExpandDirtyAll();
            }

            // What's in memory now matches the tiles on disk, unless it came from elsewhere
            // (old files, Sections.txt) or was resampled to a different display cell size.
            bool regridded = fromTiles && Math.Abs(manifest!.DisplayCellSize - _displayCellSize) > 1e-12;
            _dirtyDetectTiles.Clear();
            _dirtyDisplayTiles.Clear();
            _lastDetectTileKey = _lastDisplayTileKey = long.MinValue;
            _detectFullSave = !fromTiles;
            _displayFullSave = !fromTiles || regridded || !hasSectionDisplay;
            _savedDirectory = fromTiles ? dir : null;
            _legacyFilesDirectory = fromLegacyBins ? dir : null;
        }

        // A tiled job with no coverage yet still replaces whatever the map showed before.
        if (hasSectionDisplay || hasDetectionBits || manifest != null)
        {
            Console.WriteLine($"[Coverage] Loaded{(manifest != null ? " tiles" : "")}: detectionBits={hasDetectionBits}, sectionDisplay={hasSectionDisplay}");
            CoverageUpdated?.Invoke(this, new CoverageUpdatedEventArgs
            {
                TotalArea = _totalWorkedArea,
                PatchCount = (int)GetTotalCellCount(),
                AreaAdded = 0,
                IsFullReload = true
            });
        }
    }

    /// <summary>
    /// A manifest from the tiles alone, when manifest.json is unreadable: bounds from the
    /// detection tiles' extent, the display cell size from its folder name, area recounted.
    /// </summary>
    private static CoverageTileStore.Manifest? RecoverManifest(CoverageTileStore store)
    {
        var keys = CoverageTileStore.ListTiles(store.DetectionDir);
        if (keys.Count == 0)
            return null;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (long k in keys)
        {
            minX = Math.Min(minX, CoverageTileStore.KeyX(k)); maxX = Math.Max(maxX, CoverageTileStore.KeyX(k));
            minY = Math.Min(minY, CoverageTileStore.KeyY(k)); maxY = Math.Max(maxY, CoverageTileStore.KeyY(k));
        }
        const double tileM = CoverageTileStore.DetectTileCells * BITMAP_CELL_SIZE;
        double cell = 0;
        if (Directory.Exists(store.Root))
            foreach (var d in Directory.EnumerateDirectories(store.Root, "s*"))
                if (long.TryParse(Path.GetFileName(d).AsSpan(1), System.Globalization.NumberStyles.None,
                                  System.Globalization.CultureInfo.InvariantCulture, out long um) && um > 0)
                    cell = um / 1e6;
        Console.WriteLine("[Coverage] manifest.json unreadable — rebuilding it from the tiles");
        return new CoverageTileStore.Manifest
        {
            DisplayCellSize = cell > 0 ? cell : BITMAP_CELL_SIZE,
            TotalWorkedArea = 0, // recounted from the bits
            MinE = minX * tileM, MaxE = (maxX + 1) * tileM - 1e-6,
            MinN = minY * tileM, MaxN = (maxY + 1) * tileM - 1e-6,
        };
    }

    /// <summary>Read every tile into the (cleared) grids. Returns which layers had coverage.</summary>
    private (bool Detection, bool Display) LoadTiles(CoverageTileStore store, CoverageTileStore.Manifest manifest)
    {
        var detectKeys = CoverageTileStore.ListTiles(store.DetectionDir);
        double savedCell = manifest.DisplayCellSize;
        var displayKeys = CoverageTileStore.ListTiles(store.DisplayDir(savedCell));
        var tile = new byte[CoverageTileStore.DetectTileBytes];
        var dtile = new ushort[CoverageTileStore.DisplayTileLength];
        long setBits = 0;
        int detectTiles = 0, displayTiles = 0;

        lock (_coverageLock)
        {
            if (!_fieldBoundsSet || _detectionBits == null || _displayPixels == null)
                return (false, false);
            Array.Clear(_detectionBits);
            Array.Clear(_displayPixels);

            const int n = CoverageTileStore.DetectTileCells, rowBytes = n / 8;
            int stride = _bitmapWidth / 8;
            foreach (long key in detectKeys)
            {
                int tx = CoverageTileStore.KeyX(key), ty = CoverageTileStore.KeyY(key);
                if (!store.TryReadDetectionTile(tx, ty, tile))
                    continue;
                int lx0 = tx * n - _bitmapOriginE, ly0 = ty * n - _bitmapOriginN;
                int cx0 = Math.Max(lx0, 0), cx1 = Math.Min(lx0 + n, _bitmapWidth);
                if (cx0 >= cx1) continue;
                for (int r = Math.Max(0, -ly0); r < n && ly0 + r < _bitmapHeight; r++)
                    tile.AsSpan(r * rowBytes + (cx0 - lx0) / 8, (cx1 - cx0) / 8)
                        .CopyTo(_detectionBits.AsSpan((ly0 + r) * stride + cx0 / 8));
                foreach (ulong w in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(tile))
                    setBits += System.Numerics.BitOperations.PopCount(w);
                detectTiles++;
            }

            bool sameCell = Math.Abs(savedCell - _displayCellSize) < 1e-12;
            const int dn = CoverageTileStore.DisplayTilePixels;
            foreach (long key in displayKeys)
            {
                int tx = CoverageTileStore.KeyX(key), ty = CoverageTileStore.KeyY(key);
                if (!store.TryReadDisplayTile(tx, ty, savedCell, dtile))
                    continue;
                displayTiles++;
                if (sameCell)
                {
                    int lx0 = tx * dn - _displayOriginX, ly0 = ty * dn - _displayOriginY;
                    int cx0 = Math.Max(lx0, 0), cx1 = Math.Min(lx0 + dn, _displayWidth);
                    if (cx0 >= cx1) continue;
                    for (int r = Math.Max(0, -ly0); r < dn && ly0 + r < _displayHeight; r++)
                        dtile.AsSpan(r * dn + (cx0 - lx0), cx1 - cx0)
                             .CopyTo(_displayPixels.AsSpan((ly0 + r) * _displayWidth + cx0));
                }
                else
                {
                    // Saved at another display cell size: for each current pixel whose centre
                    // falls in this tile, take the saved pixel under it.
                    double e0 = tx * dn * savedCell, n0 = ty * dn * savedCell, size = dn * savedCell;
                    int gx0 = Math.Max(0, (int)Math.Floor(e0 / _displayCellSize) - _displayOriginX);
                    int gx1 = Math.Min(_displayWidth - 1, (int)Math.Ceiling((e0 + size) / _displayCellSize) - _displayOriginX);
                    int gy0 = Math.Max(0, (int)Math.Floor(n0 / _displayCellSize) - _displayOriginY);
                    int gy1 = Math.Min(_displayHeight - 1, (int)Math.Ceiling((n0 + size) / _displayCellSize) - _displayOriginY);
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        int sy = (int)Math.Floor((_displayOriginY + gy + 0.5) * _displayCellSize / savedCell) - ty * dn;
                        if (sy < 0 || sy >= dn) continue;
                        for (int gx = gx0; gx <= gx1; gx++)
                        {
                            int sx = (int)Math.Floor((_displayOriginX + gx + 0.5) * _displayCellSize / savedCell) - tx * dn;
                            if (sx < 0 || sx >= dn) continue;
                            ushort v = dtile[sy * dn + sx];
                            if (v != 0) _displayPixels[gy * _displayWidth + gx] = v;
                        }
                    }
                }
            }

            double area = manifest.TotalWorkedArea > 0 ? manifest.TotalWorkedArea : setBits * BITMAP_CELL_SIZE * BITMAP_CELL_SIZE;
            _totalWorkedArea = area;
            _totalWorkedAreaUser = area;
            _cellCountPerZone.Clear();
            _cellCountPerZone[0] = setBits;
            _boundsValid = setBits > 0;
            if (_boundsValid)
            {
                _minCellE = _bitmapOriginE;
                _maxCellE = _bitmapOriginE + _bitmapWidth - 1;
                _minCellN = _bitmapOriginN;
                _maxCellN = _bitmapOriginN + _bitmapHeight - 1;
            }
            ExpandDirtyAll();
        }

        Console.WriteLine($"[Coverage] Loaded {detectTiles} detection + {displayTiles} display tiles: {setBits:N0} covered cells");
        return (setBits > 0, displayTiles > 0);
    }

    public void SaveToFile(string fieldDirectory, string taskName)
    {
        var jobDir = ResolveJobDirectory(fieldDirectory, taskName);
        Directory.CreateDirectory(jobDir);
        SaveToFile(jobDir);
    }

    public void LoadFromFile(string fieldDirectory, string taskName) =>
        LoadFromFile(ResolveJobDirectory(fieldDirectory, taskName));

    private static string ResolveJobDirectory(string fieldDirectory, string taskName)
    {
        if (string.IsNullOrWhiteSpace(fieldDirectory))
            throw new ArgumentException("fieldDirectory must be set", nameof(fieldDirectory));
        if (string.IsNullOrWhiteSpace(taskName))
            throw new ArgumentException("taskName must be set", nameof(taskName));
        return Path.Combine(fieldDirectory, "jobs", taskName);
    }

    /// <summary>
    /// Load legacy AgOpenGPS Sections.txt coverage data.
    /// Format: quad strips with vertex pairs (easting, northing, 0).
    /// Rasterizes the quads into our coverage cell grid.
    /// </summary>
    private bool LoadLegacySections(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, "Sections.txt");
        if (!File.Exists(path)) return false;

        try
        {
            var lines = File.ReadAllLines(path);
            int lineIdx = 0;
            int totalCells = 0;

            while (lineIdx < lines.Length)
            {
                // Read count (number of lines in this strip: 1 color + pairs*2)
                var countLine = lines[lineIdx++].Trim();
                if (string.IsNullOrEmpty(countLine)) continue;
                if (!int.TryParse(countLine, out int n) || n < 3) continue;

                // n = colour line + vertex lines. An even n means the colour line is missing
                // (some AgOpenGPS versions wrote it that way): reading the first vertex as the
                // colour then paired every left edge with the next strip's right (AgOpenGPS
                // #1206 SectionFiles).
                bool hasColor = n % 2 == 1;
                int nPairs = hasColor ? (n - 1) / 2 : n / 2;

                // Read RGB color line (R,G,B format) — ignored, we use the default colour
                if (hasColor)
                {
                    if (lineIdx >= lines.Length) break;
                    lineIdx++;
                }

                // Read vertex pairs and rasterize each quad
                double prevLeftE = 0, prevLeftN = 0, prevRightE = 0, prevRightN = 0;
                bool hasPrev = false;

                for (int i = 0; i < nPairs; i++)
                {
                    if (lineIdx + 1 >= lines.Length) break;

                    var leftParts = lines[lineIdx++].Split(',');
                    var rightParts = lines[lineIdx++].Split(',');

                    if (leftParts.Length < 2 || rightParts.Length < 2) continue;

                    double leftE = double.Parse(leftParts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    double leftN = double.Parse(leftParts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    double rightE = double.Parse(rightParts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    double rightN = double.Parse(rightParts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);

                    if (hasPrev)
                    {
                        // Rasterize quad: prevLeft -> prevRight -> currRight -> currLeft (CW winding)
                        totalCells += RasterizeQuad(
                            prevLeftE, prevLeftN, prevRightE, prevRightN,
                            rightE, rightN, leftE, leftN);
                    }

                    prevLeftE = leftE; prevLeftN = leftN;
                    prevRightE = rightE; prevRightN = rightN;
                    hasPrev = true;
                }
            }

            if (totalCells > 0)
            {
                // Worked area = covered cells (each counted once, so overlapping strips aren't
                // double-counted). It was left at 0, and the job then saved 0 with its coverage.
                _totalWorkedArea = _totalWorkedAreaUser = totalCells * BITMAP_CELL_SIZE * BITMAP_CELL_SIZE;
                Console.WriteLine($"[Coverage] Loaded legacy Sections.txt: {totalCells} cells rasterized, {_totalWorkedArea:F0} m²");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coverage] Error loading legacy Sections.txt: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Rasterize a quad (4 vertices) into coverage cells.
    /// Uses the same cell grid and point-in-quad test as RasterizeQuadToBitmap.
    /// </summary>
    private int RasterizeQuad(
        double e0, double n0, double e1, double n1,
        double e2, double n2, double e3, double n3)
    {
        var p0 = (E: e0, N: n0);
        var p1 = (E: e1, N: n1);
        var p2 = (E: e2, N: n2);
        var p3 = (E: e3, N: n3);

        double minE = Math.Min(Math.Min(e0, e1), Math.Min(e2, e3));
        double maxE = Math.Max(Math.Max(e0, e1), Math.Max(e2, e3));
        double minN = Math.Min(Math.Min(n0, n1), Math.Min(n2, n3));
        double maxN = Math.Max(Math.Max(n0, n1), Math.Max(n2, n3));

        int cellMinE = (int)Math.Floor(minE / BITMAP_CELL_SIZE);
        int cellMaxE = (int)Math.Floor(maxE / BITMAP_CELL_SIZE);
        int cellMinN = (int)Math.Floor(minN / BITMAP_CELL_SIZE);
        int cellMaxN = (int)Math.Floor(maxN / BITMAP_CELL_SIZE);

        int count = 0;
        for (int ce = cellMinE; ce <= cellMaxE; ce++)
        {
            for (int cn = cellMinN; cn <= cellMaxN; cn++)
            {
                double cellCenterE = (ce + 0.5) * BITMAP_CELL_SIZE;
                double cellCenterN = (cn + 0.5) * BITMAP_CELL_SIZE;

                if (IsPointInTriangle(cellCenterE, cellCenterN, p0, p1, p2)
                    || IsPointInTriangle(cellCenterE, cellCenterN, p0, p2, p3))
                {
                    if (MarkCellCovered(ce, cn, 0))
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    // A corrupt header must not make SetFieldBounds allocate gigabytes. ~4 000 ha of
    // detection grid (500 MB of bits) is far beyond any real job.
    private const double MAX_LOAD_GRID_CELLS = 4e9;

    private void EnsureBoundsHoldLegacyFiles(string fieldDirectory)
    {
        // The detection grid is the exact field extent. The display grid is rounded up to whole
        // display cells, so it only stands in when there is no detection file; including it
        // would grow the bounds by up to a display cell on every reopen.
        var saved = ReadSavedExtent(Path.Combine(fieldDirectory, "coverage_detect.bin"), "COVD")
                    ?? ReadSavedExtent(Path.Combine(fieldDirectory, "coverage_disp.bin"), "COVS");
        if (saved is { } sv)
            EnsureBoundsHold(sv.MinE, sv.MaxE, sv.MinN, sv.MaxN);
    }

    /// <summary>Grow (or set) the field bounds so they hold saved coverage with this extent.</summary>
    private void EnsureBoundsHold(double minE, double maxE, double minN, double maxN)
    {
        void Include(double e0, double e1, double n0, double n1)
        {
            minE = Math.Min(minE, e0); maxE = Math.Max(maxE, e1);
            minN = Math.Min(minN, n0); maxN = Math.Max(maxN, n1);
        }

        lock (_coverageLock)
        {
            if (_fieldBoundsSet)
            {
                const double tol = 1e-6;
                if (minE >= _fieldMinE - tol && maxE <= _fieldMaxE + tol &&
                    minN >= _fieldMinN - tol && maxN <= _fieldMaxN + tol)
                    return; // already holds it — the usual case
                Include(_fieldMinE, _fieldMaxE, _fieldMinN, _fieldMaxN);
            }

            double cells = Math.Ceiling((maxE - minE) / BITMAP_CELL_SIZE) * Math.Ceiling((maxN - minN) / BITMAP_CELL_SIZE);
            if (cells > MAX_LOAD_GRID_CELLS)
            {
                Console.WriteLine($"[Coverage] Saved coverage extent too large to load ({cells:E1} cells); keeping current bounds");
                return;
            }

            Console.WriteLine($"[Coverage] Growing bounds to hold saved coverage: E[{minE:F1}, {maxE:F1}] N[{minN:F1}, {maxN:F1}]");
            SetFieldBoundsCore(minE, maxE, minN, maxN);
        }
    }

    /// <summary>
    /// World extent of a saved coverage file, from its header. Null when the file is
    /// missing, not that format, or the header is implausible.
    /// </summary>
    private static (double MinE, double MaxE, double MinN, double MaxN)? ReadSavedExtent(string path, string magic)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != magic)
                return null;
            reader.ReadByte(); // version
            if (magic == "COVS")
            {
                byte paletteSize = reader.ReadByte();
                stream.Seek(paletteSize * 2, SeekOrigin.Current);
            }
            // Stored as float: 0.1f is 0.10000000149, enough to push width x cell past the edge.
            double cell = Math.Round(reader.ReadSingle(), 6);
            double originE = reader.ReadDouble();
            double originN = reader.ReadDouble();
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();

            if (!(cell > 0) || !double.IsFinite(originE) || !double.IsFinite(originN) || width == 0 || height == 0)
                return null;
            // Pull the far edges in a hair so SetFieldBounds' Ceiling gives back exactly
            // width x height; otherwise rounding adds a cell, and a no-boundary job would
            // grow by one cell every time it is reopened.
            const double edge = 1e-6;
            double maxE = originE + width * cell - edge, maxN = originN + height * cell - edge;
            if ((double)width * height > MAX_LOAD_GRID_CELLS)
                return null;
            return (originE, maxE, originN, maxN);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Load detection bits from coverage_detect.bin (COVD format).
    /// Returns true if successfully loaded, false otherwise.
    /// </summary>
    private bool LoadDetectionBits(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, "coverage_detect.bin");
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open);
            using var reader = new BinaryReader(stream);

            // Read header
            var magic = new string(reader.ReadChars(4));
            if (magic != "COVD")
            {
                Console.WriteLine($"[Coverage] Invalid detection file magic: {magic}");
                return false;
            }

            byte version = reader.ReadByte();
            float resolution = reader.ReadSingle();
            double originE = reader.ReadDouble();
            double originN = reader.ReadDouble();
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();
            double area = reader.ReadDouble();

            Console.WriteLine($"[Coverage] Detection file v{version}: {width}x{height} @ {resolution}m, origin=({originE:F1}, {originN:F1}), area={area:F2}m²");

            // Verify resolution matches
            if (Math.Abs(resolution - BITMAP_CELL_SIZE) > 0.001)
            {
                Console.WriteLine($"[Coverage] Resolution mismatch: file={resolution}, expected={BITMAP_CELL_SIZE}");
                return false;
            }

            if (!_fieldBoundsSet || _detectionBits == null)
            {
                Console.WriteLine("[Coverage] LoadDetectionBits: no field bounds");
                return false;
            }

            // The file's grid: the same absolute 0.1 m cells, with its own origin and size
            // (LoadFromFile has grown the bounds to hold it). Decode straight into the live
            // array when the grids match, else into a scratch array copied in at its offset.
            int savedOriginE = (int)Math.Floor(originE / BITMAP_CELL_SIZE);
            int savedOriginN = (int)Math.Floor(originN / BITMAP_CELL_SIZE);
            bool sameGrid = savedOriginE == _bitmapOriginE && savedOriginN == _bitmapOriginN &&
                            width == _bitmapWidth && height == _bitmapHeight;
            long savedBytes = ((long)width * height + 7) / 8;
            Array.Clear(_detectionBits, 0, _detectionBits.Length);
            var decoded = sameGrid ? _detectionBits : new byte[savedBytes];

            // RLE decompress
            long destIndex = 0;
            long setBits = 0;
            while (destIndex < decoded.LongLength && stream.Position < stream.Length)
            {
                ushort runLength = reader.ReadUInt16();
                byte value = reader.ReadByte();

                for (int j = 0; j < runLength && destIndex < decoded.LongLength; j++, destIndex++)
                {
                    decoded[destIndex] = value;
                    // Count set bits for statistics
                    setBits += CountBits(value);
                }
            }

            if (!sameGrid)
                CopyDetectionBitsIn(decoded, (int)width, (int)height, savedOriginE, savedOriginN);

            // Update service state
            // A job migrated from Sections.txt before the legacy loader totalled its area was
            // saved with area 0: recover it from the covered cells.
            if (area <= 0 && setBits > 0) area = setBits * BITMAP_CELL_SIZE * BITMAP_CELL_SIZE;
            _totalWorkedArea = area;
            _totalWorkedAreaUser = area;
            _cellCountPerZone[0] = setBits;
            _boundsValid = setBits > 0;

            if (_boundsValid)
            {
                _minCellE = _bitmapOriginE;
                _maxCellE = _bitmapOriginE + _bitmapWidth - 1;
                _minCellN = _bitmapOriginN;
                _maxCellN = _bitmapOriginN + _bitmapHeight - 1;
            }

            Console.WriteLine($"[Coverage] Loaded detection bits: {setBits:N0} covered cells");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coverage] Failed to load detection bits: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Count number of set bits in a byte (population count).
    /// </summary>
    private static int CountBits(byte value)
    {
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    /// <summary>
    /// Load section display data from coverage_disp.bin (COVS format).
    /// Handles resolution scaling if saved resolution differs from current display resolution.
    /// Returns true if successfully loaded, false otherwise.
    /// </summary>
    private bool LoadSectionDisplay(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, "coverage_disp.bin");
        if (!File.Exists(path))
            return false;

        if (_displayPixels == null)
        {
            Console.WriteLine("[Coverage] LoadSectionDisplay: display buffer not allocated (no field bounds)");
            return false;
        }

        int targetWidth = _displayWidth;
        int targetHeight = _displayHeight;
        double targetCellSize = _displayCellSize;

        try
        {
            using var stream = new FileStream(path, FileMode.Open);
            using var reader = new BinaryReader(stream);

            // Read header
            var magic = new string(reader.ReadChars(4));
            if (magic != "COVS")
            {
                Console.WriteLine($"[Coverage] Invalid section display file magic: {magic}");
                return false;
            }

            byte version = reader.ReadByte();
            byte paletteSize = reader.ReadByte();

            // Read palette
            var palette = new ushort[paletteSize];
            for (int i = 0; i < paletteSize; i++)
                palette[i] = reader.ReadUInt16();

            // Read bitmap info from file
            float savedResolution = reader.ReadSingle();
            double originE = reader.ReadDouble();
            double originN = reader.ReadDouble();
            uint savedWidth = reader.ReadUInt32();
            uint savedHeight = reader.ReadUInt32();

            // Resample unless the saved grid is the current one. The saved grid can differ in
            // cell size (display quality) and in origin and size (bounds that grew while painting).
            double targetMinE = _displayOriginX * targetCellSize, targetMinN = _displayOriginY * targetCellSize;
            bool needsScaling = Math.Abs(savedResolution - targetCellSize) > 0.001 ||
                                savedWidth != targetWidth || savedHeight != targetHeight ||
                                Math.Abs(originE - targetMinE) > 1e-6 || Math.Abs(originN - targetMinN) > 1e-6;

            if (needsScaling)
                Console.WriteLine($"[Coverage] Section display v{version}: {savedWidth}x{savedHeight} @ {savedResolution}m origin ({originE:F1}, {originN:F1}) -> resampling to {targetWidth}x{targetHeight} @ {targetCellSize}m origin ({targetMinE:F1}, {targetMinN:F1})");
            else
                Console.WriteLine($"[Coverage] Section display v{version}: {savedWidth}x{savedHeight} @ {savedResolution}m, {paletteSize} colors");

            // Allocate buffer for saved data (section indices)
            long savedTotalPixels = (long)savedWidth * savedHeight;
            var savedIndices = new byte[savedTotalPixels];

            // RLE decompress section indices
            long destIndex = 0;
            while (destIndex < savedIndices.Length && stream.Position < stream.Length)
            {
                ushort runLength = reader.ReadUInt16();
                byte sectionIndex = reader.ReadByte();

                for (int j = 0; j < runLength && destIndex < savedIndices.Length; j++, destIndex++)
                {
                    savedIndices[destIndex] = sectionIndex;
                }
            }

            // Validate: if we didn't fill the expected size, file is corrupt
            // This catches old files where header dimensions didn't match actual data
            if (destIndex < savedTotalPixels * 0.9) // Allow 10% tolerance for RLE edge cases
            {
                Console.WriteLine($"[Coverage] Section display file corrupt: only {destIndex} indices for {savedTotalPixels} expected pixels");
                return false;
            }

            // Write decoded pixels directly into the service-owned buffer.
            // (Earlier versions allocated a fresh ushort[] and handed it off via
            // SetPixelBufferCallback to the 2D control; now we write in place.)
            var pixels = _displayPixels!;
            Array.Clear(pixels, 0, pixels.Length);
            long nonZeroPixels = 0;

            if (!needsScaling)
            {
                // No scaling - direct conversion
                long count = Math.Min(savedIndices.Length, pixels.Length);
                for (long i = 0; i < count; i++)
                {
                    byte idx = savedIndices[i];
                    if (idx > 0 && idx < palette.Length)
                    {
                        pixels[i] = palette[idx];
                        nonZeroPixels++;
                    }
                }
            }
            else
            {
                // Nearest-neighbour through world coordinates: for each target pixel,
                // take the saved pixel under its centre.
                for (int y = 0; y < targetHeight; y++)
                {
                    double worldN = targetMinN + (y + 0.5) * targetCellSize;
                    long srcY = (long)Math.Floor((worldN - originN) / savedResolution);
                    if (srcY < 0 || srcY >= savedHeight) continue;

                    for (int x = 0; x < targetWidth; x++)
                    {
                        double worldE = targetMinE + (x + 0.5) * targetCellSize;
                        long srcX = (long)Math.Floor((worldE - originE) / savedResolution);
                        if (srcX < 0 || srcX >= savedWidth) continue;

                        long srcIdx = (long)srcY * savedWidth + srcX;
                        long dstIdx = (long)y * targetWidth + x;

                        if (srcIdx < savedIndices.Length && dstIdx < pixels.Length)
                        {
                            byte idx = savedIndices[srcIdx];
                            if (idx > 0 && idx < palette.Length)
                            {
                                pixels[dstIdx] = palette[idx];
                                nonZeroPixels++;
                            }
                        }
                    }
                }
            }

            ExpandDirtyAll();
            Console.WriteLine($"[Coverage] Loaded section display: {nonZeroPixels:N0} covered pixels{(needsScaling ? " (scaled)" : "")}");

            // If the display file decoded to an empty canvas (no covered pixels) but the
            // caller also has detection bits to draw from, return false so LoadFromFile
            // still reports hasDetectionBits and fires CoverageUpdated, letting the UI
            // rebuild the display from detection bits. Otherwise a truncated / stale /
            // header-only disp file silently wins over valid detection data and the
            // field opens blank.
            if (nonZeroPixels == 0)
            {
                Console.WriteLine("[Coverage] Section display is empty — treating as no-display so detection bits can rebuild the map");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coverage] Failed to load section display: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convert RGB888 (0xRRGGBB) to RGB565.
    /// </summary>
    private static ushort Rgb888ToRgb565(uint rgb888)
    {
        byte r = (byte)((rgb888 >> 16) & 0xFF);
        byte g = (byte)((rgb888 >> 8) & 0xFF);
        byte b = (byte)(rgb888 & 0xFF);
        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }

    // Expand a packed RGB565 display pixel back to 8-bit-per-channel (replicate the high bits
    // into the dropped low bits so full-scale stays full-scale).
    private static CoverageColor Rgb565ToColor(ushort px)
    {
        int r5 = (px >> 11) & 0x1F, g6 = (px >> 5) & 0x3F, b5 = px & 0x1F;
        return new CoverageColor(
            (byte)((r5 << 3) | (r5 >> 2)),
            (byte)((g6 << 2) | (g6 >> 4)),
            (byte)((b5 << 3) | (b5 >> 2)));
    }

    /// <summary>
    /// Get color for a zone/section from configuration.
    /// Uses single color or per-section colors based on IsMultiColoredSections setting.
    /// </summary>
    private CoverageColor GetZoneColor(int zoneIndex)
    {
        var tool = _configStore.Tool;

        if (!tool.IsMultiColoredSections)
        {
            // Use single coverage color
            uint color = tool.SingleCoverageColor;
            return new CoverageColor(
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF)
            );
        }

        // Use per-section color from configuration
        uint sectionColor = tool.GetSectionColor(zoneIndex);
        return new CoverageColor(
            (byte)((sectionColor >> 16) & 0xFF),
            (byte)((sectionColor >> 8) & 0xFF),
            (byte)(sectionColor & 0xFF)
        );
    }
}
