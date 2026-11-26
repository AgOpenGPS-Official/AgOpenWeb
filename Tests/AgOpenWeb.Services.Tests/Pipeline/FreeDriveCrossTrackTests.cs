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
/// The light bar works with AutoSteer off: in free-drive the cycle reports the pivot's
/// cross-track error to the pass the display line shows, as AgOpenGPS does, with the same
/// value and sign the engaged guidance gives. Before, it was 0 until AutoSteer was engaged.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FreeDriveCrossTrackTests
{
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
            new PipelineIntents(),
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

    private GpsCycleResult Last => _results[^1];

    // Vehicle at the field origin (0,0), stationary.
    private static GpsData FixAtOrigin(double heading) => new()
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = heading, Speed = 0 },
        FixQuality = 4,
        IsValid = true,
    };

    // A north line whose pass 0 runs at this easting: the reference is the edge between
    // passes, half the 6 m tool to its left (PassGeometry).
    private void SetNorthLine(double easting) =>
        _pipeline.SetActiveTrack(
            Models.Track.Track.FromABLine("AB", new Vec3(easting - 3, -100, 0), new Vec3(easting - 3, 100, 0)),
            passNumber: 0, nudgeOffset: 0);

    [Test]
    public void FreeDrive_ReportsTheDistanceToThePass()
    {
        _pipeline.SetAutoSteerEngaged(false);
        SetNorthLine(easting: 1.0); // pass 0 runs 1 m right of the vehicle

        _gpsService.UpdateGpsData(FixAtOrigin(heading: 0));

        Assert.That(Math.Abs(Last.Guidance!.CrossTrackError), Is.EqualTo(1.0).Within(0.01));
    }

    [TestCase(0.0, 1.0, TestName = "Heading A to B, line to the right")]
    [TestCase(0.0, -1.5, TestName = "Heading A to B, line to the left")]
    [TestCase(180.0, 1.0, TestName = "Heading B to A, line to the left")]
    [TestCase(180.0, -1.5, TestName = "Heading B to A, line to the right")]
    public void FreeDrive_MatchesTheEngagedGuidance(double heading, double lineEasting)
    {
        _pipeline.SetAutoSteerEngaged(false);
        SetNorthLine(lineEasting);
        _gpsService.UpdateGpsData(FixAtOrigin(heading));
        double freeDrive = Last.Guidance!.CrossTrackError;

        _pipeline.SetAutoSteerEngaged(true);
        _gpsService.UpdateGpsData(FixAtOrigin(heading));
        double engaged = Last.Guidance!.CrossTrackError;

        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(engaged), Is.EqualTo(Math.Abs(lineEasting)).Within(0.01), "engaged guidance");
            Assert.That(freeDrive, Is.EqualTo(engaged).Within(0.01), "free-drive must match engaged, sign included");
        });
    }
}
