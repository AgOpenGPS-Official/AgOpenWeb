// Phase 0 Q1 spike — answers: does SKMatrix44 perspective work in
// SkiaSharp 3.116 / Avalonia 12.0.3?
//
// Investigation findings:
//   - SKMatrix44 ctor takes 16 floats in COLUMN-MAJOR order, NOT row-major
//     (verified empirically by comparing to CreateTranslation factory and
//     System.Numerics implicit cast — all three produced visible rects at
//     the expected screen positions only when the ctor was given column
//     vectors).
//   - The official Skia path for applying a 4x4 to SKCanvas is to convert
//     to 3x3 via SKMatrix44.Matrix, then canvas.SetMatrix(SKMatrix). Skia's
//     2D canvas is natively 3x3.
//   - System.Numerics.Matrix4x4 is row-vector (v * M); its implicit cast to
//     SKMatrix44 (column-vector, M * v) handles the transposition. So we
//     can build mvp in System.Numerics and cast.
//
// Goal: render a 10x10 ground-plane chessboard, camera tilted, watch for
// real foreshortening (far cells smaller, parallel lines converging).
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace AgValoniaGPS.Views.Controls.Spikes;

public class PerspectiveSkiaSpike : Control
{
    private double _pitchDegrees = -30.0; // -90=straight down, -10=near horizontal
    private double _camHeight = 30.0;
    private Avalonia.Threading.DispatcherTimer? _animateTimer;
    private bool _animDirection = true; // true = toward -15, false = toward -90

