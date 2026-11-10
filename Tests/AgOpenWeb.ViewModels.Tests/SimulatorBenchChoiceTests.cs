// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The bench choice: the operator lets the simulator move the real steering (a steering motor
/// on the desk). It is off unless asked for, only with the simulator on, and it does not
/// outlive the bench: the simulator turned off, or the steer module gone silent, drops it, so
/// it is not still on when the tablet is back on the tractor.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SimulatorBenchChoiceTests
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
        _vm.State.Connections.IsAutoSteerDataOk = true;   // a steer module answers
    }

    private void Engage()
    {
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
    }

    [Test]
    public void OffUnlessAskedFor()
    {
        Assert.That(_vm.IsSimulatorDrivingSteering, Is.False);
        Assert.That(_vm.State.Simulator.DrivesSteering, Is.False);
        _b.GpsPipelineService.DidNotReceive().SetSimulatorSteersModule(true);
    }

    [Test]
    public void Chosen_ThePipelineAndTheScreenAreTold()
    {
        _vm.IsSimulatorDrivingSteering = true;

        Assert.That(_vm.IsSimulatorDrivingSteering, Is.True);
        Assert.That(_vm.State.Simulator.DrivesSteering, Is.True);
        _b.GpsPipelineService.Received(1).SetSimulatorSteersModule(true);
    }

    [Test]
    public void Refused_WithTheSimulatorOff()
    {
        _vm.IsSimulatorEnabled = false;

        _vm.IsSimulatorDrivingSteering = true;

        Assert.That(_vm.IsSimulatorDrivingSteering, Is.False);
        _b.GpsPipelineService.DidNotReceive().SetSimulatorSteersModule(true);
    }

    [Test]
    public void SimulatorTurnedOff_DropsIt_AndTurningItOnAgainDoesNotBringItBack()
    {
        _vm.IsSimulatorDrivingSteering = true;

        _vm.IsSimulatorEnabled = false;
        Assert.That(_vm.IsSimulatorDrivingSteering, Is.False);
        _b.GpsPipelineService.Received().SetSimulatorSteersModule(false);

        _vm.IsSimulatorEnabled = true;
        Assert.That(_vm.IsSimulatorDrivingSteering, Is.False);
        Assert.That(_vm.State.Simulator.DrivesSteering, Is.False);
    }

    [Test]
    public void SteerModuleGoesSilent_DropsIt()
    {
        _vm.IsSimulatorDrivingSteering = true;

        _vm.DropBenchChoiceWhenSteerModuleGoesSilent(steerModuleOk: true);
        Assert.That(_vm.IsSimulatorDrivingSteering, Is.True, "the module still answers");

        _vm.DropBenchChoiceWhenSteerModuleGoesSilent(steerModuleOk: false);
        Assert.That(_vm.IsSimulatorDrivingSteering, Is.False);
    }

    [Test]
    public void ChosenWhileEngaged_TakesAutoSteerOffFirst()
    {
        Engage();
        string? notice = null; _vm.FailureReported += m => notice = m;

        _vm.IsSimulatorDrivingSteering = true;

        Assert.That(_vm.IsAutoSteerEngaged, Is.False, "the steering does not start under a line engaged before");
        Assert.That(notice, Does.StartWith("AutoSteer off:"));
        Received.InOrder(() =>
        {
            _b.AutoSteerService.Disengage();
            _b.GpsPipelineService.SetSimulatorSteersModule(true);
        });
    }

    [Test]
    public void TakenBackWhileEngaged_TakesAutoSteerOff()
    {
        _vm.IsSimulatorDrivingSteering = true;
        Engage();

        _vm.IsSimulatorDrivingSteering = false;

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }

    [Test]
    public void EngagedWithIt_SaysTheSimulatorMovesTheRealSteering()
    {
        _vm.IsSimulatorDrivingSteering = true;

        Engage();

        Assert.That(_vm.StatusMessage, Does.Contain("moves the real steering"));
    }
}
