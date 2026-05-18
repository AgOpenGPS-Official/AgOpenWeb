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
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Views.Controls;

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// Desktop implementation of IMapService.
/// Wraps the platform-specific map control (OpenGL, Skia, or DrawingContext).
/// </summary>
public class MapService : IMapService
{
    private ISharedMapControl? _mapControl;
    // Phase-2 GL renderer, registered alongside the 2D map and fed the same
    // boundary / track / headland / vehicle data so the 3D toggle shows the
    // same field. Camera and coverage paths still flow only through the 2D
    // control until those phases land.
    private GlMapControl? _glMapControl;

    /// <summary>
    /// Register the map control to receive service calls.
    /// </summary>
    public void RegisterMapControl(ISharedMapControl mapControl)
    {
        _mapControl = mapControl;
    }

    public void RegisterGlMapControl(GlMapControl glMapControl)
    {
        _glMapControl = glMapControl;
        // MainViewModel is constructed before MainView on iOS/Android, so the
        // initial field open and headland load fire SetBoundary / SetActiveTrack
        // etc. before this register call. Desktop registers earlier and usually
        // doesn't need the replay, but keeping the same pattern across
        // platforms avoids surprises if the init order ever shifts.
        if (_lastBoundary != null) glMapControl.SetBoundary(_lastBoundary);
        if (_lastHeadlandLine != null) glMapControl.SetHeadlandLine(_lastHeadlandLine);
        glMapControl.SetHeadlandVisible(_lastHeadlandVisible);
        if (_lastActiveTrack != null) glMapControl.SetActiveTrack(_lastActiveTrack);
        if (_lastBaseTrack != null) glMapControl.SetBaseTrack(_lastBaseTrack);
        if (_lastNextTrack != null) glMapControl.SetNextTrack(_lastNextTrack);
        glMapControl.SetCameraPitchDegrees(_lastPitchDegrees);
        glMapControl.SetCameraZoom(_lastZoom);
    }

    /// <summary>
    /// Phase-3 hook: push the current MainViewModel.CameraPitch (degrees,
    /// -90 = overhead) to the GL renderer. Cached so the value is replayed
    /// on register if MainViewModel updates it before the view binds.
    /// </summary>
    public void SetCameraPitchDegrees(double pitchDegrees)
    {
        _lastPitchDegrees = pitchDegrees;
        _glMapControl?.SetCameraPitchDegrees(pitchDegrees);
    }

    // Cached snapshots of low-frequency pushes for replay when GL registers late.
    private Boundary? _lastBoundary;
    private IReadOnlyList<Vec3>? _lastHeadlandLine;
    private bool _lastHeadlandVisible;
    private AgValoniaGPS.Models.Track.Track? _lastActiveTrack, _lastBaseTrack, _lastNextTrack;
    private double _lastPitchDegrees = -60.0;
    private double _lastZoom = 1.0;

    private ISharedMapControl GetMapControl()
    {
        if (_mapControl == null)
            throw new System.InvalidOperationException("Map control not set. Call RegisterMapControl first.");
        return _mapControl;
    }

    public void Toggle3DMode() => GetMapControl().Toggle3DMode();

    public void Set3DMode(bool is3D) => GetMapControl().Set3DMode(is3D);

    public bool Is3DMode => _mapControl?.Is3DMode ?? false;

    public void SetPitch(double deltaRadians) => GetMapControl().SetPitch(deltaRadians);

    public void SetPitchAbsolute(double pitchRadians) => GetMapControl().SetPitchAbsolute(pitchRadians);

    public double Pitch => 0; // TODO: Add Pitch property to IMapControl

    public void Pan(double deltaX, double deltaY) => GetMapControl().Pan(deltaX, deltaY);

    public void PanTo(double x, double y) => GetMapControl().PanTo(x, y);

    public void Zoom(double factor)
    {
        GetMapControl().Zoom(factor);
        _lastZoom = GetMapControl().GetZoom();
        _glMapControl?.SetCameraZoom(_lastZoom);
    }

    public double ZoomLevel => GetMapControl().GetZoom();

    public void Rotate(double deltaRadians) => GetMapControl().Rotate(deltaRadians);

    public void SetRotation(double radians) => GetMapControl().Rotate(radians);

    public double Rotation => 0; // TODO: Add Rotation property to IMapControl

    public void SetCamera(double x, double y, double zoom, double rotation) =>
        GetMapControl().SetCamera(x, y, zoom, rotation);

    public void StartPan(double x, double y) =>
        GetMapControl().StartPan(new Avalonia.Point(x, y));

    public void StartRotate(double x, double y) =>
        GetMapControl().StartRotate(new Avalonia.Point(x, y));

    public void UpdatePointer(double x, double y) =>
        GetMapControl().UpdateMouse(new Avalonia.Point(x, y));

    public void EndInteraction() => GetMapControl().EndPanRotate();

    public void SetBoundary(Boundary? boundary)
    {
        Console.WriteLine($"[MapService] SetBoundary called: boundary={boundary != null}, mapControl={_mapControl != null}");
        _lastBoundary = boundary;
        if (_mapControl != null)
            _mapControl.SetBoundary(boundary);
        else
            Console.WriteLine("[MapService] WARNING: MapControl not set, boundary lost!");
        _glMapControl?.SetBoundary(boundary);
    }

    public void SetVehiclePosition(double easting, double northing, double headingRadians) =>
        GetMapControl().SetVehiclePosition(easting, northing, headingRadians);

    public void SetVehicleSteerAngle(double radians) =>
        GetMapControl().SetVehicleSteerAngle(radians);

