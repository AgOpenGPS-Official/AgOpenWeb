// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Tool;

namespace AgOpenWeb.Models.Guidance
{
    /// <summary>
    /// Pure check of whether an implement's swept path stays clear of a hard
    /// boundary by a required margin. Used by the U-turn clearance loop to decide
    /// whether a candidate turn keeps the implement off a fence/obstacle, and by
    /// how much it intrudes (so the turn can be shifted away the right amount).
    /// </summary>
    public static class TurnClearance
    {
        /// <summary>Which side of the polygon the implement must stay on.</summary>
        public enum KeepSide
        {
            /// <summary>Outer field boundary — implement must stay inside it.</summary>
            Inside,
            /// <summary>Inner exclusion/obstacle — implement must stay outside it.</summary>
            Outside
        }

        /// <param name="IsClear">True when every tested point holds the margin.</param>
        /// <param name="MaxIntrusion">
        /// Worst violation in metres: &gt; 0 means the implement is that far inside
        /// the margin (shift the turn away by at least this much); ≤ 0 means clear.
        /// </param>
        public readonly record struct ClearanceResult(bool IsClear, double MaxIntrusion);

        /// <summary>
        /// Evaluates the implement body footprint (all four corner rails) of a
        /// swept path against a hard polygon.
        /// </summary>
        public static ClearanceResult Evaluate(ImplementSweptPathResult swept,
            IReadOnlyList<Vec2> polygon, KeepSide keep, double margin)
            => Evaluate(EnumerateCorners(swept), polygon, keep, margin);

        /// <summary>
        /// Evaluates an arbitrary set of implement points against a hard polygon.
        /// </summary>
        public static ClearanceResult Evaluate(IEnumerable<Vec3> implementPoints,
            IReadOnlyList<Vec2> polygon, KeepSide keep, double margin)
        {
            if (polygon == null || polygon.Count < 3)
                return new ClearanceResult(true, double.NegativeInfinity);

            double maxIntrusion = double.NegativeInfinity;
            bool any = false;

            foreach (var p in implementPoints)
            {
                any = true;
                var pt = new Vec2(p.Easting, p.Northing);
                bool inside = GeometryMath.IsPointInPolygon(polygon, pt);
                double edge = MinEdgeDistance(polygon, pt);

                // Intrusion = how far past the required margin on the allowed side.
                double intrusion = keep == KeepSide.Inside
                    ? (inside ? margin - edge : margin + edge)
                    : (inside ? margin + edge : margin - edge);

                if (intrusion > maxIntrusion) maxIntrusion = intrusion;
            }

            if (!any) return new ClearanceResult(true, double.NegativeInfinity);
            return new ClearanceResult(maxIntrusion <= 0.0, maxIntrusion);
        }

        private static IEnumerable<Vec3> EnumerateCorners(ImplementSweptPathResult s)
        {
            foreach (var p in s.FrontLeft) yield return p;
            foreach (var p in s.FrontRight) yield return p;
            foreach (var p in s.RearLeft) yield return p;
            foreach (var p in s.RearRight) yield return p;
        }

        private static double MinEdgeDistance(IReadOnlyList<Vec2> poly, Vec2 pt)
        {
            double min = double.MaxValue;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = GeometryMath.PointToSegmentDistance(pt, poly[j], poly[i]);
                if (d < min) min = d;
            }
            return min;
        }
    }
}
