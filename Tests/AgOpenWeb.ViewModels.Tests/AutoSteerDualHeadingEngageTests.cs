// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AutoSteer doesn't engage while the dual antenna's heading doesn't match the way the
/// tractor moves. In the Vehicle &amp; Tool scenarios an offset 90° out (antennas front to
/// back, set up as side by side) and one 180° out (antennas swapped) both took the tractor
/// out of its pass: guidance steered for a tractor driving across its line, or backwards.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerDualHeadingEngageTests
{
    private MainViewModel _vm = null!;
    private string? _said;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        _vm = new MainViewModelBuilder().Build();
        _vm.IsAutoSteerAvailable = true;
        _said = null;
        _vm.FailureReported += m => _said = m;
    }

    private void Engage() => _vm.ToggleAutoSteerCommand!.Execute(null);

    [Test]
    public void A_dual_heading_across_the_travel_does_not_engage()
    {
        _vm.ApplyGpsCycleResult(new GpsCycleResult { IsDualHeadingOffTravel = true, DualHeadingOffTravelDeg = 88.6 });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Is.EqualTo(
            "AutoSteer not available - the dual antenna heading is 89° off the direction of travel: check its Heading offset (GPS tab)"));
    }

    [Test]
    public void The_dual_antenna_reading_reversing_does_not_engage()
    {
        _vm.ApplyGpsCycleResult(new GpsCycleResult { IsReverse = true });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Does.StartWith("AutoSteer not available - the dual antenna reads reversing"));
        Assert.That(_said, Does.Contain("180°"));
    }

    [Test]
    public void Engaged_AutoSteer_still_switches_off()
    {
        Engage();
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
        _vm.ApplyGpsCycleResult(new GpsCycleResult { IsReverse = true });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }

    [Test]
    public void A_heading_along_the_travel_engages()
    {
        _vm.ApplyGpsCycleResult(new GpsCycleResult { DualHeadingOffTravelDeg = 3 });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
        Assert.That(_said, Is.Null);
    }

    [Test]
    public void Reversing_on_a_single_antenna_is_as_before()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = false;
        _vm.ApplyGpsCycleResult(new GpsCycleResult { IsReverse = true });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
    }

    [Test]
    public void A_missing_dual_heading_is_not_read_as_reversing_by_the_dual_antenna()
    {
        _vm.ApplyGpsCycleResult(new GpsCycleResult { IsReverse = true, IsDualHeadingMissing = true });

        Engage();

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
    }
}
