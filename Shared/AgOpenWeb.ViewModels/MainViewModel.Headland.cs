// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using Microsoft.Extensions.Logging;

using AgOpenWeb.Services;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial class containing headland segment offset computation,
/// headland polygon building from segments, and related geometry helpers.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Compute offset points for a headland segment by offsetting boundary points inward
    /// (<see cref="Services.Headland.HeadlandSegmentBuilder.ComputeSegmentOffset"/>, against the
    /// open field's outer boundary).
    /// </summary>
    public void ComputeSegmentOffset(Models.Headland.HeadlandSegment segment) =>
        Services.Headland.HeadlandSegmentBuilder.ComputeSegmentOffset(segment, State.Field.CurrentBoundary?.OuterBoundary);

    /// <summary>
    /// Build the headland polygon from segments. If a single Boundary segment exists,
    /// use its offset points directly. Otherwise, concatenate all segment offset points
    /// and check if they form a loop.
    /// </summary>
    public void BuildHeadlandFromSegments()
    {
        if (HeadlandSegments.Count == 0)
        {
            // Default: headland = boundary
            var boundary = State.Field.CurrentBoundary?.OuterBoundary;
            if (boundary?.Points != null && boundary.Points.Count >= 3)
            {
                var bndPoints = new List<Vec3>();
                foreach (var pt in boundary.Points)
                    bndPoints.Add(new Vec3(pt.Easting, pt.Northing, pt.Heading));
                bndPoints.Add(new Vec3(boundary.Points[0].Easting, boundary.Points[0].Northing, boundary.Points[0].Heading));

                State.Field.HeadlandLine = bndPoints;
                CurrentHeadlandLine = bndPoints;
                State.Field.HeadlandLine = bndPoints;
                HasHeadland = true;
                IsHeadlandOn = true;
                _mapService.SetHeadlandLine(bndPoints);
                _mapService.SetHeadlandVisible(true);
            }
            else
            {
                HasHeadland = false;
                IsHeadlandOn = false;
                State.Field.HeadlandLine = null;
                CurrentHeadlandLine = null;
                State.Field.HeadlandLine = null;
                _mapService.SetHeadlandVisible(false);
            }
            OnPropertyChanged(nameof(HeadlandStatusText));
            OnPropertyChanged(nameof(CurrentHeadlandLineForPreview));
            return;
        }

        // Start with headland = boundary
        var bnd = State.Field.CurrentBoundary?.OuterBoundary;
        if (bnd?.Points == null || bnd.Points.Count < 3)
        {
            ReportFailure("No boundary for headland");
            return;
        }

        var (headland, cutsApplied) = Services.Headland.HeadlandSegmentBuilder.Build(HeadlandSegments, bnd, _logger);

        // Apply headland
        State.Field.HeadlandLine = headland;
        CurrentHeadlandLine = headland;
        State.Field.HeadlandLine = headland;
        HasHeadland = true;
        IsHeadlandOn = true;
        _mapService.SetHeadlandLine(headland);
        _mapService.SetHeadlandVisible(true);

        OnPropertyChanged(nameof(HeadlandStatusText));
        OnPropertyChanged(nameof(CurrentHeadlandLineForPreview));
        // Log headland points for debugging
        for (int p = 0; p < headland.Count; p++)
            _logger.LogDebug($"[Headland] Point {p}: E={headland[p].Easting:F1} N={headland[p].Northing:F1}");

        StatusMessage = cutsApplied > 0
            ? $"Headland modified ({cutsApplied} cuts, {headland.Count} points)"
            : $"Headland = boundary ({headland.Count} points, no offset lines intersect)";

        SaveHeadlandSegments();
    }

    /// <summary>The drawable offset line for a segment — its inward-offset points plus the
    /// straight extension overshoots at each end. This is exactly what the headland build
    /// intersects/trims against neighbours, so drawing it shows the operator the lines that
    /// must cross to enclose the headland (mirrors native's "Create and Edit Headland" view).</summary>
    public List<Vec3> GetHeadlandSegmentEditLine(Models.Headland.HeadlandSegment seg)
        => HeadlandGeometry.BuildOffsetLineWithExtensions(seg);

    private void SaveHeadlandSegments()
    {
        if (!IsFieldOpen || string.IsNullOrEmpty(CurrentFieldName)) return;
        try
        {
            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrEmpty(fieldsDir))
                fieldsDir = Path.Combine(AppDataRoot.Documents, "Fields");
            var fieldPath = Path.Combine(fieldsDir, CurrentFieldName);
            Services.Headland.HeadlandSegmentFileService.Save(fieldPath, HeadlandSegments);
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug($"[Headland] Failed to save headland segments: {ex.Message}");
        }
    }

    /// <summary>
    /// The operator redrew, re-recorded, rebuilt or deleted the outer boundary. The headland and
    /// the tram lines were built on the old one and would stay where it was: a headland outside
    /// the new field, tram lines along the old edge, a headland with no boundary at all. A
    /// whole-boundary headland segment is rebuilt on the new boundary at its distance; a line or
    /// curve segment named points of the old boundary and is dropped; the tram systems are
    /// rebuilt (a boundary system follows the new boundary, a track system is clipped to it).
    /// Returns what was done, for the status line; empty when there was nothing to do.
    /// </summary>
    private string OuterBoundaryChanged()
    {
        var done = new List<string>();
        var droppedNames = new List<string>();
        var outer = State.Field.CurrentBoundary?.OuterBoundary;
        bool hasOuter = outer?.Points != null && outer.Points.Count >= 3;

        if (HeadlandSegments.Count > 0 || HasHeadland)
        {
            if (!hasOuter)
            {
                SelectedHeadlandSegment = null;
                _sessionData.Headland.Clear();
                ClearHeadland();
                done.Add("headland removed");
            }
            else
            {
                for (int i = HeadlandSegments.Count - 1; i >= 0; i--)
                {
                    if (HeadlandSegments[i].Type == Models.Headland.HeadlandSegmentType.Boundary) continue;
                    if (ReferenceEquals(SelectedHeadlandSegment, HeadlandSegments[i])) SelectedHeadlandSegment = null;
                    droppedNames.Insert(0, HeadlandSegments[i].Name);
                    _sessionData.Headland.RemoveAt(i);
                }
                int dropped = droppedNames.Count;
                if (HeadlandSegments.Count == 0)
                {
                    ClearHeadland();
                    done.Add("headland removed (its lines were drawn on the old boundary)");
                }
                else
                {
                    var ring = new List<Vec3>(outer!.Points.Count + 1);
                    foreach (var p in outer.Points) ring.Add(new Vec3(p.Easting, p.Northing, p.Heading));
                    ring.Add(ring[0]);
                    foreach (var seg in HeadlandSegments)
                    {
                        seg.BoundaryPoints = new List<Vec3>(ring);
                        seg.BoundaryStartIndex = 0;
                        seg.BoundaryEndIndex = outer.Points.Count - 1;
                        ComputeSegmentOffset(seg);
                    }
                    BuildHeadlandFromSegments();
                    done.Add(dropped > 0
                        ? $"headland rebuilt on the new boundary ({dropped} line/curve segment(s) of the old one dropped)"
                        : "headland rebuilt on the new boundary");
                }
            }
        }

        if (TramSystems.Count > 0 || _tramLineService.HasTramLines)
        {
            RebuildAndSaveTram();
            done.Add("tram lines rebuilt");
        }
        string summary = string.Join(", ", done);

        // A line or curve segment named points of the old boundary, so it can't be carried over.
        // The operator drew it: say so in a notification, not only in the status line.
        if (droppedNames.Count > 0)
            FailureReported?.Invoke(
                $"Headland: {string.Join(", ", droppedNames)} drawn on the old boundary, removed. Draw them again on the new boundary.");
        return summary;
    }

}
