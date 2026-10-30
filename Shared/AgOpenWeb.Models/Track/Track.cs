// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// Track type for behavior variations and file format compatibility.
/// Values match AgOpenGPS TrackMode for file compatibility.
/// </summary>
public enum TrackType
{
    /// <summary>AB line - 2 points, infinite extension</summary>
    ABLine = 2,

    /// <summary>Curve - N points, finite</summary>
    Curve = 4,

    /// <summary>Boundary track offset outward</summary>
    BoundaryOuter = 8,

    /// <summary>Boundary track offset inward</summary>
    BoundaryInner = 16,

    /// <summary>Boundary curve</summary>
    BoundaryCurve = 32,

    /// <summary>Water pivot - circular, closed loop</summary>
    WaterPivot = 64,

    /// <summary>Recorded path - previously driven path for display only</summary>
    RecordedPath = 128,

    /// <summary>Contour - contour guidance strip</summary>
    Contour = 256
}

/// <summary>
/// Unified track representation for all guidance types.
/// Key insight: An AB line is just a curve with 2 points.
///
/// Replaces: ABLine.cs, separate curve models, and track mode switching.
/// All guidance algorithms can work with List&lt;Vec3&gt; points.
/// </summary>
public class Track : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    /// <summary>
    /// Track name for display and file storage.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Track points with Easting, Northing, Heading.
    /// AB Lines have exactly 2 points. Curves have N points.
    /// Headings are stored in radians.
    /// </summary>
    public List<Vec3> Points { get; set; } = new();

    /// <summary>
    /// Track type for behavior variations and file compatibility.
    /// </summary>
    public TrackType Type { get; set; } = TrackType.ABLine;

    /// <summary>
    /// Whether this track forms a closed loop (water pivot, boundary tracks).
    /// </summary>
    public bool IsClosed { get; set; }

    /// <summary>
    /// Drive this track directly (pass 0) instead of free-drive snapping to the nearest
    /// parallel pass. Set for boundary-follow curves — you drive the boundary itself, not
    /// offset passes of it. The guidance keeps the reference line, no pass offsetting.
    /// </summary>
    public bool NoPassOffset { get; set; }

    /// <summary>
    /// Whether this track is currently active for guidance.
    /// </summary>
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Visibility on map.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Accumulated nudge offset in meters.
    /// Positive = right, Negative = left.
    /// </summary>
    public double NudgeDistance { get; set; }

    /// <summary>
    /// Set of path numbers that have been worked (driven with sections on).
    /// Used by skip-and-fill mode to find the next unworked path.
    /// </summary>
    public HashSet<int> WorkedPaths { get; set; } = new();

    public void MarkPathWorked(int pathNumber) => WorkedPaths.Add(pathNumber);
    public bool IsPathWorked(int pathNumber) => WorkedPaths.Contains(pathNumber);
    public void ClearWorkedPaths() => WorkedPaths.Clear();

    /// <summary>
    /// True if this is a 2-point track (AB line behavior).
    /// AB lines use infinite extension along the heading.
    /// </summary>
    public bool IsABLine => Points.Count == 2 && Type == TrackType.ABLine;

    /// <summary>
    /// True if this is a multi-point curve (finite track).
    /// </summary>
    public bool IsCurve => Points.Count > 2 || Type == TrackType.Curve;

    /// <summary>
    /// Overall heading of the track in radians.
    /// For AB lines: heading from point A to B.
    /// For curves: heading of first segment.
    /// </summary>
    public double Heading => Points.Count >= 2
        ? Math.Atan2(Points[1].Easting - Points[0].Easting,
                     Points[1].Northing - Points[0].Northing)
        : 0;

    /// <summary>
    /// Heading in degrees for display purposes.
    /// </summary>
    public double HeadingDegrees => Heading * 180.0 / Math.PI;

    /// <summary>
    /// Point A (first point) for AB line compatibility.
    /// </summary>
    public Vec3 PointA => Points.Count > 0 ? Points[0] : default;

    /// <summary>
    /// Point B (second/last point) for AB line compatibility.
    /// </summary>
    public Vec3 PointB => Points.Count > 1 ? Points[^1] : default;

    /// <summary>
    /// Create an AB line track from two points.
    /// </summary>
    /// <param name="name">Track name</param>
    /// <param name="pointA">Starting point (Easting, Northing, Heading in radians)</param>
    /// <param name="pointB">Ending point (Easting, Northing, Heading in radians)</param>
    /// <returns>A new Track configured as an AB line</returns>
    public static Track FromABLine(string name, Vec3 pointA, Vec3 pointB)
    {
        var heading = Math.Atan2(pointB.Easting - pointA.Easting,
                                  pointB.Northing - pointA.Northing);

        return new Track
        {
            Name = name,
            Type = TrackType.ABLine,
            Points = new List<Vec3>
            {
                new Vec3(pointA.Easting, pointA.Northing, heading),
                new Vec3(pointB.Easting, pointB.Northing, heading)
            },
            IsClosed = false
        };
    }

    /// <summary>
    /// Create a curve track from a list of points.
    /// </summary>
    /// <param name="name">Track name</param>
    /// <param name="points">List of points with Easting, Northing, Heading</param>
    /// <param name="isClosed">Whether the curve forms a closed loop</param>
    /// <returns>A new Track configured as a curve</returns>
    public static Track FromCurve(string name, List<Vec3> points, bool isClosed = false)
    {
        return new Track
        {
            Name = name,
            Type = TrackType.Curve, // closed or not: a pivot is made by FromPivot
            Points = new List<Vec3>(points),
            IsClosed = isClosed
        };
    }

    /// <summary>
    /// A water pivot track (AgOpenGPS TrackMode.waterPivot): a circle round the pivot's centre,
    /// clockwise, closed, with a point at least every metre. Its passes are circles about the same
    /// centre (<see cref="Guidance.CurveProcessing.PivotPass"/>), half a width either side of it like
    /// any reference line.
    /// </summary>
    public static Track FromPivot(string name, Vec3 center, double radius) => new()
    {
        Name = name,
        Type = TrackType.WaterPivot,
        Points = Guidance.CurveProcessing.Circle(center, radius),
        IsClosed = true,
    };

    /// <summary>A water pivot's centre and radius, from its circle.</summary>
    public (Vec3 Center, double Radius) PivotCircle()
    {
        int n = IsClosed && Points.Count > 1 ? Points.Count - 1 : Points.Count; // the closing point repeats the first
        double e = 0, no = 0;
        for (int i = 0; i < n; i++) { e += Points[i].Easting; no += Points[i].Northing; }
        var c = new Vec3(e / n, no / n, 0);
        double r = 0;
        for (int i = 0; i < n; i++) r += Math.Sqrt(Math.Pow(Points[i].Easting - c.Easting, 2) + Math.Pow(Points[i].Northing - c.Northing, 2));
        return (c, r / n);
    }

    public bool IsPivot => Type == TrackType.WaterPivot;

    /// <summary>
    /// Create a recorded path track from a list of points.
    /// Recorded paths are display-only (not used for guidance).
    /// </summary>
    public static Track FromRecordedPath(string name, List<Vec3> points)
    {
        return new Track
        {
            Name = name,
            Type = TrackType.RecordedPath,
            Points = new List<Vec3>(points),
            IsClosed = false,
            IsVisible = true
        };
    }

    /// <summary>
    /// Create a contour track from a list of points.
    /// Contours are used for contour guidance mode.
    /// </summary>
    public static Track FromContour(string name, List<Vec3> points)
    {
        return new Track
        {
            Name = name,
            Type = TrackType.Contour,
            Points = new List<Vec3>(points),
            IsClosed = false,
            IsVisible = true
        };
    }

    /// <summary>
    /// Whether this is a recorded path (display only, not for guidance).
    /// </summary>
    public bool IsRecordedPath => Type == TrackType.RecordedPath;

    /// <summary>A line to guide on: not a recorded path or a contour strip, which share the
    /// track list but are driven their own way (AgOpenGPS keeps them out of its track list).</summary>
    public bool IsGuidanceTrack => Type is not (TrackType.RecordedPath or TrackType.Contour);

    /// <summary>
    /// Whether this is a contour strip (for contour guidance).
    /// </summary>
    public bool IsContour => Type == TrackType.Contour;

}
