// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// Coverage bitmap subsystem for SkiaMapControl — ported from
// DrawingContextMapControl during Phase 2b of the GL map pivot.
// Mirrors the dual-bitmap (Rgb565 data + Bgra8888 display + SKBitmap shadow)
// layout used by DCMC so saved coverage round-trips identically and the
// background-image composite math stays consistent. The duplication is
// intentional for the transition window; Phase 4 deletes DCMC and the two
// files collapse to one canonical impl.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using AgValoniaGPS.Models.Coverage;
using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.Views.Controls;

public partial class SkiaMapControl
{
    // ------------------------------------------------------------------
    // Coverage bitmap state
    // ------------------------------------------------------------------

    private WriteableBitmap? _coverageWriteableBitmap;   // Rgb565 source-of-truth
    private WriteableBitmap? _coverageDisplayBitmap;     // Bgra8888 with transparency
    private SKBitmap? _coverageSkBitmap;                 // Render-thread shadow
    private SKBitmap? _previousCoverageSkBitmap;         // Two-deep retire buffer
    private SKBitmap? _retiredCoverageSkBitmap;

    private double _bitmapMinE, _bitmapMinN, _bitmapMaxE, _bitmapMaxN;
    private int _bitmapWidth, _bitmapHeight;
    private double _actualBitmapCellSize = MIN_BITMAP_CELL_SIZE;
    private bool _bitmapHasContent;
    private bool _bitmapExplicitlyInitialized;
    private bool _bitmapNeedsFullRebuild = true;
    private bool _bitmapNeedsIncrementalUpdate;
    private bool _bitmapUpdatePending;
    private volatile bool _writeableBitmapsDirty;
    private volatile bool _coverageSkImageDirty;

    // Provider callbacks (from ICoverageMapService)
    private Func<(double MinE, double MaxE, double MinN, double MaxN)?>? _coverageBoundsProvider;
    private Func<double, double, double, double, double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? _coverageAllCellsProvider;
    private Func<double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? _coverageNewCellsProvider;

    // Background image (composited into the coverage bitmap)
    private string? _backgroundImagePath;
    private Bitmap? _backgroundImage;
    private double _bgMinX, _bgMaxY, _bgMaxX, _bgMinY;
    private bool _backgroundComposited;
    private byte[]? _cachedBgPixels;
    private string? _cachedBgPath;
    private int _cachedBgPixelW;
    private int _cachedBgPixelH;
    private string? _compositedForPath;
    private double _compositedForMinE, _compositedForMinN;
    private int _compositedForWidth, _compositedForHeight;

    // Mercator sampling — DCMC supports it for satellite tiles; we keep the
    // field set but the linear-sample fallback path is the one actually used
    // (matches DCMC's `useMercator = false` shortcut today).
    private double _bgMercatorMinX, _bgMercatorMaxX, _bgMercatorMinY, _bgMercatorMaxY;
    private double _fieldOriginLat, _fieldOriginLon;
    private double _metersPerDegreeLat, _metersPerDegreeLon;
    private bool _useMercatorSampling;

    private const double MIN_BITMAP_CELL_SIZE = 0.1;
    private const bool USE_RGB565_FULL_RESOLUTION = false;

    // ------------------------------------------------------------------
    // ISharedMapControl coverage entry points
    // ------------------------------------------------------------------

    public void SetCoverageBitmapProviders(
        Func<(double MinE, double MaxE, double MinN, double MaxN)?>? boundsProvider,
        Func<double, double, double, double, double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? allCellsProvider,
        Func<double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? newCellsProvider)
    {
        _coverageBoundsProvider = boundsProvider;
        _coverageAllCellsProvider = allCellsProvider;
        _coverageNewCellsProvider = newCellsProvider;
        _bitmapNeedsFullRebuild = true;
    }

