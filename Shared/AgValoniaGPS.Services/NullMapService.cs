// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services;

/// <summary>
/// No-op <see cref="IMapService"/> for the headless host: there is no native
/// map control to drive (the browser is the only renderer). Everything the
/// browser needs is read from <see cref="AgValoniaGPS.Models.State.ApplicationState"/>
/// by the RemoteServer projectors — in particular the dead-reckoned render pose,
/// which <c>MainViewModel.OnRenderPullTick</c> writes to ApplicationState directly
/// (in addition to calling SetAllPositions here, which we discard). So sinking the
/// map pushes to nothing is correct and loses no state the web UI consumes.
///
/// Camera/view getters return inert defaults; the camera lives in the browser.
/// </summary>
public sealed class NullMapService : IMapService
{
    public void Toggle3DMode() { }
    public void Set3DMode(bool is3D) { }
    public bool Is3DMode => false;

    public void SetPitch(double deltaRadians) { }
    public void SetPitchAbsolute(double pitchRadians) { }
    public double Pitch => 0.0;

    public void Pan(double deltaX, double deltaY) { }
    public void PanTo(double x, double y) { }

    public void Zoom(double factor) { }
    public double ZoomLevel => 1.0;

    public void Rotate(double deltaRadians) { }
    public void SetRotation(double radians) { }
    public double Rotation => 0.0;

    public void SetCamera(double x, double y, double zoom, double rotation) { }

    public void StartPan(double x, double y) { }
    public void StartRotate(double x, double y) { }
    public void UpdatePointer(double x, double y) { }
    public void EndInteraction() { }

    public void SetBoundary(Boundary? boundary) { }
    public void SetVehiclePosition(double easting, double northing, double headingRadians) { }
    public void SetVehicleSteerAngle(double radians) { }

    public void SetAllPositions(double vehicleX, double vehicleY, double vehicleHeading,
        double toolX, double toolY, double toolHeading, double toolWidth,
        double hitchX, double hitchY, bool toolReady) { }

    public void SetSectionStates(bool[] sectionOn, double[] sectionWidths, int numSections, int[] buttonStates) { }

    public void SetReversing(bool isReversing) { }
    public void SetGuidancePoints(double goalEasting, double goalNorthing, bool isActive) { }

    public void SetNorthUp(bool isNorthUp) { }
    public void SetAutoPan(bool enabled) { }
    public void SetCameraFollowMode(int mode) { }
    public (double X, double Y) GetCameraCenter() => (0.0, 0.0);
    public void SetDayMode(bool isDayMode) { }

    public void SetFlags(IReadOnlyList<(double Easting, double Northing, string Color, string Name)> flags) { }

    public void SetRecordingPoints(IReadOnlyList<(double Easting, double Northing)> points) { }
    public void ClearRecordingPoints() { }
    public void SetBoundaryOffsetIndicator(bool show, double offsetMeters = 0.0) { }

    public void SetBackgroundImage(string imagePath, double minX, double maxY, double maxX, double minY) { }
    public void SetBackgroundImageWithMercator(string imagePath, double minX, double maxY, double maxX, double minY,
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        double originLat, double originLon) { }
    public void ClearBackground() { }

    public void SetHeadlandLine(IReadOnlyList<Vec3>? headlandPoints) { }
    public void SetHeadlandPreview(IReadOnlyList<Vec2>? previewPoints) { }
    public void SetHeadlandVisible(bool visible) { }

    public void SetYouTurnPath(IReadOnlyList<(double Easting, double Northing)>? turnPath) { }

    public void SetTramLines(
        IReadOnlyList<Vec2>? outerTrack,
        IReadOnlyList<Vec2>? innerTrack,
        IReadOnlyList<IReadOnlyList<Vec2>>? parallelLines,
        IReadOnlyList<IReadOnlyList<Vec2>>? boundaryExtraLines = null) { }
    public void SetTramControlByte(byte controlByte) { }

    public void SetNextTrack(AgValoniaGPS.Models.Track.Track? track) { }
    public void SetIsInYouTurn(bool isInTurn) { }

    public void SetActiveTrack(AgValoniaGPS.Models.Track.Track? track) { }
    public void SetBaseTrack(AgValoniaGPS.Models.Track.Track? track) { }

    public void SetRecordedPaths(IReadOnlyList<AgValoniaGPS.Models.Track.Track> paths) { }
    public void SetContourStrips(IReadOnlyList<AgValoniaGPS.Models.Track.Track> strips) { }

    public void InitializeCoverageBitmapWithBounds(double minE, double maxE, double minN, double maxN) { }
    public void RebuildCoverageBitmapForResolutionChange() { }
}
