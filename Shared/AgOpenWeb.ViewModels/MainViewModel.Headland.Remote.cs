// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;

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
            StatusMessage = "No boundary available";
            return;
        }

        int idx1 = FindNearestBoundaryVertex(boundary.Points, e1, n1);
        int idx2 = FindNearestBoundaryVertex(boundary.Points, e2, n2);
        if (idx1 < 0 || idx2 < 0 || idx1 == idx2)
        {
            StatusMessage = "Pick two distinct boundary points";
            return;
        }

        List<Vec3> bndPoints;
        if (isCurve)
        {
            bndPoints = ExtractBoundaryRing(boundary.Points, idx1, idx2);
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
        double ext = BoundaryBboxDiagonal(boundary.Points);

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
        HeadlandSegments.Add(segment);
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
            StatusMessage = "No boundary available";
            return;
        }

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
        HeadlandSegments.Add(segment);
        SelectedHeadlandSegment = segment;
        BuildHeadlandFromSegments();
    }

    /// <summary>Change a segment's inward offset and rebuild the headland.</summary>
    public void RemoteSetHeadlandOffsetAt(int index, double offset)
    {
        if (index < 0 || index >= HeadlandSegments.Count || offset <= 0) return;
        var seg = HeadlandSegments[index];
        seg.Offset = offset;
        ComputeSegmentOffset(seg);
        BuildHeadlandFromSegments();
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
        HeadlandSegments.Remove(seg);
        if (HeadlandSegments.Count == 0) ClearHeadland();
        else BuildHeadlandFromSegments();
    }

    /// <summary>Delete all headland segments and clear the headland entirely.</summary>
    public void RemoteDeleteAllHeadland()
    {
        if (HeadlandSegments.Count == 0 && !HasHeadland) return;
        SelectedHeadlandSegment = null;
        HeadlandSegments.Clear();
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

    /// <summary>Rename a headland segment (geometry unchanged; persists the list).</summary>
    public void RemoteRenameHeadlandAt(int index, string name)
    {
        if (index < 0 || index >= HeadlandSegments.Count || string.IsNullOrWhiteSpace(name)) return;
        HeadlandSegments[index].Name = name.Trim();
        SaveHeadlandSegments();
    }

    // --- Boundary geometry helpers (mirror CanvasDrawSession; the host owns the math) ---

    /// <summary>Diagonal of the boundary's bounding box — a safe "extend past the field"
    /// length so drawn headland lines overshoot far enough to cross their neighbours.</summary>
    private static double BoundaryBboxDiagonal(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts)
    {
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var p in pts)
        {
            if (p.Easting < minE) minE = p.Easting;
            if (p.Easting > maxE) maxE = p.Easting;
            if (p.Northing < minN) minN = p.Northing;
            if (p.Northing > maxN) maxN = p.Northing;
        }
        double w = maxE - minE, h = maxN - minN;
        double diag = System.Math.Sqrt(w * w + h * h);
        return diag > 1 ? diag : 50.0;
    }

    private static int FindNearestBoundaryVertex(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts, double e, double n)
    {
        double minDist = double.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = pts[i].Easting - e;
            double dy = pts[i].Northing - n;
            double d = dx * dx + dy * dy;
            if (d < minDist) { minDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    /// <summary>Boundary points along the shorter arc between two vertex indices.</summary>
    private static List<Vec3> ExtractBoundaryRing(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts, int idx1, int idx2)
    {
        int count = pts.Count;

        var forward = new List<Vec3>();
        int i = idx1;
        while (true)
        {
            var p = pts[i];
            forward.Add(new Vec3(p.Easting, p.Northing, p.Heading));
            if (i == idx2) break;
            i = (i + 1) % count;
            if (forward.Count > count + 1) break;
        }

        var reverse = new List<Vec3>();
        i = idx1;
        while (true)
        {
            var p = pts[i];
            reverse.Add(new Vec3(p.Easting, p.Northing, p.Heading));
            if (i == idx2) break;
            i = (i - 1 + count) % count;
            if (reverse.Count > count + 1) break;
        }

        return forward.Count <= reverse.Count ? forward : reverse;
    }
}