    public void MarkCoverageDirty()
    {
        _bitmapNeedsIncrementalUpdate = true;
        if (!_bitmapUpdatePending)
        {
            _bitmapUpdatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateCoverageBitmapIfNeeded();
                _bitmapUpdatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    public void MarkCoverageFullRebuildNeeded()
    {
        _bitmapNeedsFullRebuild = true;
        if (!_bitmapUpdatePending)
        {
            _bitmapUpdatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateCoverageBitmapIfNeeded();
                _bitmapUpdatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    public void InitializeCoverageBitmapWithBounds(double minE, double maxE, double minN, double maxN)
    {
        double worldWidth = maxE - minE;
        double worldHeight = maxN - minN;

        double cellSize = ComputeCellSize(worldWidth, worldHeight);
        int requiredWidth = (int)Math.Ceiling(worldWidth / cellSize);
        int requiredHeight = (int)Math.Ceiling(worldHeight / cellSize);
        if (requiredWidth <= 0 || requiredHeight <= 0) return;

        if (_coverageWriteableBitmap != null
            && Math.Abs(_bitmapMinE - minE) < 0.01
            && Math.Abs(_bitmapMaxE - maxE) < 0.01
            && Math.Abs(_bitmapMinN - minN) < 0.01
            && Math.Abs(_bitmapMaxN - maxN) < 0.01
            && _bitmapWidth == requiredWidth
            && _bitmapHeight == requiredHeight)
            return;

        _bitmapMinE = minE;
        _bitmapMaxE = maxE;
        _bitmapMinN = minN;
        _bitmapMaxN = maxN;
        _actualBitmapCellSize = cellSize;
        _bitmapWidth = requiredWidth;
        _bitmapHeight = requiredHeight;

        // Opening a new field is the canonical "the world just shifted" event —
        // reset the camera-initialized snap flag so the next vehicle position
        // recenters instead of slow-panning in from the previous field.
        _cameraInitialized = false;

        CreateCoverageBitmap();
        SendStateToHandler();

        _bitmapNeedsFullRebuild = false;
        _bitmapNeedsIncrementalUpdate = false;
    }

    public ushort GetCoveragePixel(int localX, int localY)
    {
        if (localX < 0 || localX >= _bitmapWidth || localY < 0 || localY >= _bitmapHeight) return 0;
        if (_coverageSkBitmap != null)
        {
            var c = _coverageSkBitmap.GetPixel(localX, localY);
            if (c.Alpha == 0) return 0;
            return (ushort)(((c.Red >> 3) << 11) | ((c.Green >> 2) << 5) | (c.Blue >> 3));
        }
        if (_coverageWriteableBitmap == null) return 0;
        using var framebuffer = _coverageWriteableBitmap.Lock();
        unsafe
        {
            ushort* ptr = (ushort*)framebuffer.Address;
            return ptr[localY * _bitmapWidth + localX];
        }
    }

    public void SetCoveragePixel(int localX, int localY, ushort rgb565)
    {
        if (localX < 0 || localX >= _bitmapWidth || localY < 0 || localY >= _bitmapHeight) return;
        if (rgb565 != 0) _bitmapHasContent = true;
        _coverageSkBitmap?.SetPixel(localX, localY,
            rgb565 == 0 ? SKColor.Empty : Rgb565ToSKColor(rgb565));
        _writeableBitmapsDirty = true;
        _coverageSkImageDirty = true;
    }

    public void ClearCoveragePixels()
    {
        if (_coverageWriteableBitmap == null) return;

        unsafe
        {
            using (var fb = _coverageWriteableBitmap.Lock())
                new Span<byte>((byte*)fb.Address, fb.RowBytes * _bitmapHeight).Clear();
        }
        _coverageSkBitmap?.Erase(SKColor.Empty);

        if (_coverageDisplayBitmap != null)
        {
            unsafe
            {
                using var fb = _coverageDisplayBitmap.Lock();
                new Span<byte>((byte*)fb.Address, fb.RowBytes * _bitmapHeight).Clear();
            }
        }

        if (!string.IsNullOrEmpty(_backgroundImagePath) && File.Exists(_backgroundImagePath))
        {
            // Re-compositing the field PNG into a 367 ha bitmap is a ~700 ms
            // per-cell loop that used to run synchronously here, producing a
            // 1-2 s UI beach ball on first-accelerate. Run it on a background
            // thread; the marshal-back blit is just a memcpy of ~28 MB which
            // is fast enough to fit in one frame.
            _backgroundComposited = false;
            ScheduleBackgroundCompositeAsync();
        }
        SendStateToHandler();
    }

    private int _compositeInFlight; // 0 = idle, 1 = running (Interlocked)
    private int _compositeStale;    // 1 = a re-schedule came in while running

    private void ScheduleBackgroundCompositeAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _compositeInFlight, 1, 0) != 0)
        {
            // Another composite is mid-flight; mark its result stale so the
            // completion path re-schedules with the current state.
            System.Threading.Volatile.Write(ref _compositeStale, 1);
            return;
        }
        System.Threading.Volatile.Write(ref _compositeStale, 0);

        // Snapshot UI-thread state so the background task works against a
        // stable view; the UI thread can keep mutating these between now and
        // marshal-back.
        int w = _bitmapWidth;
        int h = _bitmapHeight;
        double minE = _bitmapMinE;
        double minN = _bitmapMinN;
        double maxE = _bitmapMaxE;
        double maxN = _bitmapMaxN;
        double cellSize = _actualBitmapCellSize;
        double bgMinX = _bgMinX, bgMaxX = _bgMaxX, bgMinY = _bgMinY, bgMaxY = _bgMaxY;
        string? path = _backgroundImagePath;

        if (path == null || w <= 0 || h <= 0)
        {
            System.Threading.Volatile.Write(ref _compositeInFlight, 0);
            return;
        }

        double overlapMinE = Math.Max(bgMinX, minE);
        double overlapMaxE = Math.Min(bgMaxX, maxE);
        double overlapMinN = Math.Max(bgMinY, minN);
        double overlapMaxN = Math.Min(bgMaxY, maxN);
        if (overlapMinE >= overlapMaxE || overlapMinN >= overlapMaxN)
        {
            System.Threading.Volatile.Write(ref _compositeInFlight, 0);
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Decode + RGB-pack the PNG off the UI thread (was 100+ MB and
                // synchronous before). Uses the existing cache field so a
                // repeat composite for the same path is a no-decode hit.
                var bgPixels = LoadBackgroundPixelsCached(out int bgW, out int bgH);
                if (bgPixels == null || bgW <= 0 || bgH <= 0) return;

                double bgWorldWidth = bgMaxX - bgMinX;
                double bgWorldHeight = bgMaxY - bgMinY;
                if (bgWorldWidth <= 0 || bgWorldHeight <= 0) return;
                double bgPixelsPerMeterX = bgW / bgWorldWidth;
                double bgPixelsPerMeterY = bgH / bgWorldHeight;
                double halfCell = cellSize / 2.0;

                var buf = new ushort[w * h];
                for (int cy = 0; cy < h; cy++)
                {
                    double worldN = minN + cy * cellSize + halfCell;
                    if (worldN < overlapMinN || worldN >= overlapMaxN) continue;
                    int rowBase = cy * w;
                    for (int cx = 0; cx < w; cx++)
                    {
                        double worldE = minE + cx * cellSize + halfCell;
                        if (worldE < overlapMinE || worldE >= overlapMaxE) continue;
                        int bgX = (int)((worldE - bgMinX) * bgPixelsPerMeterX);
                        int bgY = (int)((bgMaxY - worldN) * bgPixelsPerMeterY);
                        if (bgX < 0 || bgX >= bgW || bgY < 0 || bgY >= bgH) continue;
                        int bgIdx = (bgY * bgW + bgX) * 4;
                        byte b = bgPixels[bgIdx];
                        byte g = bgPixels[bgIdx + 1];
                        byte r = bgPixels[bgIdx + 2];
                        buf[rowBase + cx] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        BlitCompositeBuffer(buf, w, h, minE, minN);
                    }
                    finally
                    {
                        bool stale = System.Threading.Interlocked.Exchange(ref _compositeStale, 0) == 1;
                        System.Threading.Volatile.Write(ref _compositeInFlight, 0);
                        if (stale) ScheduleBackgroundCompositeAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SkiaMapControl] Async composite failed: {ex.Message}");
                System.Threading.Volatile.Write(ref _compositeInFlight, 0);
            }
        });
    }

    private unsafe void BlitCompositeBuffer(ushort[] src, int w, int h, double snapMinE, double snapMinN)
    {
        // Verify the bitmap state hasn't shifted out from under the async
        // composite (field changed, bitmap resized, path swapped). If it has,
        // the buffer is stale — discard and let the next schedule run.
        if (_coverageWriteableBitmap == null
            || _bitmapWidth != w
            || _bitmapHeight != h
            || Math.Abs(_bitmapMinE - snapMinE) > 0.01
            || Math.Abs(_bitmapMinN - snapMinN) > 0.01)
            return;

        using (var fb = _coverageWriteableBitmap.Lock())
        {
            ushort* dst = (ushort*)fb.Address;
            int dstStride = fb.RowBytes / 2;
            fixed (ushort* srcPtr = src)
            {
                for (int y = 0; y < h; y++)
                    System.Buffer.MemoryCopy(srcPtr + y * w, dst + y * dstStride, w * 2L, w * 2L);
            }
        }

        _backgroundComposited = true;
        _compositedForPath = _backgroundImagePath;
        _compositedForMinE = _bitmapMinE;
        _compositedForMinN = _bitmapMinN;
        _compositedForWidth = _bitmapWidth;
        _compositedForHeight = _bitmapHeight;
        SyncDisplayBitmap();
        SyncSkBitmapFromDisplay();
        SendStateToHandler();
    }

    public ushort[]? GetCoveragePixelBuffer()
    {
        if (_coverageWriteableBitmap == null || _bitmapWidth == 0 || _bitmapHeight == 0) return null;
        SyncWriteableBitmapsFromSk();
        var pixels = new ushort[_bitmapWidth * _bitmapHeight];
        using var fb = _coverageWriteableBitmap.Lock();
        unsafe
        {
            ushort* src = (ushort*)fb.Address;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = src[i];
        }
        return pixels;
    }

    public (int Width, int Height, double CellSize)? GetDisplayBitmapInfo()
    {
        if (_bitmapWidth == 0 || _bitmapHeight == 0) return null;
        return (_bitmapWidth, _bitmapHeight, _actualBitmapCellSize);
    }

    public void SetCoveragePixelBuffer(ushort[] pixels)
    {
        if (pixels == null || _bitmapWidth == 0 || _bitmapHeight == 0) return;
        if (_coverageWriteableBitmap == null
            || _coverageWriteableBitmap.PixelSize.Width != _bitmapWidth
            || _coverageWriteableBitmap.PixelSize.Height != _bitmapHeight)
            CreateCoverageBitmap();

        unsafe
        {
            using (var fb = _coverageWriteableBitmap!.Lock())
            {
                ushort* dst = (ushort*)fb.Address;
                int count = Math.Min(pixels.Length, _bitmapWidth * _bitmapHeight);
                for (int i = 0; i < count; i++)
                    if (pixels[i] != 0) dst[i] = pixels[i];
            }
            if (_coverageDisplayBitmap != null)
            {
                using var dispFb = _coverageDisplayBitmap.Lock();
                using var dataFb = _coverageWriteableBitmap.Lock();
                ushort* src = (ushort*)dataFb.Address;
                uint* dst = (uint*)dispFb.Address;
                int count = _bitmapWidth * _bitmapHeight;
                for (int i = 0; i < count; i++) dst[i] = Rgb565ToBgra8888(src[i]);
            }
            if (_coverageSkBitmap != null && _coverageDisplayBitmap != null)
            {
                using var dispFb = _coverageDisplayBitmap.Lock();
                uint* src = (uint*)dispFb.Address;
                uint* dst = (uint*)_coverageSkBitmap.GetPixels();
                int count = _bitmapWidth * _bitmapHeight;
                Buffer.MemoryCopy(src, dst, count * 4, count * 4);
            }
        }
        _bitmapHasContent = true;
        SendStateToHandler();
    }

    // ------------------------------------------------------------------
    // Background image
    // ------------------------------------------------------------------

    public void SetBackgroundImage(string imagePath, double minX, double maxY, double maxX, double minY)
    {
        if (_backgroundImagePath != imagePath)
        {
            _cachedBgPixels = null;
            _cachedBgPath = null;
            _compositedForPath = null;
        }

        _backgroundImagePath = imagePath;
        _bgMinX = minX;
        _bgMaxY = maxY;
        _bgMaxX = maxX;
        _bgMinY = minY;

        _backgroundImage?.Dispose();
        _backgroundImage = null;

        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            try
            {
                _backgroundImage = new Bitmap(imagePath);
                if (_coverageWriteableBitmap != null && _bitmapWidth > 0 && _bitmapHeight > 0)
                {
                    CompositeBackgroundIntoBitmap();
                    SyncSkBitmapFromDisplay();
                    _backgroundComposited = true;
                }
                else
                {
                    _backgroundComposited = false;
                }
                SendStateToHandler();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SkiaMapControl] Failed to load background image: {ex.Message}");
                _backgroundComposited = false;
            }
        }
        else
        {
            _backgroundComposited = false;
        }
    }

    public void SetBackgroundImageWithMercator(string imagePath, double minX, double maxY, double maxX, double minY,
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        double originLat, double originLon)
    {
        _bgMercatorMinX = mercMinX;
        _bgMercatorMaxX = mercMaxX;
        _bgMercatorMinY = mercMinY;
        _bgMercatorMaxY = mercMaxY;
        _fieldOriginLat = originLat;
        _fieldOriginLon = originLon;
        _useMercatorSampling = true;

        double originLatRad = originLat * Math.PI / 180.0;
        _metersPerDegreeLat = 111132.92 - 559.82 * Math.Cos(2.0 * originLatRad)
            + 1.175 * Math.Cos(4.0 * originLatRad) - 0.0023 * Math.Cos(6.0 * originLatRad);
        _metersPerDegreeLon = 111412.84 * Math.Cos(originLatRad)
            - 93.5 * Math.Cos(3.0 * originLatRad) + 0.118 * Math.Cos(5.0 * originLatRad);

        SetBackgroundImage(imagePath, minX, maxY, maxX, minY);
    }

    public void ClearBackground()
    {
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _backgroundImagePath = null;
        _backgroundComposited = false;
        _useMercatorSampling = false;
        _bgMinX = _bgMaxX = _bgMinY = _bgMaxY = 0;
        _compositedForPath = null;
        // Intentionally leave _cachedBgPixels/_cachedBgPath alive across close.
        // CloseFieldAsync → ClearBackground fires on every field close; nulling
        // here forced a fresh 80 MB LOH decode on every re-open of the same
        // field, which is the reliable beach-ball repro. SetBackgroundImage
        // (line 426) already invalidates the cache when a DIFFERENT path is
        // set, which is the only correctness requirement.
    }

    // ------------------------------------------------------------------
    // Internal helpers
    // ------------------------------------------------------------------

    private void EnsureCoverageBitmapReady()
    {
        if (_coverageWriteableBitmap == null || _bitmapWidth == 0 || _bitmapHeight == 0)
        {
            if (!_bitmapUpdatePending && _coverageBoundsProvider != null)
            {
                var bounds = _coverageBoundsProvider();
                if (bounds != null)
                {
                    _bitmapUpdatePending = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateCoverageBitmapIfNeeded();
                        _bitmapUpdatePending = false;
                        SendStateToHandler();
                    }, DispatcherPriority.Background);
                }
            }
            return;
        }

        if (!_backgroundComposited)
        {
            if (!string.IsNullOrEmpty(_backgroundImagePath) && File.Exists(_backgroundImagePath))
            {
                CompositeBackgroundIntoBitmap();
                SyncSkBitmapFromDisplay();
            }
            else
            {
                unsafe
                {
                    using var fb = _coverageWriteableBitmap.Lock();
                    int count = _bitmapWidth * _bitmapHeight;
                    ushort* pixels = (ushort*)fb.Address;
                    for (int i = 0; i < count; i++) pixels[i] = 0;
                }
                _coverageSkBitmap?.Erase(SKColor.Empty);
                _backgroundComposited = true;
            }
        }
    }

    private void UpdateCoverageBitmapIfNeeded()
    {
        if (_coverageBoundsProvider == null || _coverageAllCellsProvider == null) return;
        var bounds = _coverageBoundsProvider();
        if (bounds == null)
        {
            if (_coverageWriteableBitmap != null && !_bitmapExplicitlyInitialized)
            {
                _coverageWriteableBitmap.Dispose();
                _coverageWriteableBitmap = null;
                _bitmapWidth = 0;
                _bitmapHeight = 0;
            }
            return;
        }

        var (minE, maxE, minN, maxN) = bounds.Value;
        double worldWidth = maxE - minE;
        double worldHeight = maxN - minN;
        if (worldWidth <= 0 || worldHeight <= 0) return;

        double cellSize = ComputeCellSize(worldWidth, worldHeight);
        _actualBitmapCellSize = cellSize;
        int requiredWidth = (int)Math.Ceiling(worldWidth / cellSize);
        int requiredHeight = (int)Math.Ceiling(worldHeight / cellSize);
        if (requiredWidth <= 0 || requiredHeight <= 0) return;

        bool boundsChanged = _coverageWriteableBitmap == null
            || Math.Abs(_bitmapMinE - minE) > 0.01
            || Math.Abs(_bitmapMinN - minN) > 0.01
            || _bitmapWidth != requiredWidth
            || _bitmapHeight != requiredHeight;

        if (boundsChanged)
        {
            _bitmapMinE = minE;
            _bitmapMinN = minN;
            _bitmapMaxE = maxE;
            _bitmapMaxN = maxN;
            _bitmapWidth = requiredWidth;
            _bitmapHeight = requiredHeight;
            _bitmapNeedsFullRebuild = true;
            CreateCoverageBitmap();
        }

        if (_bitmapNeedsFullRebuild)
        {
            UpdateCoverageBitmapFull();
            _bitmapNeedsFullRebuild = false;
            _bitmapNeedsIncrementalUpdate = false;
        }
        else if (_bitmapNeedsIncrementalUpdate)
        {
            UpdateCoverageBitmapIncremental();
            _bitmapNeedsIncrementalUpdate = false;
        }
    }

    private unsafe void CreateCoverageBitmap()
    {
        if (_bitmapWidth <= 0 || _bitmapHeight <= 0) return;

        _coverageWriteableBitmap?.Dispose();
        _coverageDisplayBitmap?.Dispose();
        _retiredCoverageSkBitmap?.Dispose();
        _retiredCoverageSkBitmap = _previousCoverageSkBitmap;
        _previousCoverageSkBitmap = _coverageSkBitmap;
        _coverageSkBitmap = null;

        _coverageWriteableBitmap = new WriteableBitmap(
            new PixelSize(_bitmapWidth, _bitmapHeight),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgb565);

        _coverageDisplayBitmap = new WriteableBitmap(
            new PixelSize(_bitmapWidth, _bitmapHeight),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888);

        _coverageSkBitmap = new SKBitmap(_bitmapWidth, _bitmapHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        _coverageSkBitmap.Erase(SKColor.Empty);

        using (var fb = _coverageWriteableBitmap.Lock())
            new Span<byte>((byte*)fb.Address, fb.RowBytes * _bitmapHeight).Clear();
        using (var fb = _coverageDisplayBitmap.Lock())
            new Span<byte>((byte*)fb.Address, fb.RowBytes * _bitmapHeight).Clear();

        if (!string.IsNullOrEmpty(_backgroundImagePath) && File.Exists(_backgroundImagePath))
        {
            _backgroundComposited = false;
            CompositeBackgroundIntoBitmap();
            SyncSkBitmapFromDisplay();
            _bitmapHasContent = true;
        }
        else
        {
            _backgroundComposited = false;
            _bitmapHasContent = false;
        }
        _bitmapExplicitlyInitialized = true;
    }

    private unsafe int UpdateCoverageBitmapFull()
    {
        if (_coverageWriteableBitmap == null || _coverageAllCellsProvider == null) return 0;

        if (!_backgroundComposited)
        {
            using (var fb = _coverageWriteableBitmap.Lock())
                new Span<byte>((byte*)fb.Address, fb.RowBytes * _bitmapHeight).Clear();

            if (!string.IsNullOrEmpty(_backgroundImagePath) && File.Exists(_backgroundImagePath))
            {
                CompositeBackgroundIntoBitmap();
                SyncSkBitmapFromDisplay();
            }
        }

        int cellCount = 0;
        using (var fb = _coverageWriteableBitmap.Lock())
        {
            int stride = fb.RowBytes;
            byte* ptr = (byte*)fb.Address;
            foreach (var (cellX, cellY, color) in _coverageAllCellsProvider(
                _actualBitmapCellSize, _bitmapMinE, _bitmapMaxE, _bitmapMinN, _bitmapMaxN))
            {
                if (cellX < 0 || cellX >= _bitmapWidth || cellY < 0 || cellY >= _bitmapHeight) continue;
                ushort* pixel = (ushort*)(ptr + cellY * stride + cellX * 2);
                ushort rgb565 = (ushort)(((color.R >> 3) << 11) | ((color.G >> 2) << 5) | (color.B >> 3));
                *pixel = rgb565;
                cellCount++;
            }
        }

        if (cellCount > 0)
        {
            SyncDisplayBitmap();
            SyncSkBitmapFromDisplay();
            _bitmapHasContent = true;
        }
        return cellCount;
    }

    private unsafe int UpdateCoverageBitmapIncremental()
    {
        if (_coverageWriteableBitmap == null || _coverageNewCellsProvider == null) return 0;

        using var dataFb = _coverageWriteableBitmap.Lock();
        byte* dataPtr = (byte*)dataFb.Address;
        int dataStride = dataFb.RowBytes;

        var dispFb = _coverageDisplayBitmap?.Lock();
        uint* dispPtr = dispFb != null ? (uint*)dispFb.Address : null;

        int cellCount = 0;
        foreach (var (cellX, cellY, color) in _coverageNewCellsProvider(_actualBitmapCellSize))
        {
            if (cellX < 0 || cellX >= _bitmapWidth || cellY < 0 || cellY >= _bitmapHeight) continue;
            ushort* pixel = (ushort*)(dataPtr + cellY * dataStride + cellX * 2);
            ushort rgb565 = (ushort)(((color.R >> 3) << 11) | ((color.G >> 2) << 5) | (color.B >> 3));
            *pixel = rgb565;
            if (dispPtr != null)
                dispPtr[cellY * _bitmapWidth + cellX] = Rgb565ToBgra8888(rgb565);
            _coverageSkBitmap?.SetPixel(cellX, cellY,
                rgb565 == 0 ? SKColor.Empty : Rgb565ToSKColor(rgb565));
            _coverageSkImageDirty = true;
            _bitmapHasContent = true;
            cellCount++;
        }

        dispFb?.Dispose();
        return cellCount;
    }

    private unsafe void CompositeBackgroundIntoBitmap()
    {
        if (_backgroundImage == null || _coverageWriteableBitmap == null
            || _bitmapWidth == 0 || _bitmapHeight == 0)
        {
            _backgroundComposited = false;
            return;
        }

        // Skip redundant composite when nothing changed
        if (_backgroundComposited
            && _compositedForPath == _backgroundImagePath
            && _compositedForWidth == _bitmapWidth
            && _compositedForHeight == _bitmapHeight
            && Math.Abs(_compositedForMinE - _bitmapMinE) < 0.01
            && Math.Abs(_compositedForMinN - _bitmapMinN) < 0.01)
            return;

        double overlapMinE = Math.Max(_bgMinX, _bitmapMinE);
        double overlapMaxE = Math.Min(_bgMaxX, _bitmapMaxE);
        double overlapMinN = Math.Max(_bgMinY, _bitmapMinN);
        double overlapMaxN = Math.Min(_bgMaxY, _bitmapMaxN);
        if (overlapMinE >= overlapMaxE || overlapMinN >= overlapMaxN)
        {
            _backgroundComposited = false;
            return;
        }

        byte[]? bgPixelData = LoadBackgroundPixelsCached(out int bgWidth, out int bgHeight);
        if (bgPixelData == null) { _backgroundComposited = false; return; }

        double bgWorldWidth = _bgMaxX - _bgMinX;
        double bgWorldHeight = _bgMaxY - _bgMinY;
        double bgPixelsPerMeterX = bgWidth / bgWorldWidth;
        double bgPixelsPerMeterY = bgHeight / bgWorldHeight;

        using var covBuffer = _coverageWriteableBitmap.Lock();
        ushort* covPixels = (ushort*)covBuffer.Address;
        int covStride = covBuffer.RowBytes / 2;
        double halfCell = _actualBitmapCellSize / 2.0;

        for (int cy = 0; cy < _bitmapHeight; cy++)
        {
            double worldN = _bitmapMinN + cy * _actualBitmapCellSize + halfCell;
            if (worldN < overlapMinN || worldN >= overlapMaxN) continue;
            for (int cx = 0; cx < _bitmapWidth; cx++)
            {
                double worldE = _bitmapMinE + cx * _actualBitmapCellSize + halfCell;
                if (worldE < overlapMinE || worldE >= overlapMaxE) continue;

                int bgX = (int)((worldE - _bgMinX) * bgPixelsPerMeterX);
                int bgY = (int)((_bgMaxY - worldN) * bgPixelsPerMeterY);
                if (bgX < 0 || bgX >= bgWidth || bgY < 0 || bgY >= bgHeight) continue;

                int bgIdx = (bgY * bgWidth + bgX) * 4;
                byte b = bgPixelData[bgIdx];
                byte g = bgPixelData[bgIdx + 1];
                byte r = bgPixelData[bgIdx + 2];
                ushort rgb565 = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                covPixels[cy * covStride + cx] = rgb565;
            }
        }

        _backgroundComposited = true;
        _compositedForPath = _backgroundImagePath;
        _compositedForMinE = _bitmapMinE;
        _compositedForMinN = _bitmapMinN;
        _compositedForWidth = _bitmapWidth;
        _compositedForHeight = _bitmapHeight;

        // Keep _cachedBgPixels alive until the path changes (SetBackgroundImage)
        // or the background is cleared (ClearBackground). The original code
        // nulled it here to "release once composite is done", but that meant
        // each close + reopen of the same field re-decoded the 64+ MB PNG into
        // LOH and the GC eventually paused for 1-3 s — the reliable beach-ball
        // repro. Holding ~80 MB while a field is open is cheaper than the
        // accumulated LOH churn it would otherwise create.

        SyncDisplayBitmap();
        SyncSkBitmapFromDisplay();
    }

    private byte[]? LoadBackgroundPixelsCached(out int width, out int height)
    {
        width = 0; height = 0;
        if (string.IsNullOrEmpty(_backgroundImagePath) || !File.Exists(_backgroundImagePath))
            return null;
        if (_cachedBgPixels != null && _cachedBgPath == _backgroundImagePath)
        {
            width = _cachedBgPixelW;
            height = _cachedBgPixelH;
            return _cachedBgPixels;
        }
        try
        {
            using var skBitmap = SKBitmap.Decode(_backgroundImagePath);
            if (skBitmap == null) return null;
            var data = new byte[skBitmap.Width * skBitmap.Height * 4];
            var pixels = skBitmap.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                data[i * 4 + 0] = pixels[i].Blue;
                data[i * 4 + 1] = pixels[i].Green;
                data[i * 4 + 2] = pixels[i].Red;
                data[i * 4 + 3] = pixels[i].Alpha;
            }
            _cachedBgPixels = data;
            _cachedBgPath = _backgroundImagePath;
            _cachedBgPixelW = skBitmap.Width;
            _cachedBgPixelH = skBitmap.Height;
            width = skBitmap.Width;
            height = skBitmap.Height;
            return data;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkiaMapControl] Failed to decode background: {ex.Message}");
            return null;
        }
    }

    private unsafe void SyncDisplayBitmap()
    {
        if (_coverageWriteableBitmap == null || _coverageDisplayBitmap == null) return;
        using var dataFb = _coverageWriteableBitmap.Lock();
        using var dispFb = _coverageDisplayBitmap.Lock();
        ushort* src = (ushort*)dataFb.Address;
        uint* dst = (uint*)dispFb.Address;
        int count = _bitmapWidth * _bitmapHeight;
        for (int i = 0; i < count; i++) dst[i] = Rgb565ToBgra8888(src[i]);
    }

    private unsafe void SyncSkBitmapFromDisplay()
    {
        if (_coverageSkBitmap == null || _coverageDisplayBitmap == null) return;
        using var fb = _coverageDisplayBitmap.Lock();
        var src = (uint*)fb.Address;
        var dst = (uint*)_coverageSkBitmap.GetPixels();
        int count = _bitmapWidth * _bitmapHeight;
        Buffer.MemoryCopy(src, dst, count * 4, count * 4);
        _coverageSkImageDirty = true;
    }

    private unsafe void SyncWriteableBitmapsFromSk()
    {
        if (!_writeableBitmapsDirty || _coverageSkBitmap == null) return;
        _writeableBitmapsDirty = false;

        var skPixels = (uint*)_coverageSkBitmap.GetPixels();
        int count = _bitmapWidth * _bitmapHeight;

        if (_coverageDisplayBitmap != null)
        {
            using var fb = _coverageDisplayBitmap.Lock();
            Buffer.MemoryCopy(skPixels, (void*)fb.Address, count * 4, count * 4);
        }
        if (_coverageWriteableBitmap != null)
        {
            using var fb = _coverageWriteableBitmap.Lock();
            ushort* dst = (ushort*)fb.Address;
            for (int i = 0; i < count; i++)
            {
                uint bgra = skPixels[i];
                if ((bgra & 0xFF000000) == 0) { dst[i] = 0; continue; }
                byte r = (byte)((bgra >> 16) & 0xFF);
                byte g = (byte)((bgra >> 8) & 0xFF);
                byte b = (byte)(bgra & 0xFF);
                dst[i] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            }
        }
    }

    private static double ComputeCellSize(double worldWidth, double worldHeight)
    {
        double cellSize;
        if (USE_RGB565_FULL_RESOLUTION)
        {
            cellSize = MIN_BITMAP_CELL_SIZE;
        }
        else
        {
            const long MAX_PIXELS = 25_000_000;
            cellSize = MIN_BITMAP_CELL_SIZE;
            long pixelsAtMinRes = (long)Math.Ceiling(worldWidth / MIN_BITMAP_CELL_SIZE) *
                                  (long)Math.Ceiling(worldHeight / MIN_BITMAP_CELL_SIZE);
            if (pixelsAtMinRes > MAX_PIXELS)
            {
                double scaleFactor = Math.Sqrt((double)pixelsAtMinRes / MAX_PIXELS);
                cellSize = MIN_BITMAP_CELL_SIZE * scaleFactor;
                if (cellSize <= 0.2) cellSize = 0.2;
                else if (cellSize <= 0.25) cellSize = 0.25;
                else if (cellSize <= 0.35) cellSize = 0.35;
                else if (cellSize <= 0.5) cellSize = 0.5;
                else if (cellSize <= 0.75) cellSize = 0.75;
                else cellSize = Math.Ceiling(cellSize);
            }
        }
        return ApplyResolutionMultiplier(cellSize);
    }

    private static double ApplyResolutionMultiplier(double cellSize)
    {
        var multiplier = ConfigurationStore.Instance.Display.DisplayResolutionMultiplier;
        if (multiplier <= 1.0) return cellSize;
        return cellSize * multiplier;
    }

    private static SKColor Rgb565ToSKColor(ushort rgb565)
    {
        byte r = (byte)((rgb565 >> 11) << 3);
        byte g = (byte)(((rgb565 >> 5) & 0x3F) << 2);
        byte b = (byte)((rgb565 & 0x1F) << 3);
        return new SKColor(r, g, b, 255);
    }

    private static uint Rgb565ToBgra8888(ushort rgb565)
    {
        if (rgb565 == 0) return 0;
        byte r = (byte)((rgb565 >> 11) << 3);
        byte g = (byte)(((rgb565 >> 5) & 0x3F) << 2);
        byte b = (byte)((rgb565 & 0x1F) << 3);
        return (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
    }
}
