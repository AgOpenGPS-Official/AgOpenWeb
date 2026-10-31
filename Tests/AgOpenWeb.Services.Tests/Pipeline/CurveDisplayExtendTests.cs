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
/// #492: the current pass (the magenta line) of an open curve is extended past the curve's A and
/// B points like the extra guidelines and the steering line, whether AutoSteer is engaged or
/// not. In free-drive it used to stop at A and B.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CurveDisplayExtendTests
{
    private const double Lat = 43.7128, Lon = -74.006;
    private const double MetersPerDegLon = 111320.0 * 0.7237; // cos(43.7128°) ≈ 0.7237

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
        config.Tool.Offset = 0;
        config.Display.ExtraGuidelines = true;
        config.Display.ExtraGuidelinesCount = 2;

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

        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(Lat, Lon), new SharedFieldProperties()));
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

    // The tractor `east` m east of the origin, heading north. A next cycle on the same track passes
    // reselect: false, as the host does: the cycle keeps the pass it picked.
    private GuidanceSnapshot Cycle(Models.Track.Track track, int pass, bool autoSteer, double east = 0, bool reselect = true)
    {
        if (reselect) _pipeline.SetActiveTrack(track, passNumber: pass, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(autoSteer);
        for (int i = 0; i < 3; i++)
            _gpsService.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position { Latitude = Lat, Longitude = Lon + east / MetersPerDegLon, Heading = 0, Speed = 3 },
                FixQuality = 4,
                IsValid = true,
            });
        return _results[^1].Guidance!;
    }

    // An open curve of five points, 40 m long, running north through the origin.
    private static Models.Track.Track OpenCurve() => new()
    {
        Name = "Curve",
        Points = new List<Vec3> { new(0, -20, 0), new(0, -10, 0), new(0, 0, 0), new(0, 10, 0), new(0, 20, 0) },
        Type = Models.Track.TrackType.Curve,
    };

    private static void AssertExtended(IReadOnlyList<Vec3> points, string what)
    {
        Assert.That(points.Min(p => p.Northing), Is.LessThan(-25), what + ": past A");
        Assert.That(points.Max(p => p.Northing), Is.GreaterThan(25), what + ": past B");
    }

    // Pass 0 lies half a working width beside the reference line (PassGeometry.EdgeOffset), so
    // this is an offset pass. The line the tractor steers is the reference line itself when the tool
    // offset is that half width (3 m here): the tool runs on pass 0 with the tractor on the line.
    [TestCase(false)]
    [TestCase(true)]
    public void An_offset_pass_of_an_open_curve_is_extended_past_A_and_B(bool autoSteer)
    {
        var g = Cycle(OpenCurve(), pass: 0, autoSteer);

        Assert.That(g.DisplayTrack, Is.Not.Null);
        Assert.That(Math.Abs(g.DisplayTrack!.Points.Average(p => p.Easting)), Is.GreaterThan(2), "an offset pass");
        AssertExtended(g.DisplayTrack.Points, autoSteer ? "AutoSteer on" : "free-drive");
        Assert.That(g.BaseTrack, Is.Not.Null, "the reference is drawn beside an offset pass");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void The_reference_line_of_an_open_curve_is_extended_and_drawn_once(bool autoSteer)
    {
        ConfigurationStore.Instance.Tool.Offset = 3; // the line steered is the reference line: distance 0

        var g = Cycle(OpenCurve(), pass: 0, autoSteer);

        Assert.That(g.DisplayTrack, Is.Not.Null);
        Assert.That(Math.Abs(g.DisplayTrack!.Points.Average(p => p.Easting)), Is.LessThan(0.1), "on the reference");
        AssertExtended(g.DisplayTrack.Points, autoSteer ? "AutoSteer on" : "free-drive");
        Assert.That(g.BaseTrack, Is.Null, "the reference is the line: it must not be drawn twice");
    }

    [Test]
    public void An_offset_pass_is_kept_while_nothing_changes()
    {
        // The map rebuilds the line when the track object changes: a new one every cycle would
        // rebuild it at 10 Hz.
        var track = OpenCurve();
        var first = Cycle(track, pass: 0, autoSteer: false).DisplayTrack;
        var second = Cycle(track, pass: 0, autoSteer: false, reselect: false).DisplayTrack;

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void The_extended_reference_line_is_kept_while_nothing_changes()
    {
        ConfigurationStore.Instance.Tool.Offset = 3;
        var track = OpenCurve();
        var first = Cycle(track, pass: 0, autoSteer: false).DisplayTrack;
        var second = Cycle(track, pass: 0, autoSteer: false, reselect: false).DisplayTrack;

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void A_moved_curve_gets_its_new_line_not_the_cached_one()
    {
        // Same track object, new points (extend A/B, Ref Nudge): the line follows.
        var track = OpenCurve();
        Cycle(track, pass: 0, autoSteer: false); // the cycle picks pass -1, 3 m west of the reference

        track.Points = track.Points.Select(p => new Vec3(p.Easting + 3, p.Northing, p.Heading)).ToList();
        var g = Cycle(track, pass: -1, autoSteer: false); // the host selects it again with the pass in use

        Assert.That(g.DisplayTrack!.Points.Average(p => p.Easting), Is.GreaterThan(-0.5), "was at -3 before the move");
    }

    [Test]
    public void A_closed_curve_is_not_extended()
    {
        var loop = new Models.Track.Track
        {
            Name = "Loop",
            Points = AgOpenWeb.Models.Guidance.CurveProcessing.DensifyLoop(new List<Vec3>
                { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) }),
            Type = Models.Track.TrackType.Curve,
            IsClosed = true,
        };

        var g = Cycle(loop, pass: 0, autoSteer: false);

        Assert.That(g.DisplayTrack!.Points.Max(p => Math.Abs(p.Northing)), Is.LessThan(51));
    }
}
