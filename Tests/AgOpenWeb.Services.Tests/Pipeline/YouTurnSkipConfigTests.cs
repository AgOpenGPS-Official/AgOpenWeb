// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
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
/// A snake sequence (skip-worked mode) is built for one skip pattern. Changing the pattern
/// drops it on the next cycle so the next turn rebuilds it; the ViewModel used to clear the
/// UI mirror instead, which the next snapshot overwrote, so the stale sequence survived.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class YouTurnSkipConfigTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private YouTurnWorkingState _youTurn = null!;
    private GpsCycleResult? _last;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);

        _gpsService = new GpsService();
        _gpsService.Start();

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
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
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());
        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
        _pipeline.CycleCompleted += r => _last = r;

        // The pattern the sequence belongs to; the first push differs from the defaults, so
        // one cycle consumes the reset it schedules before the sequence is planted.
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 1, isSkipWorkedMode: true, headlandCalculatedWidth: 0, headlandDistance: 0);
        Cycle();

        // The cycle-owned working state; a sequence is planted as if a turn had built it.
        var field = typeof(GpsPipelineService).GetField("_youTurn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, "Reflection target _youTurn missing");
        _youTurn = (YouTurnWorkingState)field!.GetValue(_pipeline)!;
        _youTurn.SnakeSequence = new List<int> { 1, 3, 5 };
        _youTurn.SnakeIndex = 1;
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private void Cycle() => _gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
        FixQuality = 4,
        IsValid = true,
    });

    [Test]
    public void SameConfig_KeepsTheSequence()
    {
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 1, isSkipWorkedMode: true, headlandCalculatedWidth: 0, headlandDistance: 0);
        Cycle();

        Assert.That(_last!.YouTurn!.SnakeSequence, Is.EqualTo(new[] { 1, 3, 5 }));
        Assert.That(_last.YouTurn.SnakeIndex, Is.EqualTo(1));
    }

    [Test]
    public void NewSkipCount_DropsTheSequenceOnTheNextCycle()
    {
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 2, isSkipWorkedMode: true, headlandCalculatedWidth: 0, headlandDistance: 0);
        Cycle();

        Assert.That(_last!.YouTurn!.SnakeSequence, Is.Null, "rebuilt by the next turn for the new pattern");
        Assert.That(_last.YouTurn.SnakeIndex, Is.EqualTo(-1));
    }

    [Test]
    public void LeavingSkipWorkedMode_DropsTheSequence()
    {
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 1, isSkipWorkedMode: false, headlandCalculatedWidth: 0, headlandDistance: 0);
        Cycle();

        Assert.That(_last!.YouTurn!.SnakeSequence, Is.Null);
    }
}
