// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Contour;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The GPS cycle's contour (#110, AgOpenGPS AddContourPoints + DistanceFromContourLine): the
/// strips recorded while a section paints, the guidance line built from them with the contour
/// button on, the steering along it, and what the map shows of it. One of the cycle's steps
/// (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 8); its state is its own.
///
/// <para><b>Threads.</b> <see cref="ProcessContour"/> runs on the GPS cycle's worker. The host
/// loop switches the contour on and off, locks it to the line, loads, resets and takes the strips
/// to save (<see cref="SetContourMode"/> and the others); the strips and the switch are shared
/// with it under <c>_contourLock</c>. What the map shows (<see cref="RefShown"/> and the others)
/// is written by the cycle and read by it for its snapshot.</para>
///
/// <para><b>Reads from the cycle:</b> the pivot, heading and antenna, the speed, AutoSteer
/// engaged, the field open, reversing and the roll, as arguments; the sections' painting
/// (<see cref="ISectionControlService.SectionStates"/>, written by the control tick); the
/// coverage, for the look ahead.</para>
/// </summary>
internal sealed class ContourStep
{
    private readonly ConfigurationStore _configStore;
    private readonly ISectionControlService _sectionControlService;
    private readonly Func<double, double, bool> _isPointCovered;
    private readonly IAudioService _audioService;

    public ContourStep(ConfigurationStore configStore, ISectionControlService sectionControlService,
        Func<double, double, bool> isPointCovered, IAudioService audioService)
    {
        _configStore = configStore;
        _sectionControlService = sectionControlService;
        _isPointCovered = isPointCovered;
        _audioService = audioService;
    }

    // Contour (#110): owned by the cycle, guarded by _contourLock (the UI thread
    // toggles/locks/loads/resets).
    private readonly ContourGuidance _contour = new();
    private readonly object _contourLock = new();
    private bool _contourOn;
    private Vec2? _lastContourPos; // AgOpenGPS prevContourPos
    private int _contourLineVersion = -1;           // display track cache
    private Models.Track.Track? _contourDisplayTrack;
    private (int strip, int count) _contourRefKey = (-1, 0);
    private IReadOnlyList<Vec3>? _contourRefShown;
    private bool _contourLockedShown, _contourHasPending;

    public void SetContourMode(bool on)
    {
        lock (_contourLock)
        {
            _contourOn = on;
            _contour.ClearReference(); // also unlocks
        }
    }

    public bool ToggleContourLock()
    {
        lock (_contourLock) return _contourOn && _contour.SetLockToLine();
    }

    public void LoadContours(IEnumerable<List<Vec3>> strips)
    {
        lock (_contourLock) { _contour.Load(strips); _lastContourPos = null; }
    }

    public void ResetContours()
    {
        lock (_contourLock) { _contour.Reset(); _lastContourPos = null; }
    }

    public List<List<Vec3>> TakeContoursToSave()
    {
        lock (_contourLock)
        {
            var list = new List<List<Vec3>>(_contour.PendingSave);
            _contour.PendingSave.Clear();
            return list;
        }
    }

    /// <summary>The contour button, as the cycle reads it at its start.</summary>
    public bool IsOn { get { lock (_contourLock) return _contourOn; } }

    /// <summary>The reference strip the line was built from, for the map (the cycle's snapshot).</summary>
    public IReadOnlyList<Vec3>? RefShown => _contourRefShown;
    /// <summary>Locked to the line, for the snapshot.</summary>
    public bool LockedShown => _contourLockedShown;
    /// <summary>Strips waiting to be saved, for the snapshot.</summary>
    public bool HasPending => _contourHasPending;
    /// <summary>A stretch ahead that can't be driven as a lap, and how far ahead, for the snapshot.</summary>
    public (ContourAhead What, double AtM) Ahead => _contourAhead;

