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
/// AgOpenGPS #1173 (RebuildAfterNudge): a U-turn planned but not yet driven was built
/// for the pre-nudge line, so nudging (or resetting the nudge) must drop it for a
/// re-plan, including the snake / alternate target pass. A turn already being driven
/// is left alone, like a snap mid-turn (#50).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class YouTurnNudgeReplanTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
    private PipelineIntents _intents = null!;

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

        _intents = new PipelineIntents();
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
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance, _appState,
            config,
            new PositionEstimator());

        _pipeline.SynchronousMode = true;
        _pipeline.Start();

    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private YouTurnWorkingState GetCycleYouTurn()
    {
        var fld = typeof(GpsPipelineService).GetField("_youTurn",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(fld, Is.Not.Null, "Reflection target _youTurn missing — pipeline internals changed");
        return (YouTurnWorkingState)fld!.GetValue(_pipeline)!;
    }

    private static GpsData ValidFix() => new()
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
        FixQuality = 4,
        IsValid = true,
    };


    private YouTurnWorkingState SeedPlannedTurn(bool executing)
    {
        var youTurn = GetCycleYouTurn();
        youTurn.TurnPath = new List<Vec3> { new(0, 40, 0), new(3, 43, 0), new(6, 40, 0) };
        youTurn.NextTrack = Models.Track.Track.FromABLine("next", new Vec3(6, -100, 0), new Vec3(6, 100, 0));
        youTurn.ReturnPassTargetPath = 4;
        youTurn.IsTriggered = executing;
        youTurn.IsExecuting = executing;
        _pipeline.SetAutoSteerEngaged(true); // the disengage clear would hide the result
        return youTurn;
    }

    [Test]
    public void Without_a_nudge_the_planned_turn_is_kept()
    {
        var youTurn = SeedPlannedTurn(executing: false);
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(youTurn.TurnPath, Is.Not.Null, "control: the cycle itself mustn't clear it");
        Assert.That(youTurn.ReturnPassTargetPath, Is.EqualTo(4));
    }

    [Test]
    public void Nudge_drops_a_planned_turn()
    {
        var youTurn = SeedPlannedTurn(executing: false);
        _intents.RequestGuidanceNudge(0.1);
        _gpsService.UpdateGpsData(ValidFix());
        Assert.Multiple(() =>
        {
            Assert.That(youTurn.TurnPath, Is.Null);
            Assert.That(youTurn.NextTrack, Is.Null);
            Assert.That(youTurn.ReturnPassTargetPath, Is.Null);
        });
    }

    [Test]
    public void Nudge_reset_drops_a_planned_turn()
    {
        var youTurn = SeedPlannedTurn(executing: false);
        _intents.RequestGuidanceResetNudge();
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(youTurn.TurnPath, Is.Null);
        Assert.That(youTurn.ReturnPassTargetPath, Is.Null);
    }

    [Test]
    public void Nudge_mid_turn_leaves_the_executing_turn_alone()
    {
        var youTurn = SeedPlannedTurn(executing: true);
        _intents.RequestGuidanceNudge(0.1);
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(youTurn.IsExecuting, Is.True);
        Assert.That(youTurn.TurnPath, Is.Not.Null);
        Assert.That(youTurn.ReturnPassTargetPath, Is.EqualTo(4));
    }
}
