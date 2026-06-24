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

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Stateless utility for porting guidance tracks between fields.
/// Each source point is taken to be in the source field's local plane,
/// projected up to WGS84, then projected back down into the target
/// plane. Per-point headings are recomputed from the converted geometry
/// because rotating between planes can shift heading near the edges.
/// </summary>
public sealed class TrackCopierService : ITrackCopierService
{
    public List<TrackModel> ConvertTracks(
        IReadOnlyList<TrackModel> tracks,
        LocalPlane sourcePlane,
        LocalPlane targetPlane)
    {
        if (tracks == null || tracks.Count == 0)
            return new List<TrackModel>();

        var converted = new List<TrackModel>(tracks.Count);
        foreach (var source in tracks)
        {
            converted.Add(ConvertSingleTrack(source, sourcePlane, targetPlane));
        }
        return converted;
    }

    public int CopyTracksToField(
        string sourceFieldDirectory,
        string targetFieldDirectory,
        IReadOnlyList<TrackModel> tracks,
        SharedFieldProperties? sharedFieldProperties = null)
    {
        if (tracks == null || tracks.Count == 0) return 0;

        var props = sharedFieldProperties ?? new SharedFieldProperties();

        var sourceOrigin = LoadOrigin(sourceFieldDirectory);
        var targetOrigin = LoadOrigin(targetFieldDirectory);

        var sourcePlane = new LocalPlane(sourceOrigin, props);
        var targetPlane = new LocalPlane(targetOrigin, props);

        var convertedTracks = ConvertTracks(tracks, sourcePlane, targetPlane);

        // Append-then-save so existing tracks in the target field aren't lost
        var existing = TrackFilesService.Load(targetFieldDirectory);
        existing.AddRange(convertedTracks);
        TrackFilesService.Save(targetFieldDirectory, existing);

        return convertedTracks.Count;
    }

    private static TrackModel ConvertSingleTrack(TrackModel source, LocalPlane sourcePlane, LocalPlane targetPlane)
    {
        // Step 1: project every point through source → WGS84 → target.
        // Heading is left at zero here; we recompute below from the
        // converted geometry so it's consistent with the new positions.
        var convertedPoints = new List<Vec3>(source.Points.Count);
        foreach (var p in source.Points)
        {
            var sourceGeo = new GeoCoord(p.Northing, p.Easting);
            var wgs = sourcePlane.ConvertGeoCoordToWgs84(sourceGeo);
            var targetGeo = targetPlane.ConvertWgs84ToGeoCoord(wgs);
            convertedPoints.Add(new Vec3(targetGeo.Easting, targetGeo.Northing, 0));
        }

        // Step 2: recompute headings from the converted geometry.
        RecomputeHeadings(convertedPoints, source.IsClosed);

        return new TrackModel
        {
            Name = source.Name,
            Type = source.Type,
            IsClosed = source.IsClosed,
            NudgeDistance = source.NudgeDistance,
            IsVisible = true, // Copied tracks should be visible by default
            Points = convertedPoints,
            // Reset worked paths — they're meaningless in the new field's coordinate system
            WorkedPaths = new HashSet<int>(),
        };
    }

    /// <summary>
    /// Replace each point's heading with the bearing from this point to the
    /// next. The last point inherits the second-to-last point's heading
    /// (matching upstream <c>TrackCopier</c>'s behavior). For 2-point AB
    /// lines both points share the same heading, which is correct.
    /// For closed loops (water pivot), the last point bridges back to the
    /// first.
    /// </summary>
    private static void RecomputeHeadings(List<Vec3> pts, bool isClosed)
    {
        if (pts.Count < 2) return;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            double heading = NormalizeHeading(
                Math.Atan2(pts[i + 1].Easting - pts[i].Easting,
                          pts[i + 1].Northing - pts[i].Northing));
            pts[i] = new Vec3(pts[i].Easting, pts[i].Northing, heading);
        }

        // Last point
        double lastHeading;
        if (isClosed)
        {
            lastHeading = NormalizeHeading(
                Math.Atan2(pts[0].Easting - pts[^1].Easting,
                          pts[0].Northing - pts[^1].Northing));
        }
        else
        {
            lastHeading = pts[^2].Heading;
        }
        pts[^1] = new Vec3(pts[^1].Easting, pts[^1].Northing, lastHeading);
    }

    private static double NormalizeHeading(double heading)
    {
        const double TwoPi = Math.PI * 2.0;
        // Guard against the 0/2π wrap: a round-trip through WGS84 can leave
        // an "exactly zero" Atan2 result as a tiny negative residual. Without
        // this clamp the `while (heading < 0)` flip would produce ~2π for
        // what should stay ~0 — visually identical but breaks tests and
        // surprises any code that compares headings near zero.
        if (Math.Abs(heading) < 1e-9) return 0;
        while (heading < 0) heading += TwoPi;
        while (heading >= TwoPi) heading -= TwoPi;
        return heading;
    }

    /// <summary>
    /// Read the WGS84 origin from a field directory's Field.txt.
    /// Wraps <see cref="FieldPlaneFileService"/> so the call site doesn't
    /// have to manage the round-trip through <see cref="Field"/>.
    /// </summary>
    private static Wgs84 LoadOrigin(string fieldDirectory)
    {
        var field = new FieldPlaneFileService().LoadField(fieldDirectory);
        return new Wgs84(field.Origin.Latitude, field.Origin.Longitude);
    }
}
