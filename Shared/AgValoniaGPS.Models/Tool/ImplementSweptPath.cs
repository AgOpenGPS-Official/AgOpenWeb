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

namespace AgValoniaGPS.Models.Tool
{
    /// <summary>How the implement is attached to the vehicle.</summary>
    public enum ToolMount
    {
        /// <summary>Rigidly attached ahead of the pivot (follows vehicle heading).</summary>
        FrontFixed,
        /// <summary>Rigidly attached behind the pivot (follows vehicle heading).</summary>
        RearFixed,
        /// <summary>Trails behind on a hitch; swings during turns (off-tracking).</summary>
        Trailing,
        /// <summary>Tow-between-tractor: two-stage trailing (tank then tool).</summary>
        TBT
    }

    /// <summary>
    /// Pure implement geometry needed to reproduce <c>ToolPositionService</c>'s
    /// kinematics without depending on <c>ConfigurationStore</c>. Distances in metres.
    /// </summary>
    /// <param name="Mount">Attachment type.</param>
    /// <param name="Width">Implement working width.</param>
    /// <param name="Offset">Lateral offset of the tool centre (right positive).</param>
    /// <param name="VehicleHitchLength">Axle → tractor hitch pin (used by Trailing/TBT).</param>
    /// <param name="ToolHitchLength">Axle → implement working centre (used by rigid tools).</param>
    /// <param name="TrailingHitchLength">Hitch → tool pivot, along the tool.</param>
    /// <param name="TrailingToolToPivotLength">Tool pivot → tool working centre.</param>
    /// <param name="TankTrailingHitchLength">Hitch → tank pivot (TBT only).</param>
    /// <param name="Length">
    /// Fore-aft length of the implement body, from its attachment to the
    /// rearmost point. The attachment is the rigid hitch (fixed tools) or the
    /// tool pivot (trailing/TBT, so the drawbar <see cref="TrailingHitchLength"/>
    /// is counted separately). 0 collapses the body to the working-centre line
    /// (legacy behaviour).
    /// </param>
    public readonly record struct ToolGeometry(
        ToolMount Mount,
        double Width,
        double Offset,
        double VehicleHitchLength,
        double ToolHitchLength,
        double TrailingHitchLength,
        double TrailingToolToPivotLength,
        double TankTrailingHitchLength,
        double Length = 0.0);

    /// <summary>
    /// The implement's swept path along a candidate vehicle-pivot trajectory.
    /// <see cref="ToolCenter"/> + <see cref="LeftEdge"/>/<see cref="RightEdge"/>
    /// are the working-centre line and its rails (for coverage). The four
    /// corner rails (<see cref="FrontLeft"/>, <see cref="FrontRight"/>,
    /// <see cref="RearLeft"/>, <see cref="RearRight"/>) trace the physical body
    /// footprint when <c>ToolGeometry.Length &gt; 0</c> — the rear-outer corner
    /// is the worst case for hard-boundary clearance. All lists align 1:1 with
    /// the input pivot path.
    /// </summary>
    public sealed class ImplementSweptPathResult
    {
        public List<Vec3> ToolCenter { get; } = new();
        public List<Vec3> LeftEdge { get; } = new();
        public List<Vec3> RightEdge { get; } = new();

        public List<Vec3> FrontLeft { get; } = new();
        public List<Vec3> FrontRight { get; } = new();
        public List<Vec3> RearLeft { get; } = new();
        public List<Vec3> RearRight { get; } = new();
    }

    /// <summary>
    /// Reproduces <c>ToolPositionService</c>'s tractor/implement kinematics as a
    /// pure function so a *candidate* turn path can be evaluated offline: given
    /// the vehicle pivot trajectory, returns where the implement edges sweep.
    ///
    /// Heading convention matches the rest of the guidance code:
    /// <c>heading = atan2(easting-delta, northing-delta)</c> (clockwise from north),
    /// positions advance by <c>sin(heading)</c> east / <c>cos(heading)</c> north.
    ///
    /// Trailing/TBT use Torriem's algorithm (tool heading = direction from the
    /// trailed pivot toward its tow point), seeded aligned behind the first pose
    /// — appropriate for a turn that begins after driving straight. The live
    /// service's startup-snap, GPS-jump and jackknife guards are intentionally
    /// omitted: a generated turn path is smooth and glitch-free.
    /// </summary>
    public static class ImplementSweptPath
    {
        private const double TwoPi = Math.PI * 2.0;
        private const double Eps = 1e-9;

