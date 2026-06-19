// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Headland;
using AgValoniaGPS.Models.Track;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial: web/remote-client entry points for Field Builder stage 4 —
/// on-map point editing. The browser captures the dragged point positions and ships them;
/// the host rebuilds the track (heading recompute) or headland segment (snap to boundary,
/// re-extract arc, re-offset, rebuild) and persists, mirroring the native edit-session save.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Replace a track's geometry with dragged points (2 = AB line, more = curve),
    /// recomputing per-point heading. Mirrors native CreateABLineFromPoints / curve create.</summary>
    public void RemoteSaveTrackEdit(int index, List<(double e, double n)> pts)
    {
        if (index < 0 || index >= SavedTracks.Count || pts.Count < 2) return;
        var track = SavedTracks[index];

        var newPts = new List<Vec3>(pts.Count);
        if (pts.Count == 2)
        {
            double h = Math.Atan2(pts[1].e - pts[0].e, pts[1].n - pts[0].n);
            newPts.Add(new Vec3(pts[0].e, pts[0].n, h));
            newPts.Add(new Vec3(pts[1].e, pts[1].n, h));
            track.Type = TrackType.ABLine;
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double h = i < pts.Count - 1
                    ? Math.Atan2(pts[i + 1].e - pts[i].e, pts[i + 1].n - pts[i].n)
                    : (newPts.Count > 0 ? newPts[^1].Heading : 0);
                newPts.Add(new Vec3(pts[i].e, pts[i].n, h));
            }
            track.Type = TrackType.Curve;
        }

        track.Points = newPts;
        SaveTracksToFile();
        // Re-select to refresh nudge/guidance state for the (possibly active) edited track.
        SelectedTrack = track;
        OnTrackVisibilityChanged();
    }

    /// <summary>Re-point a Line/Curve headland segment from two dragged endpoints (snapped to
    /// the boundary), re-extracting the boundary arc for curves, then re-offset + rebuild.
    /// Whole-boundary segments aren't point-edited.</summary>
    public void RemoteSaveHeadlandEdit(int index, double e1, double n1, double e2, double n2)
    {
        if (index < 0 || index >= HeadlandSegments.Count) return;
        var seg = HeadlandSegments[index];
        if (seg.Type == HeadlandSegmentType.Boundary) return;

        var boundary = State.Field.CurrentBoundary?.OuterBoundary;
        if (boundary?.Points == null || boundary.Points.Count < 3) return;

        int idx1 = FindNearestBoundaryVertex(boundary.Points, e1, n1);
        int idx2 = FindNearestBoundaryVertex(boundary.Points, e2, n2);
        if (idx1 < 0 || idx2 < 0 || idx1 == idx2) return;

        if (seg.Type == HeadlandSegmentType.Curve)
        {
            var ring = ExtractBoundaryRing(boundary.Points, idx1, idx2);
            if (ring.Count < 2) return;
            seg.BoundaryPoints = ring;
        }
        else
        {
            var p1 = boundary.Points[idx1];
            var p2 = boundary.Points[idx2];
            seg.BoundaryPoints = new List<Vec3>
            {
                new(p1.Easting, p1.Northing, p1.Heading),
                new(p2.Easting, p2.Northing, p2.Heading),
            };
        }
        seg.BoundaryStartIndex = idx1;
        seg.BoundaryEndIndex = idx2;

        ComputeSegmentOffset(seg);
        SelectedHeadlandSegment = seg;
        BuildHeadlandFromSegments();
    }
}
