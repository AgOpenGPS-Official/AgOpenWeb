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
/// #95: the cycle exposes the Pure Pursuit goal point in free-drive (autosteer off) so
/// the operator can see where the steering would aim before engaging — without
/// disturbing the steering state the engaged path carries between cycles.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class IndividualRowsCycleTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
    private List<GpsCycleResult> _results = null!;
    private PipelineIntents intents = null!;

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
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        intents=new PipelineIntents();
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
            intents,
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

    private static Models.Track.Track Row(double offset) => new()
    {
        Name="Row",IsIndividualRow=true,NoPassOffset=true,Type=Models.Track.TrackType.Curve,
        Points=Enumerable.Range(0,121).Select(i=>new Vec3(offset+.08*Math.Sin(i*.05),i*.25,0)).ToList()
    };
    private void Fix(double e=.75,double n=5,bool valid=true)=>_gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition=new Position {Latitude=43.7128,Longitude=-74.006,Easting=e,Northing=n,Heading=0,Speed=0},
        FixQuality=valid?4:0,IsValid=valid
    });
    private Models.Track.Track EnableRows()
    {
        var first=Row(0);var second=Row(.75);
        intents.RequestIndividualRows(new([first,second],false,.5,1));
        _pipeline.SetHasActiveField(true);
        _pipeline.SetActiveTrack(first,42,12,false);
        return second;
    }
    [Test] public void ActualSecondRowIsDisplayedWithoutPassOffsetOrEndExtension()
    {
        var row=EnableRows();Fix();
        var g=Last.Guidance!;
        Assert.Multiple(()=>{Assert.That(g.ActiveTrack,Is.SameAs(row));Assert.That(g.HowManyPathsAway,Is.Zero);
            Assert.That(g.NudgeOffset,Is.Zero);Assert.That(g.DisplayTrack!.Points,Is.EqualTo(row.Points));});
        intents.RequestGuidanceSnap(true);intents.RequestGuidanceNudge(1);intents.RequestManualYouTurn(true);Fix();
        Assert.That(Last.Guidance!.DisplayTrack!.Points,Is.EqualTo(row.Points));
        Assert.That(Last.Guidance.HowManyPathsAway,Is.Zero);
    }
    [TestCase(31)] [TestCase(-1)] public void SteeringStopsBeyondTheRecordedEndpoints(double outside)
    {
        EnableRows();Fix();_pipeline.SetAutoSteerEngaged(true);Fix(n:outside);
        Assert.That(Last.AutoSteerDisengagedThisCycle,Is.True);
        Assert.That(Last.Guidance!.HasGuidance,Is.False);
        Assert.That(Last.DisengageReason,Does.Contain("saved row"));
    }
    [Test] public void InvalidFixCannotCaptureASavedRow()
    {
        EnableRows();Fix(valid:false);
        Assert.That(Last.Guidance!.ActiveTrack,Is.Null);
        Assert.That(Last.Guidance.DisplayTrack,Is.Null);
    }
}
