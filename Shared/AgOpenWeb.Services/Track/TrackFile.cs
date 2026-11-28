// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// Reading a field's tracks (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 5: track persistence).
/// Moved from MainViewModel.LoadTracksFromField, MigrateCurveTrack and SamePoints; the view
/// model keeps adding them to its list, restoring the last track used, and saving (one call to
/// GeoJsonFieldService.SaveTracks with its pass and nudge). No state, so any thread.
/// </summary>
public static class TrackFile
{
    /// <summary>
    /// The field's tracks from its field.geojson (opening the field imported any AgOpenGPS track
    /// files), as the view model shows them: a recorded path saved more than once only once,
    /// every track inactive, curves migrated, an AgOpenGPS pivot's centre made its circle.
    /// Throws when the file cannot be read.
    /// </summary>
    public static List<Models.Track.Track> Load(string fieldDirectory, double passWidth)
    {
        var loaded = new List<Models.Track.Track>();
        // field.geojson (opening the field imported any AgOpenGPS track files).
        var tracks = Services.GeoJson.GeoJsonFieldService.LoadTracks(fieldDirectory);

        foreach (var track in tracks)
        {
            // A recorded path saved more than once (each open added a copy, fixed above) is
            // loaded once; the next save drops the copies.
            if (track.IsRecordedPath && loaded.Any(t => t.IsRecordedPath && SamePoints(t, track)))
                continue;

            // Ensure all tracks start inactive (SelectedTrack setter will activate)
            track.IsActive = false;
            MigrateCurveTrack(track);
            // A pivot imported from AgOpenGPS has only its centre: its reference circle is a
            // pass width out, so its passes are AgOpenGPS's (half a width, then whole widths).
            if (track.IsPivot && track.Points.Count < 3 && track.Points.Count > 0)
            {
                var c = track.Points[0];
                track.Points = Models.Guidance.CurveProcessing.Circle(new Vec3(c.Easting, c.Northing, 0), passWidth);
                track.IsClosed = true;
            }
            loaded.Add(track);
        }
        return loaded;
    }

    /// <summary>
    /// Migrate a loaded curve track to the resolution-independent guidance
    /// conventions: recompute per-point headings (atan2(dEast,dNorth)) and flag
    /// closed loops. Tracks saved before #422's fix carried boundary-convention or
    /// zero headings and IsClosed=false, which made the "which way is forward"
    /// decision random (spin/reverse). Idempotent for correctly-built tracks.
    /// </summary>
    public static void MigrateCurveTrack(Models.Track.Track track)
    {
        if (track == null || !track.IsCurve || track.Points.Count < 3)
            return;

        // Closed loop if the first and last points coincide.
        var first = track.Points[0];
        var last = track.Points[^1];
        double de = first.Easting - last.Easting;
        double dn = first.Northing - last.Northing;
        if (de * de + dn * dn < 1.0)
            track.IsClosed = true;

        track.Points = Models.Guidance.CurveProcessing.CalculateHeadings(track.Points);
    }

    public static bool SamePoints(Models.Track.Track a, Models.Track.Track b) =>
        a.Points.Count == b.Points.Count
        && a.Points.Zip(b.Points).All(p => Math.Abs(p.First.Easting - p.Second.Easting) < 0.01 && Math.Abs(p.First.Northing - p.Second.Northing) < 0.01);
}
