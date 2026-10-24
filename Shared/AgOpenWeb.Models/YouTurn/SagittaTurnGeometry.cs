// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.YouTurn
{
    /// <summary>
    /// Pure geometry for the sagitta U-turn, after Brian Tischler's (AOG dev-fork
    /// CYouTurn.GetOffsetSemicirclePoints / AddCoordinatesToPath).
    ///
    /// A pass closer than twice the turn radius can't be reached with one semicircle, so the
    /// turn is a keyhole: a counter-arc swings away from the target, a main arc loops over,
    /// and a second counter-arc brings it onto the pass. All three arcs have the turn radius
    /// and meet tangentially, so the path meets both passes with no curvature step.
    ///
    /// The two counter-arcs are mirror images (#418). Brian's turn has only the first one,
    /// leaving the main arc off to the start side; it lands on the target already heading
    /// back, but the loop sits further out and swings further sideways: with 6 m passes at an
    /// 8 m radius it is 22.8 m deep and reaches 10 m to the far side, against 19.6 m and 5 m.
    ///
    /// Heading convention matches the rest of the guidance code:
    /// <c>theta = atan2(easting, northing)</c> (clockwise from north).
    /// </summary>
    public static class SagittaTurnGeometry
    {
        private const double TwoPi = Math.PI * 2.0;

        /// <summary>
        /// The keyhole turn from <paramref name="start"/> onto the pass <paramref name="lateral"/>
        /// metres to the side: it ends there, level with the start, heading back.
        /// </summary>
        /// <param name="start">Turn start, on the current pass.</param>
        /// <param name="theta">Start heading in radians.</param>
        /// <param name="isTurningRight">True to turn right, false to turn left.</param>
        /// <param name="turningRadius">Turn radius in metres (must be &gt; 0).</param>
        /// <param name="lateral">Distance to the target pass, in [0, 2R]. At 2R the
        /// counter-arcs vanish and the turn is a plain semicircle.</param>
        public static List<Vec3> BuildKeyhole(Vec3 start, double theta, bool isTurningRight,
            double turningRadius, double lateral)
        {
            var points = new List<Vec3> { start };
            if (turningRadius <= 0) return points;

            // Counter-arc centres sit R beyond each pass, level with the start; the main arc's
            // centre midway between the passes, 2R from both (the arcs touch). Each counter-arc
            // turns through the angle of the line between its centre and the main one.
            double l = Math.Clamp(lateral, 0, 2 * turningRadius);
            double across = l / 2 + turningRadius;
            double ahead = Math.Sqrt(Math.Max(0, 4 * turningRadius * turningRadius - across * across));
            double counter = Math.Atan2(ahead, across);

            var pos = start;
            if (counter > 0) AddArc(ref pos, ref theta, points, counter * turningRadius, !isTurningRight, turningRadius);
            AddArc(ref pos, ref theta, points, (Math.PI + 2 * counter) * turningRadius, isTurningRight, turningRadius);
            if (counter > 0) AddArc(ref pos, ref theta, points, counter * turningRadius, !isTurningRight, turningRadius);
            return points;
        }

        /// <summary>How far past the start a keyhole onto a pass <paramref name="lateral"/>
        /// metres away reaches: the main arc's centre plus its radius.</summary>
        public static double KeyholeDepth(double turningRadius, double lateral)
        {
            double across = Math.Clamp(lateral, 0, 2 * turningRadius) / 2 + turningRadius;
            return turningRadius + Math.Sqrt(Math.Max(0, 4 * turningRadius * turningRadius - across * across));
        }

        /// <summary>
        /// Appends points along a constant-radius arc of the given <paramref name="length"/>,
        /// advancing <paramref name="pos"/> and <paramref name="theta"/> by reference.
        /// </summary>
        private static void AddArc(ref Vec3 pos, ref double theta, List<Vec3> path,
            double length, bool isTurningRight, double turningRadius)
        {
            int segments = (int)Math.Ceiling(length / (turningRadius * 0.1));
            if (segments < 1) segments = 1;

            double dist = length / segments;
            double turnParameter = (dist / turningRadius) * (isTurningRight ? 1.0 : -1.0);
            double radius = isTurningRight ? turningRadius : -turningRadius;

            double sinH = Math.Sin(theta);
            double cosH = Math.Cos(theta);

            for (int i = 0; i < segments; i++)
            {
                // Step to the arc centre, rotate the heading, step back out to the rim.
                pos.Easting += cosH * radius;
                pos.Northing -= sinH * radius;

                theta += turnParameter;
                theta %= TwoPi;

                sinH = Math.Sin(theta);
                cosH = Math.Cos(theta);

                pos.Easting -= cosH * radius;
                pos.Northing += sinH * radius;
                pos.Heading = theta;

                path.Add(pos);
            }
        }
    }
}
