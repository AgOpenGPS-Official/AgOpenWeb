// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Tool;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Tool;

/// <summary>
/// Where the tool hangs on the tractor is worked out in one place, <see cref="RigGeometry"/>
/// (phase 1 of NonTracked/HITCH_MODEL_PLAN.md: today's model, no change in where the tool is).
/// The tool position, the map's implement, the U-turn's swept path and its remembered-end key
/// each had their own copy, and the copies disagreed on which flag won when a profile set two.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class RigGeometryTests
{
    // Every combination of the four tool-type flags (a profile sets them one by one).
    private static IEnumerable<TestCaseData> Flags() =>
        Enumerable.Range(0, 16).Select(b => new TestCaseData((b & 1) != 0, (b & 2) != 0, (b & 4) != 0, (b & 8) != 0)
            .SetName($"front {(b & 1) != 0}, rear {(b & 2) != 0}, trailing {(b & 4) != 0}, TBT {(b & 8) != 0}"));

    private static ToolConfig Tool(bool front, bool rear, bool trailing, bool tbt) => new()
    {
        IsToolFrontFixed = front, IsToolRearFixed = rear, IsToolTrailing = trailing, IsToolTBT = tbt,
        WorkingPointFromHitch = 0, TrailingHitchLength = 2.5, TankTrailingHitchLength = 3.0, TrailingToolToPivotLength = 0.4,
    };

    // The tool position's hitch before RigGeometry (ToolPositionService.Update, develop 5babf3ad),
    // with a fixed tool's axle → working point 1.3 m (format 2's Tool.HitchLength). In the hitch
    // model (#547) that is the vehicle's 3-point and front hitch at 1.3 m, the tool at them.
    private const double OldToolHitch = 1.3;
    private static double OldAttachX(VehicleConfig v, ToolConfig t)
    {
        double d = (t.IsToolFrontFixed || t.IsToolRearFixed) ? OldToolHitch : Math.Abs(v.HitchLength);
        if (t.IsToolRearFixed || t.IsToolTrailing || t.IsToolTBT) d = -d;
        return d;
    }

    [TestCaseSource(nameof(Flags))]
    public void The_hitch_point_is_where_the_tool_position_had_it(bool front, bool rear, bool trailing, bool tbt)
    {
        var v = new VehicleConfig { HitchLength = 0.9, ThreePointHitchLength = OldToolHitch, FrontHitchLength = OldToolHitch };
        var t = Tool(front, rear, trailing, tbt);
        if (!front && !rear && !trailing && !tbt)
        {
            // No flag set: the old copy put the tractor's hitch pin ahead of the axle and a fixed
            // tool on it; RigGeometry makes it the default rear-fixed tool.
            Assert.That(RigGeometry.Mount(t), Is.EqualTo(ToolMount.RearFixed));
            Assert.That(RigGeometry.AttachX(v, t), Is.EqualTo(-1.3));
            return;
        }
        bool oldRigid = front || rear;
        Assert.Multiple(() =>
        {
            Assert.That(RigGeometry.AttachX(v, t), Is.EqualTo(OldAttachX(v, t)));
            Assert.That(RigGeometry.IsRigid(RigGeometry.Mount(t)), Is.EqualTo(oldRigid));
            if (!oldRigid)
                Assert.That(RigGeometry.Mount(t), Is.EqualTo(tbt ? ToolMount.TBT : ToolMount.Trailing));
            // The swept path's geometry gives the same point.
            Assert.That(RigGeometry.Geometry(v, t, 6).AttachX, Is.EqualTo(OldAttachX(v, t)));
        });
    }

    [TestCase("front", 1.3)]
    [TestCase("rear", -1.3)]
    [TestCase("trailing", -0.9)]
    [TestCase("tbt", -0.9)]
    public void The_tool_position_service_puts_the_hitch_there(string type, double x)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Vehicle.HitchLength = 0.9;
        store.Vehicle.ThreePointHitchLength = store.Vehicle.FrontHitchLength = 1.3;
        store.Tool.WorkingPointFromHitch = 0;
        store.Tool.SetToolType(type);
        var svc = new ToolPositionService(store);
        svc.Update(new Vec3(10, 20, 0), 0); // heading north
        Assert.Multiple(() =>
        {
            Assert.That(svc.HitchPosition.Easting, Is.EqualTo(10).Within(1e-9));
            Assert.That(svc.HitchPosition.Northing, Is.EqualTo(20 + x).Within(1e-9));
        });
    }

    // No hitch branch of its own outside RigGeometry: the files that place the tool read the
    // hitch lengths and the type flags only through it.
    [TestCase("Shared/AgOpenWeb.Services/Tool/ToolPositionService.cs")]
    [TestCase("Shared/AgOpenWeb.ViewModels/MainViewModel.cs")]
    [TestCase("Shared/AgOpenWeb.Models/Tool/ImplementSweptPath.cs")]
    [TestCase("Shared/AgOpenWeb.Services/YouTurn/YouTurnCreationService.Orchestration.cs")]
    [TestCase("Shared/AgOpenWeb.Services/YouTurn/YouTurnStateMachine.cs")]
    [TestCase("Shared/AgOpenWeb.RemoteServer/SceneProjector.cs")]
    public void Places_the_tool_only_through_RigGeometry(string file)
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), file));
        // The config DTOs carry the raw values to the settings panel; those lines are not geometry.
        var lines = src.Split('\n').Where(l => !l.Contains("ConfigDto(") && !l.TrimStart().StartsWith("//")
            && !l.TrimStart().StartsWith("///") && !l.Contains("foreach (var d in new[]"));
        var bad = lines.Where(l => Regex.IsMatch(l, @"\.(HitchLength|ToolHitchLength|VehicleHitchLength)\b|IsTool(FrontFixed|RearFixed|Trailing|TBT)\b"))
            .Select(l => l.Trim()).ToList();
        Assert.That(bad, Is.Empty, $"{file} works out the hitch itself; use RigGeometry");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return dir!.FullName;
    }
}
