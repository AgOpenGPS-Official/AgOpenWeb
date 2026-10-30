// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
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
/// #450: a curve's extra guidelines are built on the host from the reference, like the pass,
/// so they keep its shape: on a boundary curve they round off at the corners and close at the
/// seam. The map used to move the pass's points sideways, which pinched them into spikes at
/// corners and folded them over on tight bends.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CurveGuideLinesTests
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
        config.Display.ExtraGuidelines = true;
        config.Display.ExtraGuidelinesCount = 3;

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

    // A 100 m square boundary curve round the origin (clockwise: right is inward), the tractor
    // at the origin heading north, guiding.
    private GuidanceSnapshot Cycle(Models.Track.Track track, int pass)
    {
        _pipeline.SetActiveTrack(track, passNumber: pass, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        for (int i = 0; i < 3; i++)
            _gpsService.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 3 },
                FixQuality = 4,
                IsValid = true,
            });
        return _results[^1].Guidance!;
    }

    private static Models.Track.Track BoundaryCurve() => new()
    {
        Name = "Boundary Curve",
        Points = AgOpenWeb.Models.Guidance.CurveProcessing.DensifyLoop(new List<Vec3>
            { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) }),
        Type = Models.Track.TrackType.Curve,
        IsClosed = true,
    };

    private static double ToSides(Vec3 p) =>
        Math.Min(Math.Min(Math.Abs(p.Easting + 50), Math.Abs(p.Easting - 50)), Math.Min(Math.Abs(p.Northing + 50), Math.Abs(p.Northing - 50)));

    [Test]
    public void Guidelines_of_a_boundary_curve_round_off_close_and_alternate_the_tool_offset()
    {
        var track = BoundaryCurve();
        var g = Cycle(track, pass: 4);
        var lines = g.GuideLines!;
        Assert.That(lines, Has.Count.EqualTo(6), "three each side");

        // Where each line crosses the west side's normal at y = 0, from the fence.
        var across = lines.Select(l => l.Where(p => p.Easting < 0 && Math.Abs(p.Northing) < 0.6).Min(p => p.Easting) + 50).OrderBy(x => x).ToList();
        var steps = across.Zip(across.Skip(1), (a, b) => Math.Round(b - a, 1)).ToList();

        Assert.Multiple(() =>
        {
            foreach (var l in lines)
            {
                Assert.That(l[0].Easting, Is.EqualTo(l[^1].Easting).Within(1e-9), "closed at the seam");
                Assert.That(l[0].Northing, Is.EqualTo(l[^1].Northing).Within(1e-9), "closed at the seam");
                double d = l.Where(p => p.Easting < 0 && Math.Abs(p.Northing) < 0.6).Min(p => p.Easting) + 50;
                Assert.That(l.Min(ToSides), Is.GreaterThan(d - 0.6), "no spike toward a corner");
            }
            // 6 m passes; the tool offset (0.8 m) moves to the other side on every other pass,
            // so neighbouring lines are 6 - 1.6 or 6 + 1.6 apart, and 12 across the pass shown.
            foreach (double s in steps) Assert.That(new[] { 4.4, 7.6, 12.0 }, Does.Contain(s), string.Join(", ", steps));
        });
    }

    [Test]
    public void Guidelines_are_built_once_per_pass()
    {
        var track = BoundaryCurve();
        var first = Cycle(track, pass: 4).GuideLines;
        var again = Cycle(track, pass: 4).GuideLines;
        Assert.That(again, Is.SameAs(first));
    }

    [Test]
    public void An_AB_line_has_no_host_guidelines()
    {
        var ab = new Models.Track.Track { Name = "AB", Points = new List<Vec3> { new(0, -100, 0), new(0, 100, 0) }, Type = Models.Track.TrackType.ABLine };
        Assert.That(Cycle(ab, pass: 0).GuideLines, Is.Null);
    }
}