        public static ImplementSweptPathResult Compute(IReadOnlyList<Vec3> pivotPath, ToolGeometry geom)
        {
            var result = new ImplementSweptPathResult();
            int n = pivotPath?.Count ?? 0;
            if (n == 0) return result;

            double halfWidth = geom.Width / 2.0;

            // Hitch distance + sign: rigid tools measure axle→implement centre,
            // trailing/TBT measure axle→tractor pin. Behind for everything except
            // a front-fixed tool.
            bool rigid = geom.Mount == ToolMount.FrontFixed || geom.Mount == ToolMount.RearFixed;
            double hitchDistance = Math.Abs(rigid ? geom.ToolHitchLength : geom.VehicleHitchLength);
            if (geom.Mount != ToolMount.FrontFixed) hitchDistance = -hitchDistance;

            Vec3 lastToolPivot = default;
            Vec3 lastTank = default;
            bool seeded = false;

            for (int i = 0; i < n; i++)
            {
                Vec3 pivot = pivotPath![i];
                double heading = HeadingAt(pivotPath, i);
                double sinH = Math.Sin(heading), cosH = Math.Cos(heading);

                Vec3 hitch = new Vec3(
                    pivot.Easting + sinH * hitchDistance,
                    pivot.Northing + cosH * hitchDistance,
                    heading);

                double toolHeading;
                Vec3 toolCenter;
                Vec3 attachment;   // implement's attachment: hitch (rigid) or tool pivot (trailed)

                switch (geom.Mount)
                {
                    case ToolMount.Trailing:
                    {
                        if (!seeded) { lastToolPivot = Behind(hitch, heading, geom.TrailingHitchLength); seeded = true; }
                        toolHeading = DirectionFrom(lastToolPivot, hitch, heading);
                        attachment = Behind(hitch, toolHeading, geom.TrailingHitchLength);
                        toolCenter = Behind(hitch, toolHeading, geom.TrailingHitchLength - geom.TrailingToolToPivotLength);
                        lastToolPivot = attachment;
                        break;
                    }
                    case ToolMount.TBT:
                    {
                        if (!seeded)
                        {
                            lastTank = Behind(hitch, heading, geom.TankTrailingHitchLength);
                            lastToolPivot = Behind(lastTank, heading, geom.TrailingHitchLength);
                            seeded = true;
                        }
                        double tankHeading = DirectionFrom(lastTank, hitch, heading);
                        Vec3 tank = Behind(hitch, tankHeading, geom.TankTrailingHitchLength);
                        toolHeading = DirectionFrom(lastToolPivot, tank, tankHeading);
                        attachment = Behind(tank, toolHeading, geom.TrailingHitchLength);
                        toolCenter = Behind(tank, toolHeading, geom.TrailingHitchLength - geom.TrailingToolToPivotLength);
                        lastTank = tank;
                        lastToolPivot = attachment;
                        break;
                    }
                    case ToolMount.FrontFixed:
                    case ToolMount.RearFixed:
                    default:
                        toolHeading = heading;
                        attachment = hitch;
                        toolCenter = hitch;
                        break;
                }

                // Lateral offset, perpendicular to the tool (right positive). Shifts
                // the whole implement (working line and body) by the same amount.
                double perpH = toolHeading + Math.PI / 2.0;
                double sp = Math.Sin(perpH), cp = Math.Cos(perpH);
                double offE = sp * geom.Offset, offN = cp * geom.Offset;

                toolCenter = new Vec3(toolCenter.Easting + offE, toolCenter.Northing + offN, toolHeading);

                result.ToolCenter.Add(toolCenter);
                result.LeftEdge.Add(new Vec3(toolCenter.Easting - sp * halfWidth, toolCenter.Northing - cp * halfWidth, toolHeading));
                result.RightEdge.Add(new Vec3(toolCenter.Easting + sp * halfWidth, toolCenter.Northing + cp * halfWidth, toolHeading));

                // Physical body footprint: a rectangle from the attachment to the
                // rearmost point (forward for a front-mounted tool), full width.
                var front = new Vec3(attachment.Easting + offE, attachment.Northing + offN, toolHeading);
                double bodyDist = (geom.Mount == ToolMount.FrontFixed) ? -geom.Length : geom.Length;
                var rear = Behind(front, toolHeading, bodyDist);

                result.FrontLeft.Add(new Vec3(front.Easting - sp * halfWidth, front.Northing - cp * halfWidth, toolHeading));
                result.FrontRight.Add(new Vec3(front.Easting + sp * halfWidth, front.Northing + cp * halfWidth, toolHeading));
                result.RearLeft.Add(new Vec3(rear.Easting - sp * halfWidth, rear.Northing - cp * halfWidth, toolHeading));
                result.RearRight.Add(new Vec3(rear.Easting + sp * halfWidth, rear.Northing + cp * halfWidth, toolHeading));
            }

            return result;
        }

        /// <summary>Heading from the path tangent (forward diff; backward at the last point).</summary>
        private static double HeadingAt(IReadOnlyList<Vec3> path, int i)
        {
            int a = i, b = i + 1;
            if (b >= path.Count) { a = i - 1; b = i; }
            if (a < 0) return path[i].Heading;

            double dx = path[b].Easting - path[a].Easting;
            double dy = path[b].Northing - path[a].Northing;
            if (Math.Abs(dx) < Eps && Math.Abs(dy) < Eps) return path[i].Heading;
            return Normalize(Math.Atan2(dx, dy));
        }

        /// <summary>Torriem heading: direction from a trailed point toward its tow point.</summary>
        private static double DirectionFrom(Vec3 from, Vec3 to, double fallback)
        {
            double dx = to.Easting - from.Easting;
            double dy = to.Northing - from.Northing;
            if (Math.Abs(dx) < Eps && Math.Abs(dy) < Eps) return fallback;
            return Normalize(Math.Atan2(dx, dy));
        }

        private static Vec3 Behind(Vec3 from, double heading, double dist) =>
            new Vec3(from.Easting - Math.Sin(heading) * dist, from.Northing - Math.Cos(heading) * dist, heading);

        private static double Normalize(double a) => a < 0 ? a + TwoPi : a;
    }
}
