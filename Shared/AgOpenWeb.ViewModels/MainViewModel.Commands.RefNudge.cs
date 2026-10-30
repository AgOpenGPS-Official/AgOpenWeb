// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Track;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Reference line nudge (AgOpenGPS FormRefNudge / NudgeRefTrack, #489): moves the selected
/// track itself, and saves it, where the normal nudge only offsets the guidance line. The
/// Ref Nudge panel stays open while the operator moves a line, saves, selects the next
/// line and moves that. The step is its own setting in the vehicle profile
/// (AutoSteer.RefNudgeDistance), apart from the normal nudge's.
/// </summary>
public partial class MainViewModel
{
    private readonly ITrackNudgingService _refNudging = new TrackNudgingService();

    // The track the panel's "moved" total belongs to; another track starts again at zero.
    private Track? _refNudgeTrack;

    public ICommand? RefNudgeLeftCommand { get; private set; }
    public ICommand? RefNudgeRightCommand { get; private set; }
    public ICommand? RefHalfNudgeLeftCommand { get; private set; }
    public ICommand? RefHalfNudgeRightCommand { get; private set; }
    public ICommand? RefNudgeBeginCommand { get; private set; }

    private void InitializeRefNudgeCommands()
    {
        // The panel's left/right are the driver's, as the normal nudge's.
        RefNudgeLeftCommand = new RelayCommand(() => RefNudgeSelectedTrack(-ConfigStore.AutoSteer.RefNudgeDistance * 0.01));
        RefNudgeRightCommand = new RelayCommand(() => RefNudgeSelectedTrack(ConfigStore.AutoSteer.RefNudgeDistance * 0.01));
        RefHalfNudgeLeftCommand = new RelayCommand(() => RefNudgeSelectedTrack(-RefHalfWorkingWidth()));
        RefHalfNudgeRightCommand = new RelayCommand(() => RefNudgeSelectedTrack(RefHalfWorkingWidth()));
        // The panel opened: the moved total starts at zero.
        RefNudgeBeginCommand = new RelayCommand(() =>
        {
            _refNudgeTrack = SelectedTrack;
            State.Operation.RefNudgeMoved = 0;
        });
    }

    /// <summary>Half a working width, as AgOpenGPS (tool width − overlap) × 0.5.</summary>
    private double RefHalfWorkingWidth() => (ConfigStore.ActualToolWidth - Tool.Overlap) * 0.5;

    /// <summary>
    /// Move the selected track sideways by <paramref name="meters"/> (+ = the driver's right,
    /// as the normal nudge), then refresh guidance so the swaths and the pass number follow
    /// the moved line, and save the field's tracks.
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
        var moved = MoveTrackSideways(track.Points, alongTrack);
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

        // A new list: the cycle may be reading the old one.
        track.Points = moved;
        // Pending U-turn was built from the old line.
        _intents.RequestClearYouTurn();
        OnSelectedTrackGeometryChanged();

        StatusMessage = string.Create(CultureInfo.InvariantCulture,
            $"Moved '{track.Name}' {(meters > 0 ? "right" : "left")} {Math.Abs(meters * 100):F1} cm");
    }

    /// <summary>The track's points moved perpendicular to the line by <paramref name="distance"/>
    /// (+ = right of A→B), or null for a curve too short or too tight to move.</summary>
    private List<Vec3>? MoveTrackSideways(List<Vec3> points, double distance)
    {
        if (points.Count == 2)
        {
            double heading = Math.Atan2(points[1].Easting - points[0].Easting, points[1].Northing - points[0].Northing);
            var output = _refNudging.NudgeABLine(new ABLineNudgeInput
            {
                PointA = new Vec2(points[0].Easting, points[0].Northing),
                PointB = new Vec2(points[1].Easting, points[1].Northing),
                Heading = heading,
                Distance = distance,
            });
            return new List<Vec3>
            {
                new(output.NewPointA.Easting, output.NewPointA.Northing, points[0].Heading),
                new(output.NewPointB.Easting, output.NewPointB.Northing, points[1].Heading),
            };
        }

        var curve = _refNudging.NudgeCurve(new CurveNudgeInput { CurvePoints = points, Distance = distance });
        return curve.NewCurvePoints.Count > 0 ? curve.NewCurvePoints : null;
    }
}
