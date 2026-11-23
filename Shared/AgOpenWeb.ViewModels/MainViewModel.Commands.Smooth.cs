// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Windows.Input;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Smooth Curve panel (AgOpenGPS FormSmoothAB, #929): the operator sets how much to smooth the
/// selected curve with − and +, sees the result on the map, and keeps it with Save or puts the
/// curve back with Cancel. Every amount is one moving average of the curve as it was saved
/// (<see cref="CurveProcessing.SmoothMovingAverage"/>, the amount is the points averaged either
/// side), so the curve keeps its number of points and a step back is exact. The smoothed curve
/// is live at once, as a reference nudge is, and written to field.geojson only by Save: until
/// then every other save of the field's tracks writes the curve as it was.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The most points averaged either side, as AgOpenGPS's panel.</summary>
    public const int SmoothCountMax = 100;

    // The curve being smoothed and its points as saved (never changed: an amount builds a new list).
    private Track? _smoothTrack;
    private List<Vec3>? _smoothSaved;

    public ICommand? SmoothBeginCommand { get; private set; }
    public ICommand? SmoothMoreCommand { get; private set; }
    public ICommand? SmoothLessCommand { get; private set; }
    public ICommand? SmoothSaveCommand { get; private set; }
    public ICommand? SmoothCancelCommand { get; private set; }

    private void InitializeSmoothCommands()
    {
        SmoothBeginCommand = new RelayCommand(BeginSmooth);
        SmoothMoreCommand = new RelayCommand(() => SetSmoothCount(State.Operation.SmoothCount + 1));
        SmoothLessCommand = new RelayCommand(() => SetSmoothCount(State.Operation.SmoothCount - 1));
        SmoothSaveCommand = new RelayCommand(SaveSmooth);
        SmoothCancelCommand = new RelayCommand(() =>
        {
            var track = _smoothTrack;
            if (track == null) return;
            bool changed = State.Operation.SmoothCount > 0;
            EndSmooth(restore: true);
            if (changed) RefreshGuidanceAfterRefNudge();
        });
    }

    /// <summary>The points a track with an open Smooth panel is saved with: the curve as it was.</summary>
    private bool TryGetSmoothSaved(Track track, out List<Vec3> saved)
    {
        saved = _smoothSaved!;
        return ReferenceEquals(track, _smoothTrack) && _smoothSaved != null;
    }

    /// <summary>The panel opens on the selected curve, at no smoothing.</summary>
    private void BeginSmooth()
    {
        var track = SelectedTrack;
        if (track == null)
        {
            ReportFailure("No track selected");
            return;
        }
        if (ReferenceEquals(track, _smoothTrack)) return; // open already
        if (track.IsABLine)
        {
            ReportFailure("Cannot smooth AB lines (only 2 points)");
            return;
        }
        if (track.Points.Count < 5)
        {
            ReportFailure("Too few points to smooth (need at least 5)");
            return;
        }
        if (_refNudgeSaved.ContainsKey(track))
        {
            // Both keep "the curve as saved": one unsaved change to a line at a time.
            ReportFailure("Save or undo the reference line move first");
            return;
        }

        _smoothTrack = track;
        _smoothSaved = track.Points;
        State.Operation.SmoothOpen = true;
        State.Operation.SmoothCount = 0;
    }

    /// <summary>Show the curve smoothed over <paramref name="count"/> points either side (0 = as saved).</summary>
    private void SetSmoothCount(int count)
    {
        var track = _smoothTrack;
        if (track == null || _smoothSaved == null) return;
        count = Math.Clamp(count, 0, SmoothCountMax);
        if (count == State.Operation.SmoothCount) return;

        State.Operation.SmoothCount = count;
        track.Points = count == 0
            ? _smoothSaved
            : CurveProcessing.SmoothMovingAverage(_smoothSaved, count, track.IsClosed);
        RefreshGuidanceAfterRefNudge();
    }

    /// <summary>Write the smoothed curve to field.geojson; it is the saved one from now on.</summary>
    private void SaveSmooth()
    {
        var track = _smoothTrack;
        if (track == null) return;
        bool changed = State.Operation.SmoothCount > 0;
        EndSmooth(restore: false);
        if (!changed) return;
        SaveTracksToFile();
        StatusMessage = $"Smoothed '{track.Name}'";
    }

    /// <summary>A curve we leave with the panel open gets its saved points back.</summary>
    private void DiscardPendingSmooth(Track track)
    {
        if (ReferenceEquals(track, _smoothTrack)) EndSmooth(restore: true);
    }

    private void EndSmooth(bool restore)
    {
        if (restore && _smoothTrack != null && _smoothSaved != null)
            _smoothTrack.Points = _smoothSaved;
        _smoothTrack = null;
        _smoothSaved = null;
        State.Operation.SmoothOpen = false;
        State.Operation.SmoothCount = 0;
    }
}
