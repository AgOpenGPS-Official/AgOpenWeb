// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Guidance
{
    /// <summary>
    /// Curve preprocessing utilities for guidance line preparation.
    /// Provides algorithms for spacing, interpolation, and heading calculation.
    /// </summary>
    public static class CurveProcessing
    {
        /// <summary>
        /// Full preprocessing pipeline: minimum spacing → interpolation → heading calculation
        /// </summary>
        /// <param name="points">Input curve points</param>
        /// <param name="minSpacing">Minimum distance between points (meters)</param>
        /// <param name="interpolationSpacing">Target spacing for interpolated points (meters)</param>
        /// <returns>Processed curve points with calculated headings</returns>
        public static List<Vec3> Preprocess(IReadOnlyList<Vec3> points, double minSpacing, double interpolationSpacing)
        {
            var result = EnsureMinimumSpacing(points, minSpacing);
            result = InterpolatePoints(result, interpolationSpacing);
            result = CalculateHeadings(result);
            return result;
        }

        /// <summary>
        /// Ensures minimum spacing between consecutive points by removing points that are too close.
        /// Always preserves the first and last points.
        /// </summary>
        /// <param name="points">Input points</param>
        /// <param name="minSpacing">Minimum allowed distance between points (meters)</param>
        /// <returns>Points with minimum spacing enforced</returns>
        public static List<Vec3> EnsureMinimumSpacing(IReadOnlyList<Vec3> points, double minSpacing)
        {
            if (points == null || points.Count < 2)
                return points != null ? new List<Vec3>(points) : new List<Vec3>();

            var spaced = new List<Vec3>(points.Count);
            Vec3 last = points[0];
            spaced.Add(last);

            double minSq = minSpacing * minSpacing;

            for (int i = 1; i < points.Count; i++)
            {
                double dx = points[i].Easting - last.Easting;
                double dy = points[i].Northing - last.Northing;
                if ((dx * dx + dy * dy) >= minSq)
                {
                    spaced.Add(points[i]);
                    last = points[i];
                }
            }

            // Always add the original last point if it's not already there
            Vec3 final = points[points.Count - 1];
            Vec3 compare = spaced[spaced.Count - 1];
            if (GeometryMath.DistanceSquared(final, compare) > 1e-10)
                spaced.Add(final);

            return spaced;
        }

        /// <summary>
        /// Interpolates additional points between existing points at fixed spacing.
        /// Creates a smoother curve with consistent point density.
        /// </summary>
        /// <param name="points">Input points</param>
        /// <param name="spacingMeters">Target spacing between interpolated points (meters)</param>
        /// <returns>Interpolated points with consistent spacing</returns>
        public static List<Vec3> InterpolatePoints(IReadOnlyList<Vec3> points, double spacingMeters)
        {
            if (points == null || points.Count < 2)
                return points != null ? new List<Vec3>(points) : new List<Vec3>();

            var result = new List<Vec3>(points.Count * 2);

            for (int i = 0; i < points.Count - 1; i++)
            {
                Vec3 a = points[i];
                Vec3 b = points[i + 1];
                result.Add(a);

                double dx = b.Easting - a.Easting;
                double dy = b.Northing - a.Northing;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                int steps = (int)(distance / spacingMeters);
                for (int j = 1; j < steps; j++)
                {
                    double t = (double)j / steps;
                    double x = a.Easting + dx * t;
                    double y = a.Northing + dy * t;
                    result.Add(new Vec3(x, y, 0));
                }
            }

            result.Add(points[points.Count - 1]);
            return result;
        }

        /// <summary>
        /// A Catmull-Rom curve through the points, only on the segments longer than
        /// <paramref name="minLength"/> (with a point every <paramref name="step"/> metres): a line drawn
        /// through a few taps and along stretches of fence, whose fence points stay where they are.
        /// </summary>
        public static List<Vec3> SmoothLongSegments(IReadOnlyList<Vec3> points, double minLength = 2.0, double step = 1.0)
        {
            int n = points.Count;
            var result = new List<Vec3>(n);
            if (n == 0) return result;
            for (int i = 0; i + 1 < n; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                result.Add(p1);
                double len = Math.Sqrt(Math.Pow(p2.Easting - p1.Easting, 2) + Math.Pow(p2.Northing - p1.Northing, 2));
                if (len <= minLength) continue;
                var p0 = i > 0 ? points[i - 1] : ExtrapolatePoint(p2, p1);
                var p3 = i + 2 < n ? points[i + 2] : ExtrapolatePoint(p1, p2);
                int steps = (int)Math.Ceiling(len / step);
                for (int k = 1; k < steps; k++)
                    result.Add(CatmullRomPoint(p0, p1, p2, p3, (double)k / steps));
            }
            result.Add(points[n - 1]);
            return result;
        }

        /// <summary>
        /// Smooths a curve using Catmull-Rom spline interpolation.
        /// Creates a smooth curve that passes through all control points.
        /// </summary>
        /// <param name="controlPoints">Control points to smooth through</param>
        /// <param name="pointsPerSegment">Number of interpolated points per segment (default 10)</param>
        /// <returns>Smoothed curve points</returns>
        public static List<Vec3> SmoothWithCatmullRom(IReadOnlyList<Vec3> controlPoints, int pointsPerSegment = 10)
        {
            if (controlPoints == null || controlPoints.Count < 2)
                return controlPoints != null ? new List<Vec3>(controlPoints) : new List<Vec3>();

            // Need at least 2 points for a curve
            if (controlPoints.Count == 2)
                return new List<Vec3>(controlPoints);

            var result = new List<Vec3>();

            // For Catmull-Rom, we need 4 points per segment (p0, p1, p2, p3)
            // The curve is drawn between p1 and p2
            // For endpoints, we extrapolate virtual points

            for (int i = 0; i < controlPoints.Count - 1; i++)
            {
                // Get the 4 control points for this segment
                Vec3 p0 = i > 0 ? controlPoints[i - 1] : ExtrapolatePoint(controlPoints[1], controlPoints[0]);
                Vec3 p1 = controlPoints[i];
                Vec3 p2 = controlPoints[i + 1];
                Vec3 p3 = i + 2 < controlPoints.Count ? controlPoints[i + 2] : ExtrapolatePoint(controlPoints[controlPoints.Count - 2], controlPoints[controlPoints.Count - 1]);

                // Add interpolated points for this segment
                for (int j = 0; j < pointsPerSegment; j++)
                {
                    double t = (double)j / pointsPerSegment;
                    var point = CatmullRomPoint(p0, p1, p2, p3, t);
                    result.Add(point);
                }
            }

            // Add the final point
            result.Add(controlPoints[controlPoints.Count - 1]);

            return result;
        }

        /// <summary>
        /// Extrapolates a virtual point beyond the endpoint for Catmull-Rom.
        /// </summary>
        private static Vec3 ExtrapolatePoint(Vec3 from, Vec3 to)
        {
            double dx = to.Easting - from.Easting;
            double dy = to.Northing - from.Northing;
            return new Vec3(to.Easting + dx, to.Northing + dy, to.Heading);
        }

        /// <summary>
        /// Calculates a point on a Catmull-Rom spline segment.
        /// </summary>
        /// <param name="p0">Control point before segment start</param>
        /// <param name="p1">Segment start point</param>
        /// <param name="p2">Segment end point</param>
        /// <param name="p3">Control point after segment end</param>
        /// <param name="t">Parameter (0 to 1)</param>
        /// <returns>Interpolated point on the curve</returns>
        private static Vec3 CatmullRomPoint(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;

            // Catmull-Rom basis functions
            double b0 = -0.5 * t3 + t2 - 0.5 * t;
            double b1 = 1.5 * t3 - 2.5 * t2 + 1.0;
            double b2 = -1.5 * t3 + 2.0 * t2 + 0.5 * t;
            double b3 = 0.5 * t3 - 0.5 * t2;

            double x = b0 * p0.Easting + b1 * p1.Easting + b2 * p2.Easting + b3 * p3.Easting;
            double y = b0 * p0.Northing + b1 * p1.Northing + b2 * p2.Northing + b3 * p3.Northing;

            return new Vec3(x, y, 0);
        }

        /// <summary>
        /// Calculates heading angles for each point based on direction to next point.
        /// Last point uses the heading from the second-to-last segment.
        /// </summary>
        /// <param name="points">Input points (modified in place)</param>
        /// <returns>Points with calculated headings</returns>
        public static List<Vec3> CalculateHeadings(List<Vec3> points)
        {
            if (points == null || points.Count < 2) return points;

            for (int i = 0; i < points.Count - 1; i++)
            {
                double dx = points[i + 1].Easting - points[i].Easting;
                double dy = points[i + 1].Northing - points[i].Northing;
                double heading = Math.Atan2(dx, dy);
                if (heading < 0) heading += GeometryMath.twoPI;

                points[i] = new Vec3(points[i].Easting, points[i].Northing, heading);
            }

            // Copy last heading from the second-to-last point
            var last = points[points.Count - 1];
            double lastHeading = points[points.Count - 2].Heading;
            points[points.Count - 1] = new Vec3(last.Easting, last.Northing, lastHeading);

            return points;
        }

        /// <summary>
        /// Computes the circular mean of heading angles.
        /// Uses vector averaging to handle the circular nature of angles correctly.
        /// </summary>
        /// <param name="points">Points with heading values</param>
        /// <returns>Average heading in radians (0 to 2π)</returns>
        public static double ComputeAverageHeading(IReadOnlyList<Vec3> points)
        {
            if (points == null || points.Count == 0) return 0;

            double cx = 0, sy = 0;
            for (int i = 0; i < points.Count; i++)
            {
                cx += Math.Cos(points[i].Heading);
                sy += Math.Sin(points[i].Heading);
            }
            cx /= points.Count;
            sy /= points.Count;

            double avg = Math.Atan2(sy, cx);
            if (avg < 0) avg += GeometryMath.twoPI;
            return avg;
        }

        /// <summary>
        /// Creates a clean offset curve by offsetting points and removing self-intersections.
        /// Use this instead of manually offsetting and calling CalculateHeadings.
        /// </summary>
        /// <param name="originalPoints">Original curve points with headings</param>
        /// <param name="offsetDistance">Perpendicular offset distance (positive = right of heading)</param>
        /// <returns>Clean offset curve without self-intersections</returns>
        public static List<Vec3> CreateOffsetCurve(IReadOnlyList<Vec3> originalPoints, double offsetDistance)
        {
            var (result, _) = CreateOffsetCurveWithInfo(originalPoints, offsetDistance);
            return result;
        }

        /// <summary>
        /// Extends a curve straight along its end tangents at both ends so it behaves like
        /// an (effectively infinite) AB line near the field edges. Without this, an offset
        /// curve stops "parallel to the end of the base track" — at the field boundary — so
        /// U-turn geometry and the displayed guidance line never reach into the turn the way
        /// a straight track does. Mirrors AgOpenGPS CABCurve.GetExtendedReferenceCurve.
        /// </summary>
        /// <param name="points">Curve points with headings (heading = atan2(dEasting, dNorthing)).</param>
        /// <param name="lengthMeters">How far to extend past each end.</param>
        /// <param name="spacing">Spacing between added extension points.</param>
        public static List<Vec3> ExtendCurveEnds(IReadOnlyList<Vec3> points,
            double lengthMeters = 200.0, double spacing = 1.0)
        {
            if (points == null || points.Count < 2 || lengthMeters <= 0 || spacing <= 0)
                return points != null ? new List<Vec3>(points) : new List<Vec3>();

            // Closed loops (boundary curves / water pivots) must not be extended.
            var first = points[0];
            var last = points[points.Count - 1];
            double closeSq = (first.Easting - last.Easting) * (first.Easting - last.Easting)
                + (first.Northing - last.Northing) * (first.Northing - last.Northing);
            if (closeSq < 1.0)
                return new List<Vec3>(points);

            int steps = (int)(lengthMeters / spacing);
            var extended = new List<Vec3>(points.Count + steps * 2);

            // Extend along a SMOOTHED end direction measured from point POSITIONS over a span,
            // not the single endpoint's stored heading. Recorded-curve endpoints frequently
            // carry a noisy/abrupt last-point heading; extending along it sends the straight
            // lead-in/out (and the U-turn entry/exit legs built on it) off at a wrong angle,
            // ~20° from the curve's actual direction — the operator's "tractor points 20° off
            // the entry leg and hunts" symptom.
            int span = Math.Min(5, points.Count - 1);

            // Lead-out direction: from points[N-1-span] toward points[N-1].
            var outFrom = points[points.Count - 1 - span];
            double outHead = Math.Atan2(last.Easting - outFrom.Easting, last.Northing - outFrom.Northing);
            // Lead-in direction (pointing INTO the curve): from points[span] back toward points[0].
            var inTo = points[span];
            double inHead = Math.Atan2(inTo.Easting - first.Easting, inTo.Northing - first.Northing);

            // Lead-in: walk backward from the first point (opposite the into-curve direction).
            double sinH = Math.Sin(inHead), cosH = Math.Cos(inHead);
            for (int i = steps; i >= 1; i--)
            {
                extended.Add(new Vec3(
                    first.Easting - sinH * (i * spacing),
                    first.Northing - cosH * (i * spacing),
                    inHead));
            }

            extended.AddRange(points);

            // Lead-out: walk forward from the last point along the smoothed end direction.
            sinH = Math.Sin(outHead); cosH = Math.Cos(outHead);
            for (int i = 1; i <= steps; i++)
            {
                extended.Add(new Vec3(
                    last.Easting + sinH * (i * spacing),
                    last.Northing + cosH * (i * spacing),
                    outHead));
            }

            return extended;
        }

        /// <summary>
        /// Creates a clean offset curve and returns information about whether points were removed.
        /// Uses AgOpenGPS collision detection: if an offset point is within offset² distance
        /// of ANY original curve point, it indicates self-intersection and the point is skipped.
        /// </summary>
        /// <param name="originalPoints">Original curve points with headings</param>
        /// <param name="offsetDistance">Perpendicular offset distance (positive = right of heading)</param>
        /// <returns>Tuple of (cleaned curve, percentage of points removed due to self-intersection)</returns>
        /// <param name="closed">The curve is a closed loop (its last point repeats the first): the
        /// offset curve is closed too, so the pass does not stop short at the seam.</param>
        public static (List<Vec3> Points, double PercentRemoved) CreateOffsetCurveWithInfo(
            IReadOnlyList<Vec3> originalPoints, double offsetDistance, bool closed = false)
        {
            WorkCounters.OffsetCurvePoints += originalPoints?.Count ?? 0;
            if (originalPoints == null || originalPoints.Count < 2)
                return (originalPoints != null ? new List<Vec3>(originalPoints) : new List<Vec3>(), 0);

            if (Math.Abs(offsetDistance) < 0.01)
                return (new List<Vec3>(originalPoints), 0);

            // AgOpenGPS collision detection threshold: offset² - small margin
            // If offset point is closer than this to ANY original point, it's a self-intersection
            double distSqAway = (offsetDistance * offsetDistance) - 0.01;
            int refCount = originalPoints.Count;
            int skippedCount = 0;

            var offsetPoints = new List<Vec3>(originalPoints.Count);
            // A long curve (a boundary with a point every metre) is bucketed once, so the test
            // looks only at the points near each offset point instead of all of them.
            var near = refCount > NearGridMinPoints ? new PointGrid(originalPoints, Math.Abs(offsetDistance)) : null;
            int lastClear = -1; // the last reference point whose offset point cleared the collision test

            for (int i = 0; i < refCount; i++)
            {
                var pt = originalPoints[i];

                // Calculate offset point position
                double perpAngle = pt.Heading + Math.PI / 2;
                double offsetE = pt.Easting + Math.Sin(perpAngle) * offsetDistance;
                double offsetN = pt.Northing + Math.Cos(perpAngle) * offsetDistance;

                // AgOpenGPS collision detection: check if offset point is too close
                // to ANY original curve point (indicates self-intersection)
                bool addPoint = near != null
                    ? !near.AnyCloser(originalPoints, offsetE, offsetN, distSqAway)
                    : !AnyCloser(originalPoints, offsetE, offsetN, distSqAway);
                if (!addPoint) skippedCount++;

                if (addPoint)
                {
                    // Round the outside of a bend: the offset jumps from the last point's normal to
                    // this one's, which left a chord with a kink at each end that the tractor overshot.
                    if (lastClear == i - 1 && i > 0)
                        AddArc(offsetPoints, pt, originalPoints[i - 1].Heading, pt.Heading, offsetDistance);
                    lastClear = i;

                    // Also check minimum spacing from previous offset point (AgOpenGPS uses ~0.48 * tool width)
                    // We use a simpler 1m minimum spacing check
                    if (offsetPoints.Count > 0)
                    {
                        var lastPt = offsetPoints[offsetPoints.Count - 1];
                        double dx = offsetE - lastPt.Easting;
                        double dy = offsetN - lastPt.Northing;
                        double distSq = dx * dx + dy * dy;

                        if (distSq < 1.0) // Less than 1m from previous point
                        {
                            continue; // Skip to maintain spacing
                        }
                    }

                    offsetPoints.Add(new Vec3(offsetE, offsetN, pt.Heading));
                }
            }

            // Need at least 2 points for a valid track
            if (offsetPoints.Count < 2)
            {
                // Fallback: return first and last original points offset
                var first = originalPoints[0];
                var last = originalPoints[refCount - 1];

                double perpAngle1 = first.Heading + Math.PI / 2;
                double perpAngle2 = last.Heading + Math.PI / 2;

                return (new List<Vec3>
                {
                    new Vec3(
                        first.Easting + Math.Sin(perpAngle1) * offsetDistance,
                        first.Northing + Math.Cos(perpAngle1) * offsetDistance,
                        first.Heading),
                    new Vec3(
                        last.Easting + Math.Sin(perpAngle2) * offsetDistance,
                        last.Northing + Math.Cos(perpAngle2) * offsetDistance,
                        last.Heading)
                }, skippedCount * 100.0 / refCount);
            }

            // Collision removal at a tight inward bend (e.g. a boundary curve wrapping a field
            // end) deletes the colliding inner-corner points, leaving a straight chord across
            // the gap that renders as a SHARP corner — unlike the reference pass, which is
            // rounded. Round that chord back out with one Chaikin pass so an offset pass and its
            // cyan next-track match the reference's smoothness. Gated on removal so gentle curves
            // (nothing skipped) are untouched; endpoints are preserved so the track→turn handoff
            // and the boundary extension stay put. Both the displayed guidance line and the U-turn
            // generator build from this same routine, so they stay byte-for-byte matched.
            if (skippedCount > 0 && offsetPoints.Count > 2)
                offsetPoints = ChaikinsSmooth(offsetPoints, 1);

            // A loop's first and last points are offset along different sides of the seam corner:
            // join them, or the pass (and its guidelines) stop short there (#450). Round the join
            // on the outside of the seam's bend, as at any other.
            if (closed && offsetPoints.Count > 2)
            {
                if (refCount > 2)
                    AddArc(offsetPoints, originalPoints[0], originalPoints[refCount - 2].Heading, originalPoints[0].Heading, offsetDistance);
                var first = offsetPoints[0];
                var last = offsetPoints[^1];
                if (Math.Abs(first.Easting - last.Easting) > 1e-6 || Math.Abs(first.Northing - last.Northing) > 1e-6)
                    offsetPoints.Add(first);
            }

            // Recalculate headings based on actual offset point positions
            CalculateHeadings(offsetPoints);

            double percentRemoved = refCount > 0 ? skippedCount * 100.0 / refCount : 0;
            return (offsetPoints, percentRemoved);
        }

        /// <summary>
        /// A boundary ring as a closed curve with a point at least every <see cref="FenceSpacing"/>
        /// metres along each side, the last point repeating the first. The collision test above
        /// only sees the curve's points, so a corner between two long bare sides went unseen and
        /// inward passes kept sharp, skewed corners (#450). AgOpenGPS's fence line is filled in the
        /// same way (CFenceLine.FixFenceLine) before it becomes a boundary curve.
        /// </summary>
        public static List<Vec3> DensifyLoop(IReadOnlyList<Vec3> ring)
        {
            if (ring == null || ring.Count < 2) return new List<Vec3>();
            var closed = new List<Vec3>(ring) { ring[0] };
            return Densify(closed, FenceSpacing(ring));
        }

        /// <summary>
        /// A polyline with a point at least every <paramref name="spacing"/> metres along each
        /// segment (the given points kept), with headings. A curve's passes are offset point by
        /// point, so a curve with long bare segments offsets badly at its corners (#450).
        /// </summary>
        public static List<Vec3> Densify(IReadOnlyList<Vec3> points, double spacing)
        {
            var result = new List<Vec3>();
            if (points == null || points.Count == 0) return result;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                double len = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
                int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));
                for (int k = 0; k < steps; k++)
                {
                    double t = (double)k / steps;
                    result.Add(new Vec3(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t, 0));
                }
            }
            result.Add(new Vec3(points[^1].Easting, points[^1].Northing, 0));
            return result.Count < 2 ? result : CalculateHeadings(result);
        }

        /// <summary>The point spacing of a fence line, by the field's area, as AgOpenGPS FixFenceLine: 1.1 m under 20 ha, 2.2 m under 40 ha, else 3.3 m.</summary>
        public static double FenceSpacing(IReadOnlyList<Vec3> ring)
        {
            double twiceArea = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                twiceArea += a.Easting * b.Northing - b.Easting * a.Northing;
            }
            return FenceSpacing(Math.Abs(twiceArea) / 2);
        }

        /// <summary>The point spacing of a fence line around <paramref name="areaSquareMetres"/>.</summary>
        public static double FenceSpacing(double areaSquareMetres) =>
            areaSquareMetres < 200_000 ? 1.1 : areaSquareMetres < 400_000 ? 2.2 : 3.3;

        /// <summary>
        /// The round join of a parallel offset on the outside of a bend at <paramref name="corner"/>:
        /// points <paramref name="offset"/> from it, at least a metre apart, turning from the normal of
        /// <paramref name="fromHeading"/> to that of <paramref name="toHeading"/> (the end points are the
        /// offset points either side, already there). Nothing on the inside of a bend, where the
        /// collision test removes the points instead, or for a turn that spans under two metres.
        /// </summary>
        private static void AddArc(List<Vec3> pts, Vec3 corner, double fromHeading, double toHeading, double offset)
        {
            double turn = Math.IEEERemainder(toHeading - fromHeading, 2 * Math.PI); // + = a right turn
            if (Math.Sign(turn) != -Math.Sign(offset)) return; // a right turn's outside is on the left
            int steps = (int)Math.Ceiling(Math.Abs(turn * offset) / 1.0);
            for (int k = 1; k < steps; k++)
            {
                double a = fromHeading + turn * k / steps + Math.PI / 2;
                pts.Add(new Vec3(corner.Easting + Math.Sin(a) * offset, corner.Northing + Math.Cos(a) * offset, 0));
            }
        }

        /// <summary>
        /// A clockwise circle from north, closed (the last point repeats the first), with a point at
        /// least every metre (36 to 4000 points), with headings: a water pivot's reference and passes.
        /// </summary>
        public static List<Vec3> Circle(Vec3 center, double radius)
        {
            radius = Math.Max(Math.Abs(radius), 0.1);
            int n = Math.Clamp((int)Math.Ceiling(2 * Math.PI * radius), 36, 4000);
            var pts = new List<Vec3>(n + 1);
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts.Add(new Vec3(center.Easting + radius * Math.Sin(a), center.Northing + radius * Math.Cos(a), 0));
            }
            pts.Add(pts[0]);
            return CalculateHeadings(pts);
        }

        /// <summary>
        /// A water pivot's pass <paramref name="offsetDistance"/> from its reference circle (+ = right of
        /// travel, inward on the clockwise circle): the circle about the same centre, as AgOpenGPS builds
        /// it. Past the centre it is the circle on the other side, as AgOpenGPS's negative radius is.
        /// </summary>
        public static List<Vec3> PivotPass(Vec3 center, double referenceRadius, double offsetDistance) =>
            Circle(center, referenceRadius - offsetDistance);

        /// <summary>
        /// The circle through points driven round a pivot (an algebraic least-squares fit), with the
        /// points' RMS distance from it; null when they don't define one (fewer than 3, or in a line).
        /// </summary>
        public static (Vec3 Center, double Radius, double Rms)? FitCircle(IReadOnlyList<Vec3> points)
        {
            if (points == null || points.Count < 3) return null;
            // Relative to the mean for conditioning; solve for a, b, c in x² + y² + a·x + b·y + c = 0.
            double mx = points.Average(p => p.Easting), my = points.Average(p => p.Northing);
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
            int n = points.Count;
            foreach (var p in points)
            {
                double x = p.Easting - mx, y = p.Northing - my, z = x * x + y * y;
                sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
                sxz += x * z; syz += y * z; sz += z;
            }
            // Normal equations of [x y 1]·[a b c] = −z.
            double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
            double[] rhs = { -sxz, -syz, -sz };
            double det = Det3(m);
            if (Math.Abs(det) < 1e-9 * Math.Max(1, sxx * syy)) return null;
            double[] sol = new double[3];
            for (int k = 0; k < 3; k++)
            {
                var mk = (double[,])m.Clone();
                for (int r = 0; r < 3; r++) mk[r, k] = rhs[r];
                sol[k] = Det3(mk) / det;
            }
            double cx = -sol[0] / 2, cy = -sol[1] / 2;
            double r2 = cx * cx + cy * cy - sol[2];
            if (r2 <= 0) return null;
            double radius = Math.Sqrt(r2);
            var center = new Vec3(cx + mx, cy + my, 0);
            double ss = 0;
            foreach (var p in points)
            {
                double d = Math.Sqrt(Math.Pow(p.Easting - center.Easting, 2) + Math.Pow(p.Northing - center.Northing, 2)) - radius;
                ss += d * d;
            }
            return (center, radius, Math.Sqrt(ss / n));
        }

        private static double Det3(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        /// <summary>
        /// Each point averaged with the <paramref name="half"/> points either side (fewer at the ends,
        /// whose first and last points stay put), with headings: takes the wobble out of a driven line.
        /// </summary>
        public static List<Vec3> SmoothMovingAverage(IReadOnlyList<Vec3> points, int half)
        {
            int n = points.Count;
            var result = new List<Vec3>(n);
            for (int i = 0; i < n; i++)
            {
                int k = Math.Min(half, Math.Min(i, n - 1 - i)); // a symmetric window that fits
                double e = 0, no = 0;
                for (int j = i - k; j <= i + k; j++) { e += points[j].Easting; no += points[j].Northing; }
                result.Add(new Vec3(e / (2 * k + 1), no / (2 * k + 1), 0));
            }
            return n < 2 ? result : CalculateHeadings(result);
        }

        // Below this many points the plain scan is as quick as building a grid.
        private const int NearGridMinPoints = 64;

        internal static bool AnyCloser(IReadOnlyList<Vec3> points, double e, double n, double distSq)
        {
            for (int t = 0; t < points.Count; t++)
            {
                double dx = e - points[t].Easting, dy = n - points[t].Northing;
                if (dx * dx + dy * dy < distSq) return true;
            }
            return false;
        }

        /// <summary>
        /// The curve's points bucketed in square cells as wide as the offset: a point closer than the
        /// offset to a query point lies in its cell or one of the eight around it. The answer is the
        /// plain scan's, found in a few cells.
        /// </summary>
        internal sealed class PointGrid
        {
            private readonly double _cell;
            private readonly Dictionary<(long, long), List<int>> _cells = new();

            public PointGrid(IReadOnlyList<Vec3> points, double cell)
            {
                _cell = Math.Max(cell, 0.01);
                for (int i = 0; i < points.Count; i++)
                {
                    var key = Key(points[i].Easting, points[i].Northing);
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(4);
                    list.Add(i);
                }
            }

            private (long, long) Key(double e, double n) => ((long)Math.Floor(e / _cell), (long)Math.Floor(n / _cell));

            public bool AnyCloser(IReadOnlyList<Vec3> points, double e, double n, double distSq)
            {
                var (cx, cy) = Key(e, n);
                for (long x = cx - 1; x <= cx + 1; x++)
                    for (long y = cy - 1; y <= cy + 1; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list)) continue;
                        foreach (int t in list)
                        {
                            double dx = e - points[t].Easting, dy = n - points[t].Northing;
                            if (dx * dx + dy * dy < distSq) return true;
                        }
                    }
                return false;
            }
        }

        /// <summary>
        /// Calculates the minimum radius of curvature along a curve.
        /// This determines the maximum inward offset before self-intersection occurs.
        /// </summary>
        /// <param name="points">Curve points with headings</param>
        /// <returns>Minimum radius in meters, or double.MaxValue for straight lines</returns>
        public static double CalculateMinRadiusOfCurvature(IReadOnlyList<Vec3> points)
        {
            if (points == null || points.Count < 3)
                return double.MaxValue; // Straight line or too few points

            double minRadius = double.MaxValue;

            for (int i = 0; i < points.Count - 1; i++)
            {
                // Calculate distance to next point
                double dx = points[i + 1].Easting - points[i].Easting;
                double dy = points[i + 1].Northing - points[i].Northing;
                double segmentLength = Math.Sqrt(dx * dx + dy * dy);

                if (segmentLength < 0.01) continue; // Skip very short segments

                // Calculate heading change
                double headingChange = points[i + 1].Heading - points[i].Heading;

                // Normalize to [-PI, PI]
                while (headingChange > Math.PI) headingChange -= GeometryMath.twoPI;
                while (headingChange < -Math.PI) headingChange += GeometryMath.twoPI;

                double absHeadingChange = Math.Abs(headingChange);
                if (absHeadingChange < 0.001) continue; // Nearly straight segment

                // Radius = arc_length / angle (in radians)
                // For small angles, segment length ≈ arc length
                double radius = segmentLength / absHeadingChange;

                if (radius < minRadius)
                    minRadius = radius;
            }

            return minRadius;
        }

        /// <summary>
        /// Calculates how many inward passes can be made before hitting the curve's geometric limit.
        /// </summary>
        /// <param name="points">Curve points with headings</param>
        /// <param name="passWidth">Width of each pass (tool width minus overlap)</param>
        /// <returns>Maximum number of inward passes possible, or int.MaxValue for straight lines</returns>
        public static int CalculateMaxInwardPasses(IReadOnlyList<Vec3> points, double passWidth)
        {
            if (passWidth <= 0) return int.MaxValue;

            double minRadius = CalculateMinRadiusOfCurvature(points);
            if (minRadius >= double.MaxValue - 1) return int.MaxValue;

            // Leave some margin (80% of min radius) to avoid artifacts
            return (int)(minRadius * 0.8 / passWidth);
        }

        // ──────────────────────────────────────────────────────────────────
        // Track extension and reduction (ported from AgOpenGPS 6.8.2
        // CTrackMethods, issue #229)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extends a track by adding straight-line points before the start
        /// and after the end, projected along the existing endpoint headings.
        /// Useful for AB lines that need to extend past their recorded
        /// endpoints to reach field boundaries.
        ///
        /// The first point's heading drives the prepend direction (going
        /// backwards), the last point's heading drives the append direction
        /// (going forwards).
        /// </summary>
        /// <param name="points">Input track (must have at least one point with valid heading on the endpoints).</param>
        /// <param name="ptsToAdd">Number of points to add at each end. Default 10.</param>
        /// <param name="distBetweenPoints">Spacing in meters. Default 50.</param>
        /// <returns>Extended track: ptsToAdd-1 prepended points + original + ptsToAdd-1 appended points.</returns>
        public static List<Vec3> AddStartEndPoints(IReadOnlyList<Vec3> points, int ptsToAdd = 10, double distBetweenPoints = 50)
        {
            if (points == null || points.Count == 0)
                return new List<Vec3>();

            var result = new List<Vec3>(points.Count + 2 * Math.Max(0, ptsToAdd - 1));
            AppendPrependPoints(result, points, ptsToAdd, distBetweenPoints, prepend: true, append: true);
            return result;
        }

        /// <summary>
        /// Extends a track only at the start. See <see cref="AddStartEndPoints"/>.
        /// </summary>
        public static List<Vec3> AddStartPoints(IReadOnlyList<Vec3> points, int ptsToAdd = 10, double distBetweenPoints = 50)
        {
            if (points == null || points.Count == 0)
                return new List<Vec3>();

            var result = new List<Vec3>(points.Count + Math.Max(0, ptsToAdd));
            AppendPrependPoints(result, points, ptsToAdd, distBetweenPoints, prepend: true, append: false);
            return result;
        }

        /// <summary>
        /// Extends a track only at the end. See <see cref="AddStartEndPoints"/>.
        /// </summary>
        public static List<Vec3> AddEndPoints(IReadOnlyList<Vec3> points, int ptsToAdd = 10, double distBetweenPoints = 50)
        {
            if (points == null || points.Count == 0)
                return new List<Vec3>();

            var result = new List<Vec3>(points.Count + Math.Max(0, ptsToAdd));
            AppendPrependPoints(result, points, ptsToAdd, distBetweenPoints, prepend: false, append: true);
            return result;
        }

        // Shared helper. The 6.8.2 originals used different loop bounds for
        // the two-sided vs one-sided variants (i < ptsToAdd vs i <= ptsToAdd).
        // We preserve that distinction: AddStartEndPoints adds ptsToAdd-1
        // points at each end (matching 6.8.2's `for (int i = 1; i < ptsToAdd; i++)`),
        // while AddStartPoints / AddEndPoints add ptsToAdd points (matching
        // `for (int i = 1; i <= ptsToAdd; i++)`).
        private static void AppendPrependPoints(
            List<Vec3> output,
            IReadOnlyList<Vec3> points,
            int ptsToAdd,
            double distBetweenPoints,
            bool prepend,
            bool append)
        {
            // For two-sided extension, the original 6.8.2 code uses i < ptsToAdd
            // (so adds ptsToAdd-1 points). Single-sided uses i <= ptsToAdd
            // (adds ptsToAdd). Pick the bound to match.
            int upperExclusive = (prepend && append) ? ptsToAdd : ptsToAdd + 1;

            if (prepend)
            {
                var start = points[0];
                double sin = Math.Sin(start.Heading);
                double cos = Math.Cos(start.Heading);
                for (int i = upperExclusive - 1; i >= 1; i--)
                {
                    output.Add(new Vec3(
                        start.Easting - sin * i * distBetweenPoints,
                        start.Northing - cos * i * distBetweenPoints,
                        start.Heading));
                }
            }

            output.AddRange(points);

            if (append)
            {
                var end = points[points.Count - 1];
                double sin = Math.Sin(end.Heading);
                double cos = Math.Cos(end.Heading);
                for (int i = 1; i < upperExclusive; i++)
                {
                    output.Add(new Vec3(
                        end.Easting + sin * i * distBetweenPoints,
                        end.Northing + cos * i * distBetweenPoints,
                        end.Heading));
                }
            }
        }

        /// <summary>
        /// Reduces a track's point count by skipping points that don't
        /// contribute meaningful curvature change. Useful for compressing
        /// recorded curve tracks (storage + render perf) without losing
        /// shape. The first and last two points are always preserved.
        ///
        /// A point is kept when EITHER the accumulated heading delta since
        /// the last kept point exceeds <paramref name="angleDelta"/> radians,
        /// OR the squared distance from the last kept point exceeds
        /// (spread² × 0.95).
        /// </summary>
        /// <param name="points">Input track. Each point must have a valid Heading.</param>
        /// <param name="angleDelta">Max heading change in radians before a point is forced. Default 0.005 (~0.29°).</param>
        /// <param name="spread">Max distance in meters before a point is forced. Default 2.</param>
        public static List<Vec3> ReducePointsByAngle(IReadOnlyList<Vec3> points, double angleDelta = 0.005, double spread = 2)
        {
            if (points == null || points.Count < 6)
                return points == null ? new List<Vec3>() : new List<Vec3>(points);

            int count = points.Count;
            int last = count - 1;

            var result = new List<Vec3>(count);
            double accumulatedDelta = 0;
            double accumulatedDistSq = 0;
            double maxDistSq = spread * spread * 0.95;
            Vec3 lastKeptPos = points[0];

            for (int i = 0; i < count; i++)
            {
                // Always keep the first two and last two points
                if (i < 2 || i > last - 2)
                {
                    result.Add(points[i]);
                    continue;
                }

                double headingDelta = points[i - 1].Heading - points[i].Heading;
                if (headingDelta > Math.PI) headingDelta -= GeometryMath.twoPI;
                else if (headingDelta < -Math.PI) headingDelta += GeometryMath.twoPI;

                accumulatedDelta += headingDelta;
                accumulatedDistSq += GeometryMath.DistanceSquared(lastKeptPos, points[i]);
                lastKeptPos = points[i];

                if (Math.Abs(accumulatedDelta) > angleDelta || accumulatedDistSq >= maxDistSq)
                {
                    result.Add(points[i]);
                    accumulatedDelta = 0;
                    accumulatedDistSq = 0;
                }
            }

            return result;
        }

        /// <summary>
        /// Smooths a track using Chaikin's corner-cutting algorithm. Each
        /// iteration replaces every interior segment endpoint pair with two
        /// new points at 25% and 75% along the segment, rounding sharp
        /// corners. Use small iteration counts (1–3) — each iteration
        /// roughly doubles the point count.
        ///
        /// Alternative to <see cref="SmoothWithCatmullRom"/>: Chaikin produces
        /// a tighter, more "rounded" curve and is cheaper but less faithful
        /// to the original control points.
        /// </summary>
        /// <param name="points">Input polyline.</param>
        /// <param name="iterations">Number of corner-cutting passes (1–3 typical).</param>
        /// <param name="preserveEndPoints">If true, the first and last points are kept exactly (open curve). If false, all points are rounded (closed loop).</param>
        public static List<Vec3> ChaikinsSmooth(IReadOnlyList<Vec3> points, int iterations, bool preserveEndPoints = true)
        {
            if (points == null || points.Count < 2 || iterations <= 0)
                return points == null ? new List<Vec3>() : new List<Vec3>(points);

            var current = new List<Vec3>(points);
            for (int iter = 0; iter < iterations; iter++)
            {
                var next = new List<Vec3>(current.Count * 2);

                if (preserveEndPoints && current.Count > 0)
                    next.Add(current[0]);

                for (int i = 0; i < current.Count - 1; i++)
                {
                    var p0 = current[i];
                    var p1 = current[i + 1];

                    next.Add(new Vec3(
                        0.75 * p0.Easting + 0.25 * p1.Easting,
                        0.75 * p0.Northing + 0.25 * p1.Northing,
                        0));
                    next.Add(new Vec3(
                        0.25 * p0.Easting + 0.75 * p1.Easting,
                        0.25 * p0.Northing + 0.75 * p1.Northing,
                        0));
                }

                if (preserveEndPoints && current.Count > 1)
                    next.Add(current[current.Count - 1]);

                current = next;
            }

            return current;
        }

        /// <summary>
        /// Lengthens or shortens one end of an open track (the AB flyout's A+/A−/B+/B−, #93).
        /// A positive <paramref name="meters"/> extends past the end along its end tangent; a
        /// negative value trims back along the path. A 2-point AB line just moves its endpoint;
        /// a curve gets the extension densified at <paramref name="spacing"/> so the nearest-point
        /// search never lands on one long lone segment (see ExtendCurvePastBoundary). Headings of
        /// the touched points are recomputed from the geometry.
        /// </summary>
        /// <param name="points">Input track (A = first point, B = last point).</param>
        /// <param name="atStart">True to move the A end, false for the B end.</param>
        /// <param name="meters">+ extends, − trims.</param>
        /// <param name="minRemaining">A trim that would leave less than this length is refused.</param>
        /// <param name="spacing">Point spacing for a curve extension (m).</param>
        /// <returns>The new point list, or null when the edit is refused (too short / degenerate).</returns>
        public static List<Vec3>? MoveTrackEnd(IReadOnlyList<Vec3> points, bool atStart, double meters,
            double minRemaining = 1.0, double spacing = 2.0)
        {
            if (points == null || points.Count < 2 || meters == 0)
                return null;

            // Work on the B end; the A end is the same edit on the reversed list.
            var pts = new List<Vec3>(points);
            if (atStart) pts.Reverse();

            if (meters > 0)
            {
                var end = pts[pts.Count - 1];
                var prev = pts[pts.Count - 2];
                double len = GeometryMath.Distance(prev, end);
                if (len < 1e-6) return null;
                double ux = (end.Easting - prev.Easting) / len;
                double uy = (end.Northing - prev.Northing) / len;

                if (pts.Count == 2)
                {
                    pts[1] = new Vec3(end.Easting + ux * meters, end.Northing + uy * meters, 0);
                }
                else
                {
                    int steps = Math.Max(1, (int)Math.Ceiling(meters / spacing));
                    for (int i = 1; i <= steps; i++)
                    {
                        double d = meters * i / steps;
                        pts.Add(new Vec3(end.Easting + ux * d, end.Northing + uy * d, 0));
                    }
                }
            }
            else
            {
                double trim = -meters;
                double total = 0;
                for (int i = 0; i < pts.Count - 1; i++)
                    total += GeometryMath.Distance(pts[i], pts[i + 1]);
                if (total - trim < minRemaining) return null;

                double remaining = trim;
                while (remaining > 0)
                {
                    var end = pts[pts.Count - 1];
                    var prev = pts[pts.Count - 2];
                    double seg = GeometryMath.Distance(prev, end);
                    if (seg > remaining)
                    {
                        double f = (seg - remaining) / seg;
                        pts[pts.Count - 1] = new Vec3(
                            prev.Easting + (end.Easting - prev.Easting) * f,
                            prev.Northing + (end.Northing - prev.Northing) * f, 0);
                        break;
                    }
                    remaining -= seg;
                    pts.RemoveAt(pts.Count - 1);
                }
            }

            if (atStart) pts.Reverse();

            // Recompute headings for the edited end (+ its neighbour); leave the body untouched.
            int touched = Math.Min(pts.Count, Math.Abs(pts.Count - points.Count) + 2);
            int from = atStart ? 0 : pts.Count - touched;
            int to = atStart ? touched : pts.Count;
            for (int i = from; i < to; i++)
            {
                int a = Math.Min(i, pts.Count - 2);
                double h = Math.Atan2(pts[a + 1].Easting - pts[a].Easting, pts[a + 1].Northing - pts[a].Northing);
                if (h < 0) h += GeometryMath.twoPI;
                pts[i] = new Vec3(pts[i].Easting, pts[i].Northing, h);
            }

            return pts;
        }
    }
}
