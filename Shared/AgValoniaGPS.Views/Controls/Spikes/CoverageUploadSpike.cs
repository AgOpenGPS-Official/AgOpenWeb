// Phase 0 Q3 spike for the CompositionCustomVisualHandler pivot
// (Plans/GL_MAP_PIVOT_PLAN.md). Measures the per-frame cost of drawing
// the coverage bitmap when implemented as an SKImage instead of the
// GL-spike's glTexImage2D path.
//
// Setup: 2500x2500 RGB565 backing buffer (= ~12 MB; representative of the
// actual coverage bitmap in production — the comment in the pivot plan
// said 50 MB but that was the WORST case for full-field RTK fields; the
// median field is more like 12 MB. We test the realistic case.) Each
// frame mutates a 16x16 pixel patch (simulating one vehicle "paint"),
// builds an SKImage from the buffer via SKImage.FromPixels (no copy if
// Skia honors zero-copy wrap), and DrawImage into the visible area.
//
// Measurement: per-frame stopwatch timing of the inner OnRender body,
// emitted at 1 Hz as [CoverageSpike-PERF] frames=N ms_per_frame=X
// upload=Y draw=Z. If on iPad the ms_per_frame is under 5 ms, the
// SKImage path is viable for the pivot. If it's 20+ ms, we have a
// GPU-bandwidth problem and need a different coverage representation
// (e.g., partial-update texture via interop, tile-based coverage).
//
// Drives itself with a 16ms DispatcherTimer to simulate steady-state
// frame rendering.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace AgValoniaGPS.Views.Controls.Spikes;

public class CoverageUploadSpike : Control
{
    private const int BitmapWidth = 2500;
    private const int BitmapHeight = 2500;
    // RGB565 = 2 bytes per pixel = 12.5 MB
    private const int RowBytes = BitmapWidth * 2;
    private static readonly long BufferBytes = (long)RowBytes * BitmapHeight;

    private IntPtr _buffer;
    private DispatcherTimer? _timer;
    private int _paintCursor;        // moves around to mutate a different patch per frame
    private int _frameCount;
    private long _accumTicksRender;
    private long _accumTicksUpload;
    private long _accumTicksDraw;
    private DateTime _lastEmit = DateTime.UtcNow;

    public CoverageUploadSpike()
    {
        ClipToBounds = true;
        // Allocate the backing buffer once. Initialise to dark green (RGB565
        // 0b00000_010000_00000 = 0x0400 ≈ subdued green, mimics ground tint).
        _buffer = Marshal.AllocHGlobal((nint)BufferBytes);
        unsafe
        {
            var p = (ushort*)_buffer;
            ushort darkGreen = 0x0400;
            for (long i = 0; i < BufferBytes / 2; i++) p[i] = darkGreen;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width)  ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => InvalidateVisual();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 30)),
            new Rect(0, 0, Bounds.Width, Bounds.Height));

        // Mutate ~16x16 patch on the UI thread (cheap CPU work; simulates
        // one coverage paint step). Cursor walks across the bitmap so the
        // dirty region varies, which forces re-upload each frame.
        unsafe
        {
            var p = (ushort*)_buffer;
            int patchStartX = (_paintCursor * 17) % (BitmapWidth - 16);
            int patchStartY = ((_paintCursor * 23) / BitmapWidth) % (BitmapHeight - 16);
            ushort paintColor = 0xF800;   // RGB565 red
            for (int dy = 0; dy < 16; dy++)
            for (int dx = 0; dx < 16; dx++)
            {
                long off = (long)(patchStartY + dy) * BitmapWidth + (patchStartX + dx);
                p[off] = paintColor;
            }
            _paintCursor++;
        }

        context.Custom(new SpikeOp(this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
    }

    private sealed class SpikeOp : ICustomDrawOperation
    {
        private readonly CoverageUploadSpike _owner;
        public Rect Bounds { get; }

        public SpikeOp(CoverageUploadSpike owner, Rect bounds)
        { _owner = owner; Bounds = bounds; }

        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var skia = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (skia == null) return;
            using var lease = skia.Lease();
            var canvas = lease.SkCanvas;

            long t0 = Stopwatch.GetTimestamp();

            // Build an SKImage from the raw RGB565 buffer. FromPixels is the
            // documented zero-copy wrap path.
            var info = new SKImageInfo(BitmapWidth, BitmapHeight,
                SKColorType.Rgb565, SKAlphaType.Opaque);
            using var image = SKImage.FromPixels(info, _owner._buffer, RowBytes);

            long t1 = Stopwatch.GetTimestamp();

            using var paint = new SKPaint { IsAntialias = false };
            // Scale to fit control. DrawImage from CPU-side pixels triggers
            // an upload to the GPU texture cache on first use; subsequent
            // draws of the SAME SKImage instance reuse the cached texture.
            // We re-create the SKImage every frame here to force the cache
            // miss — that's the worst-case "coverage is dirty every frame"
            // measurement, which is what we care about.
            var dst = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            canvas.DrawImage(image, dst, paint);

            long t2 = Stopwatch.GetTimestamp();

            _owner._accumTicksUpload += t1 - t0;
            _owner._accumTicksDraw   += t2 - t1;
            _owner._accumTicksRender += t2 - t0;
            _owner._frameCount++;

            var elapsed = (DateTime.UtcNow - _owner._lastEmit).TotalSeconds;
            if (elapsed >= 1.0 && _owner._frameCount > 0)
            {
                double ticksPerMs = Stopwatch.Frequency / 1_000.0;
                Console.WriteLine(
                    $"[CoverageSpike-PERF] frames={_owner._frameCount} " +
                    $"ms_per_frame={_owner._accumTicksRender / ticksPerMs / _owner._frameCount:F2} " +
                    $"wrap_ms={_owner._accumTicksUpload / ticksPerMs / _owner._frameCount:F2} " +
                    $"draw_ms={_owner._accumTicksDraw / ticksPerMs / _owner._frameCount:F2} " +
                    $"bitmap={BitmapWidth}x{BitmapHeight}_RGB565 ({BufferBytes/1024/1024}MB)");
                _owner._frameCount = 0;
                _owner._accumTicksRender = 0;
                _owner._accumTicksUpload = 0;
                _owner._accumTicksDraw = 0;
                _owner._lastEmit = DateTime.UtcNow;
            }
        }
    }
}
