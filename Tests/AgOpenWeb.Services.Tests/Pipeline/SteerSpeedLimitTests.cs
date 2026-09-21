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
/// #106: Min / Max steer speed were read by nothing. Like AgOpenGPS: above max disengages at
/// once; below min for ~8 s disengages; neither applies on the simulator; 0 = off.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SteerSpeedLimitTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
    private List<GpsCycleResult> _results = null!;
    private long _now;

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

    private void Drive(double kmh)
    {
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Speed = kmh / 3.6 },
            FixQuality = 4,
            IsValid = true,
        });
    }

    private void Engage(double min, double max)
    {
        var a = ConfigurationStore.Instance.AutoSteer;
        a.MinSteerSpeed = min;
        a.MaxSteerSpeed = max;
        _now = 0;
        _pipeline.NowMs = () => _now;
        _pipeline.SetAutoSteerEngaged(true);
    }

    [Test]
    public void AboveMax_DisengagesImmediately()
    {
        Engage(min: 0, max: 15);

        Drive(10);
        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
        Drive(16);
        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.True);
        Assert.That(Last.DisengageReason, Does.Contain("maximum"));
    }

    [Test]
    public void BelowMin_DisengagesOnlyAfterTheGracePeriod()
    {
        Engage(min: 3, max: 0);

        Drive(1);
        _now = 7000; Drive(1);
        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False, "engage at a standstill, then pull away");

        _now = 8100; Drive(1);
        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.True);
        Assert.That(Last.DisengageReason, Does.Contain("minimum"));
    }

    [Test]
    public void BelowMin_TimerResetsWhenSpeedRecovers()
    {
        Engage(min: 3, max: 0);

        Drive(1);
        _now = 6000; Drive(8);      // back above min
        _now = 7000; Drive(1);      // dips again — new grace period starts here
        _now = 12000; Drive(1);
        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
    }

    [Test]
    public void Simulator_IsExempt_LikeAgOpenGps()
    {
        Engage(min: 0, max: 15);
        _appState.Simulator.IsEnabled = true;

        Drive(30);

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
    }

    [Test]
    public void ZeroLimits_AreOff()
    {
        Engage(min: 0, max: 0);

        Drive(60);
        _now = 60000; Drive(0.1);

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
    }
}
