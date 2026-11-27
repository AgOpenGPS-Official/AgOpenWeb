// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: web/remote-client entry points for the Field Builder
/// Headland tab (Phase MT, Field Builder stage 2). These mirror the native
/// <c>FieldBuilderDialogPanel</c> headland *save* paths exactly — the browser only
/// captures map-tap points and the offset, then ships them here; ALL geometry
/// (snap, segment extraction, inward offset, headland build) runs on the host via
/// the existing <see cref="ComputeSegmentOffset"/> / <see cref="BuildHeadlandFromSegments"/>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Create a Line or Curve headland segment from two tapped points. Each point is
    /// snapped to the nearest boundary vertex (host-side, like native's draw session);
    /// a Line uses the two snapped points directly, a Curve follows the shorter boundary
    /// arc between them. Offset is the inward distance (m). Mirrors the
    /// <c>_session.IsHeadland</c> branch of <c>CreateAB_Click</c>.
    /// </summary>
    public void RemoteCreateHeadlandFromMapPoints(bool isCurve, double offset, double e1, double n1, double e2, double n2)
    {
        var boundary = State.Field.CurrentBoundary?.OuterBoundary;
        if (boundary?.Points == null || boundary.Points.Count < 3)
        {
            ReportFailure("No boundary available");
            return;
        }

        if (!IsHeadlandOffsetANumber(offset)) return;

        int idx1 = HeadlandGeometry.FindNearestBoundaryVertex(boundary.Points, e1, n1);
        int idx2 = HeadlandGeometry.FindNearestBoundaryVertex(boundary.Points, e2, n2);
        if (idx1 < 0 || idx2 < 0 || idx1 == idx2)
        {
            StatusMessage = "Pick two distinct boundary points";
            return;
        }

        List<Vec3> bndPoints;
        if (isCurve)
        {
            bndPoints = HeadlandGeometry.ExtractBoundaryRing(boundary.Points, idx1, idx2);
            if (bndPoints.Count < 2) { StatusMessage = "Could not build curve"; return; }
        }
        else
        {
            var p1 = boundary.Points[idx1];
            var p2 = boundary.Points[idx2];
            bndPoints = new List<Vec3>
            {
                new(p1.Easting, p1.Northing, p1.Heading),
                new(p2.Easting, p2.Northing, p2.Heading),
            };
        }

        if (offset <= 0) offset = HeadlandDistance > 0 ? HeadlandDistance : 12.0;

        // Auto-extend the inset line well past the field so it overshoots the corners and
        // crosses the neighbouring edge lines — that crossing is how multiple lines enclose
        // the headland (native lets you drag the extend-arrows to do this; that on-map drag
        // is the Stage-4 editor). Use the boundary's bounding-box diagonal so the overshoot
        // reaches across any field. A single line still can't close a loop on its own; you
        // add one per edge until they enclose an area, exactly like native.
        double ext = HeadlandGeometry.BoundaryBboxDiagonal(boundary.Points);

        var segment = new HeadlandSegment
        {
            Name = $"{(isCurve ? "Curve" : "Line")} {HeadlandSegments.Count + 1}",
            Type = isCurve ? HeadlandSegmentType.Curve : HeadlandSegmentType.Line,
            Offset = offset,
            BoundaryStartIndex = idx1,
            BoundaryEndIndex = idx2,
            BoundaryIndex = 0,
            BoundaryPoints = bndPoints,
            StartExtension = ext,
            EndExtension = ext,
        };

        ComputeSegmentOffset(segment);
        _sessionData.Headland.Add(segment);
        SelectedHeadlandSegment = segment;
        BuildHeadlandFromSegments();
    }

    /// <summary>
    /// Create a whole-boundary headland: the entire outer boundary polygon offset inward
    /// by <paramref name="offset"/> (m). Mirrors <c>AddHeadlandBoundary_Click</c> +
    /// <c>CreateAB_Click</c> — the closed boundary loop goes through Clipper inward offset.
    /// </summary>
    public void RemoteCreateHeadlandWholeBoundary(double offset)
    {
        var boundary = State.Field.CurrentBoundary?.OuterBoundary;
        if (boundary?.Points == null || boundary.Points.Count < 3)
        {
            ReportFailure("No boundary available");
            return;
        }

        if (!IsHeadlandOffsetANumber(offset)) return;

        // Closed loop: all boundary points + a duplicate of the first to close it
        // (matches the native session which appends boundary.Points[0] at the end).
        var bndPoints = new List<Vec3>(boundary.Points.Count + 1);
        foreach (var pt in boundary.Points)
            bndPoints.Add(new Vec3(pt.Easting, pt.Northing, pt.Heading));
        var f = boundary.Points[0];
        bndPoints.Add(new Vec3(f.Easting, f.Northing, f.Heading));

        if (offset <= 0)
        {
            double tw = ConfigStore.ActualToolWidth;
            offset = tw > 0 ? tw * 2 : (HeadlandDistance > 0 ? HeadlandDistance : 12.0);
        }

        var segment = new HeadlandSegment
        {
            Name = $"Boundary {HeadlandSegments.Count + 1}",
            Type = HeadlandSegmentType.Boundary,
            Offset = offset,
            BoundaryStartIndex = 0,
            BoundaryEndIndex = boundary.Points.Count - 1,
            BoundaryIndex = 0,
            BoundaryPoints = bndPoints,
        };

        ComputeSegmentOffset(segment);
        if (!WholeBoundaryOffsetFits(segment)) return;
        _sessionData.Headland.Add(segment);
        SelectedHeadlandSegment = segment;
        BuildHeadlandFromSegments();
    }

    /// <summary>Change a segment's inward offset and rebuild the headland.</summary>
    public void RemoteSetHeadlandOffsetAt(int index, double offset)
    {
        if (index < 0 || index >= HeadlandSegments.Count) return;
        if (!IsHeadlandOffsetANumber(offset)) return;
        if (offset <= 0)
        {
            ReportFailure("Headland distance must be more than 0 m");
            return;
        }
        var seg = HeadlandSegments[index];
        double previousOffset = seg.Offset;
        var previousPoints = seg.OffsetPoints;
        seg.Offset = offset;
        ComputeSegmentOffset(seg);
        if (seg.Type == HeadlandSegmentType.Boundary && !WholeBoundaryOffsetFits(seg))
        {
            seg.Offset = previousOffset; // the headland stays as it was
            seg.OffsetPoints = previousPoints;
            return;
        }
        BuildHeadlandFromSegments();
    }

    /// <summary>False, and the operator told, for a distance that is not a number (NaN, infinity).</summary>
    private bool IsHeadlandOffsetANumber(double offset)
    {
        if (double.IsFinite(offset)) return true;
        ReportFailure("Headland distance must be a number");
        return false;
    }

    /// <summary>
    /// A whole-boundary offset that swallowed the field (nothing is left inside it) is refused,
    /// with the most the field takes, so a typo like 120 m for 12 m, or 5000 m for 50 m, does
    /// not build a "headland" outside the field that crosses itself.
    /// </summary>
    private bool WholeBoundaryOffsetFits(HeadlandSegment segment)
    {
        if (segment.OffsetPoints.Count >= 4) return true;
        double most = LargestWholeBoundaryOffset(segment.BoundaryPoints);
        ReportFailure(most >= 1
            ? $"A {segment.Offset:0.#} m headland leaves nothing of this field; the most it takes is about {Math.Floor(most):0} m"
            : $"A {segment.Offset:0.#} m headland leaves nothing of this field");
        return false;
    }

    /// <summary>The largest inward offset of the ring that still leaves a polygon, to half a metre, by halving.</summary>
    private static double LargestWholeBoundaryOffset(List<Vec3> ring)
    {
        if (ring.Count < 3) return 0;
        var pts = new List<Vec2>(ring.Count);
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var p in ring)
        {
            pts.Add(new Vec2(p.Easting, p.Northing));
            minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
            minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
        }
        var offsets = new Services.Geometry.PolygonOffsetService();
        double lo = 0, hi = Math.Sqrt((maxE - minE) * (maxE - minE) + (maxN - minN) * (maxN - minN)) / 2;
        for (int i = 0; i < 16 && hi - lo > 0.5; i++)
        {
            double mid = (lo + hi) / 2;
            var left = offsets.CreateInwardOffset(pts, mid);
            if (left != null && left.Count >= 3) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Delete a single headland segment. With segments remaining, rebuild;
    /// when the last one is removed, CLEAR the headland (do not let
    /// BuildHeadlandFromSegments silently revert it to the whole boundary, which draws a
    /// green line on the outer boundary that looks undeletable).</summary>
    public void RemoteDeleteHeadlandAt(int index)
    {
        if (index < 0 || index >= HeadlandSegments.Count) return;
        var seg = HeadlandSegments[index];
        if (SelectedHeadlandSegment == seg) SelectedHeadlandSegment = null;
        _sessionData.Headland.Remove(seg);
        if (HeadlandSegments.Count == 0) ClearHeadland();
        else BuildHeadlandFromSegments();
    }

    /// <summary>Delete all headland segments and clear the headland entirely.</summary>
    public void RemoteDeleteAllHeadland()
    {
        if (HeadlandSegments.Count == 0 && !HasHeadland) return;
        SelectedHeadlandSegment = null;
        _sessionData.Headland.Clear();
        ClearHeadland();
    }

    /// <summary>Remove the built headland (line off, not present). Mirrors the
    /// "no boundary" else-branch of BuildHeadlandFromSegments, plus persists the now-empty
    /// segment list so the cleared state survives a field reopen.</summary>
    private void ClearHeadland()
    {
        State.Field.HeadlandLine = null;
        CurrentHeadlandLine = null;
        HasHeadland = false;
        IsHeadlandOn = false;
        _mapService.SetHeadlandVisible(false);
        OnPropertyChanged(nameof(HeadlandStatusText));
        OnPropertyChanged(nameof(CurrentHeadlandLineForPreview));
        SaveHeadlandSegments();
    }

    /// <summary>
    /// The operator made or removed the headland: a headland polygon the field's file carried with
    /// its boundary (an import, a field from a test rig) is not the field's headland any more. Left
    /// in the boundary it came back: after Delete All (the Field Builder listed nothing on such a
    /// field) the next change of the boundary, the outer boundary made hard, put it back as the
    /// headland (<see cref="SetCurrentBoundary"/>) and wrote it to the file again; a headland made
    /// in the Field Builder was replaced by it the same way.
    /// </summary>
    private void DropHeadlandPolygon()
    {
        var boundary = State.Field.CurrentBoundary;
        if (boundary?.HeadlandPolygon == null) return;
        boundary.HeadlandPolygon = null;
        if (ActiveField?.Boundary is { } fieldBoundary && !ReferenceEquals(fieldBoundary, boundary))
            fieldBoundary.HeadlandPolygon = null;
        var dir = _fieldService.ActiveField?.DirectoryPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Services.GeoJson.GeoJsonFieldService.RemoveHeadlandPolygon(dir);
            _logger.LogInformation("[Headland] The headland polygon from the field's file was removed: the headland made in the Field Builder (or none) replaces it");
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[Headland] Failed to remove the headland polygon: {ex.Message}");
        }
    }

    /// <summary>Rename a headland segment (geometry unchanged; persists the list).</summary>
    public void RemoteRenameHeadlandAt(int index, string name)
    {
        if (index < 0 || index >= HeadlandSegments.Count || string.IsNullOrWhiteSpace(name)) return;
        HeadlandSegments[index].Name = name.Trim();
        SaveHeadlandSegments();
    }

    // --- Boundary geometry helpers (mirror CanvasDrawSession; the host owns the math) ---

}
