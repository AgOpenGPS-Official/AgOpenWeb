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

using System.Linq;

using AgOpenWeb.Services.Interfaces;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial class containing Boundary Recording state and event handling.
/// Manages boundary point collection and area calculation during field boundary recording.
/// </summary>
public partial class MainViewModel
{
    #region Boundary Recording Fields

    private bool _isBoundaryRecording;

    #endregion

    #region Boundary Recording Properties

    public bool IsBoundaryRecording
    {
        get => _isBoundaryRecording;
        set => SetProperty(ref _isBoundaryRecording, value);
    }

    // PointCount/AreaHectares — single home is State.BoundaryRec (§12.3). These
    // VM properties are thin pass-throughs for AXAML binding; no local copy.
    public int BoundaryPointCount
    {
        get => State.BoundaryRec.PointCount;
        private set
        {
            if (State.BoundaryRec.PointCount != value)
            {
                State.BoundaryRec.PointCount = value;
                OnPropertyChanged();
            }
        }
    }

    public double BoundaryAreaHectares
    {
        get => State.BoundaryRec.AreaHectares;
        private set
        {
            if (State.BoundaryRec.AreaHectares != value)
            {
                State.BoundaryRec.AreaHectares = value;
                OnPropertyChanged();
            }
        }
    }

    #endregion

    #region Boundary Recording Event Handlers

    private void OnBoundaryPointAdded(object? sender, BoundaryPointAddedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            // Update centralized state (BoundaryPointCount/AreaHectares pass
            // through to State.BoundaryRec and notify bindings).
            BoundaryPointCount = e.TotalPoints;
            BoundaryAreaHectares = e.AreaHectares;
            State.BoundaryRec.AreaAcres = e.AreaHectares * 2.47105;

            // Update map with recorded points
            var points = _boundaryRecordingService.RecordedPoints
                .Select(p => (p.Easting, p.Northing))
                .ToList();
            _mapService.SetRecordingPoints(points);
        });
    }

    private void OnBoundaryStateChanged(object? sender, BoundaryRecordingStateChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            // Update centralized state
            State.BoundaryRec.IsRecording = e.State == BoundaryRecordingState.Recording;
            State.BoundaryRec.IsPaused = e.State == BoundaryRecordingState.Paused;
            State.BoundaryRec.AreaAcres = e.AreaHectares * 2.47105;

            // Pass-through properties (write State.BoundaryRec + notify bindings)
            IsBoundaryRecording = e.State == BoundaryRecordingState.Recording;
            BoundaryPointCount = e.PointCount;
            BoundaryAreaHectares = e.AreaHectares;

            // Update header text when recording state changes
            OnPropertyChanged(nameof(BoundaryRecordingHeaderText));

            // Clear recording points from map when recording becomes idle
            if (e.State == BoundaryRecordingState.Idle)
            {
                State.BoundaryRec.RecordingPoints.Clear();
                _mapService.ClearRecordingPoints();
            }
            // Update map with current recorded points (for undo/clear operations)
            else if (e.PointCount >= 0)
            {
                var points = _boundaryRecordingService.RecordedPoints
                    .Select(p => (p.Easting, p.Northing))
                    .ToList();
                _mapService.SetRecordingPoints(points);
            }
        });
    }

    #endregion
}
