// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// The simulator never steers the real module. The steer module stays on the network while the
/// simulator runs; with AutoSteer engaged it got PGN 254 status 1 and the simulator's speed, so
/// it opened the lock valve and drove the steering valve of a parked tractor (the valve hummed
/// until AutoSteer went off). Read here where the module reads it: the status byte of every
/// PGN 254 the 100 Hz control tick sends.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SimulatorNeverSteersTests
{
    private const int StatusByte = 7;

    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private AutoSteerService _autoSteer = null!;
    private List<byte[]> _steerPgns = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);

        _gpsService = new GpsService();
        _gpsService.Start();

        // What goes to the module network: PGN 254 only (the buffer is reused, so copy it).
        _steerPgns = new List<byte[]>();
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.IsConnected.Returns(true);
        udp.SendToModules(Arg.Do<byte[]>(p =>
        {
            if (p.Length > StatusByte && p[3] == PgnBuilder.PGN_AUTOSTEER) _steerPgns.Add((byte[])p.Clone());
        }));

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        _autoSteer = new AutoSteerService(new TrackGuidanceService(), udp, _gpsService, config);
        _autoSteer.Start();

        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, _autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _autoSteer.Stop();
        _gpsService.Stop();
    }

    /// <summary>The operator's AutoSteer button, as MainViewModel does it.</summary>
    private void Engage()
    {
        _autoSteer.Engage();
        _pipeline.SetAutoSteerEngaged(true);
    }

    /// <summary>One fix at 8 km/h, then ten control ticks; the status of every PGN 254 they sent.</summary>
    private byte[] StatusesAfterFix(bool simulated)
    {
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Speed = 8 / 3.6 },
            FixQuality = 4,
            IsValid = true,
            SentenceType = simulated ? GpsSentenceType.Simulator : GpsSentenceType.Panda,
        });
        _steerPgns.Clear();
        for (int i = 0; i < 10; i++) _autoSteer.SendPgnsForControlTick();
        Assume.That(_steerPgns, Is.Not.Empty, "the control tick sends PGN 254");
        return _steerPgns.Select(p => p[StatusByte]).ToArray();
    }

    [Test]
    public void ReceiverFix_Engaged_AsksTheModuleToSteer() // the rig can see a 1
    {
        Engage();

        Assert.That(StatusesAfterFix(simulated: false), Has.All.EqualTo(1));
    }

    [Test]
    public void SimulatedFix_Engaged_NeverAsksTheModuleToSteer()
    {
        _pipeline.SetSimulatorEnabled(true);
        Engage();

        Assert.That(StatusesAfterFix(simulated: true), Has.All.EqualTo(0));
        Assert.That(_pipeline.IsAutoSteerEngaged, Is.True, "AutoSteer stays engaged: the simulator follows the line");

        // The module still hears the program (it answers each PGN 254 with its 253) and
        // gets the simulator's speed, as a machine module does.
        byte[] last = _steerPgns[^1];
        Assert.That(last[5] | (last[6] << 8), Is.EqualTo(80), "8.0 km/h");
    }

    [Test]
    public void SimulatorOn_HoldsFromThatMoment_NotFromTheNextFix()
    {
        _pipeline.SetSimulatorEnabled(true);
        Engage();

        for (int i = 0; i < 10; i++) _autoSteer.SendPgnsForControlTick();

        Assume.That(_steerPgns, Is.Not.Empty, "the control tick sends PGN 254");
        Assert.That(_steerPgns.Select(p => p[StatusByte]), Has.All.EqualTo(0));
    }

    [Test]
    public void BenchChoice_TheSimulatorMovesTheSteering()
    {
        // A steering motor on the desk: the operator lets the simulator drive the module.
        _pipeline.SetSimulatorEnabled(true);
        _pipeline.SetSimulatorSteersModule(true);
        Engage();

        Assert.That(StatusesAfterFix(simulated: true), Has.All.EqualTo(1));
    }

    [Test]
    public void BenchChoice_TakenBack_HoldsAgainAtOnce()
    {
        _pipeline.SetSimulatorEnabled(true);
        _pipeline.SetSimulatorSteersModule(true);
        Engage();
        StatusesAfterFix(simulated: true);

        _pipeline.SetSimulatorSteersModule(false);
        _steerPgns.Clear();
        for (int i = 0; i < 10; i++) _autoSteer.SendPgnsForControlTick();

        Assert.That(_steerPgns.Select(p => p[StatusByte]), Has.All.EqualTo(0));
    }

    [Test]
    public void AFixLabelledSimulator_WithTheSimulatorOff_Steers()
    {
        // The hold is the app's simulator being the chosen source, not the label on a fix:
        // the scenario harnesses feed their own fixes with this label to an emulated module.
        Engage();

        Assert.That(StatusesAfterFix(simulated: true), Has.All.EqualTo(1));
    }

    [Test]
    public void ReceiverFix_WhileTheSimulatorIsStillTheSource_DoesNotSteer()
    {
        // The receiver's first fixes come before the simulator's timer notices them and
        // stops: AutoSteer engaged on the simulator must not reach the module with them.
        _pipeline.SetSimulatorEnabled(true);
        Engage();
        StatusesAfterFix(simulated: true);

        Assert.That(StatusesAfterFix(simulated: false), Has.All.EqualTo(0));
    }

    [Test]
    public void AfterTheSimulator_ReceiverFix_SteersAgain()
    {
        _pipeline.SetSimulatorEnabled(true);
        Engage();
        StatusesAfterFix(simulated: true);

        _pipeline.SetSimulatorEnabled(false);
        byte[] statuses = StatusesAfterFix(simulated: false);

        Assert.That(statuses[^1], Is.EqualTo(1), "the hold is the simulator's, not a latch");
    }

    [Test]
    public void FreeDrive_OnTheSimulator_StillMovesTheSteering()
    {
        // Free Drive is the operator's own test of the steering (AutoSteer settings, the
        // steer wizard), on the bench with the simulator as the GPS: it is not held back.
        _pipeline.SetSimulatorEnabled(true);
        StatusesAfterFix(simulated: true);

        _autoSteer.EnableFreeDrive();
        _steerPgns.Clear();
        for (int i = 0; i < 10; i++) _autoSteer.SendPgnsForControlTick();

        Assert.That(_steerPgns.Select(p => p[StatusByte]), Has.All.EqualTo(1));
    }
}
