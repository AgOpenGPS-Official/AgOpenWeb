// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Ported from AgOpenGPS BoundaryBuilder by Brian Tischler.

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

public class BoundaryBuilderService : IBoundaryBuilderService
{
    private const double IntersectionTolerance = 0.01;
    private const double IntersectionToleranceSq = IntersectionTolerance * IntersectionTolerance;
    private const double MaxSegmentStep = 1.5;
    private const double IntersectionSearchRadius = 5.0;
    private const double TrimSegmentLength = 0.5;
    private const int MinPolygonPoints = 3;

    public BoundaryPolygon? BuildBoundaryFromTracks(List<Models.Track.Track> tracks, double extendMeters = 20.0)
    {
        if (tracks == null || tracks.Count < 2)
            return null;

        // Get points from each track, extend endpoints
        var trackPointSets = new List<(List<Vec2> Points, int TrackId)>();
        for (int i = 0; i < tracks.Count; i++)
        {
            var points = GetTrackPoints(tracks[i]);
            if (points.Count < 2) continue;

            points = ExtendEndpoints(points, extendMeters);
            points = EnforceMaxStep(points, MaxSegmentStep);
            trackPointSets.Add((points, i));
        }

        if (trackPointSets.Count < 2)
            return null;

        // Build segments from all tracks
        var segments = new List<Segment>();
        foreach (var (points, trackId) in trackPointSets)
        {
            for (int i = 0; i < points.Count - 1; i++)
                segments.Add(new Segment(points[i], points[i + 1], trackId));
        }

        // Find intersections between segments of different tracks
        FindIntersections(segments);

        // Trim each track to its outermost intersections
        var trimmedSegments = TrimSegmentsToIntersections(segments, trackPointSets);

        // Build polygon from trimmed segments
        var polygon = ConvertSegmentsToPolygon(trimmedSegments);

        if (polygon.Count < MinPolygonPoints)
            return null;

        // Convert to BoundaryPolygon, then normalize resolution once at the source so
        // the 0.5 m build-segment density doesn't propagate downstream. See
        // Plans/BOUNDARY_RESOLUTION_NORMALIZATION.md.
        var raw = new List<BoundaryPoint>(polygon.Count);
        foreach (var point in polygon)
            raw.Add(new BoundaryPoint(point.Easting, point.Northing, 0));

        var boundaryPolygon = new BoundaryPolygon
        {
            Points = Models.Base.BoundaryResolution.Normalize(raw)
        };

        return boundaryPolygon;
    }

    private static List<Vec2> GetTrackPoints(Models.Track.Track track)
    {
        return track.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
    }

    private static List<Vec2> ExtendEndpoints(List<Vec2> points, double extendMeters)
    {
        if (points.Count < 2) return points;

        var result = new List<Vec2>(points.Count + 2);

        // Extend start
        var dir = points[0] - points[1];
        double length = dir.GetLength();
        if (length > 1e-6)
        {
            var extend = dir * (extendMeters / length);
            result.Add(points[0] + extend);
        }

        result.AddRange(points);

        // Extend end
        dir = points[^1] - points[^2];
        length = dir.GetLength();
        if (length > 1e-6)
        {
            var extend = dir * (extendMeters / length);
            result.Add(points[^1] + extend);
        }

        return result;
    }

