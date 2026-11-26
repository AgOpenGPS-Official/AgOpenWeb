// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The map draws a trailed or TBT implement where the sections and the coverage work. The map's
/// implement comes from the 30 Hz render pull (<c>MainViewModel.OnRenderPullTick</c>), which
/// placed it on its own: it still subtracted the pivot distance after the tool position started
/// adding it (AgOpenGPS's sign), so a tool set "Behind" its wheels was drawn ahead of them, and
/// "Ahead" behind (a Hungarian user, 2026-11-07: "I have to pick Ahead and that makes it behind").
/// A TBT tool was also drawn from the tractor's hitch, without its tank.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class MapImplementPositionTests
{
    // The tractor's pivot at the origin heading north; the hitch 2 m behind it, the tank's axle
    // 3 m behind the hitch, the tool's wheels 4 m behind what pulls them.
    private const double Hitch = 2, Tank = 3, Trailing = 4;

    private static VehicleState DrawnFor(string mount, double pivotDistance)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.HitchLength = Hitch;
        c.Tool.TankTrailingHitchLength = Tank;
        c.Tool.TrailingHitchLength = Trailing;
        c.Tool.TrailingToolToPivotLength = pivotDistance;
        c.Tool.IsToolFrontFixed = c.Tool.IsToolRearFixed = false;
        c.Tool.IsToolTrailing = true;
        c.Tool.IsToolTBT = mount == "tbt";

        var estimator = Substitute.For<IPositionEstimator>();
        estimator.GetLatestSnapshot().Returns(new PoseSnapshot(new Vec2(0, 0), 0, 0, 0, 0, 0));
        estimator.GetPose(Arg.Any<long>()).Returns(new InterpolatedPose(new Vec2(0, 0), 0, 0, 0));
        var b = new MainViewModelBuilder { PositionEstimator = estimator };
        // Everything trails straight behind, heading north.
        b.ToolPositionService.ToolHeading.Returns(0.0);
        b.ToolPositionService.TankPosition.Returns(new Vec3(0, -(Hitch + Tank), 0));
        b.ToolPositionService.IsToolPositionReady.Returns(true);
        var vm = b.Build();

        // The render pull is the first 30 Hz (20 Hz on mobile) timer the view model makes.
        b.Timers.First(t => t.Interval.TotalMilliseconds is 33 or 50).Fire();
        Assert.That(vm.State.Vehicle.RenderPoseValid, Is.True, "the render pull ran");
        return vm.State.Vehicle;
    }

    [TestCase(1.0, -(Hitch + Trailing + 1), TestName = "Trailing_ToolBehindItsWheels_IsDrawnBehindThem")]
    [TestCase(-1.0, -(Hitch + Trailing - 1), TestName = "Trailing_ToolAheadOfItsWheels_IsDrawnAheadOfThem")]
    [TestCase(0.0, -(Hitch + Trailing), TestName = "Trailing_ToolOnItsWheels_IsDrawnOnThem")]
    public void TrailedTool_IsDrawnWhereItWorks(double pivotDistance, double northing)
    {
        var v = DrawnFor("trailing", pivotDistance);
        Assert.Multiple(() =>
        {
            Assert.That(v.RenderToolNorthing, Is.EqualTo(northing).Within(1e-9), "the working centre on the map");
            Assert.That(v.RenderToolEasting, Is.EqualTo(0).Within(1e-9));
            Assert.That(v.RenderHitchNorthing, Is.EqualTo(-Hitch).Within(1e-9), "the tractor's hitch");
        });
    }

    [TestCase(0.5, -(Hitch + Tank + Trailing + 0.5), TestName = "Tbt_ToolBehindItsWheels_IsDrawnBehindTheTank")]
    [TestCase(-0.5, -(Hitch + Tank + Trailing - 0.5), TestName = "Tbt_ToolAheadOfItsWheels_IsDrawnBehindTheTank")]
    public void TbtTool_IsDrawnWhereItWorks(double pivotDistance, double northing)
    {
        var v = DrawnFor("tbt", pivotDistance);
        Assert.That(v.RenderToolNorthing, Is.EqualTo(northing).Within(1e-9), "the working centre on the map, behind the tank");
    }
}
