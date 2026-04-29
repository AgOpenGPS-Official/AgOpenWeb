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

using System.Collections.Generic;
using AgValoniaGPS.Models;
using TrackModel = AgValoniaGPS.Models.Track.Track;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Copies guidance tracks between fields, transforming each point through
/// the source field's local plane → WGS84 → target field's local plane.
///
/// Ported from upstream TrackCopier. AgValonia's unified Track model holds
/// all geometry in <c>Points</c> (no separate ptA/ptB/curvePts), so the
/// transform is simpler than upstream's per-component conversion.
/// </summary>
public interface ITrackCopierService
{
    /// <summary>
    /// Convert tracks between two LocalPlanes. Each input track is cloned,
    /// every point is transformed, and per-point heading is recomputed
    /// from the converted geometry. WorkedPaths is reset and IsVisible
    /// is forced true on the output (matches upstream behavior — copied
    /// tracks should show up in the target field by default).
    /// </summary>
    List<TrackModel> ConvertTracks(
        IReadOnlyList<TrackModel> tracks,
        LocalPlane sourcePlane,
        LocalPlane targetPlane);

    /// <summary>
    /// Copy <paramref name="tracks"/> from <paramref name="sourceFieldDirectory"/>
    /// into <paramref name="targetFieldDirectory"/>. Loads the target's
    /// existing tracks via <c>TrackFilesService</c>, appends the converted
    /// tracks, then saves. Returns the count of tracks added.
    /// </summary>
    int CopyTracksToField(
        string sourceFieldDirectory,
        string targetFieldDirectory,
        IReadOnlyList<TrackModel> tracks,
        SharedFieldProperties? sharedFieldProperties = null);
}
