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
/// #446: with a tool offset, AB lines and curves steer the tractor so the TOOL runs on the
/// pass, beside it on one side going one way and on the other coming back (AgOpenGPS).
/// Steering the tractor onto the pass gave 2 × offset of overlap and gap on alternate passes.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ToolOffsetGuidanceTests
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
        config.Tool.Offset = 0.8; // tool 0.8 m right of the tractor

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

        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    // A north-running reference line at easting x; pass 0 is half the 6 m tool to its right.
    private static List<Vec3> NorthLine(double x, bool curve)
    {
        var pts = new List<Vec3>();
        int n = curve ? 21 : 2;
        for (int i = 0; i < n; i++) pts.Add(new Vec3(x, -100 + 200.0 * i / (n - 1), 0));
        return pts;
    }

    // Cross-track error of a tractor at the origin, heading north or south, on pass 0.
    private double Xte(List<Vec3> pts, double headingDeg)
    {
        var track = new Models.Track.Track
        {
            Name = "T", Points = pts,
            Type = pts.Count == 2 ? Models.Track.TrackType.ABLine : Models.Track.TrackType.Curve,
        };
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        for (int i = 0; i < 3; i++) // the first cycle learns the direction of travel
            _gpsService.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = headingDeg, Speed = 3 },
                FixQuality = 4,
                IsValid = true,
            });
        return _results[^1].Guidance!.CrossTrackError;
    }

    // Pass 0's centre is 3 m right of the line (east). Going north the tool (right of the tractor)
    // is east of it, so the tractor steers 0.8 m west of the pass centre: a line at -2.2 puts that
    // on the origin. Going south the tool is west of the tractor: the tractor steers 0.8 m east of
    // the pass centre, which a line at -3.8 puts on the origin.
    [TestCase(false, 0.0, -2.2, TestName = "AB_North_TractorBesideThePass")]
    [TestCase(false, 180.0, -3.8, TestName = "AB_South_TractorOnTheOtherSide")]
    [TestCase(true, 0.0, -2.2, TestName = "Curve_North_TractorBesideThePass")]
    [TestCase(true, 180.0, -3.8, TestName = "Curve_South_TractorOnTheOtherSide")]
    public void Tractor_steers_so_the_tool_runs_on_the_pass(bool curve, double headingDeg, double lineX)
    {
        Assert.That(Xte(NorthLine(lineX, curve), headingDeg), Is.EqualTo(0).Within(0.05));
    }
}
