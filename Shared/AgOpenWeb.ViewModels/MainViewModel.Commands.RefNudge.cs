// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Reference line nudge (AgOpenGPS FormRefNudge / NudgeRefTrack, #489): moves the selected
/// track itself, where the normal nudge only offsets the guidance line. A move is live at
/// once (guidance and the map follow the moved line) but only written to field.geojson by
/// Save; Undo puts the line back as it was. Until then the file keeps the saved line: every
/// other save of the field's tracks (selecting another track, closing the field) writes the
/// line as it was. The step is its own setting in the vehicle profile
/// (AutoSteer.RefNudgeDistance), apart from the normal nudge's.
/// </summary>
public partial class MainViewModel
{
    // The track the panel's "moved" total belongs to; another track starts again at zero.
    private Track? _refNudgeTrack;

    // Unsaved reference moves: the line as it was saved, per moved track. The lists are
    // never changed, a move builds a new one.
    private readonly Dictionary<Track, List<Vec3>> _refNudgeSaved = new();

    // How far each of those tracks is moved from its saved line, m, + = right of A→B (the
    // track's own frame: the driver may turn round between steps). Every move is one offset of
    // the saved line by this total, so steps don't resample a curve again and again, and back
    // to 0 is the saved line exactly.
    private readonly Dictionary<Track, double> _refNudgeOffset = new();

    public ICommand? RefNudgeLeftCommand { get; private set; }
    public ICommand? RefNudgeRightCommand { get; private set; }
    public ICommand? RefHalfNudgeLeftCommand { get; private set; }
    public ICommand? RefHalfNudgeRightCommand { get; private set; }
    public ICommand? RefNudgeBeginCommand { get; private set; }
    public ICommand? RefNudgeSaveCommand { get; private set; }
    public ICommand? RefNudgeUndoCommand { get; private set; }

    private void InitializeRefNudgeCommands()
    {
        // The panel's left/right are the driver's, as the normal nudge's.
        RefNudgeLeftCommand = new RelayCommand(() => RefNudgeSelectedTrack(-ConfigStore.AutoSteer.RefNudgeDistance * 0.01));
        RefNudgeRightCommand = new RelayCommand(() => RefNudgeSelectedTrack(ConfigStore.AutoSteer.RefNudgeDistance * 0.01));
        RefHalfNudgeLeftCommand = new RelayCommand(() => RefNudgeSelectedTrack(-RefHalfWorkingWidth()));
        RefHalfNudgeRightCommand = new RelayCommand(() => RefNudgeSelectedTrack(RefHalfWorkingWidth()));
        // The panel opened: the moved total starts at zero, unless a move is still waiting for Save.
        RefNudgeBeginCommand = new RelayCommand(() =>
        {
            _refNudgeTrack = SelectedTrack;
            if (SelectedTrack == null || !_refNudgeSaved.ContainsKey(SelectedTrack))
                State.Operation.RefNudgeMoved = 0;
            State.Operation.RefNudgePending = SelectedTrack != null && _refNudgeSaved.ContainsKey(SelectedTrack);
        });
        RefNudgeSaveCommand = new RelayCommand(SaveRefNudge);
        RefNudgeUndoCommand = new RelayCommand(UndoRefNudge);
    }

    /// <summary>Half a working width, as AgOpenGPS (tool width − overlap) × 0.5.</summary>
    private double RefHalfWorkingWidth() => (ConfigStore.ActualToolWidth - Tool.Overlap) * 0.5;

    /// <summary>The tracks as they are written to field.geojson: a track with an unsaved
    /// reference move as it was saved.</summary>
    private List<Track> TracksForSave() =>
        SavedTracks.Select(t => _refNudgeSaved.TryGetValue(t, out var saved) ? t.WithPoints(saved) : t).ToList();

