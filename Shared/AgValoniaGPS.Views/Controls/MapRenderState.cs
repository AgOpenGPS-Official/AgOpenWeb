// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Coverage;

namespace AgValoniaGPS.Views.Controls;

/// <summary>
/// Lightweight snapshot of all state the render thread needs.
/// Built on UI thread, sent to handler via SendHandlerMessage.
/// Uses references (not copies) for large data — benign torn reads are acceptable.
/// </summary>
internal class MapRenderState
{
    // Camera
    public double CameraX, CameraY, Zoom, Rotation, CameraPitch;
    public bool Is3DMode, IsNorthUp, IsDayMode;

    // Screen
    public double BoundsWidth, BoundsHeight;

    // Vehicle
    public double VehicleX, VehicleY, VehicleHeading;
    // Live wheel angle (signed radians, +right). Renderer rotates the
    // front-wheel sprite by this. See issue #336.
    public double VehicleSteerAngle;
    // Pulled from VehicleConfig so the renderer can place wheels at
    // (±TrackWidth/2, Wheelbase) from the vehicle pivot (rear axle).
    public double VehicleWheelbase, VehicleTrackWidth;
    public IImage? FrontWheelImage;
    public bool HasValidHeading, IsReversing, ShowVehicle;

    // Tool
    public double ToolX, ToolY, ToolHeading, ToolWidth, HitchX, HitchY;
    public bool ToolReady;
    public double HitchLength;
    public bool IsToolTrailing;       // includes TBT — render as a single tongue line
    public double ToolArmHalfSpread;  // half lateral spread of 3PT arms at the tractor mount
    public double ToolArmBaseX, ToolArmBaseY; // tractor-side anchor for the 3PT arms (rear axle for rear-mounted, front of tractor for front-mounted)
    public double ToolDrawbarBaseX, ToolDrawbarBaseY; // tractor-side anchor for the trailing drawbar (always at the rear axle)

    // Sections
    public bool[] SectionOn = Array.Empty<bool>();
    public double[] SectionWidths = Array.Empty<double>();
    public double[] SectionLeft = Array.Empty<double>();
    public double[] SectionRight = Array.Empty<double>();
    public int[] SectionButtonState = Array.Empty<int>();
    public int NumSections;

    // Coverage bitmap (SKBitmap reference — render thread reads directly)
    public SKImage? CoverageSkImage;  // Legacy — kept for field load/save
    public SKBitmap? CoverageSkBitmap; // Direct reference for render-thread drawing
    public double BitmapMinE, BitmapMinN, BitmapMaxE, BitmapMaxN;
    public int BitmapWidth, BitmapHeight;
    public bool BitmapHasContent;
    public bool BitmapExplicitlyInitialized;

    // Boundary
    public Boundary? Boundary;

    // Headland
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec3>? HeadlandLine;
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? HeadlandPreview;
    public bool IsHeadlandVisible;

    // YouTurn
    public IReadOnlyList<(double Easting, double Northing)>? YouTurnPath;
    public bool IsInYouTurn;

    // Tracks
    public AgValoniaGPS.Models.Track.Track? ActiveTrack;
    public AgValoniaGPS.Models.Track.Track? BaseTrack;
    public AgValoniaGPS.Models.Track.Track? NextTrack;
    public AgValoniaGPS.Models.Position? PendingPointA;
    public IReadOnlyList<AgValoniaGPS.Models.Track.Track> RecordedPaths = Array.Empty<AgValoniaGPS.Models.Track.Track>();
    public IReadOnlyList<AgValoniaGPS.Models.Track.Track> ContourStrips = Array.Empty<AgValoniaGPS.Models.Track.Track>();

    // Recording
    public List<(double Easting, double Northing)>? RecordingPoints;
    public bool ShowBoundaryOffsetIndicator;
    public double BoundaryOffsetMeters;

    // Selection markers
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? SelectionMarkers;
    public (AgValoniaGPS.Models.Base.Vec2 Start, AgValoniaGPS.Models.Base.Vec2 End)? ClipLine;
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? ClipPath;

    // Flags
    public IReadOnlyList<(double Easting, double Northing, string Color, string Name)> Flags
        = Array.Empty<(double, double, string, string)>();

    // Grid
    public bool IsGridVisible;

    // Guidance
    public double GoalEasting, GoalNorthing;
    public bool GuidanceActive;

    // Ground texture (Avalonia Bitmap — safe to use from render thread via DrawBitmap)
    public Bitmap? GroundTexture;

    // Background image
    public Bitmap? BackgroundImage;
    public double BgMinX, BgMaxY, BgMaxX, BgMinY;
    public bool BackgroundComposited;

    // Vehicle image
    public IImage? VehicleImage;

    // Display config flags
    public bool PolygonsVisible;
    public bool SectionLinesVisible;
    public bool SvennArrowVisible;
    public bool DirectionMarkersVisible;
    public bool FieldTextureVisible;
    /// <summary>
    /// Opt-in tiled mode: when true, the ground texture is rendered as a
    /// grid of world-anchored tiles so it visibly scrolls under the
    /// tractor as the camera pans. When false (default), the texture is
    /// a single stretched bitmap centered on the camera — FPS-stable but
    /// visually static.
    /// </summary>
    public bool GroundTextureMoveable;
    public bool LineSmoothEnabled;
    public bool ExtraGuidelines;
    public int ExtraGuidelinesCount;
    public bool HeadlandDistanceVisible;
    public double HeadlandProximityDistance;
    public bool HeadlandProximityWarning;
    public bool HasHeadland;

    // Coverage patches (vestigial — patches replaced by cell-based detection;
    // see [[coverage-architecture]]). Kept on the interface for save/load only.
    public IReadOnlyList<CoveragePatch> CoveragePatches = Array.Empty<CoveragePatch>();
    public List<(Geometry Geometry, IBrush Brush, int VertexCount, bool IsFinalized,
        double MinX, double MinY, double MaxX, double MaxY)>? CachedCoverageGeometry;

    // Tram lines
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? TramOuterTrack;
    public IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? TramInnerTrack;
    public IReadOnlyList<IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>>? TramParallelLines;
    public IReadOnlyList<IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>>? TramBoundaryExtraLines;
    public AgValoniaGPS.Models.Configuration.TramDisplayMode TramDisplayMode;
    public float TramAlpha;
    public byte TramControlByte; // bit 0=right wheel, bit 1=left wheel
    public double HalfWheelTrack;
    public bool IsDisplayTramControl;

    // Vehicle config
    public double AntennaPivot, AntennaOffset;
    public bool IsMetric;
}
