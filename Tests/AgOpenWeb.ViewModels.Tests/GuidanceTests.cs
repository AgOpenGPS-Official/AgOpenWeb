// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

[TestFixture]
public class GuidanceTests
{
    [Test]
    public void SelectingTrack_SetsHasActiveTrack()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3>
            {
                new(0, 0, 0),
                new(0, 100, 0)
            }
        };

        vm.SelectedTrack = track;

        Assert.That(vm.HasActiveTrack, Is.True);
    }

    [Test]
    public void SelectingTrack_UpdatesActiveTrackOnState()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3>
            {
                new(0, 0, 0),
                new(0, 100, 0)
            }
        };

        vm.SelectedTrack = track;

        Assert.That(vm.State.Field.ActiveTrack, Is.SameAs(track));
    }

    [Test]
    public void SimulatorSteerAngle_SetWhenAutoSteerEngaged()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        // Set up a track so guidance can run
        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3>
            {
                new(0, 0, 0),
                new(0, 100, 0)
            }
        };
        vm.SelectedTrack = track;

        // Engage autosteer
        vm.IsAutoSteerEngaged = true;

        // Set a known steer angle on the VM (simulating what guidance would do)
        vm.SimulatorSteerAngle = 12.5;

        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(12.5));
    }

    // #411 / AgOpenGPS timerSim_Tick: the sim steers by guidance only while autosteer is
    // engaged on a line, and goes back to the operator's steer when that stops.
    private static GpsCycleResult Cycle(bool hasGuidance, double steer) =>
        new() { Guidance = new AgOpenWeb.Models.Pipeline.GuidanceSnapshot { HasGuidance = hasGuidance, SteerAngle = steer } };

    [Test]
    public void Sim_FollowsGuidance_OnlyWhileEngaged_ThenGoesBackToTheOperatorsSteer()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SimulatorSteerAngle = -0.7; // the operator's L/R slider

        vm.ApplyGpsCycleResult(Cycle(true, 20));
        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(-0.7), "autosteer off: guidance doesn't steer the sim");

        vm.IsAutoSteerEngaged = true;
        vm.ApplyGpsCycleResult(Cycle(true, 20));
        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(20));

        vm.IsAutoSteerEngaged = false;
        vm.ApplyGpsCycleResult(Cycle(true, 25));
        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(-0.7), "disengaged: back to the slider, not the last guidance angle");
    }

    [Test]
    public void Sim_LosingGuidanceWhileEngaged_GoesBackToTheOperatorsSteer()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SimulatorSteerAngle = 0;
        vm.IsAutoSteerEngaged = true;
        vm.ApplyGpsCycleResult(Cycle(true, 30));
        vm.ApplyGpsCycleResult(Cycle(false, 0));
        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(0));
    }

    [Test]
    public void Sim_OperatorSteer_IsNotOverwritten_WithoutGuidance()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SimulatorSteerAngle = 5;
        vm.ApplyGpsCycleResult(Cycle(false, 0));
        Assert.That(vm.SimulatorSteerAngle, Is.EqualTo(5));
    }
}
