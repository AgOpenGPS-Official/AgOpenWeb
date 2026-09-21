// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
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
/// #99: the pipeline steers with the algorithm and gains in GuidanceConfig instead of
/// hard-coded Pure Pursuit.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SteeringAlgorithmSelectionTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
    private List<GpsCycleResult> _results = null!;

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

        _appState = new ApplicationState();
        _appState.Field.LocalPlane = new LocalPlane(
            new Wgs84(43.7128, -74.006), new SharedFieldProperties());

        _gpsService = new GpsService();
        _gpsService.Start();

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, _appState, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, _appState, config);

        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
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
            NullLogger<GpsPipelineService>.Instance, _appState,
            config,
            new PositionEstimator());

        _pipeline.SynchronousMode = true;
        _pipeline.Start();

        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private GpsCycleResult Last => _results[^1];

    // Vehicle at the origin heading north at 3 m/s; the line is 0.5 m to its right.
    private double EngagedSteerAngle(bool stanley, double stanleyDistanceGain = 0.8)
    {
        var g = ConfigurationStore.Instance.Guidance;
        g.IsPurePursuit = !stanley;
        g.StanleyDistanceErrorGain = stanleyDistanceGain;
        _pipeline.SetActiveTrack(
            Models.Track.Track.FromABLine("AB", new Vec3(0.5, -100, 0), new Vec3(0.5, 100, 0)),
            passNumber: 0, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 3 },
            FixQuality = 4,
            IsValid = true,
        });
        return Last.Guidance!.SteerAngle;
    }

    [Test]
    public void Stanley_SteerAngleFollowsStanleyGain()
    {
        double soft = EngagedSteerAngle(stanley: true, stanleyDistanceGain: 0.8);
        double hard = EngagedSteerAngle(stanley: true, stanleyDistanceGain: 2.0);

        Assert.That(Math.Abs(hard), Is.GreaterThan(Math.Abs(soft) + 1.0),
            "A higher Stanley aggressiveness must steer harder — i.e. Stanley is actually running");
    }

    [Test]
    public void Stanley_And_PurePursuit_SteerTowardTheLine_SameDirection()
    {
        double pp = EngagedSteerAngle(stanley: false);
        double st = EngagedSteerAngle(stanley: true);
        TestContext.Out.WriteLine($"line 0.5 m right: PP={pp:F2}° Stanley={st:F2}°");

        Assert.That(Math.Abs(pp), Is.GreaterThan(0.1), "Offset from the line must produce a steer command");
        Assert.That(Math.Sign(st), Is.EqualTo(Math.Sign(pp)),
            "Both algorithms must turn toward the line (a sign flip would steer away)");
    }

    [Test]
    public void PurePursuit_IgnoresStanleyGain()
    {
        double a = EngagedSteerAngle(stanley: false, stanleyDistanceGain: 0.8);
        double b = EngagedSteerAngle(stanley: false, stanleyDistanceGain: 2.0);

        Assert.That(b, Is.EqualTo(a).Within(1e-6));
    }

    [Test]
    public void Stanley_HasNoGoalMarker_PurePursuitDoes()
    {
        EngagedSteerAngle(stanley: true);
        Assert.That(Last.Guidance!.HasGoalPoint, Is.False, "Stanley has no look-ahead target");

        EngagedSteerAngle(stanley: false);
        Assert.That(Last.Guidance!.HasGoalPoint, Is.True);
    }

    [Test]
    public void Stanley_FreeDrive_HasNoGoalMarker()
    {
        ConfigurationStore.Instance.Guidance.IsPurePursuit = false;
        _pipeline.SetActiveTrack(
            Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0)),
            passNumber: 0, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(false);
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
            FixQuality = 4,
            IsValid = true,
        });

        Assert.That(Last.Guidance!.HasGoalPoint, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SteerCommand_IsClampedToTheVehicleMaxSteerAngle(bool stanley)
    {
        // #106: the AutoSteer panel / wizard max angle now IS Vehicle.MaxSteerAngle, which
        // guidance clamps with. Far off the line so the raw command would exceed it.
        ConfigurationStore.Instance.Vehicle.MaxSteerAngle = 12;
        var g = ConfigurationStore.Instance.Guidance;
        g.IsPurePursuit = !stanley;
        g.StanleyDistanceErrorGain = 5;
        _pipeline.SetActiveTrack(
            Models.Track.Track.FromABLine("AB", new Vec3(8, -100, 0), new Vec3(8, 100, 0)),
            passNumber: 0, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 3 },
            FixQuality = 4,
            IsValid = true,
        });

        Assert.That(Math.Abs(Last.Guidance!.SteerAngle), Is.EqualTo(12).Within(1e-6));
    }
}