    public PerspectiveSkiaSpike()
    {
        Focusable = true;
        ClipToBounds = true;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// When true, the control auto-animates pitch between -90 (top-down) and
    /// -15 (near-horizontal) on a 2-second cycle. Used on iPad where there
    /// are no arrow keys to drive the spike interactively.
    /// </summary>
    public bool AutoAnimate
    {
        get => _animateTimer?.IsEnabled ?? false;
        set
        {
            if (value && _animateTimer == null)
            {
                _animateTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                };
                _animateTimer.Tick += (_, _) =>
                {
                    if (_animDirection)
                    {
                        _pitchDegrees += 2;
                        if (_pitchDegrees >= -15) _animDirection = false;
                    }
                    else
                    {
                        _pitchDegrees -= 2;
                        if (_pitchDegrees <= -90) _animDirection = true;
                    }
                    InvalidateVisual();
                };
                _animateTimer.Start();
            }
            else if (!value && _animateTimer != null)
            {
                _animateTimer.Stop();
                _animateTimer = null;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width)  ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        Console.WriteLine($"[PerspectiveSpike] KeyDown {e.Key}, before: pitch={_pitchDegrees:F0} height={_camHeight:F0}");
        switch (e.Key)
        {
            case Key.Up:    _pitchDegrees = Math.Min(_pitchDegrees + 15, -5); break;
            case Key.Down:  _pitchDegrees = Math.Max(_pitchDegrees - 15, -90); break;
            case Key.Right: _camHeight = Math.Min(_camHeight + 20, 300); break;
            case Key.Left:  _camHeight = Math.Max(_camHeight - 20,   5); break;
            default: return;
        }
        Console.WriteLine($"[PerspectiveSpike] after: pitch={_pitchDegrees:F0} height={_camHeight:F0}");
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 30)),
            new Rect(0, 0, Bounds.Width, Bounds.Height));
        context.Custom(new SkiaOp(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            (float)_pitchDegrees, (float)_camHeight));
    }

    private sealed class SkiaOp : ICustomDrawOperation
    {
        public Rect Bounds { get; }
        private readonly float _pitchDeg;
        private readonly float _camHeight;

        public SkiaOp(Rect bounds, float pitchDeg, float camHeight)
        { Bounds = bounds; _pitchDeg = pitchDeg; _camHeight = camHeight; }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var skia = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (skia == null) { Console.WriteLine("[PerspectiveSpike] no Skia feature"); return; }
            using var lease = skia.Lease();
            var canvas = lease.SkCanvas;

            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;
            float cx = width * 0.5f;
            float cy = height * 0.5f;

            // === REAL PERSPECTIVE TEST ===
            // Camera in world: at (50, -20, height), looking forward (+y) tilted
            // down by pitch. World is +x east, +y north, +z up. Ground is z=0.
            // Chessboard spans (0..100, 0..100) on the ground.

            // Build view: System.Numerics row-vector convention.
            float pitchRad = (float)(_pitchDeg * Math.PI / 180.0);
            var camPos = new System.Numerics.Vector3(50f, -20f, _camHeight);
            var target = camPos + new System.Numerics.Vector3(
                0f,
                100f * (float)Math.Cos(pitchRad),
                100f * (float)Math.Sin(pitchRad));
            var up = new System.Numerics.Vector3(0f, 0f, 1f);
            var view = System.Numerics.Matrix4x4.CreateLookAt(camPos, target, up);

            // Build perspective projection.
            float aspect = width / height;
            float fovY = (float)(60.0 * Math.PI / 180.0);
            var proj = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
                fovY, aspect, 1.0f, 1000.0f);

            // mvp in row-vector form. Implicit cast to SKMatrix44 transposes
            // (row-vector → column-vector — the conventions are duals).
            var mvpNumerics = view * proj;
            SKMatrix44 mvp44 = mvpNumerics;

            // Viewport: NDC (-1..+1, y-up) → pixel (0..W, 0..H, y-down).
            // ROW-MAJOR construction (matches SKMatrix44 ctor / .Matrix
            // collapse convention). Row-vector form: v * M.
            // For NDC point (x, y, z, 1) → pixel:
            //   pixel_x =  w/2 * x +  0   * y + 0*z + w/2
            //   pixel_y =  0   * x + -h/2 * y + 0*z + h/2
            // So m00=w/2 m11=-h/2 m30=w/2 m31=h/2 (translation in row 3).
            var viewport44 = new SKMatrix44(
                width * 0.5f,  0f,            0f, 0f,  // row 0
                0f,            -height * 0.5f, 0f, 0f, // row 1
                0f,            0f,             1f, 0f, // row 2
                width * 0.5f,  height * 0.5f, 0f, 1f); // row 3 (translation)

            // Concat(first, second) in row-vector convention applies first
            // then second: v * (first * second). We want mvp first (world →
            // NDC), viewport second (NDC → pixel). So Concat(mvp, viewport).
            var screen44 = SKMatrix44.Concat(mvp44, viewport44);

            // Convert to 3x3 SKMatrix for SKCanvas — per the official Skia
            // pattern. SKCanvas is natively 3x3; the .Matrix property does
            // the 4x4 → 3x3 collapse correctly.
            SKMatrix screen33 = screen44.Matrix;

            canvas.Save();
            canvas.SetMatrix(screen33);

            // SINGLE BIG colored quad on the ground at world (0..100, 0..100, 0).
            // True perspective test: at pitch=-90 this MUST look like a square.
            // At pitch=-30 it MUST be a trapezoid (narrow at top/far, wide at
            // bottom/near). At pitch=-15, dramatic trapezoid.
            //
            // We draw it as a PATH with 4 explicit corners so each corner is
            // independently projected. (DrawRect axis-aligned rect can't tilt;
            // we need DrawPath of a quadrilateral.)
            using var groundPaint = new SKPaint { Color = SKColors.OrangeRed, Style = SKPaintStyle.Fill };
            using var groundPath = new SKPath();
            groundPath.MoveTo(0f, 0f);     // SW corner (near camera-left)
            groundPath.LineTo(100f, 0f);   // SE corner (near camera-right)
            groundPath.LineTo(100f, 100f); // NE corner (far)
            groundPath.LineTo(0f, 100f);   // NW corner (far)
            groundPath.Close();
            canvas.DrawPath(groundPath, groundPaint);

            // Yellow stripe near camera (y=0..10) and Cyan stripe far (y=90..100)
            // — if perspective works, near stripe wider, far stripe narrower.
            using var yPaint = new SKPaint { Color = SKColors.Yellow, Style = SKPaintStyle.Fill };
            using var yPath = new SKPath();
            yPath.MoveTo(0f, 0f); yPath.LineTo(100f, 0f);
            yPath.LineTo(100f, 10f); yPath.LineTo(0f, 10f); yPath.Close();
            canvas.DrawPath(yPath, yPaint);

            using var cPaint = new SKPaint { Color = SKColors.Cyan, Style = SKPaintStyle.Fill };
            using var cPath = new SKPath();
            cPath.MoveTo(0f, 90f); cPath.LineTo(100f, 90f);
            cPath.LineTo(100f, 100f); cPath.LineTo(0f, 100f); cPath.Close();
            canvas.DrawPath(cPath, cPaint);

            // Also log the four corner pixel coords so we can verify.
            void LogCorner(string label, float wx, float wy)
            {
                var pt3 = screen44.MapPoint(new SKPoint3(wx, wy, 0));
                Console.WriteLine($"[PerspectiveSpike] {label} world=({wx},{wy},0) -> screen=({pt3.X:F1},{pt3.Y:F1}) w_z={pt3.Z:F1}");
            }
            LogCorner("SW(near-left)",   0f,   0f);
            LogCorner("SE(near-right)", 100f,  0f);
            LogCorner("NE(far-right)",  100f, 100f);
            LogCorner("NW(far-left)",     0f, 100f);

            canvas.Restore();

            // HUD in screen space (no transform)
            using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var font = new SKFont { Size = 18 };
            canvas.DrawText($"pitch={_pitchDeg:F0}deg height={_camHeight:F0}m (up/down=pitch, left/right=height)",
                10, 24, SKTextAlign.Left, font, textPaint);
            canvas.DrawText("Expected: chessboard with FAR cells smaller than NEAR cells.",
                10, 48, SKTextAlign.Left, font, textPaint);
        }
    }
}
