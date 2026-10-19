// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using AgOpenWeb.Models.Base;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.State;

/// <summary>
/// Boundary recording state (distinct from field boundaries in FieldState).
/// </summary>
public class BoundaryRecState : ObservableObject
{
    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    private int _pointCount;
    public int PointCount
    {
        get => _pointCount;
        set => SetProperty(ref _pointCount, value);
    }

    private double _areaHectares;
    public double AreaHectares
    {
        get => _areaHectares;
        set => SetProperty(ref _areaHectares, value);
    }

    private double _areaAcres;
    public double AreaAcres
    {
        get => _areaAcres;
        set => SetProperty(ref _areaAcres, value);
    }

    // Recording options (IsDrawRightSide / IsDrawAtPivot / BoundaryOffset) are
    // the last-used recording setup → persistent state, in PersistentAppState.

    // Current recording points
    public ObservableCollection<Vec2> RecordingPoints { get; } = new();

    // Live preview line
    private Vec2? _lastRecordedPoint;
    public Vec2? LastRecordedPoint
    {
        get => _lastRecordedPoint;
        set => SetProperty(ref _lastRecordedPoint, value);
    }

    public void Reset()
    {
        IsRecording = false;
        IsPaused = false;
        PointCount = 0;
        AreaHectares = 0;
        AreaAcres = 0;
        RecordingPoints.Clear();
        LastRecordedPoint = null;
    }

    public void AddPoint(Vec2 point)
    {
        RecordingPoints.Add(point);
        LastRecordedPoint = point;
        PointCount = RecordingPoints.Count;
    }
}
