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

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.YouTurn
{
    /// <summary>
    /// Pure geometry for Brian Tischler's "sagitta" U-turn (AOG dev-fork
    /// CYouTurn.GetOffsetSemicirclePoints / AddCoordinatesToPath).
    ///
    /// A sagitta turn is a single offset arc with an optional short counter-arc
    /// lead-in. The lead-in (curving the opposite way by the sagitta angle) makes
    /// the path meet the crop row tangentially, eliminating the straight-leg→arc
    /// curvature step that makes Omega turns feel sharp. The sagitta (extra arc
    /// depth) lets the arc reach rows closer than twice the turn radius.
    ///
    /// Heading convention matches the rest of the guidance code:
    /// <c>theta = atan2(easting, northing)</c> (clockwise from north).
    /// </summary>
    public static class SagittaTurnGeometry
    {
        private const double TwoPi = Math.PI * 2.0;

        /// <summary>
        /// Builds the sagitta offset arc starting at <paramref name="start"/>.
        /// </summary>
        /// <param name="start">Arc start point.</param>
        /// <param name="theta">Start heading in radians.</param>
        /// <param name="isTurningRight">True to curve right, false to curve left.</param>
        /// <param name="turningRadius">Turn radius in metres (must be &gt; 0).</param>
        /// <param name="offsetDistance">
        /// Pullback from a full 2R semicircle, in metres. The arc lands
        /// <c>2R - offsetDistance</c> sideways, so to land exactly on a row at
        /// lateral distance <c>L</c> pass <c>offsetDistance = 2R - L</c>. 0 gives a
        /// plain semicircle landing at 2R. Valid range [0, 2R].
        /// </param>
        /// <param name="angle">Main sweep angle in radians (Math.PI for a semicircle).</param>
        public static List<Vec3> BuildOffsetArc(Vec3 start, double theta, bool isTurningRight,
            double turningRadius, double offsetDistance, double angle)
        {
            var points = new List<Vec3> { start };
            if (turningRadius <= 0) return points;

            // Sagitta relation s = R(1 - cos θ)  ->  θ = acos(1 - s/R).
            // The 0.5 factor splits the sagitta across the lead-in counter-arc.
            double ratio = 1.0 - (offsetDistance * 0.5) / turningRadius;
            if (ratio > 1.0) ratio = 1.0;
            if (ratio < -1.0) ratio = -1.0;
            double firstArcAngle = Math.Acos(ratio);

            var pos = start;

            if (offsetDistance > 0)
            {
                // Counter-arc lead-in: curve the opposite way to align the entry tangent.
                AddArc(ref pos, ref theta, points, firstArcAngle * turningRadius, !isTurningRight, turningRadius);
            }

            // Main arc completes the turn; the extra firstArcAngle compensates for the lead-in.
            double remainingAngle = angle + firstArcAngle;
            AddArc(ref pos, ref theta, points, remainingAngle * turningRadius, isTurningRight, turningRadius);

            return points;
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
