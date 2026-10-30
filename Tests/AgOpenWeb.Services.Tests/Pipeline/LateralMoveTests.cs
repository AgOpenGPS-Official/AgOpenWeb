// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
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
/// A lateral move (the on-screen lateral buttons, the nudge flyout's Pass buttons) goes to the
/// next pass and keeps the nudge, as AgOpenGPS's BuildManualYouLateral only changes the pass. It
/// used to zero the nudge, so a line nudged onto the crop lost that on the next pass.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class LateralMoveTests
{
    private readonly PipelineIntents _intents = new();
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
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
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _pipeline.SetLocalPlane(new LocalPlane( new Wgs84(43.7128, -74.006), new SharedFieldProperties()));
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

    private GuidanceSnapshot Cycle()
    {
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 2 },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1].Guidance!;
    }

    [Test]
    public void Moving_a_pass_keeps_the_nudge()
    {
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        Cycle();
        _intents.RequestGuidanceNudge(0.3);
        var nudged = Cycle();

        _intents.RequestGuidanceSnap(left: false);
        var moved = Cycle();

        Assert.Multiple(() =>
        {
            Assert.That(moved.HowManyPathsAway, Is.EqualTo(nudged.HowManyPathsAway + 1));
            Assert.That(moved.NudgeOffset, Is.EqualTo(nudged.NudgeOffset).Within(1e-9));
            Assert.That(moved.NudgeOffset, Is.EqualTo(0.3).Within(1e-9));
        });
    }

    // The pass label, as AgOpenGPS's DrawTrackInfo: an arrow for the way you drive along the line
    // (↑ A→B, ↓ B→A) and the pass, counted from 1 either side of the reference line.
    [TestCase(0.0, 0, "↑1R")]
    [TestCase(0.0, 2, "↑3R")]
    [TestCase(0.0, -1, "↑1L")]
    [TestCase(180.0, 0, "↓1R")]
    public void Pass_label_shows_the_way_you_drive_and_the_pass(double headingDeg, int pass, string label)
    {
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _pipeline.SetActiveTrack(track, passNumber: pass, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        GuidanceSnapshot g = null!;
        for (int i = 0; i < 3; i++)
        {
            _gpsService.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = headingDeg, Speed = 2 },
                FixQuality = 4,
                IsValid = true,
            });
            g = _results[^1].Guidance!;
        }
        Assert.That(g.CurrentLineLabel, Is.EqualTo(label));
    }
}