    public void SetAllPositions(double vehicleX, double vehicleY, double vehicleHeading,
        double toolX, double toolY, double toolHeading, double toolWidth,
        double hitchX, double hitchY, bool toolReady)
    {
        GetMapControl().SetAllPositions(vehicleX, vehicleY, vehicleHeading,
            toolX, toolY, toolHeading, toolWidth, hitchX, hitchY, toolReady);
        _glMapControl?.SetAllPositions(vehicleX, vehicleY, vehicleHeading,
            toolX, toolY, toolHeading, toolWidth, hitchX, hitchY, toolReady);
    }

    public void SetSectionStates(bool[] sectionOn, double[] sectionWidths, int numSections, int[] buttonStates) =>
        GetMapControl().SetSectionStates(sectionOn, sectionWidths, numSections, buttonStates);

    public bool IsGridVisible
    {
        get => _mapControl?.IsGridVisible ?? false;
        set
        {
            if (_mapControl != null)
                _mapControl.IsGridVisible = value;
        }
    }

    public void SetReversing(bool isReversing) => GetMapControl().IsReversing = isReversing;
    public void SetGuidancePoints(double goalEasting, double goalNorthing, bool isActive) => GetMapControl().SetGuidancePoints(goalEasting, goalNorthing, isActive);

    public void SetNorthUp(bool isNorthUp) => GetMapControl().SetNorthUp(isNorthUp);
    public void SetAutoPan(bool enabled) => GetMapControl().AutoPanEnabled = enabled;
    public void SetCameraFollowMode(int mode) => GetMapControl().CameraFollowMode = mode;
    public (double X, double Y) GetCameraCenter() => GetMapControl().GetCameraCenter();

    public void SetDayMode(bool isDayMode) => GetMapControl().SetDayMode(isDayMode);

    public void SetFlags(IReadOnlyList<(double Easting, double Northing, string Color, string Name)> flags) =>
        GetMapControl().SetFlags(flags);

    public void SetRecordingPoints(IReadOnlyList<(double Easting, double Northing)> points) =>
        GetMapControl().SetRecordingPoints(points);

    public void ClearRecordingPoints() => GetMapControl().ClearRecordingPoints();

    public void SetBoundaryOffsetIndicator(bool show, double offsetMeters = 0.0) =>
        GetMapControl().SetBoundaryOffsetIndicator(show, offsetMeters);

    public void SetBackgroundImage(string imagePath, double minX, double maxY, double maxX, double minY) =>
        GetMapControl().SetBackgroundImage(imagePath, minX, maxY, maxX, minY);

    public void SetBackgroundImageWithMercator(string imagePath, double minX, double maxY, double maxX, double minY,
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        double originLat, double originLon) =>
        GetMapControl().SetBackgroundImageWithMercator(imagePath, minX, maxY, maxX, minY,
            mercMinX, mercMaxX, mercMinY, mercMaxY, originLat, originLon);

    public void ClearBackground() => GetMapControl().ClearBackground();

    // Headland visualization
    public void SetHeadlandLine(IReadOnlyList<Vec3>? headlandPoints)
    {
        _lastHeadlandLine = headlandPoints;
        GetMapControl().SetHeadlandLine(headlandPoints);
        _glMapControl?.SetHeadlandLine(headlandPoints);
    }

    public void SetHeadlandPreview(IReadOnlyList<Vec2>? previewPoints) =>
        GetMapControl().SetHeadlandPreview(previewPoints);

    public void SetHeadlandVisible(bool visible)
    {
        _lastHeadlandVisible = visible;
        GetMapControl().SetHeadlandVisible(visible);
        _glMapControl?.SetHeadlandVisible(visible);
    }

    // YouTurn path visualization
    public void SetYouTurnPath(IReadOnlyList<(double Easting, double Northing)>? turnPath) =>
        GetMapControl().SetYouTurnPath(turnPath);

    public void SetTramLines(
        IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? outerTrack,
        IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>? innerTrack,
        IReadOnlyList<IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>>? parallelLines,
        IReadOnlyList<IReadOnlyList<AgValoniaGPS.Models.Base.Vec2>>? boundaryExtraLines = null) =>
        GetMapControl().SetTramLines(outerTrack, innerTrack, parallelLines, boundaryExtraLines);

    public void SetTramControlByte(byte controlByte) =>
        GetMapControl().SetTramControlByte(controlByte);

    // Track visualization for U-turns
    public void SetNextTrack(AgValoniaGPS.Models.Track.Track? track)
    {
        _lastNextTrack = track;
        GetMapControl().SetNextTrack(track);
        _glMapControl?.SetNextTrack(track);
    }

    public void SetIsInYouTurn(bool isInTurn) =>
        GetMapControl().SetIsInYouTurn(isInTurn);

    // Active Track for guidance
    public void SetActiveTrack(AgValoniaGPS.Models.Track.Track? track)
    {
        _lastActiveTrack = track;
        GetMapControl().SetActiveTrack(track);
        _glMapControl?.SetActiveTrack(track);
    }

    public void SetBaseTrack(AgValoniaGPS.Models.Track.Track? track)
    {
        _lastBaseTrack = track;
        GetMapControl().SetBaseTrack(track);
        _glMapControl?.SetBaseTrack(track);
    }

    // Recorded path / contour strip visualization
    public void SetRecordedPaths(System.Collections.Generic.IReadOnlyList<AgValoniaGPS.Models.Track.Track> paths) =>
        GetMapControl().SetRecordedPaths(paths);

    public void SetContourStrips(System.Collections.Generic.IReadOnlyList<AgValoniaGPS.Models.Track.Track> strips) =>
        GetMapControl().SetContourStrips(strips);

    // Coverage bitmap initialization on field load
    public void InitializeCoverageBitmapWithBounds(double minE, double maxE, double minN, double maxN) =>
        GetMapControl().InitializeCoverageBitmapWithBounds(minE, maxE, minN, maxN);
}
