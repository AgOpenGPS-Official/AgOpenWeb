// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Coverage;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Diagnostics;
using AgValoniaGPS.Models.Track;
using AssetLoader = Avalonia.Platform.AssetLoader;
using Vec2 = AgValoniaGPS.Models.Base.Vec2;
using Vec3 = AgValoniaGPS.Models.Base.Vec3;

namespace AgValoniaGPS.Views.Controls;

/// <summary>
/// Phase-1 skeleton of the new map control that the GL pivot lands on.
/// Mirrors the CompositionCustomVisualHandler scheduling pattern used by
/// <see cref="DrawingContextMapControl"/> — which the spike confirmed is the
/// only uncapped path on iPad (issue #21409). Reuses the existing
/// <see cref="MapRenderState"/> snapshot so the data contract from the
/// services and ViewModel doesn't change between the old and new control.
///
/// Phase 1 scope: top-down only (CameraPitch=0, Is3DMode=false). Renders
/// background fill, grid, ground texture, boundary, headland, vehicle.
/// Track/section/coverage/tram/youturn rendering is deferred to Phase 2.
/// </summary>
public class SkiaMapControl : Control, ISharedMapControl
{
    // ------------------------------------------------------------------
    // Avalonia styled properties (mirror DCMC for binding parity)
    // ------------------------------------------------------------------

    public static readonly StyledProperty<bool> IsGridVisibleProperty =
        AvaloniaProperty.Register<SkiaMapControl, bool>(nameof(IsGridVisible), defaultValue: true);

    public bool IsGridVisible
    {
        get => GetValue(IsGridVisibleProperty);
        set => SetValue(IsGridVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> ShowVehicleProperty =
        AvaloniaProperty.Register<SkiaMapControl, bool>(nameof(ShowVehicle), defaultValue: true);

    public bool ShowVehicle
    {
        get => GetValue(ShowVehicleProperty);
        set => SetValue(ShowVehicleProperty, value);
    }

    // ------------------------------------------------------------------
    // Camera/viewport state (matches DCMC layout)
    // ------------------------------------------------------------------

    private double _cameraX, _cameraY;
    private double _zoom = 1.0;
    private double _rotation;
    private bool _isNorthUp;
    private bool _isDayMode = ConfigurationStore.Instance.Display.IsDayMode;
    private int _cameraFollowMode = 3;
    public int CameraFollowMode
    {
        get => _cameraFollowMode;
        set { _cameraFollowMode = value; SendStateToHandler(); }
    }
    public event Action? UserPanned;
    public bool AutoPanEnabled { get; set; } = true;

    // ------------------------------------------------------------------
    // Vehicle / tool / section state
    // ------------------------------------------------------------------

    private double _vehicleX, _vehicleY, _vehicleHeading, _vehicleSteerAngle;
    private bool _hasValidHeading;
    private bool _isReversing;
    public bool IsReversing { get => _isReversing; set { _isReversing = value; SendStateToHandler(); } }

    private double _toolX, _toolY, _toolHeading, _toolWidth, _hitchX, _hitchY;
    private bool _toolPositionReady;

    private bool[] _sectionOn = new bool[16];
    private int[] _sectionButtonState = new int[16];
    private double[] _sectionWidths = new double[16];
    private double[] _sectionLeft = new double[16];
    private double[] _sectionRight = new double[16];
    private int _numSections;

    // ------------------------------------------------------------------
    // Data we render in Phase 1
    // ------------------------------------------------------------------

    private Boundary? _boundary;
    private IReadOnlyList<Vec3>? _headlandLine;
    private IReadOnlyList<Vec2>? _headlandPreview;
    private bool _isHeadlandVisible = true;

    private Bitmap? _groundTexture;
    private Bitmap? _groundTextureDay;
    private Bitmap? _groundTextureNight;
    private IImage? _vehicleImage;
    private IImage? _frontWheelImage;

    // ------------------------------------------------------------------
    // Data we accept but don't render yet (Phase 2+)
    // ------------------------------------------------------------------

    private Track? _activeTrack;
    private Track? _baseTrack;
    private Track? _nextTrack;
    private bool _isInYouTurn;
    private AgValoniaGPS.Models.Position? _pendingPointA;
    private IReadOnlyList<Track> _recordedPaths = Array.Empty<Track>();
    private IReadOnlyList<Track> _contourStrips = Array.Empty<Track>();
    private List<(double Easting, double Northing)>? _recordingPoints;
    private IReadOnlyList<(double Easting, double Northing, string Color, string Name)> _flags
        = Array.Empty<(double, double, string, string)>();
    private double _goalEasting, _goalNorthing;
    private bool _guidanceActive;

    // ------------------------------------------------------------------
    // Composition visual
    // ------------------------------------------------------------------

    private CompositionCustomVisual? _customVisual;
    private SkiaMapVisualHandler? _handler;

    // ------------------------------------------------------------------
    // FPS
    // ------------------------------------------------------------------

    private double _currentFps;
    public double CurrentFps => _currentFps;
    public event Action<double>? FpsUpdated;
    internal void ReportFps(double fps)
    {
        _currentFps = fps;
        FpsUpdated?.Invoke(fps);
    }

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    public SkiaMapControl()
    {
        Focusable = true;
        IsHitTestVisible = true;
        ClipToBounds = true;

        try
        {
            var dayUri = new Uri("avares://AgValoniaGPS.Views/Assets/Images/GroundTexture.png");
            using var dayStream = AssetLoader.Open(dayUri);
            _groundTextureDay = new Bitmap(dayStream);
            var nightUri = new Uri("avares://AgValoniaGPS.Views/Assets/Images/GroundTextureDark.png");
            using var nightStream = AssetLoader.Open(nightUri);
            _groundTextureNight = new Bitmap(nightStream);
            _groundTexture = _isDayMode ? _groundTextureDay : _groundTextureNight;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkiaMapControl] Ground texture not found: {ex.Message}");
        }

        try
        {
            var uri = new Uri("avares://AgValoniaGPS.Views/Assets/Images/TractorAoG.png");
            using var stream = AssetLoader.Open(uri);
            _vehicleImage = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkiaMapControl] Tractor image not found: {ex.Message}");
        }

        try
        {
            var uri = new Uri("avares://AgValoniaGPS.Views/Assets/Images/FrontWheels.png");
            using var stream = AssetLoader.Open(uri);
            _frontWheelImage = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkiaMapControl] Front wheels image not found: {ex.Message}");
        }

