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
/// AgOpenGPS's pass model (CABLine.BuildCurrentABLineList): the reference line is the edge
/// between passes, so pass n lies (n + ½) × width to its right. A line drawn on the fence
/// gets its first pass half a width inside; a driven line (moved half a width at creation)
/// has a pass where the tractor drove; a boundary-follow track is driven on the line.
/// Tractor at E=0 heading north, 6 m tool, free-drive (nearest pass).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class PassModelTests
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

    private GuidanceSnapshot FreeDriveOn(Models.Track.Track track, int pass = 0)
    {
        _pipeline.SetActiveTrack(track, passNumber: pass, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(false);
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 0 },
            FixQuality = 4,
            IsValid = true,
        });
        return Last.Guidance!;
    }

    private static Models.Track.Track NorthLine(double easting) =>
        Models.Track.Track.FromABLine("AB", new Vec3(easting, -100, 0), new Vec3(easting, 100, 0));

    [Test]
    public void LineOnTheFence_FirstPassIsHalfAWidthInside()
    {
        var fence = NorthLine(-2); // tractor 2 m inside a west fence
        var g = FreeDriveOn(fence);
        Assert.Multiple(() =>
        {
            Assert.That(g.HowManyPathsAway, Is.EqualTo(0));
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(1).Within(1e-6), "pass 0 = fence + 3 m");
            Assert.That(g.BaseTrack, Is.SameAs(fence), "the reference line is drawn where it was put");
        });
    }

    [Test]
    public void DrivenLine_HasAPassWhereTheTractorDrove()
    {
        // Drive AB at E=0 with First pass Right moves the reference to the tool's right edge.
        var g = FreeDriveOn(NorthLine(3));
        Assert.Multiple(() =>
        {
            Assert.That(g.HowManyPathsAway, Is.EqualTo(-1));
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(0).Within(1e-6));
        });
    }

    [Test]
    public void BoundaryFollowTrack_IsDrivenOnTheLine()
    {
        var follow = NorthLine(1);
        follow.NoPassOffset = true;
        var g = FreeDriveOn(follow);
        Assert.Multiple(() =>
        {
            Assert.That(g.HowManyPathsAway, Is.EqualTo(0));
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(1).Within(1e-6));
        });
    }
}
