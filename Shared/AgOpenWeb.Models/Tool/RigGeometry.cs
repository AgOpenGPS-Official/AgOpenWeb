// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.Tool
{
    /// <summary>
    /// Where the tool hangs on the tractor: the one place that turns the vehicle's and the tool's
    /// settings into the tool's mount and the point it is attached at. The tool position (sections,
    /// coverage), the map's implement, the U-turn's swept path and the settings that invalidate a
    /// remembered U-turn all read it, so they cannot disagree (they each had their own copy, and
    /// the copies differed on which flag won when a profile set two).
    /// Distances follow ISO 11783-10's device axes: x along the tractor from the centre of the
    /// rear axle, positive ahead (NonTracked/HITCH_MODEL_PLAN.md).
    /// </summary>
    public static class RigGeometry
    {
        /// <summary>
        /// The tool's mount from its type flags. The flags are set together by
        /// <see cref="ToolConfig.SetToolType"/>, but a profile sets them one by one, so more than
        /// one can be on. As the tool position always read them: a fixed flag makes the tool fixed,
        /// and it is ahead only with the front flag alone; otherwise TBT before trailing. None set
        /// is a rear-fixed tool.
        /// </summary>
        public static ToolMount Mount(ToolConfig tool)
        {
            bool trailed = tool.IsToolTrailing || tool.IsToolTBT;
            if (tool.IsToolFrontFixed && !tool.IsToolRearFixed && !trailed) return ToolMount.FrontFixed;
            if (tool.IsToolFrontFixed || tool.IsToolRearFixed) return ToolMount.RearFixed;
            if (tool.IsToolTBT) return ToolMount.TBT;
            if (tool.IsToolTrailing) return ToolMount.Trailing;
            return ToolMount.RearFixed;
        }

        /// <summary>Front or rear fixed: the tool follows the tractor's heading.</summary>
        public static bool IsRigid(ToolMount mount) => mount is ToolMount.FrontFixed or ToolMount.RearFixed;

        /// <summary>
        /// The point the tool is attached at, along the tractor from the rear axle (+ ahead).
        /// A fixed tool: its working point, <c>Tool.HitchLength</c> (axle → working point),
        /// ahead for a front tool, behind for a rear one. A trailed or TBT tool: the tractor's
        /// hitch pin, <c>Vehicle.HitchLength</c>, behind. Both are kept positive; the sign is
        /// the mount's.
        /// </summary>
        public static double AttachX(ToolMount mount, double vehicleHitchLength, double toolHitchLength)
        {
            double d = Math.Abs(IsRigid(mount) ? toolHitchLength : vehicleHitchLength);
            return mount == ToolMount.FrontFixed ? d : -d;
        }

        /// <inheritdoc cref="AttachX(ToolMount, double, double)"/>
        public static double AttachX(VehicleConfig vehicle, ToolConfig tool) =>
            AttachX(Mount(tool), vehicle.HitchLength, tool.HitchLength);

        /// <inheritdoc cref="AttachX(ToolMount, double, double)"/>
        public static double AttachX(in ToolGeometry geom) =>
            AttachX(geom.Mount, geom.VehicleHitchLength, geom.ToolHitchLength);

        /// <summary>The point <paramref name="x"/> along the tractor (+ ahead) from <paramref name="pivot"/>.</summary>
        public static Vec3 AlongTractor(Vec3 pivot, double heading, double x) =>
            new(pivot.Easting + Math.Sin(heading) * x, pivot.Northing + Math.Cos(heading) * x, heading);

        /// <summary>
        /// How far a trailed tool's working centre is behind what pulls it (the hitch, or the TBT
        /// tank): the trailing hitch length plus AgOpenGPS's pivot distance
        /// (setTool_trailingToolToPivotLength, the Tool panel's Behind / Ahead): positive with the
        /// tool behind its wheels, negative with it ahead.
        /// </summary>
        public static double ToolBehindPivot(ToolConfig tool) => tool.TrailingHitchLength + tool.TrailingToolToPivotLength;

        /// <summary>
        /// A trailed or TBT tool's working centre, from the tractor's hitch and the headings of the
        /// TBT tank and of the tool.
        /// </summary>
        public static (double Easting, double Northing) TrailedToolCentre(
            double hitchEasting, double hitchNorthing, double tankHeading, double toolHeading, ToolConfig tool)
        {
            if (tool.IsToolTBT)
            {
                hitchEasting -= Math.Sin(tankHeading) * tool.TankTrailingHitchLength;
                hitchNorthing -= Math.Cos(tankHeading) * tool.TankTrailingHitchLength;
            }
            double behind = ToolBehindPivot(tool);
            return (hitchEasting - Math.Sin(toolHeading) * behind, hitchNorthing - Math.Cos(toolHeading) * behind);
        }

        /// <summary>The implement's geometry for <see cref="ImplementSweptPath"/>.</summary>
        public static ToolGeometry Geometry(VehicleConfig vehicle, ToolConfig tool, double width) => new(
            Mount: Mount(tool),
            Width: width,
            Offset: tool.Offset,
            VehicleHitchLength: vehicle.HitchLength,
            ToolHitchLength: tool.HitchLength,
            TrailingHitchLength: tool.TrailingHitchLength,
            TrailingToolToPivotLength: tool.TrailingToolToPivotLength,
            TankTrailingHitchLength: tool.TankTrailingHitchLength,
            Length: tool.Length);

        /// <summary>
        /// A stamp of every setting that moves the tool relative to the tractor: a value worked out
        /// from the tool's position (a U-turn tried and refused at this end) is stale once it changes.
        /// </summary>
        public static int Stamp(VehicleConfig vehicle, ToolConfig tool) => HashCode.Combine(
            Mount(tool), vehicle.HitchLength, tool.HitchLength, tool.TrailingHitchLength,
            tool.TrailingToolToPivotLength, tool.TankTrailingHitchLength, tool.Offset, tool.Length);
    }
}
