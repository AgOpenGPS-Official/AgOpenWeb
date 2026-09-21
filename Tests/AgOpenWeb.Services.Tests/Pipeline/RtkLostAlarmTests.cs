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
/// #106: the "RTK lost alarm" and "Alarm stops AutoSteer" settings were ignored — the sound
/// played regardless and autosteer never stopped. Now they behave like AgOpenGPS
/// (isRTK_AlarmOn / isRTK_KillAutosteer).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class RtkLostAlarmTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
    private List<GpsCycleResult> _results = null!;
    private IAudioService _audio = null!;

    [SetUp]
    public void SetUp()
    {
        _audio = Substitute.For<IAudioService>();
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
            _audio,
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

    private void Fix(int quality) => _gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
        FixQuality = quality,
        IsValid = true,
    });

    private void EngagedOnRtk(bool alarm, int action)
    {
        var con = ConfigurationStore.Instance.Connections;
        con.RtkLostAlarm = alarm;
        con.RtkLostAction = action;
        _pipeline.SetAutoSteerEngaged(true);
        Fix(4);
        _audio.ClearReceivedCalls();
    }

    [Test]
    public void AlarmOn_StopsAutoSteer_WhenSet()
    {
        EngagedOnRtk(alarm: true, action: 1);

        Fix(1); // lost RTK

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.True);
        Assert.That(Last.DisengageReason, Does.Contain("RTK"));
        _audio.Received(1).Play(SoundEffect.RtkLost);
    }

    [Test]
    public void AlarmOn_WarnOnly_KeepsSteering()
    {
        EngagedOnRtk(alarm: true, action: 0);

        Fix(1);

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
        _audio.Received(1).Play(SoundEffect.RtkLost);
    }

    [Test]
    public void AlarmOff_NoSound_NoDisengage()
    {
        EngagedOnRtk(alarm: false, action: 1);

        Fix(1);

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.False);
        _audio.DidNotReceive().Play(SoundEffect.RtkLost);
    }

    [Test]
    public void FloatCountsAsLost_LikeAgOpenGps()
    {
        EngagedOnRtk(alarm: true, action: 1);

        Fix(5); // RTK float

        Assert.That(Last.AutoSteerDisengagedThisCycle, Is.True);
    }

    [Test]
    public void RtkRecovered_PlaysOnce()
    {
        EngagedOnRtk(alarm: true, action: 0);
        Fix(1);

        Fix(4);

        _audio.Received(1).Play(SoundEffect.RtkRecovered);
    }
}
