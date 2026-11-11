// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#126: engaged + module reporting "not steering" (PGN 253 steer bit high) is
/// shown, and alerted once when it follows real steering (a kickout), like AgOpenGPS's
/// red steer circle.</summary>
[TestFixture]
public class ModuleSteeringStateTests
{
    private static SteerModuleData Bit(bool notSteering) => new(0, 0, 0, false, notSteering, false, false, 0);

    private static (MainViewModel vm, MainViewModelBuilder b) Engaged()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSimulatorEnabled = false;   // a receiver's fix: on the simulator the module is never asked to steer
        vm.State.Connections.IsAutoSteerDataOk = true;
        vm.IsAutoSteerEngaged = true;
        return (vm, b);
    }

    [Test]
    public void KickoutWhileSteering_ShowsAndAlertsOnce()
    {
        var (vm, b) = Engaged();
        string? alert = null; int alerts = 0;
        vm.FailureReported += m => { alert = m; alerts++; };

        b.AutoSteerService.LastSteerData.Returns(Bit(false));   // module steering
        vm.UpdateModuleSteeringState();
        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.False);

        b.AutoSteerService.LastSteerData.Returns(Bit(true));    // kickout
        vm.UpdateModuleSteeringState();
        vm.UpdateModuleSteeringState();
        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.True);
        Assert.That(alerts, Is.EqualTo(1));
        Assert.That(alert, Does.Contain("stopped steering"));
    }

    [Test]
    public void BeforeTheModuleArms_ShowsButDoesNotAlert()
    {
        var (vm, b) = Engaged();
        int alerts = 0; vm.FailureReported += _ => alerts++;

        b.AutoSteerService.LastSteerData.Returns(Bit(true));    // just engaged, not armed yet
        vm.UpdateModuleSteeringState();

        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.True);
        Assert.That(alerts, Is.Zero);
    }

    [Test]
    public void NotEngaged_OrNoModule_NeverNotSteering()
    {
        var (vm, b) = Engaged();
        b.AutoSteerService.LastSteerData.Returns(Bit(true));

        vm.IsAutoSteerEngaged = false;
        vm.UpdateModuleSteeringState();
        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.False);

        vm.IsAutoSteerEngaged = true;
        vm.State.Connections.IsAutoSteerDataOk = false;          // simulator / no steer module
        vm.UpdateModuleSteeringState();
        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.False);
    }

    [Test]
    public void OnTheSimulator_TheModuleIsNotAskedToSteer_SoNothingIsShownOrAlerted()
    {
        // PGN 254 status stays 0 on the simulator, so a module on the network reports "not
        // steering" all the time: that is what it should do, not a kickout.
        var (vm, b) = Engaged();
        int alerts = 0; vm.FailureReported += _ => alerts++;
        b.AutoSteerService.LastSteerData.Returns(Bit(false));
        vm.UpdateModuleSteeringState();                          // steering on the receiver's fix

        vm.IsAutoSteerEngaged = false;
        vm.IsSimulatorEnabled = true;
        vm.IsAutoSteerEngaged = true;
        b.AutoSteerService.LastSteerData.Returns(Bit(true));
        vm.UpdateModuleSteeringState();
        vm.UpdateModuleSteeringState();

        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.False);
        Assert.That(alerts, Is.Zero);
    }

    [Test]
    public void OnTheSimulator_WithTheBenchChoice_AModuleThatStopsSteeringIsShown()
    {
        // The operator let the simulator move the steering: the module is asked to steer,
        // so one that stops is a kickout again.
        var (vm, b) = Engaged();
        vm.IsAutoSteerEngaged = false;
        vm.IsSimulatorEnabled = true;
        vm.IsSimulatorDrivingSteering = true;
        vm.IsAutoSteerEngaged = true;
        int alerts = 0; vm.FailureReported += _ => alerts++;

        b.AutoSteerService.LastSteerData.Returns(Bit(false));
        vm.UpdateModuleSteeringState();
        b.AutoSteerService.LastSteerData.Returns(Bit(true));
        vm.UpdateModuleSteeringState();

        Assert.That(vm.State.Connections.IsModuleNotSteering, Is.True);
        Assert.That(alerts, Is.EqualTo(1));
    }

    [Test]
    public void WithSwitchTypeNone_KickoutDoesNotDisengage() // AgOpenGPS: shows only
    {
        var (vm, b) = Engaged();
        b.AutoSteerService.LastSteerData.Returns(Bit(false));
        vm.UpdateModuleSteeringState();
        b.AutoSteerService.LastSteerData.Returns(Bit(true));
        vm.UpdateModuleSteeringState();
        Assert.That(vm.IsAutoSteerEngaged, Is.True);
    }
}
