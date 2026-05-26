// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Coverage;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Coverage;

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
            ? (_fieldMinE, _fieldMinN,
               _fieldMinE + _displayWidth * _displayCellSize,
               _fieldMinN + _displayHeight * _displayCellSize)
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
            if (!AgValoniaGPS.Models.Diagnostics.DiagFlags.PerfCoverage)
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

        // Track for batched write by map control (via GetNewCoverageBitmapCells)
        _newCells.Add((cellE, cellN, zone));

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
        int dx = (int)Math.Floor((worldX - _fieldMinE) / _displayCellSize);
        int dy = (int)Math.Floor((worldY - _fieldMinN) / _displayCellSize);
        if (dx < 0 || dx >= _displayWidth || dy < 0 || dy >= _displayHeight) return;

        ushort rgb565 = GetZoneColorRgb565(zone);
        _displayPixels[(long)dy * _displayWidth + dx] = rgb565;
        ExpandDirty(dx, dy);
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
    private static ushort GetZoneColorRgb565(int zoneIndex)
    {
        var tool = ConfigurationStore.Instance.Tool;
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

    public void ClearAll()
    {
        // Clear display pixel buffer and detection bits in lockstep
        if (_displayPixels != null)
            Array.Clear(_displayPixels, 0, _displayPixels.Length);
        ExpandDirtyAll();
        _newCells.Clear();
        if (_detectionBits != null)
            Array.Clear(_detectionBits, 0, _detectionBits.Length);
        _cellCountPerZone.Clear();

        // Reset bounds
        _minCellE = int.MaxValue;
        _maxCellE = int.MinValue;
        _minCellN = int.MaxValue;
        _maxCellN = int.MinValue;
        _boundsValid = false;

        // Clear tracking state
        _activeSections.Clear();
        _lastEdgesPerSection.Clear();

        // Reset totals
        _totalWorkedArea = 0;
        _totalWorkedAreaUser = 0;
        _coverageDirty = false;
        _pendingAreaAdded = 0;

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

    public void SetFieldBounds(double minE, double maxE, double minN, double maxN)
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

        // Calculate bitmap dimensions: (int)Math.Ceiling((max - min) / cellSize)
        _bitmapOriginE = (int)Math.Floor(minE / BITMAP_CELL_SIZE);
        _bitmapOriginN = (int)Math.Floor(minN / BITMAP_CELL_SIZE);
        _bitmapWidth = (int)Math.Ceiling((maxE - minE) / BITMAP_CELL_SIZE);
        _bitmapHeight = (int)Math.Ceiling((maxN - minN) / BITMAP_CELL_SIZE);

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

        // Compute display resolution + dimensions (pillared on
        // DisplayConfig.DisplayResolutionMultiplier and a 25M-pixel cap)
        // then allocate (or reuse) the RGB565 display buffer.
        _displayCellSize = ComputeDisplayCellSize(maxE - minE, maxN - minN);
        _displayWidth = (int)Math.Ceiling((maxE - minE) / _displayCellSize);
        _displayHeight = (int)Math.Ceiling((maxN - minN) / _displayCellSize);
        long totalDisplayPixels = (long)_displayWidth * _displayHeight;
        if (_displayPixels != null && _displayPixels.LongLength == totalDisplayPixels)
            Array.Clear(_displayPixels, 0, _displayPixels.Length);
        else
            _displayPixels = new ushort[totalDisplayPixels];
        _dirtyValid = false;

        double areaMSq = (maxE - minE) * (maxN - minN);
        double areaHa = areaMSq / 10000.0;
        double displayMB = totalDisplayPixels * 2 / (1024.0 * 1024.0);
        double detectionMB = totalBytes / (1024.0 * 1024.0);
        Console.WriteLine($"[Coverage] Field bounds set: E[{minE:F1}, {maxE:F1}] N[{minN:F1}, {maxN:F1}] {areaHa:F0}ha");
        Console.WriteLine($"[Coverage] Detection {_bitmapWidth}x{_bitmapHeight} @ {BITMAP_CELL_SIZE}m = {totalDetectionCells:N0} cells / {detectionMB:F1}MB");
        Console.WriteLine($"[Coverage] Display   {_displayWidth}x{_displayHeight} @ {_displayCellSize:F2}m = {totalDisplayPixels:N0} pixels / {displayMB:F1}MB");
    }

    /// <summary>
    /// Pick a display-layer cell size. Detection is fixed at 0.1m; display
    /// scales coarser when needed to keep the RGB565 buffer within budget:
    /// cap at ~25M pixels, then apply the user's DisplayResolutionMultiplier
    /// (Ultra=1.0, High=1.5, Med=2.5, Low=4.0, Min=6.0).
    /// </summary>
    private static double ComputeDisplayCellSize(double worldWidthM, double worldHeightM)
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

        double multiplier = ConfigurationStore.Instance.Display.DisplayResolutionMultiplier;
        if (multiplier > 1.0) cellSize *= multiplier;

        return cellSize;
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
        double oldMinE = _fieldMinE;
        double oldMinN = _fieldMinN;

        // Reallocate with new bounds
        SetFieldBounds(newMinE, newMaxE, newMinN, newMaxN);

        // Copy old detection bits to new array
        if (oldBits != null && _detectionBits != null)
        {
            int offsetE = oldOriginE - _bitmapOriginE;
            int offsetN = oldOriginN - _bitmapOriginN;

            for (int y = 0; y < oldHeight; y++)
            {
                for (int x = 0; x < oldWidth; x++)
                {
                    long oldIdx = (long)y * oldWidth + x;
                    if ((oldBits[oldIdx / 8] & (1 << (int)(oldIdx % 8))) != 0)
                    {
                        int newX = x + offsetE;
                        int newY = y + offsetN;
                        if (newX >= 0 && newX < _bitmapWidth && newY >= 0 && newY < _bitmapHeight)
                        {
                            long newIdx = (long)newY * _bitmapWidth + newX;
                            _detectionBits[newIdx / 8] |= (byte)(1 << (int)(newIdx % 8));
                        }
                    }
                }
            }
        }

        // Copy old display pixels to new buffer. If the display cell size
        // changed (rare — only when the new bounds cross a policy threshold),
        // resample by mapping each old pixel through world coordinates.
        if (oldPixels != null && _displayPixels != null)
        {
            bool sameCell = Math.Abs(oldDispCell - _displayCellSize) < 1e-9;
            for (int oy = 0; oy < oldDispHeight; oy++)
            {
                for (int ox = 0; ox < oldDispWidth; ox++)
                {
                    ushort px = oldPixels[(long)oy * oldDispWidth + ox];
                    if (px == 0) continue;

                    int nx, ny;
                    if (sameCell)
                    {
                        // Direct offset in display cells based on origin shift.
                        double worldX = oldMinE + (ox + 0.5) * oldDispCell;
                        double worldY = oldMinN + (oy + 0.5) * oldDispCell;
                        nx = (int)Math.Floor((worldX - _fieldMinE) / _displayCellSize);
                        ny = (int)Math.Floor((worldY - _fieldMinN) / _displayCellSize);
                    }
                    else
                    {
                        double worldX = oldMinE + (ox + 0.5) * oldDispCell;
                        double worldY = oldMinN + (oy + 0.5) * oldDispCell;
                        nx = (int)Math.Floor((worldX - _fieldMinE) / _displayCellSize);
                        ny = (int)Math.Floor((worldY - _fieldMinN) / _displayCellSize);
                    }
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
    /// Clear field bounds (when field is closed).
    /// </summary>
    public void ClearFieldBounds()
    {
        _fieldBoundsSet = false;
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
        Console.WriteLine("[Coverage] Field bounds cleared");
    }

    public void ResetUserArea()
    {
        _totalWorkedAreaUser = 0;
    }

    public void SaveToFile(string fieldDirectory)
    {
        // Save detection bits (authoritative coverage data at 0.1m resolution)
        SaveDetectionBits(fieldDirectory);

        // Save section display data (colors with palette, resolution-independent)
        SaveSectionDisplay(fieldDirectory);
    }

    public void LoadFromFile(string fieldDirectory)
    {
        // Load detection bits (authoritative coverage data at 0.1m resolution)
        bool hasDetectionBits = LoadDetectionBits(fieldDirectory);

        // Load section display (colors with palette, resolution-independent)
        bool hasSectionDisplay = LoadSectionDisplay(fieldDirectory);

        // Fallback: try legacy AgOpenGPS Sections.txt format
        if (!hasDetectionBits && !hasSectionDisplay)
        {
            hasDetectionBits = LoadLegacySections(fieldDirectory);
        }

        if (hasSectionDisplay || hasDetectionBits)
        {
            Console.WriteLine($"[Coverage] Loaded: detectionBits={hasDetectionBits}, sectionDisplay={hasSectionDisplay}");
            CoverageUpdated?.Invoke(this, new CoverageUpdatedEventArgs
            {
                TotalArea = _totalWorkedArea,
                PatchCount = (int)GetTotalCellCount(),
                AreaAdded = 0,
                IsFullReload = true
            });
        }
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

                int nPairs = (n - 1) / 2;

                // Read RGB color line (R,G,B format)
                if (lineIdx >= lines.Length) break;
                var colorParts = lines[lineIdx++].Split(',');
                // We ignore the color and use default coverage color

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
                Console.WriteLine($"[Coverage] Loaded legacy Sections.txt: {totalCells} cells rasterized");
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

    /// <summary>
    /// Save detection bits to coverage_detect.bin (COVD format).
    /// This is the authoritative source for coverage detection at 0.1m resolution.
    /// Format: Header + RLE-compressed bit array
    /// </summary>
    private void SaveDetectionBits(string fieldDirectory)
    {
        if (!_fieldBoundsSet || _detectionBits == null)
            return;

        var filename = Path.Combine(fieldDirectory, "coverage_detect.bin");

        using var stream = new FileStream(filename, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        // Write header - COVD format
        writer.Write("COVD".ToCharArray()); // Magic (4 bytes)
        writer.Write((byte)1);               // Version
        writer.Write((float)BITMAP_CELL_SIZE); // Resolution (always 0.1m)
        writer.Write(_fieldMinE);            // Origin E
        writer.Write(_fieldMinN);            // Origin N
        writer.Write((uint)_bitmapWidth);    // Width in cells
        writer.Write((uint)_bitmapHeight);   // Height in cells
        writer.Write(_totalWorkedArea);      // Total area for quick restore

        // RLE compress the bit array
        // Format: [runLength:ushort][value:byte] pairs
        // value is 0x00 (8 zero bits) or 0xFF (8 one bits) or actual mixed byte
        long compressedSize = 0;
        int i = 0;
        while (i < _detectionBits.Length)
        {
            byte value = _detectionBits[i];
            int runLength = 1;

            // Only RLE consecutive identical bytes
            while (i + runLength < _detectionBits.Length &&
                   _detectionBits[i + runLength] == value &&
                   runLength < 65535)
            {
                runLength++;
            }

            writer.Write((ushort)runLength);
            writer.Write(value);
            compressedSize += 3;
            i += runLength;
        }

        Console.WriteLine($"[Coverage] Saved detection bits: {_detectionBits.Length / 1024}KB -> {compressedSize / 1024}KB compressed to {filename}");
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

            // Calculate expected bit array size
            long totalCells = (long)width * height;
            int expectedBytes = (int)((totalCells + 7) / 8);

            // Allocate detection bits if needed
            if (_detectionBits == null || _detectionBits.Length != expectedBytes)
            {
                _detectionBits = new byte[expectedBytes];
            }
            Array.Clear(_detectionBits, 0, _detectionBits.Length);

            // RLE decompress
            int destIndex = 0;
            long setBits = 0;
            while (destIndex < _detectionBits.Length && stream.Position < stream.Length)
            {
                ushort runLength = reader.ReadUInt16();
                byte value = reader.ReadByte();

                for (int j = 0; j < runLength && destIndex < _detectionBits.Length; j++, destIndex++)
                {
                    _detectionBits[destIndex] = value;
                    // Count set bits for statistics
                    setBits += CountBits(value);
                }
            }

            // Update service state
            _bitmapWidth = (int)width;
            _bitmapHeight = (int)height;
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
    /// Save section display data to coverage_disp.bin (COVS format).
    /// Stores section indices with color palette for resolution-independent display.
    /// Format: Header + Palette + RLE-compressed section indices
    /// Uses detection bits to filter out background image pixels.
    /// </summary>
    private void SaveSectionDisplay(string fieldDirectory)
    {
        if (!_fieldBoundsSet || _displayPixels == null || _displayPixels.Length == 0)
            return;

        var pixels = _displayPixels;
        int dispWidth = _displayWidth;
        int dispHeight = _displayHeight;
        double dispCellSize = _displayCellSize;

        // Verify pixel count matches expected display dimensions
        long expectedPixels = (long)dispWidth * dispHeight;
        if (pixels.Length != expectedPixels)
        {
            Console.WriteLine($"[Coverage] SaveSectionDisplay: Pixel count mismatch: {pixels.Length} vs expected {expectedPixels}");
            return;
        }

        var filename = Path.Combine(fieldDirectory, "coverage_disp.bin");

        // Build palette from current tool config
        var tool = ConfigurationStore.Instance.Tool;
        var palette = new List<ushort>();
        var colorToIndex = new Dictionary<ushort, byte>();

        // Index 0 is reserved for "not covered"
        palette.Add(0);
        colorToIndex[0] = 0;

        // Add all section colors to palette
        for (int i = 0; i < 16; i++)
        {
            uint rgb888 = tool.GetSectionColor(i);
            ushort rgb565 = Rgb888ToRgb565(rgb888);
            if (!colorToIndex.ContainsKey(rgb565))
            {
                colorToIndex[rgb565] = (byte)palette.Count;
                palette.Add(rgb565);
            }
        }

        // Add single coverage color
        ushort singleColor = Rgb888ToRgb565(tool.SingleCoverageColor);
        if (!colorToIndex.ContainsKey(singleColor))
        {
            colorToIndex[singleColor] = (byte)palette.Count;
            palette.Add(singleColor);
        }

        // Calculate scale factor from detection to display resolution
        double scaleRatio = BITMAP_CELL_SIZE / dispCellSize; // e.g., 0.1/0.2 = 0.5

        // Scan COVERED pixels only - map detection coordinates to display coordinates
        // This is O(covered cells) not O(total pixels)
        var indices = new byte[pixels.Length];
        if (_detectionBits != null)
        {
            for (int byteIdx = 0; byteIdx < _detectionBits.Length; byteIdx++)
            {
                byte bits = _detectionBits[byteIdx];
                if (bits == 0) continue; // Skip 8 uncovered cells at once

                // Calculate detection cell coordinates for this byte
                long baseBitIdx = (long)byteIdx * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((bits & (1 << bit)) == 0) continue;

                    long bitIdx = baseBitIdx + bit;
                    int detY = (int)(bitIdx / _bitmapWidth);
                    int detX = (int)(bitIdx % _bitmapWidth);

                    // Map detection cell to display pixel
                    int dispX = (int)(detX * scaleRatio);
                    int dispY = (int)(detY * scaleRatio);

                    // Bounds check for display
                    if (dispX >= dispWidth || dispY >= dispHeight) continue;

                    long dispIdx = (long)dispY * dispWidth + dispX;
                    if (dispIdx >= pixels.Length) continue;

                    ushort color = pixels[dispIdx];
                    if (color == 0) continue;

                    // Add to palette if not seen
                    if (!colorToIndex.ContainsKey(color) && palette.Count < 255)
                    {
                        colorToIndex[color] = (byte)palette.Count;
                        palette.Add(color);
                    }

                    // Set index (may overwrite same pixel multiple times when downscaling, that's fine)
                    if (colorToIndex.TryGetValue(color, out byte idx))
                        indices[dispIdx] = idx;
                    else if (palette.Count > 1)
                        indices[dispIdx] = FindClosestColorIndex(color, palette);
                }
            }
        }
        else
        {
            // Fallback: iterate all pixels (slow but works without detection bits)
            for (long i = 0; i < pixels.Length; i++)
            {
                ushort color = pixels[i];
                if (color != 0)
                {
                    if (!colorToIndex.ContainsKey(color) && palette.Count < 255)
                    {
                        colorToIndex[color] = (byte)palette.Count;
                        palette.Add(color);
                    }
                    if (colorToIndex.TryGetValue(color, out byte idx))
                        indices[i] = idx;
                    else
                        indices[i] = FindClosestColorIndex(color, palette);
                }
            }
        }

        using var stream = new FileStream(filename, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        // Write header - COVS format
        writer.Write("COVS".ToCharArray());  // Magic (4 bytes)
        writer.Write((byte)1);                // Version
        writer.Write((byte)palette.Count);    // Palette size (1-255)

        // Write palette (RGB565 colors)
        foreach (var color in palette)
            writer.Write(color);

        // Write bitmap info - use ACTUAL display resolution and dimensions
        writer.Write((float)dispCellSize);    // Resolution when saved
        writer.Write(_fieldMinE);              // Origin E
        writer.Write(_fieldMinN);              // Origin N
        writer.Write((uint)dispWidth);         // Width at display resolution
        writer.Write((uint)dispHeight);        // Height at display resolution

        // RLE compress section indices
        long compressedSize = 0;
        int idx2 = 0;
        while (idx2 < indices.Length)
        {
            byte value = indices[idx2];
            int runLength = 1;
            while (idx2 + runLength < indices.Length &&
                   indices[idx2 + runLength] == value &&
                   runLength < 65535)
            {
                runLength++;
            }
            writer.Write((ushort)runLength);
            writer.Write(value);
            compressedSize += 3;
            idx2 += runLength;
        }

        Console.WriteLine($"[Coverage] Saved section display: {palette.Count} colors, {dispWidth}x{dispHeight} @ {dispCellSize}m -> {compressedSize / 1024}KB to {filename}");
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

            // Check if resolution scaling is needed (compare to actual display resolution, not detection)
            bool needsScaling = Math.Abs(savedResolution - targetCellSize) > 0.001 ||
                                savedWidth != targetWidth || savedHeight != targetHeight;
            double scaleRatio = savedResolution / targetCellSize;

            if (needsScaling)
                Console.WriteLine($"[Coverage] Section display v{version}: {savedWidth}x{savedHeight} @ {savedResolution}m -> scaling to {targetWidth}x{targetHeight} @ {targetCellSize}m (ratio {scaleRatio:F2})");
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
                // Scale using nearest-neighbor interpolation
                // For each pixel in target (display bitmap), find corresponding pixel in source (saved)
                for (int y = 0; y < targetHeight; y++)
                {
                    // Map target Y to source Y
                    int srcY = (int)(y * scaleRatio);
                    if (srcY >= savedHeight) srcY = (int)savedHeight - 1;

                    for (int x = 0; x < targetWidth; x++)
                    {
                        // Map target X to source X
                        int srcX = (int)(x * scaleRatio);
                        if (srcX >= savedWidth) srcX = (int)savedWidth - 1;

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

    /// <summary>
    /// Find closest color index in palette (simple Euclidean distance in RGB565 space).
    /// </summary>
    private static byte FindClosestColorIndex(ushort color, List<ushort> palette)
    {
        // Extract RGB components from RGB565
        int r1 = (color >> 11) & 0x1F;
        int g1 = (color >> 5) & 0x3F;
        int b1 = color & 0x1F;

        int bestIndex = 1; // Default to first non-zero color
        int bestDist = int.MaxValue;

        for (int i = 1; i < palette.Count; i++) // Skip index 0 (not covered)
        {
            int r2 = (palette[i] >> 11) & 0x1F;
            int g2 = (palette[i] >> 5) & 0x3F;
            int b2 = palette[i] & 0x1F;

            int dist = (r1 - r2) * (r1 - r2) + (g1 - g2) * (g1 - g2) + (b1 - b2) * (b1 - b2);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        return (byte)bestIndex;
    }

    /// <summary>
    /// Get color for a zone/section from configuration.
    /// Uses single color or per-section colors based on IsMultiColoredSections setting.
    /// </summary>
    private CoverageColor GetZoneColor(int zoneIndex)
    {
        var tool = ConfigurationStore.Instance.Tool;

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
