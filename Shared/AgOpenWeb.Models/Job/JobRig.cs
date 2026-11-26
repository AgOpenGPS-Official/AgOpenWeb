// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;

namespace AgOpenWeb.Models.Job;

/// <summary>
/// The machine a job was worked with: the vehicle's hitch points and the chain of tools hung on
/// them, in order from the vehicle (#547). It is a copy, not a profile name: a profile can be
/// edited or deleted later, and the job's coverage only makes sense with the tools that painted
/// it. As ISO 11783-10 chains a tractor's connectors and each implement's connector and elements,
/// each tool is measured from the connector it hangs on: the vehicle's, or the rear connector of
/// the tool before it. The web client works with one tool, so AgOpenWeb writes a chain of one;
/// a file from another program may carry more.
/// </summary>
public sealed class JobRig
{
    public RigVehicle Vehicle { get; set; } = new();

    /// <summary>The tools in order from the vehicle; the first hangs on the vehicle.</summary>
    public List<RigTool> Tools { get; set; } = new();

    /// <summary>The vehicle and the one tool loaded now.</summary>
    public static JobRig From(ConfigurationStore store)
    {
        var v = store.Vehicle;
        var t = store.Tool;
        var mount = RigGeometry.Mount(t);
        int n = Math.Clamp(store.NumSections, 0, ToolConfig.MaxSections);
        return new JobRig
        {
            Vehicle = new RigVehicle
            {
                ProfileName = store.ActiveVehicleProfileName ?? string.Empty,
                Type = v.Type.ToString(),
                HitchType = v.HitchType,
                DrawbarHitch = v.HitchLength,
                RearThreePointHitch = v.ThreePointHitchLength,
                FrontThreePointHitch = v.FrontHitchLength,
            },
            Tools =
            {
                new RigTool
                {
                    ProfileName = store.ActiveToolProfileName ?? string.Empty,
                    Connector = mount switch
                    {
                        ToolMount.FrontFixed => RigConnector.FrontThreePoint,
                        ToolMount.RearFixed => RigConnector.RearThreePoint,
                        _ => RigConnector.Drawbar,
                    },
                    Mount = mount,
                    HitchType = t.HitchType,
                    WorkingPointFromHitch = RigGeometry.IsRigid(mount) ? t.WorkingPointFromHitch : 0,
                    TrailingHitchLength = RigGeometry.IsRigid(mount) ? 0 : t.TrailingHitchLength,
                    TankTrailingHitchLength = mount == ToolMount.TBT ? t.TankTrailingHitchLength : 0,
                    TrailingToolToPivotLength = RigGeometry.IsRigid(mount) ? 0 : t.TrailingToolToPivotLength,
                    Offset = t.Offset,
                    Length = t.Length,
                    Width = store.ActualToolWidth,
                    SectionWidths = Enumerable.Range(0, n).Select(i => t.GetSectionWidth(i) / 100.0).ToList(),
                },
            },
        };
    }

    /// <summary>The same machine, every value equal (to decide whether a job's copy is out of date).</summary>
    public bool SameAs(JobRig? other) =>
        other != null && Vehicle.SameAs(other.Vehicle)
        && Tools.Count == other.Tools.Count && Tools.Zip(other.Tools).All(p => p.First.SameAs(p.Second));
}

/// <summary>A vehicle's connectors, each its distance from the rear axle in metres (+ in its own direction).</summary>
public sealed class RigVehicle
{
    public string ProfileName { get; set; } = string.Empty;

    /// <summary><see cref="Models.VehicleType"/> by name: Tractor, Harvester, Articulated.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>ISO 11783 coupling type (DDI 157) on the vehicle's side; -1 not available, 0 unknown.</summary>
    public int HitchType { get; set; }

    /// <summary>Drawbar pin, behind the rear axle.</summary>
    public double DrawbarHitch { get; set; }

    /// <summary>Rear 3-point hitch (the centre of the lower link points), behind the rear axle.</summary>
    public double RearThreePointHitch { get; set; }

    /// <summary>Front 3-point hitch, ahead of the rear axle; 0 when the vehicle has none set.</summary>
    public double FrontThreePointHitch { get; set; }

    public bool SameAs(RigVehicle o) =>
        ProfileName == o.ProfileName && Type == o.Type && HitchType == o.HitchType
        && DrawbarHitch.Equals(o.DrawbarHitch) && RearThreePointHitch.Equals(o.RearThreePointHitch)
        && FrontThreePointHitch.Equals(o.FrontThreePointHitch);
}

/// <summary>Where a tool hangs: one of the vehicle's connectors, or the rear connector of the tool before it.</summary>
public enum RigConnector
{
    RearThreePoint,
    Drawbar,
    FrontThreePoint,
    PreviousTool,
}

/// <summary>
/// One tool of the chain, its distances in metres along the tool from the connector it hangs on,
/// as the tool profile keeps them.
/// </summary>
public sealed class RigTool
{
    public string ProfileName { get; set; } = string.Empty;

    public RigConnector Connector { get; set; }

    /// <summary>How it hangs: FrontFixed, RearFixed, Trailing or TBT.</summary>
    public ToolMount Mount { get; set; }

    /// <summary>ISO 11783 coupling type (DDI 157) on the tool's side; -1 not available, 0 unknown.</summary>
    public int HitchType { get; set; }

    /// <summary>A fixed tool: its working point from its connector (negative: on the vehicle's side of it).</summary>
    public double WorkingPointFromHitch { get; set; }

    /// <summary>A trailed tool: its connector to its wheels.</summary>
    public double TrailingHitchLength { get; set; }

    /// <summary>TBT: the connector to the tank's wheels (the tool then hangs on the tank).</summary>
    public double TankTrailingHitchLength { get; set; }

    /// <summary>A trailed tool: its wheels to its working point (+ behind).</summary>
    public double TrailingToolToPivotLength { get; set; }

    /// <summary>The working point to the side of the vehicle's centre line, + to the right.</summary>
    public double Offset { get; set; }

    /// <summary>The tool's length along the direction of travel.</summary>
    public double Length { get; set; }

    /// <summary>The working width: the sections together.</summary>
    public double Width { get; set; }

    /// <summary>Each section's width, left to right.</summary>
    public List<double> SectionWidths { get; set; } = new();

    /// <summary>A tool hung behind this one: its connector's distance behind this tool's connector; null when it has none.</summary>
    public double? RearConnectorFromHitch { get; set; }

    public bool SameAs(RigTool o) =>
        ProfileName == o.ProfileName && Connector == o.Connector && Mount == o.Mount && HitchType == o.HitchType
        && WorkingPointFromHitch.Equals(o.WorkingPointFromHitch) && TrailingHitchLength.Equals(o.TrailingHitchLength)
        && TankTrailingHitchLength.Equals(o.TankTrailingHitchLength) && TrailingToolToPivotLength.Equals(o.TrailingToolToPivotLength)
        && Offset.Equals(o.Offset) && Length.Equals(o.Length) && Width.Equals(o.Width)
        && SectionWidths.SequenceEqual(o.SectionWidths) && Nullable.Equals(RearConnectorFromHitch, o.RearConnectorFromHitch);
}