    /// <summary>
    /// Contour for one cycle (#110), like AgOpenGPS: record the pivot (+ tool offset) as a
    /// strip while any section paints, each time it has moved a third of a width (or 1 m);
    /// with the contour button on, rebuild the guidance line and compute steering against
    /// it (also when not engaged, for the XTE display). Returns null with no usable line.
    /// </summary>
    public ContourSteer? ProcessContour(bool contourOn, bool hasActiveField,
        double pivotE, double pivotN, double headingRad, double antennaE, double antennaN,
        double speedKmh, bool autoSteerEngaged, bool isReverse, double roll, out Models.Track.Track? displayTrack)
    {
        var config = _configStore;
        var pivot = new Vec3(pivotE, pivotN, headingRad);
        ContourSteer? steer = null;
        lock (_contourLock)
        {
            _contour.FollowAutoSteer(autoSteerEngaged); // before the rebuild: lock the current line
            if (hasActiveField)
            {
                double contourWidth = (config.ActualToolWidth - config.Tool.Overlap) / 3.0;
                double moved = _lastContourPos is { } lp
                    ? Math.Sqrt((pivotE - lp.Easting) * (pivotE - lp.Easting) + (pivotN - lp.Northing) * (pivotN - lp.Northing))
                    : double.MaxValue;
                if (moved > Math.Min(contourWidth, 1.0))
                {
                    bool painting = false;
                    var states = _sectionControlService.SectionStates;
                    for (int i = 0; i < states.Count && !painting; i++) painting = states[i].IsMappingOn;
                    _contour.Record(painting, pivot, config.Tool.Offset);

                    var p = ContourParamsFor(speedKmh);
                    if (contourOn)
                        _contour.BuildContourGuidanceLine(pivot, headingRad, p,
                            Clock.Current.GetTimestamp() / (double)Clock.Current.Frequency, autoSteerEngaged);
                    _lastContourPos = new Vec2(pivotE, pivotN);
                }
            }

            if (contourOn)
            {
                var p = ContourParamsFor(speedKmh);
                double wb = config.Vehicle.Wheelbase;
                var steerPos = new Vec3(pivotE + Math.Sin(headingRad) * wb, pivotN + Math.Cos(headingRad) * wb, headingRad);
                steer = _contour.DistanceFromContourLine(pivot, steerPos, new Vec2(antennaE, antennaN), headingRad,
                    p, speedKmh, isReverse, autoSteerEngaged, roll);
            }

            // Display: the line as a track (rebuilt only when it changes) and the reference strip.
            if (_contour.LineVersion != _contourLineVersion)
            {
                _contourLineVersion = _contour.LineVersion;
                _contourDisplayTrack = _contour.Line.Count >= 2
                    ? new Models.Track.Track
                    {
                        Name = "Contour", Points = new List<Vec3>(_contour.Line),
                        Type = Models.Track.TrackType.Contour, IsVisible = true, IsActive = true,
                    }
                    : null;
            }
            displayTrack = contourOn ? _contourDisplayTrack : null;

            int sn = contourOn && _contour.Line.Count >= 2 ? _contour.StripNum : -1;
            var key = (sn, sn >= 0 ? _contour.Strips[sn].Count : 0);
            if (key != _contourRefKey)
            {
                _contourRefKey = key;
                _contourRefShown = sn >= 0 ? new List<Vec3>(_contour.Strips[sn]) : null;
            }
            _contourLockedShown = contourOn && _contour.IsLocked;
            _contourHasPending = _contour.PendingSave.Count > 0;

            // Look along the line ahead for a stretch that can't be driven as a lap, early enough
            // for the operator to take over; the alarm sounds once as it appears.
            // Checked again when the line changes or the tractor has moved a metre.
            if (!contourOn || _contour.Line.Count < 3)
            {
                _contourAhead = (ContourAhead.Clear, 0);
                _contourAheadVersion = -1;
            }
            else if (_contour.LineVersion != _contourAheadVersion
                || Math.Abs(pivotE - _contourAheadPos.Easting) + Math.Abs(pivotN - _contourAheadPos.Northing) > 1)
            {
                _contourAheadVersion = _contour.LineVersion;
                _contourAheadPos = new Vec2(pivotE, pivotN);
                var ahead = ContourAheadCheck.Check(_contour.Line, _contourAheadPos, headingRad,
                    ContourAheadCheck.Reach(speedKmh), _isPointCovered, config.Guidance.UTurnRadius);
                if (ahead.What != ContourAhead.Clear && _contourAhead.What != ahead.What && autoSteerEngaged)
                    _audioService.Play(SoundEffect.BoundaryAlarm);
                _contourAhead = ahead;
            }
        }
        return steer;
    }

    /// <summary>What the contour line ahead holds (<see cref="ContourAheadCheck"/>), and how far ahead.</summary>
    private (ContourAhead What, double AtM) _contourAhead;
    private int _contourAheadVersion = -1;
    private Vec2 _contourAheadPos;

    private ContourParams ContourParamsFor(double speedKmh)
    {
        var c = _configStore;
        return new ContourParams(
            c.ActualToolWidth, c.Tool.Overlap, c.Tool.Offset,
            c.Guidance.IsStanley, c.Guidance.StanleyHeadingErrorGain, c.Guidance.StanleyDistanceErrorGain,
            c.Vehicle.Wheelbase, c.Vehicle.MaxSteerAngle, c.Guidance.PurePursuitIntegralGain,
            c.AutoSteer.SideHillCompensation, GoalLookAheadDistance.For(_configStore, speedKmh, _contour.LastCrossTrackError));
    }

}