    private static List<Vec2> EnforceMaxStep(List<Vec2> points, double maxStep)
    {
        var result = new List<Vec2>();
        if (points.Count == 0) return result;

        result.Add(points[0]);

        for (int i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1];
            var current = points[i];
            double distance = GeometryMath.Distance(prev, current);

            if (distance <= maxStep)
            {
                result.Add(current);
                continue;
            }

            int steps = (int)Math.Ceiling(distance / maxStep);
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                result.Add(Vec2.Lerp(prev, current, t));
            }
            result.Add(current);
        }

        return result;
    }

    private static void FindIntersections(List<Segment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var seg1 = segments[i];
            for (int j = i + 1; j < segments.Count; j++)
            {
                var seg2 = segments[j];

                // Only check segments from different tracks
                if (seg1.TrackId == seg2.TrackId) continue;

                // Bounding box early-out
                if (!BoundingBoxesOverlap(seg1, seg2, IntersectionSearchRadius)) continue;

                var (intersects, point) = LineSegmentsIntersect(seg1.Start, seg1.End, seg2.Start, seg2.End);
                if (intersects)
                {
                    seg1.AddIntersection(point);
                    seg2.AddIntersection(point);
                }
            }
        }
    }

    private static bool BoundingBoxesOverlap(Segment s1, Segment s2, double tolerance)
    {
        double minX1 = Math.Min(s1.Start.Easting, s1.End.Easting) - tolerance;
        double maxX1 = Math.Max(s1.Start.Easting, s1.End.Easting) + tolerance;
        double minY1 = Math.Min(s1.Start.Northing, s1.End.Northing) - tolerance;
        double maxY1 = Math.Max(s1.Start.Northing, s1.End.Northing) + tolerance;

        double minX2 = Math.Min(s2.Start.Easting, s2.End.Easting);
        double maxX2 = Math.Max(s2.Start.Easting, s2.End.Easting);
        double minY2 = Math.Min(s2.Start.Northing, s2.End.Northing);
        double maxY2 = Math.Max(s2.Start.Northing, s2.End.Northing);

        return !(maxX1 < minX2 || maxX2 < minX1 || maxY1 < minY2 || maxY2 < minY1);
    }

    private static (bool Intersects, Vec2 Point) LineSegmentsIntersect(Vec2 p1, Vec2 p2, Vec2 p3, Vec2 p4)
    {
        var r = p2 - p1;
        var s = p4 - p3;
        var pq = p3 - p1;

        double rxs = Vec2.Cross(r, s);
        double pqxr = Vec2.Cross(pq, r);

        if (Math.Abs(rxs) < 1e-10)
        {
            // Collinear case
            if (Math.Abs(pqxr) < 1e-10)
            {
                double rDotR = Vec2.Dot(r, r);
                if (rDotR < 1e-10) return (false, default);

                double t0 = Vec2.Dot(pq, r) / rDotR;
                double t1 = t0 + Vec2.Dot(s, r) / rDotR;
                if (t0 > t1) (t0, t1) = (t1, t0);

                if (t0 <= 1 && t1 >= 0)
                {
                    double intersectionT = Math.Max(0, Math.Min(t0, 1));
                    return (true, p1 + r * intersectionT);
                }
            }
            return (false, default);
        }

        double t = Vec2.Cross(pq, s) / rxs;
        double u = pqxr / rxs;

        if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
            return (true, p1 + r * t);

        return (false, default);
    }

    private static List<Segment> TrimSegmentsToIntersections(
        List<Segment> allSegments,
        List<(List<Vec2> Points, int TrackId)> trackPointSets)
    {
        var trimmed = new List<Segment>();

        foreach (var (points, trackId) in trackPointSets)
        {
            var trackSegments = allSegments.Where(s => s.TrackId == trackId).ToList();
            if (trackSegments.Count == 0) continue;

            var intersections = trackSegments
                .SelectMany(s => s.Intersections)
                .Distinct(new Vec2ApproxComparer())
                .ToList();

            if (intersections.Count < 2) continue;

            var (startDist, endDist) = GetTrimDistances(points, intersections);
            if (startDist >= endDist) continue;

            var trimmedPoints = ExtractTrimmedPoints(points, startDist, endDist);
            trimmed.AddRange(CreateUniformSegments(trimmedPoints, trackId, TrimSegmentLength));
        }

        return trimmed;
    }

    private static (double StartDist, double EndDist) GetTrimDistances(List<Vec2> points, List<Vec2> intersections)
    {
        var distances = new List<double> { 0 };
        for (int i = 1; i < points.Count; i++)
            distances.Add(distances[i - 1] + GeometryMath.Distance(points[i - 1], points[i]));

        var intersectionDistances = new List<double>();
        foreach (var pt in intersections)
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                var projected = Vec2.ProjectOnSegment(pt, points[i], points[i + 1]);
                double distToSeg = GeometryMath.Distance(pt, projected);

                if (distToSeg < IntersectionSearchRadius)
                {
                    intersectionDistances.Add(distances[i] + GeometryMath.Distance(points[i], pt));
                    break;
                }
            }
        }

        if (intersectionDistances.Count < 2) return (0, 0);
        return (intersectionDistances.Min(), intersectionDistances.Max());
    }

    private static List<Vec2> ExtractTrimmedPoints(List<Vec2> points, double startDist, double endDist)
    {
        var trimmed = new List<Vec2>();
        double accumulatedDist = 0;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            double segmentLength = GeometryMath.Distance(a, b);
            double segmentStart = accumulatedDist;
            double segmentEnd = accumulatedDist + segmentLength;

            if (segmentEnd < startDist || segmentStart > endDist)
            {
                accumulatedDist = segmentEnd;
                continue;
            }

            if (segmentLength < 1e-10)
            {
                accumulatedDist = segmentEnd;
                continue;
            }

            double t1 = Math.Max(0, (startDist - segmentStart) / segmentLength);
            double t2 = Math.Min(1, (endDist - segmentStart) / segmentLength);

            var p1 = Vec2.Lerp(a, b, t1);
            var p2 = Vec2.Lerp(a, b, t2);

            if (trimmed.Count == 0 || GeometryMath.Distance(trimmed[^1], p1) > IntersectionTolerance)
                trimmed.Add(p1);

            if (GeometryMath.Distance(p1, p2) > IntersectionTolerance)
                trimmed.Add(p2);

            accumulatedDist = segmentEnd;
        }

        return trimmed;
    }

    private static List<Segment> CreateUniformSegments(List<Vec2> points, int trackId, double segmentLength)
    {
        var segments = new List<Segment>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            double distance = GeometryMath.Distance(start, end);
            int steps = Math.Max(1, (int)(distance / segmentLength));

            for (int j = 0; j < steps; j++)
            {
                double t1 = (double)j / steps;
                double t2 = (double)(j + 1) / steps;
                segments.Add(new Segment(Vec2.Lerp(start, end, t1), Vec2.Lerp(start, end, t2), trackId));
            }
        }

        return segments;
    }

    private static List<Vec2> ConvertSegmentsToPolygon(List<Segment> segments)
    {
        if (segments.Count == 0) return new List<Vec2>();

        var polygon = new List<Vec2>();
        var visited = new HashSet<int>();
        int currentIdx = 0;

        polygon.Add(segments[0].Start);
        polygon.Add(segments[0].End);
        visited.Add(0);

        while (visited.Count < segments.Count)
        {
            var connectionPoint = polygon[^1];
            int nextIdx = -1;

            for (int i = 0; i < segments.Count; i++)
            {
                if (visited.Contains(i)) continue;

                if (GeometryMath.Distance(segments[i].Start, connectionPoint) < IntersectionTolerance)
                {
                    nextIdx = i;
                    break;
                }

                if (GeometryMath.Distance(segments[i].End, connectionPoint) < IntersectionTolerance)
                {
                    segments[i].Reverse();
                    nextIdx = i;
                    break;
                }
            }

            if (nextIdx < 0) break;

            polygon.Add(segments[nextIdx].End);
            visited.Add(nextIdx);
        }

        return polygon;
    }

    private class Segment
    {
        public Vec2 Start { get; private set; }
        public Vec2 End { get; private set; }
        public int TrackId { get; }
        public List<Vec2> Intersections { get; } = new();

        public Segment(Vec2 start, Vec2 end, int trackId)
        {
            Start = start;
            End = end;
            TrackId = trackId;
        }

        public void Reverse() => (Start, End) = (End, Start);

        public void AddIntersection(Vec2 point)
        {
            if (!Intersections.Any(p => (p - point).GetLengthSquared() < IntersectionToleranceSq))
                Intersections.Add(point);
        }
    }

    private class Vec2ApproxComparer : IEqualityComparer<Vec2>
    {
        public bool Equals(Vec2 a, Vec2 b) =>
            (a - b).GetLengthSquared() < IntersectionToleranceSq;

        public int GetHashCode(Vec2 p) =>
            HashCode.Combine(
                Math.Round(p.Easting / IntersectionTolerance),
                Math.Round(p.Northing / IntersectionTolerance));
    }
}