        PropertyChanged += OnControlPropertyChanged;
    }

    public override void Render(DrawingContext context)
    {
        // Composition handler does all real drawing — we just provide a hit-test surface.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetupCompositionVisual();
    }

    private void SetupCompositionVisual()
    {
        var compositionVisual = ElementComposition.GetElementVisual(this);
        if (compositionVisual == null) return;

        var compositor = compositionVisual.Compositor;
        _handler = new SkiaMapVisualHandler(this);
        _customVisual = compositor.CreateCustomVisual(_handler);
        _customVisual.Size = new Vector(Bounds.Width, Bounds.Height);
        ElementComposition.SetElementChildVisual(this, _customVisual);
        SendStateToHandler();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _customVisual != null)
        {
            _customVisual.Size = new Vector(Bounds.Width, Bounds.Height);
            SendStateToHandler();
        }
    }

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(IsVisible) && e.NewValue is true)
            SendStateToHandler();
    }

    // ------------------------------------------------------------------
    // State snapshot
    // ------------------------------------------------------------------

    internal void SendStateToHandler()
    {
        if (_customVisual == null || _handler == null) return;

        var displayCfg = ConfigurationStore.Instance.Display;
        var vehicleCfg = ConfigurationStore.Instance.Vehicle;
        var toolCfg = ConfigurationStore.Instance.Tool;

        var state = new MapRenderState
        {
            // Phase 1: top-down camera only — no perspective math, no 3D fork.
            CameraX = _cameraX,
            CameraY = _cameraY,
            Zoom = _zoom,
            Rotation = _rotation,
            CameraPitch = 0.0,
            Is3DMode = false,
            IsNorthUp = _isNorthUp,
            IsDayMode = _isDayMode,

            BoundsWidth = Bounds.Width,
            BoundsHeight = Bounds.Height,

            VehicleX = _vehicleX,
            VehicleY = _vehicleY,
            VehicleHeading = _vehicleHeading,
            VehicleSteerAngle = _vehicleSteerAngle,
            VehicleWheelbase = vehicleCfg.Wheelbase,
            VehicleTrackWidth = vehicleCfg.TrackWidth,
            FrontWheelImage = _frontWheelImage,
            HasValidHeading = _hasValidHeading,
            IsReversing = _isReversing,
            ShowVehicle = ShowVehicle,
            VehicleImage = _vehicleImage,
            AntennaPivot = vehicleCfg.AntennaPivot,
            AntennaOffset = vehicleCfg.AntennaOffset,

            ToolX = _toolX,
            ToolY = _toolY,
            ToolHeading = _toolHeading,
            ToolWidth = _toolWidth,
            HitchX = _hitchX,
            HitchY = _hitchY,
            ToolReady = _toolPositionReady,

            Boundary = _boundary,
            HeadlandLine = _headlandLine,
            HeadlandPreview = _headlandPreview,
            IsHeadlandVisible = _isHeadlandVisible,

            GroundTexture = _groundTexture,
            FieldTextureVisible = displayCfg.FieldTextureVisible,
            GroundTextureMoveable = displayCfg.FieldTextureMoveable,
            LineSmoothEnabled = displayCfg.LineSmoothEnabled,

            IsGridVisible = IsGridVisible,
            IsMetric = ConfigurationStore.Instance.IsMetric,
        };

        _customVisual.SendHandlerMessage(state);
    }

    // ------------------------------------------------------------------
    // ISharedMapControl — camera / view
    // ------------------------------------------------------------------

    public bool Is3DMode => false; // Phase 1: top-down only

    public void Toggle3DMode() { /* Phase 1: no 3D fork */ }
    public void Set3DMode(bool is3D) { /* Phase 1: no 3D fork */ }
    public void SetPitch(double deltaRadians) { /* Phase 1: top-down only */ }
    public void SetPitchAbsolute(double pitchRadians) { /* Phase 1: top-down only */ }

    public void PanTo(double x, double y)
    {
        _cameraX = x; _cameraY = y;
        SendStateToHandler();
    }

    public void Pan(double deltaX, double deltaY)
    {
        _cameraX += deltaX; _cameraY += deltaY;
        SendStateToHandler();
    }

    public void Zoom(double factor)
    {
        _zoom *= factor;
        SendStateToHandler();
    }

    public double GetZoom() => _zoom;

    public (double X, double Y) GetCameraCenter() => (_cameraX, _cameraY);

    public void SetCamera(double x, double y, double zoom, double rotation)
    {
        _cameraX = x; _cameraY = y; _zoom = zoom; _rotation = rotation;
        SendStateToHandler();
    }

    public void Rotate(double deltaRadians)
    {
        _rotation += deltaRadians;
        SendStateToHandler();
    }

    // Mouse interaction — Phase 1 doesn't enable pan-by-drag; revisit when
    // the new control becomes the default (Phase 4). UserPanned event stays
    // declared so the platform code-behind can subscribe blindly.
    public void StartPan(Point position) { _ = position; }
    public void StartRotate(Point position) { _ = position; }
    public void UpdateMouse(Point position) { _ = position; }
    public void EndPanRotate() { }

    // ------------------------------------------------------------------
    // ISharedMapControl — content setters
    // ------------------------------------------------------------------

    public void SetBoundary(Boundary? boundary)
    {
        _boundary = boundary;
        SendStateToHandler();
    }

    public void SetVehiclePosition(double x, double y, double heading)
    {
        _vehicleX = x; _vehicleY = y; _vehicleHeading = heading;
        _hasValidHeading = true;
        SendStateToHandler();
    }

    public void SetVehicleSteerAngle(double radians)
    {
        _vehicleSteerAngle = radians;
        SendStateToHandler();
    }

    public void SetToolPosition(double x, double y, double heading, double width,
        double hitchX, double hitchY, bool isReady = true)
    {
        _toolX = x; _toolY = y; _toolHeading = heading; _toolWidth = width;
        _hitchX = hitchX; _hitchY = hitchY; _toolPositionReady = isReady;
        SendStateToHandler();
    }

    public void SetAllPositions(double vehicleX, double vehicleY, double vehicleHeading,
        double toolX, double toolY, double toolHeading, double toolWidth,
        double hitchX, double hitchY, bool toolReady)
    {
        _vehicleX = vehicleX; _vehicleY = vehicleY; _vehicleHeading = vehicleHeading;
        _hasValidHeading = true;
        _toolX = toolX; _toolY = toolY; _toolHeading = toolHeading; _toolWidth = toolWidth;
        _hitchX = hitchX; _hitchY = hitchY; _toolPositionReady = toolReady;
        SendStateToHandler();
    }

    public void SetSectionStates(bool[] sectionOn, double[] sectionWidths, int numSections,
        int[]? buttonStates = null)
    {
        _numSections = Math.Min(numSections, 16);
        for (int i = 0; i < _numSections && i < sectionOn.Length; i++)
            _sectionOn[i] = sectionOn[i];
        for (int i = 0; i < _numSections && i < sectionWidths.Length; i++)
            _sectionWidths[i] = sectionWidths[i];
        if (buttonStates != null)
            for (int i = 0; i < _numSections && i < buttonStates.Length; i++)
                _sectionButtonState[i] = buttonStates[i];
        SendStateToHandler();
    }

    public void SetGridVisible(bool visible)
    {
        IsGridVisible = visible;
        SendStateToHandler();
    }

    public void SetNorthUp(bool isNorthUp)
    {
        _isNorthUp = isNorthUp;
        SendStateToHandler();
    }

    public void SetDayMode(bool isDayMode)
    {
        _isDayMode = isDayMode;
        _groundTexture = isDayMode ? _groundTextureDay : _groundTextureNight;
        SendStateToHandler();
    }

    public void SetRecordingPoints(IReadOnlyList<(double Easting, double Northing)> points)
    {
        _recordingPoints = new List<(double, double)>(points);
        SendStateToHandler();
    }

    public void ClearRecordingPoints()
    {
        _recordingPoints = null;
        SendStateToHandler();
    }

    public void SetBackgroundImage(string imagePath, double minX, double maxY, double maxX, double minY)
    {
        // Phase 1: imagery overlay deferred (per [[imagery-png-render-cost]] this is the
        // single largest remaining FPS lever — pulled into Phase 2/3 with measurement).
    }

    public void SetBackgroundImageWithMercator(string imagePath, double minX, double maxY, double maxX, double minY,
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        double originLat, double originLon)
    {
        // Phase 1: deferred (see SetBackgroundImage).
    }

    public void ClearBackground() { /* Phase 1: no imagery yet */ }

    public void SetBoundaryOffsetIndicator(bool show, double offsetMeters = 0.0)
    {
        // Phase 1: boundary recording UX deferred until Phase 2's input wiring.
    }

    public void SetHeadlandLine(IReadOnlyList<Vec3>? headlandPoints)
    {
        _headlandLine = headlandPoints;
        SendStateToHandler();
    }

    public void SetHeadlandPreview(IReadOnlyList<Vec2>? previewPoints)
    {
        _headlandPreview = previewPoints;
        SendStateToHandler();
    }

    public void SetHeadlandVisible(bool visible)
    {
        _isHeadlandVisible = visible;
        SendStateToHandler();
    }

    public void SetYouTurnPath(IReadOnlyList<(double Easting, double Northing)>? turnPath)
    {
        // Phase 2: u-turn path drawing.
    }

    public void SetNextTrack(Track? track) { _nextTrack = track; }
    public void SetIsInYouTurn(bool isInTurn) { _isInYouTurn = isInTurn; }
    public void SetActiveTrack(Track? track) { _activeTrack = track; }
    public void SetBaseTrack(Track? track) { _baseTrack = track; }

    public void SetTramLines(
        IReadOnlyList<Vec2>? outerTrack,
        IReadOnlyList<Vec2>? innerTrack,
        IReadOnlyList<IReadOnlyList<Vec2>>? parallelLines,
        IReadOnlyList<IReadOnlyList<Vec2>>? boundaryExtraLines = null)
    {
        // Phase 2: tram lines.
    }

    public void SetTramControlByte(byte controlByte) { /* Phase 2 */ }

    public void SetRecordedPaths(IReadOnlyList<Track> paths) { _recordedPaths = paths; }
    public void SetContourStrips(IReadOnlyList<Track> strips) { _contourStrips = strips; }

    // ------------------------------------------------------------------
    // Coverage — Phase 1 stores nothing; ICoverageMapService just talks to
    // the live DCMC instance. Once SkiaMapControl handles coverage (Phase 2)
    // these will route into the renderer.
    // ------------------------------------------------------------------

    public void SetCoveragePatches(IReadOnlyList<CoveragePatch> patches) { }

    public void SetCoverageBitmapProviders(
        Func<(double MinE, double MaxE, double MinN, double MaxN)?>? boundsProvider,
        Func<double, double, double, double, double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? allCellsProvider,
        Func<double, IEnumerable<(int CellX, int CellY, CoverageColor Color)>>? newCellsProvider)
    { }

    public void MarkCoverageDirty() { }
    public void MarkCoverageFullRebuildNeeded() { }
    public void InitializeCoverageBitmapWithBounds(double minE, double maxE, double minN, double maxN) { }
    public ushort GetCoveragePixel(int localX, int localY) => 0;
    public void SetCoveragePixel(int localX, int localY, ushort rgb565) { }
    public void ClearCoveragePixels() { }
    public ushort[]? GetCoveragePixelBuffer() => null;
    public void SetCoveragePixelBuffer(ushort[] pixels) { }
    public (int Width, int Height, double CellSize)? GetDisplayBitmapInfo() => null;

    public void SetFlags(IReadOnlyList<(double Easting, double Northing, string Color, string Name)> flags)
    {
        _flags = flags;
        SendStateToHandler();
    }

    public void SetGuidancePoints(double goalEasting, double goalNorthing, bool isActive)
    {
        _goalEasting = goalEasting; _goalNorthing = goalNorthing; _guidanceActive = isActive;
    }

    // ==================================================================
    // SkiaMapVisualHandler — renders on the compositor render thread
    // ==================================================================

    private class SkiaMapVisualHandler : CompositionCustomVisualHandler
    {
        private readonly SkiaMapControl _owner;
        private MapRenderState? _state;

        // FPS tracking
        private DateTime _lastFpsUpdate = DateTime.UtcNow;
        private int _frameCount;

        // Cached SK paints — per-frame allocation kills iPad FPS, mirroring DCMC's
        // PERF-05 caching. Same paint set, narrower scope (no track / section paints
        // until Phase 2).
        private SKPaint? _gridMinorPaintSk;
        private SKPaint? _gridMajorPaintSk;
        private SKPaint? _gridAxisXPaintSk;
        private SKPaint? _gridAxisYPaintSk;
        private bool _gridPaintIsDayMode;
        private double _gridPaintMinorThickness;
        private double _gridPaintMajorThickness;
        private double _gridPaintAxisThickness;

        private readonly SKPaint _boundaryOuterPaint;
        private readonly SKPaint _boundaryInnerPaint;
        private readonly SKPaint _headlandPaint;
        private readonly SKPaint _headlandPreviewPaint;

        private readonly SKPaint _vehicleFallbackPaint;
        private readonly SKPaint _antennaPaint;
        private readonly SKPaint _headingUnknownLinePaint;
        private readonly SKPaint _headingUnknownDotPaint;
        private readonly SKPaint _reverseIndicatorPaint;

        // Vehicle bitmap shadow caches (decode once on first frame)
        private SKBitmap? _vehicleSkBitmap;
        private SKBitmap? _frontWheelSkBitmap;

        // Vehicle bitmap proportions — same constants DCMC uses so the sprite
        // sits on the configured wheelbase / track-width regardless of zoom.
        private const double BitmapRearAxleYNorm = 0.245;
        private const double BitmapFrontAxleYNorm = 0.75;
        private const double BitmapFrontWheelHalfXNorm = 0.245;
        private const double BitmapAxleSpanYNorm = BitmapFrontAxleYNorm - BitmapRearAxleYNorm;
        private const double WheelBitmapContentWFraction = 0.27;
        private const double WheelBitmapContentHFraction = 0.29;
        private const double FrontTireWidthM = 0.378;
        private const double FrontTireDiameterM = 0.85;
        private const double FrontWheelSpriteForwardOffsetM = -0.05;

        public SkiaMapVisualHandler(SkiaMapControl owner)
        {
            _owner = owner;

            _boundaryOuterPaint = new SKPaint { Color = new SKColor(242, 112, 89, 204), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            _boundaryInnerPaint = new SKPaint { Color = new SKColor(245, 245, 77), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            _headlandPaint = new SKPaint { Color = new SKColor(251, 235, 107), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            _headlandPreviewPaint = new SKPaint { Color = new SKColor(77, 250, 0, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

            _vehicleFallbackPaint = new SKPaint { Color = new SKColor(0, 200, 0), Style = SKPaintStyle.Fill, IsAntialias = true };
            _antennaPaint = new SKPaint { Color = new SKColor(0, 120, 255), Style = SKPaintStyle.Fill, IsAntialias = true };
            _headingUnknownLinePaint = new SKPaint { Color = new SKColor(255, 0, 0), Style = SKPaintStyle.Stroke, StrokeWidth = 0.1f, IsAntialias = true };
            _headingUnknownDotPaint = new SKPaint { Color = new SKColor(255, 0, 0), Style = SKPaintStyle.Fill, IsAntialias = true };
            _reverseIndicatorPaint = new SKPaint { Color = new SKColor(255, 60, 60), Style = SKPaintStyle.Fill, IsAntialias = true };
        }

        public override Rect GetRenderBounds()
        {
            var s = _state;
            if (s != null && s.BoundsWidth > 0 && s.BoundsHeight > 0)
                return new Rect(0, 0, s.BoundsWidth, s.BoundsHeight);
            return new Rect(0, 0, 4000, 4000);
        }

        public override void OnMessage(object message)
        {
            if (message is MapRenderState state)
            {
                bool firstState = _state == null;
                _state = state;
                Invalidate();
                if (firstState)
                    RegisterForNextAnimationFrameUpdate();
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            Invalidate();
            if (!DiagFlags.DisableAnimationFrameUpdate)
                RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            var s = _state;
            if (s == null) return;
            if (s.BoundsWidth <= 0 || s.BoundsHeight <= 0) return;

            // FPS
            _frameCount++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastFpsUpdate).TotalSeconds;
            if (elapsed >= 1.0)
            {
                var fps = _frameCount / elapsed;
                _frameCount = 0;
                _lastFpsUpdate = now;
                Dispatcher.UIThread.Post(() => _owner.ReportFps(fps), DispatcherPriority.Background);
            }

            try
            {
                // Background fill (screen space — before camera transform)
                var bgColor = s.IsDayMode
                    ? Color.FromRgb(69, 102, 179)
                    : Color.FromRgb(10, 10, 10);
                drawingContext.FillRectangle(
                    new ImmutableSolidColorBrush(bgColor),
                    new Rect(0, 0, s.BoundsWidth, s.BoundsHeight));

                // Camera transform → world space
                double aspect = s.BoundsWidth / s.BoundsHeight;
                double viewWidth = 200.0 * aspect / s.Zoom;
                double viewHeight = 200.0 / s.Zoom;
                var cameraMatrix = GetCameraTransform(s, viewWidth, viewHeight);
                using var cameraScope = drawingContext.PushPreTransform(cameraMatrix);

                // Ground texture via DrawingContext (no Skia lease needed)
                if (s.GroundTexture != null && s.FieldTextureVisible && !DiagFlags.SkipGroundTexture)
                    DrawGroundTexture(drawingContext, s, viewWidth, viewHeight);

                // Everything else through SkiaSharp lease — batched paths,
                // identical to the DCMC pattern that ships parity-stable.
                var skiaFeature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (skiaFeature != null)
                {
                    using var skiaLease = skiaFeature.Lease();
                    var canvas = skiaLease.SkCanvas;

                    try
                    {
                        if (s.IsGridVisible && !DiagFlags.SkipGrid)
                            DrawGridSk(canvas, s, viewWidth, viewHeight);

                        if (!DiagFlags.SkipBoundaryDraw)
                        {
                            if (s.Boundary != null)
                                DrawBoundary(canvas, s);
                            if (s.IsHeadlandVisible && s.HeadlandLine != null && s.HeadlandLine.Count > 2)
                                DrawHeadlandLine(canvas, s);
                            if (s.HeadlandPreview != null && s.HeadlandPreview.Count > 2)
                                DrawHeadlandPreview(canvas, s);
                        }

                        if (s.ShowVehicle && !DiagFlags.SkipVehicle)
                            DrawVehicleSk(canvas, s);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SkiaMapVisualHandler] Render error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SkiaMapVisualHandler] OnRender outer error: {ex.Message}");
            }
        }

        // ----------------- Camera transform -----------------

        private static Matrix GetCameraTransform(MapRenderState s, double viewWidth, double viewHeight)
        {
            // Phase 1 == top-down only, so no pitch foreshortening. Y is mirrored
            // because world coordinates are Y-up but screen coordinates are Y-down.
            double scaleX = s.BoundsWidth / viewWidth;
            double scaleY = -s.BoundsHeight / viewHeight;

            var matrix = Matrix.Identity;
            matrix = matrix * Matrix.CreateTranslation(s.BoundsWidth / 2, s.BoundsHeight / 2);
            matrix = Matrix.CreateScale(scaleX, scaleY) * matrix;
            if (Math.Abs(s.Rotation) > 0.001)
                matrix = Matrix.CreateRotation(-s.Rotation) * matrix;
            matrix = Matrix.CreateTranslation(-s.CameraX, -s.CameraY) * matrix;
            return matrix;
        }

        // ----------------- Ground texture -----------------

        private void DrawGroundTexture(ImmediateDrawingContext dc, MapRenderState s, double viewWidth, double viewHeight)
        {
            if (!s.GroundTextureMoveable)
            {
                double centerX = s.CameraX;
                double centerY = s.CameraY;
                double diagonal = Math.Sqrt(viewWidth * viewWidth + viewHeight * viewHeight) / 2 + 100.0;
                var viewRect = new Rect(centerX - diagonal, centerY - diagonal, diagonal * 2, diagonal * 2);
                dc.DrawBitmap(s.GroundTexture!, viewRect);
                return;
            }

            const double BaseTileSize = 50.0;
            const int MaxTilesPerAxis = 6;
            double halfDiagonal = Math.Sqrt(viewWidth * viewWidth + viewHeight * viewHeight) / 2;
            double viewSpan = halfDiagonal * 2;

            double tileSize = BaseTileSize;
            while (tileSize * MaxTilesPerAxis < viewSpan)
                tileSize *= 2.0;

            double minX = s.CameraX - halfDiagonal;
            double minY = s.CameraY - halfDiagonal;
            double maxX = s.CameraX + halfDiagonal;
            double maxY = s.CameraY + halfDiagonal;
            int x0 = (int)Math.Floor(minX / tileSize);
            int y0 = (int)Math.Floor(minY / tileSize);
            int x1 = (int)Math.Ceiling(maxX / tileSize);
            int y1 = (int)Math.Ceiling(maxY / tileSize);
            int xCount = Math.Min(x1 - x0 + 1, MaxTilesPerAxis);
            int yCount = Math.Min(y1 - y0 + 1, MaxTilesPerAxis);

            for (int iy = 0; iy < yCount; iy++)
            for (int ix = 0; ix < xCount; ix++)
            {
                double tx = (x0 + ix) * tileSize;
                double ty = (y0 + iy) * tileSize;
                dc.DrawBitmap(s.GroundTexture!, new Rect(tx, ty, tileSize, tileSize));
            }
        }

        // ----------------- Grid -----------------

        private void DrawGridSk(SKCanvas canvas, MapRenderState s, double viewWidth, double viewHeight)
        {
            const double gridSize = 2000.0;
            double toolW = s.ToolWidth > 0.5 ? s.ToolWidth : 6.0;
            double viewSpan = Math.Max(viewWidth, viewHeight);

            double spacing, majorEvery;
            if (viewSpan < toolW * 30) { spacing = toolW; majorEvery = toolW * 10; }
            else if (viewSpan < toolW * 100) { spacing = toolW * 5; majorEvery = toolW * 50; }
            else { spacing = toolW * 10; majorEvery = toolW * 100; }

            double screenHeight = s.BoundsHeight > 0 ? s.BoundsHeight : 600;
            double worldPerPixel = viewHeight / screenHeight;
            double minorThickness = Math.Max(0.3 * worldPerPixel, 0.05);
            double majorThickness = Math.Max(0.6 * worldPerPixel, 0.1);
            double axisThickness = Math.Max(0.9 * worldPerPixel, 0.15);

            EnsureGridPaintsSk(s.IsDayMode, minorThickness, majorThickness, axisThickness);

            double minX = Math.Max(s.CameraX - viewWidth, -gridSize);
            double maxX = Math.Min(s.CameraX + viewWidth, gridSize);
            double minY = Math.Max(s.CameraY - viewHeight, -gridSize);
            double maxY = Math.Min(s.CameraY + viewHeight, gridSize);

            double startX = Math.Floor(minX / spacing) * spacing;
            double startY = Math.Floor(minY / spacing) * spacing;
            float lineXStart = (float)Math.Max(minX, -gridSize);
            float lineXEnd = (float)Math.Min(maxX, gridSize);
            float lineYStart = (float)Math.Max(minY, -gridSize);
            float lineYEnd = (float)Math.Min(maxY, gridSize);

            using var minorPath = new SKPath();
            using var majorPath = new SKPath();
            bool hasAxisY = false, hasAxisX = false;

            for (double x = startX; x <= maxX; x += spacing)
            {
                if (x < -gridSize || x > gridSize) continue;
                if (Math.Abs(x) < 0.1) { hasAxisY = true; continue; }
                bool isMajor = Math.Abs(x % majorEvery) < 0.1;
                var path = isMajor ? majorPath : minorPath;
                path.MoveTo((float)x, lineYStart);
                path.LineTo((float)x, lineYEnd);
            }
            for (double y = startY; y <= maxY; y += spacing)
            {
                if (y < -gridSize || y > gridSize) continue;
                if (Math.Abs(y) < 0.1) { hasAxisX = true; continue; }
                bool isMajor = Math.Abs(y % majorEvery) < 0.1;
                var path = isMajor ? majorPath : minorPath;
                path.MoveTo(lineXStart, (float)y);
                path.LineTo(lineXEnd, (float)y);
            }

            canvas.DrawPath(minorPath, _gridMinorPaintSk!);
            canvas.DrawPath(majorPath, _gridMajorPaintSk!);
            if (hasAxisY)
                canvas.DrawLine(0, lineYStart, 0, lineYEnd, _gridAxisYPaintSk!);
            if (hasAxisX)
                canvas.DrawLine(lineXStart, 0, lineXEnd, 0, _gridAxisXPaintSk!);
        }

        private void EnsureGridPaintsSk(bool isDayMode, double minorThickness, double majorThickness, double axisThickness)
        {
            if (_gridMinorPaintSk != null
                && _gridPaintIsDayMode == isDayMode
                && Math.Abs(_gridPaintMinorThickness - minorThickness) < 1e-4
                && Math.Abs(_gridPaintMajorThickness - majorThickness) < 1e-4
                && Math.Abs(_gridPaintAxisThickness - axisThickness) < 1e-4)
                return;

            _gridMinorPaintSk?.Dispose();
            _gridMajorPaintSk?.Dispose();
            _gridAxisXPaintSk?.Dispose();
            _gridAxisYPaintSk?.Dispose();

            SKColor minorColor, majorColor;
            if (isDayMode)
            {
                minorColor = new SKColor(40, 40, 40, 120);
                majorColor = new SKColor(30, 30, 30, 180);
            }
            else
            {
                minorColor = new SKColor(180, 180, 180, 80);
                majorColor = new SKColor(200, 200, 200, 120);
            }

            _gridMinorPaintSk = new SKPaint { Color = minorColor, Style = SKPaintStyle.Stroke, StrokeWidth = (float)minorThickness, IsAntialias = false };
            _gridMajorPaintSk = new SKPaint { Color = majorColor, Style = SKPaintStyle.Stroke, StrokeWidth = (float)majorThickness, IsAntialias = false };
            _gridAxisXPaintSk = new SKPaint { Color = new SKColor(204, 51, 51, 70), Style = SKPaintStyle.Stroke, StrokeWidth = (float)axisThickness, IsAntialias = false };
            _gridAxisYPaintSk = new SKPaint { Color = new SKColor(51, 204, 51, 70), Style = SKPaintStyle.Stroke, StrokeWidth = (float)axisThickness, IsAntialias = false };

            _gridPaintIsDayMode = isDayMode;
            _gridPaintMinorThickness = minorThickness;
            _gridPaintMajorThickness = majorThickness;
            _gridPaintAxisThickness = axisThickness;
        }

        // ----------------- Boundary -----------------

        private void DrawBoundary(SKCanvas canvas, MapRenderState s)
        {
            if (s.Boundary == null) return;

            if (s.Boundary.OuterBoundary != null && s.Boundary.OuterBoundary.IsValid
                && s.Boundary.OuterBoundary.Points.Count > 1)
            {
                var points = s.Boundary.OuterBoundary.Points;
                using var path = new SKPath();
                path.MoveTo((float)points[0].Easting, (float)points[0].Northing);
                for (int i = 1; i < points.Count; i++)
                    path.LineTo((float)points[i].Easting, (float)points[i].Northing);
                path.Close();
                canvas.DrawPath(path, _boundaryOuterPaint);
            }

            foreach (var inner in s.Boundary.InnerBoundaries)
            {
                if (inner.IsValid && inner.Points.Count > 1)
                {
                    var points = inner.Points;
                    using var path = new SKPath();
                    path.MoveTo((float)points[0].Easting, (float)points[0].Northing);
                    for (int i = 1; i < points.Count; i++)
                        path.LineTo((float)points[i].Easting, (float)points[i].Northing);
                    path.Close();
                    canvas.DrawPath(path, _boundaryInnerPaint);
                }
            }

            if (s.Boundary.HeadlandPolygon != null && s.Boundary.HeadlandPolygon.IsValid
                && s.Boundary.HeadlandPolygon.Points.Count > 1)
            {
                var points = s.Boundary.HeadlandPolygon.Points;
                using var path = new SKPath();
                path.MoveTo((float)points[0].Easting, (float)points[0].Northing);
                for (int i = 1; i < points.Count; i++)
                    path.LineTo((float)points[i].Easting, (float)points[i].Northing);
                path.Close();
                canvas.DrawPath(path, _boundaryInnerPaint);
            }
        }

        private void DrawHeadlandLine(SKCanvas canvas, MapRenderState s)
        {
            if (s.HeadlandLine == null || s.HeadlandLine.Count < 3) return;
            using var path = new SKPath();
            path.MoveTo((float)s.HeadlandLine[0].Easting, (float)s.HeadlandLine[0].Northing);
            for (int i = 1; i < s.HeadlandLine.Count; i++)
                path.LineTo((float)s.HeadlandLine[i].Easting, (float)s.HeadlandLine[i].Northing);
            path.Close();
            canvas.DrawPath(path, _headlandPaint);
        }

        private void DrawHeadlandPreview(SKCanvas canvas, MapRenderState s)
        {
            if (s.HeadlandPreview == null || s.HeadlandPreview.Count < 3) return;
            using var path = new SKPath();
            path.MoveTo((float)s.HeadlandPreview[0].Easting, (float)s.HeadlandPreview[0].Northing);
            for (int i = 1; i < s.HeadlandPreview.Count; i++)
                path.LineTo((float)s.HeadlandPreview[i].Easting, (float)s.HeadlandPreview[i].Northing);
            path.Close();
            canvas.DrawPath(path, _headlandPreviewPaint);
        }

        // ----------------- Vehicle -----------------

        private static void BitmapTractorSize(MapRenderState s, out double widthWorld, out double heightWorld)
        {
            double trackWidth = s.VehicleTrackWidth > 0.01 ? s.VehicleTrackWidth : 1.8;
            double wheelbase = s.VehicleWheelbase > 0.01 ? s.VehicleWheelbase : 2.8;
            widthWorld = trackWidth / (2.0 * BitmapFrontWheelHalfXNorm);
            heightWorld = wheelbase / BitmapAxleSpanYNorm;
        }

        private void DrawVehicleSk(SKCanvas canvas, MapRenderState s)
        {
            BitmapTractorSize(s, out double bitmapWWorld, out double bitmapHWorld);
            float trackWidth = (float)(s.VehicleTrackWidth > 0.01 ? s.VehicleTrackWidth : 1.8);
            float wheelbase = (float)(s.VehicleWheelbase > 0.01 ? s.VehicleWheelbase : 2.8);
            float bodyHalfWidth = (float)(bitmapWWorld / 2.0);
            float rectTopWorldY = (float)((1.0 - BitmapRearAxleYNorm) * bitmapHWorld);
            float rectBottomWorldY = (float)(-BitmapRearAxleYNorm * bitmapHWorld);
            float vx = (float)s.VehicleX, vy = (float)s.VehicleY;

            canvas.Save();
            canvas.Translate(vx, vy);
            canvas.RotateRadians(-(float)s.VehicleHeading);

            if (_vehicleSkBitmap == null && s.VehicleImage is Bitmap avBitmap)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    avBitmap.Save(ms);
                    ms.Position = 0;
                    _vehicleSkBitmap = SKBitmap.Decode(ms);
                }
                catch { /* fall through to triangle */ }
            }

            if (_vehicleSkBitmap != null)
            {
                canvas.Scale(1, -1);
                var dst = new SKRect(-bodyHalfWidth, -rectTopWorldY, bodyHalfWidth, -rectBottomWorldY);
                canvas.DrawBitmap(_vehicleSkBitmap, dst);
                canvas.Scale(1, -1);
            }
            else
            {
                using var path = new SKPath();
                path.MoveTo(0, wheelbase);
                path.LineTo(-bodyHalfWidth, 0);
                path.LineTo(bodyHalfWidth, 0);
                path.Close();
                canvas.DrawPath(path, _vehicleFallbackPaint);
            }

            canvas.DrawCircle((float)s.AntennaOffset, (float)s.AntennaPivot, 0.25f, _antennaPaint);

            if (_frontWheelSkBitmap == null && s.FrontWheelImage is Bitmap wheelAvBitmap)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    wheelAvBitmap.Save(ms);
                    ms.Position = 0;
                    _frontWheelSkBitmap = SKBitmap.Decode(ms);
                }
                catch { /* leave null */ }
            }

            if (_frontWheelSkBitmap != null && s.VehicleWheelbase > 0.01 && s.VehicleTrackWidth > 0.01)
            {
                float wheelOffsetX = trackWidth / 2.0f;
                float wheelOffsetY = wheelbase + (float)FrontWheelSpriteForwardOffsetM;
                float wheelW = (float)(FrontTireWidthM / WheelBitmapContentWFraction);
                float wheelH = (float)(FrontTireDiameterM / WheelBitmapContentHFraction);
                var wheelDst = new SKRect(-wheelW / 2, -wheelH / 2, wheelW / 2, wheelH / 2);
                float steerDeg = -(float)(s.VehicleSteerAngle * 180.0 / Math.PI);

                canvas.Save();
                canvas.Translate(wheelOffsetX, wheelOffsetY);
                canvas.RotateDegrees(steerDeg);
                canvas.Scale(1, -1);
                canvas.DrawBitmap(_frontWheelSkBitmap, wheelDst);
                canvas.Restore();

                canvas.Save();
                canvas.Translate(-wheelOffsetX, wheelOffsetY);
                canvas.RotateDegrees(steerDeg);
                canvas.Scale(1, -1);
                canvas.DrawBitmap(_frontWheelSkBitmap, wheelDst);
                canvas.Restore();
            }

            if (!s.HasValidHeading)
            {
                float qx = bodyHalfWidth + 1;
                canvas.DrawLine(qx, wheelbase, qx, wheelbase * 0.4f, _headingUnknownLinePaint);
                canvas.DrawCircle(qx, 0, 0.2f, _headingUnknownDotPaint);
            }

            if (s.IsReversing)
            {
                using var revPath = new SKPath();
                revPath.MoveTo(0, -1);
                revPath.LineTo(-bodyHalfWidth * 0.4f, -2.5f);
                revPath.LineTo(bodyHalfWidth * 0.4f, -2.5f);
                revPath.Close();
                canvas.DrawPath(revPath, _reverseIndicatorPaint);
            }

            canvas.Restore();
        }
    }
}
