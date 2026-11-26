// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A change of GPS source takes AutoSteer off. Engaged on the simulator, it stayed engaged when
/// the receiver's fix took over (the simulator stops by itself then): the steer module got
/// PGN 254 status 1 with the tractor's real speed, and steered to a line nobody had engaged on
/// that tractor.
/// </summary>
[TestFixture]
public class SimulatorAutoSteerOffTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        _vm.IsSimulatorEnabled = true;
        _vm.IsAutoSteerAvailable = true;
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
        _b.AutoSteerService.ClearReceivedCalls();
        _b.GpsPipelineService.ClearReceivedCalls();
    }

    [Test]
    public void EngagedOnTheSimulator_SaysTheModuleIsNotDriven()
        => Assert.That(_vm.StatusMessage, Does.Contain("simulator").And.Contain("not driven"));

    [Test]
    public void SimulatorTurnedOff_TakesAutoSteerOff_BeforeThePipelineIsTold()
    {
        string? notice = null; _vm.FailureReported += m => notice = m;

        _vm.IsSimulatorEnabled = false;

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(notice, Is.EqualTo("AutoSteer off: simulator turned off"));
        // In this order: once the pipeline knows the simulator is off, a receiver's fix may steer.
        Received.InOrder(() =>
        {
            _b.AutoSteerService.Disengage();
            _b.GpsPipelineService.SetSimulatorEnabled(false);
        });
    }

    [Test]
    public void ReceiverFixArrives_SimulatorStops_AndAutoSteerGoesOff()
    {
        _b.GpsService.IsGpsLive.Returns(true);   // the receiver got its fix

        typeof(MainViewModel).GetMethod("OnSimulatorTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_vm, new object?[] { null, EventArgs.Empty });

        Assert.That(_vm.IsSimulatorEnabled, Is.False, "the simulator gives way to the receiver");
        Assert.That(_vm.IsAutoSteerEngaged, Is.False, "and what was engaged on it does not steer the tractor");
        _b.AutoSteerService.Received().Disengage();
    }

    [Test]
    public void SimulatorTurnedOn_WhileEngaged_TakesAutoSteerOff()
    {
        _vm.IsSimulatorEnabled = false;
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);

        _vm.IsSimulatorEnabled = true;

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }

    [Test]
    public void NotEngaged_TurningTheSimulatorOff_SaysNothing()
    {
        _vm.ToggleAutoSteerCommand!.Execute(null);
        int notices = 0; _vm.FailureReported += _ => notices++;

        _vm.IsSimulatorEnabled = false;

        Assert.That(notices, Is.Zero);
    }
}

/// <summary>
/// The simulator gives way to a receiver that sends fixes. The operator is told so: the button
/// snapped back with no word, and a running simulator went off without one (the HAT on the bench,
/// 2026-11-15).
/// </summary>
[TestFixture]
public class SimulatorLiveReceiverNoticeTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [Test]
    public void Turning_the_simulator_on_while_a_receiver_sends_fixes_says_why_it_stays_off()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSimulatorEnabled = false; // on by default in a new install
        b.GpsService.IsGpsLive.Returns(true);
        string? notice = null; vm.FailureReported += m => notice = m;

        vm.IsSimulatorEnabled = true;

        Assert.That(vm.IsSimulatorEnabled, Is.False);
        Assert.That(notice, Is.EqualTo(MainViewModel.SimulatorRefusedMessage));
    }

    [Test]
    public void A_receiver_taking_over_from_the_simulator_says_so()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSimulatorEnabled = true;
        var notices = new List<string>(); vm.FailureReported += notices.Add;
        b.GpsService.IsGpsLive.Returns(true);

        typeof(MainViewModel).GetMethod("OnSimulatorTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, new object?[] { null, EventArgs.Empty });

        Assert.That(vm.IsSimulatorEnabled, Is.False);
        Assert.That(notices, Is.EqualTo(new[] { MainViewModel.SimulatorGivesWayMessage }));
    }
}