    /// <summary>
    /// Move the selected track sideways by <paramref name="meters"/> (+ = the driver's right,
    /// as the normal nudge), then refresh guidance so the swaths and the pass number follow
    /// the moved line. Not saved: that is Save's.
    /// </summary>
    private void RefNudgeSelectedTrack(double meters)
    {
        var track = SelectedTrack;
        if (track == null)
        {
            ReportFailure("No track selected");
            return;
        }
        if (track.IsClosed || track.IsContour || track.IsRecordedPath)
        {
            ReportFailure("This track can't be moved");
            return;
        }

        // Positive in the track's own frame is right of A→B; against that, the driver's right is its left.
        double alongTrack = State.Guidance.IsHeadingSameWay ? meters : -meters;
        var saved = _refNudgeSaved.TryGetValue(track, out var s) ? s : track.Points;
        double offset = _refNudgeOffset.GetValueOrDefault(track) + alongTrack;
        var moved = Math.Abs(offset) < 1e-9 ? saved : MoveTrackSideways(saved, offset);
        if (moved == null)
        {
            ReportFailure("This curve can't be moved that far");
            return;
        }

        if (!ReferenceEquals(_refNudgeTrack, track))
        {
            _refNudgeTrack = track;
            State.Operation.RefNudgeMoved = 0;
        }
        State.Operation.RefNudgeMoved += meters;

        _refNudgeSaved.TryAdd(track, saved); // the line as saved, kept for Undo and for other saves
        _refNudgeOffset[track] = offset;
        track.Points = moved; // a new list (or the saved one): the cycle may be reading the old one
        State.Operation.RefNudgePending = true;
        RefreshGuidanceAfterRefNudge();

        StatusMessage = string.Create(CultureInfo.InvariantCulture,
            $"Moved '{track.Name}' {(meters > 0 ? "right" : "left")} {Math.Abs(meters * 100):F1} cm");
    }

    /// <summary>Write the moved line to field.geojson; the moved line is the saved one from now on.</summary>
    private void SaveRefNudge()
    {
        var track = SelectedTrack;
        if (track == null || !_refNudgeSaved.Remove(track))
        {
            StatusMessage = "No reference move to save";
            return;
        }
        _refNudgeOffset.Remove(track);
        SaveTracksToFile();
        State.Operation.RefNudgeMoved = 0;
        State.Operation.RefNudgePending = false;
        StatusMessage = $"Saved '{track.Name}'";
    }

    /// <summary>Put the selected line back as it was saved.</summary>
    private void UndoRefNudge()
    {
        var track = SelectedTrack;
        if (track == null || !RestoreSavedLine(track))
        {
            StatusMessage = "No reference move to undo";
            return;
        }
        RefreshGuidanceAfterRefNudge();
        StatusMessage = $"Undid the move of '{track.Name}'";
    }

    /// <summary>A track we leave with an unsaved reference move gets its saved line back,
    /// so nothing unsaved is carried over to the next line.</summary>
    private void DiscardPendingRefNudge(Track track)
    {
        if (RestoreSavedLine(track))
            StatusMessage = $"The unsaved move of '{track.Name}' was undone";
    }

    private bool RestoreSavedLine(Track track)
    {
        if (!_refNudgeSaved.Remove(track, out var saved)) return false;
        _refNudgeOffset.Remove(track);
        track.Points = saved;
        State.Operation.RefNudgeMoved = 0;
        State.Operation.RefNudgePending = false;
        return true;
    }

    /// <summary>The moved or restored line becomes the one guidance and the map use, a pending
    /// U-turn was built from the old line.</summary>
    private void RefreshGuidanceAfterRefNudge()
    {
        _intents.RequestClearYouTurn();
        SyncGuidanceStateToPipeline();
        _mapService.SetActiveTrack(SelectedTrack);
    }

    /// <summary>The track's points moved perpendicular to the line by <paramref name="distance"/>
    /// (+ = right of A→B), or null for a curve too tight to offset. A curve gets the same offset
    /// the passes are built with, so a moved reference is the pass that was there. Called with
    /// the saved line and the whole move, never with a moved line.</summary>
    private static List<Vec3>? MoveTrackSideways(List<Vec3> points, double distance)
    {
        if (points.Count == 2)
        {
            double heading = Math.Atan2(points[1].Easting - points[0].Easting, points[1].Northing - points[0].Northing);
            double dE = Math.Cos(heading) * distance, dN = -Math.Sin(heading) * distance;
            return new List<Vec3>
            {
                new(points[0].Easting + dE, points[0].Northing + dN, points[0].Heading),
                new(points[1].Easting + dE, points[1].Northing + dN, points[1].Heading),
            };
        }

        var moved = CurveProcessing.CreateOffsetCurve(points, distance);
        return moved.Count >= 2 ? moved : null;
    }
}
