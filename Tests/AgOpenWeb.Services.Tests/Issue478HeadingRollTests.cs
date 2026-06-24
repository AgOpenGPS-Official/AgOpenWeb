// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Net;
using System.Net.Sockets;
using AgOpenWeb.IntegrationTests.VirtualModules;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Repro harness for issue #478 (heading sticks at the 360/0 wrap; roll has a
/// ±10° dead-zone / one-sided / invert-no-effect). Drives REAL $PANDA bytes
/// from the Vehicle Simulator through the production zero-copy parser
/// (<see cref="NmeaParserServiceFast.ParseIntoState"/>) and asserts the parsed
/// VehicleState. This isolates the FIRST hop (parse) from the downstream
/// consumer/mapping layer.
/// </summary>
[TestFixture]
public class Issue478HeadingRollTests
{
    /// <summary>Capture the raw $PANDA bytes the simulator emits for a given heading/roll.</summary>
    private static byte[] CapturePanda(double headingDeg, double rollDeg)
    {
        using var listener = new UdpClient(0);
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var gps = new VirtualGpsReceiver(targetPort: port);
        gps.Latitude = 43.7128;
        gps.Longitude = -74.0060;
        gps.FixQuality = 4;
        gps.Satellites = 14;
        gps.SpeedKnots = 5.0;
        gps.HeadingDegrees = headingDeg;
        gps.RollDegrees = rollDeg;

        gps.SendOnce();

        listener.Client.ReceiveTimeout = 2000;
        IPEndPoint? remote = null;
        return listener.Receive(ref remote);
    }

    private static AgOpenWeb.Models.VehicleState ParsePanda(double headingDeg, double rollDeg)
    {
        var bytes = CapturePanda(headingDeg, rollDeg);
        var state = new AgOpenWeb.Models.VehicleState();
        var config = new ConfigurationStore(); // IsRollInvert=false, RollZero=0
        NmeaParserServiceFast.ParseIntoState(bytes, ref state, config);
        return state;
    }

    // ── ROLL: both signs and small magnitudes should round-trip ──────────────
    [TestCase(15.0)]
    [TestCase(-15.0)]
    [TestCase(3.0)]
    [TestCase(-3.0)]
    [TestCase(0.5)]
    [TestCase(-0.5)]
    public void Roll_RoundTripsBothSigns(double rollDeg)
    {
        var state = ParsePanda(headingDeg: 90.0, rollDeg: rollDeg);

        Assert.That(state.Roll, Is.EqualTo(rollDeg).Within(0.06),
            $"Parsed roll should equal sent roll ({rollDeg}°). Dead-zone/one-sided => bug here.");
    }

    [Test]
    public void Roll_InvertFlipsSign()
    {
        var bytes = CapturePanda(headingDeg: 90.0, rollDeg: 12.0);

        var normal = new AgOpenWeb.Models.VehicleState();
        var cfgNormal = new ConfigurationStore();
        NmeaParserServiceFast.ParseIntoState(bytes, ref normal, cfgNormal);

        var inverted = new AgOpenWeb.Models.VehicleState();
        var cfgInvert = new ConfigurationStore();
        cfgInvert.Ahrs.IsRollInvert = true;
        NmeaParserServiceFast.ParseIntoState(bytes, ref inverted, cfgInvert);

        Assert.That(inverted.Roll, Is.EqualTo(-normal.Roll).Within(0.06),
            "IsRollInvert should flip the sign of parsed roll.");
    }

    // ── HEADING: must track continuously across the 360/0 wrap ───────────────
    [TestCase(350.0)]
    [TestCase(359.0)]
    [TestCase(0.0)]
    [TestCase(1.0)]
    [TestCase(10.0)]
    public void Heading_TracksAcrossWrap(double headingDeg)
    {
        var state = ParsePanda(headingDeg: headingDeg, rollDeg: 0.0);

        Assert.That(state.Heading, Is.EqualTo(headingDeg).Within(0.06),
            $"Parsed heading should equal sent heading ({headingDeg}°), including near the 360/0 wrap.");
    }
}

/// <summary>
/// End-to-end repro for #478: feed a GpsData (exactly what
/// AutoSteerService.PublishGpsData emits — ImuRoll = _state.Roll, ImuValid set)
/// through the REAL GpsPipelineService + real GpsHeadingFusionService and read
/// the published GpsCycleResult (what the gauge/display binds to).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class Issue478PipelineTests
{
    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private GpsCycleResult? _lastResult;

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

        var appState = new ApplicationState();
        appState.Field.LocalPlane = new LocalPlane(
            new Wgs84(43.7128, -74.006), new SharedFieldProperties());

        _gps = new GpsService();
        _gps.Start();

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, appState, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gps, appState, config);

        // REAL heading fusion — this is what we're testing for the wrap.
        var headingFusion = new GpsHeadingFusionService(config);

        _pipeline = new GpsPipelineService(
            _gps, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance, appState,
            config,
            new PositionEstimator());

        _pipeline.SynchronousMode = true;
        _pipeline.CycleCompleted += r => _lastResult = r;
        _pipeline.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gps.Stop();
    }

    private static GpsData Fix(double heading, double roll, double speed = 5.0, bool imuValid = true)
        => new()
        {
            CurrentPosition = new Position
            {
                Latitude = 43.7128, Longitude = -74.006,
                Heading = heading, Speed = speed,
            },
            FixQuality = 4,
            ImuValid = imuValid,
            ImuHeading = heading,
            ImuRoll = roll,
            IsValid = true,
        };

    // ── ROLL: the value the gauge binds to (GpsCycleResult.RollDegrees) ──────
    [TestCase(15.0)]
    [TestCase(-15.0)]
    [TestCase(3.0)]
    [TestCase(-3.0)]
    public void Pipeline_RollDegrees_RoundTripsBothSigns(double rollDeg)
    {
        _gps.UpdateGpsData(Fix(heading: 90, roll: rollDeg));

        Assert.That(_lastResult, Is.Not.Null);
        Assert.That(_lastResult!.RollDegrees, Is.EqualTo(rollDeg).Within(0.06),
            $"Published RollDegrees should equal input roll ({rollDeg}°) for both signs.");
    }

    // ── HEADING: published heading must track continuously across 360/0 ──────
    [TestCase(350.0)]
    [TestCase(359.0)]
    [TestCase(0.0)]
    [TestCase(1.0)]
    [TestCase(10.0)]
    public void Pipeline_Heading_TracksAcrossWrap(double headingDeg)
    {
        // Low speed so fix-to-fix doesn't override the IMU heading.
        _gps.UpdateGpsData(Fix(heading: headingDeg, roll: 0, speed: 0.0));

        Assert.That(_lastResult, Is.Not.Null);
        double h = ((_lastResult!.Heading % 360) + 360) % 360;
        Assert.That(h, Is.EqualTo(headingDeg).Within(0.5),
            $"Published heading should equal input heading ({headingDeg}°) across the wrap.");
    }
}
