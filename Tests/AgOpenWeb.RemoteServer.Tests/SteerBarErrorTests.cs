// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The steer bar (AgOpenGPS DrawSteerBarText) shows the wheels' angle minus the angle AutoSteer
/// asks for. While the simulator drives and holds the steering, the wheels that follow AutoSteer
/// are the simulator's: the steer module on the network is sent status 0 and its sensor reads a
/// wheel nobody steers (AgOpenGPS's simulator writes its own angle as the actual one, CSim).
/// From a bug report dump (2026-11-19, "Steer bar off"): the simulator on, AutoSteer on its line
/// 1 cm off, commanded −0.064°, and the steer bar at full length.
/// </summary>
[TestFixture]
public class SteerBarErrorTests
{
    private static (SceneProjector Projector, ApplicationState State, IAutoSteerService AutoSteer) Make(double moduleWas)
    {
        var state = new ApplicationState();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = "" });
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(new SteerModuleData(moduleWas, 0, 0, false, false, false, false, 0));
        var projector = new SceneProjector(state, Substitute.For<ISectionControlService>(),
            Substitute.For<IToolPositionService>(), new ConfigurationStore(), Substitute.For<ICoverageMapService>(),
            Substitute.For<IJobService>(), Substitute.For<IConfigurationService>(), autoSteer,
            Substitute.For<ISmartWasCalibrationService>(), Substitute.For<IUdpCommunicationService>(),
            Substitute.For<INtripProfileService>(), Substitute.For<IFieldService>(), settings,
            Substitute.For<IVehicleProfileService>(), Substitute.For<IPersistentStateService>());
        return (projector, state, autoSteer);
    }

    [Test]
    public void OnTheSimulator_TheBarIsTheSimulatorsWheels_NotTheIdleModulesSensor()
    {
        // The module's sensor reads its own wheel, standing at 15° (status 0: it is not steering).
        var (projector, state, _) = Make(moduleWas: 15);
        state.Simulator.IsEnabled = true;
        state.Guidance.SteerAngle = -0.064;          // the dump's commanded angle
        state.Simulator.SteerAngle = -0.064;         // the simulator follows AutoSteer (ApplyResults)

        var tick = projector.BuildTick(1);

        Assert.That(tick.SteerAngleError, Is.EqualTo(0).Within(1e-9), "the simulated wheels are on the commanded angle");
    }

    [Test]
    public void WithoutTheSimulator_TheBarIsTheModulesSensor()
    {
        var (projector, state, _) = Make(moduleWas: 2.5);
        state.Guidance.SteerAngle = 1.0;

        Assert.That(projector.BuildTick(1).SteerAngleError, Is.EqualTo(1.5).Within(1e-9));
    }

    [Test]
    public void SimulatorMovingTheRealSteering_TheBarIsTheModulesSensor()
    {
        // The bench test: the simulator drives and the module steers a motor on the desk.
        var (projector, state, _) = Make(moduleWas: 2.5);
        state.Simulator.IsEnabled = true;
        state.Simulator.SteersModule = true;
        state.Guidance.SteerAngle = 1.0;
        state.Simulator.SteerAngle = 1.0;

        Assert.That(projector.BuildTick(1).SteerAngleError, Is.EqualTo(1.5).Within(1e-9));
    }
}
