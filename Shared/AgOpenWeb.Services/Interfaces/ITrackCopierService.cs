// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Copies guidance tracks between fields, transforming each point through
/// the source field's local plane → WGS84 → target field's local plane.
///
/// Ported from upstream TrackCopier. AgOpenWeb's unified Track model holds
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
