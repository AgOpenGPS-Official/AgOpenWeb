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
/// #104: a swapped A/B track is the same physical line driven the other way round, so
/// guidance toward it must not change — for both algorithms and for AB lines and curves.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SwapABGuidanceTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private ApplicationState _appState = null!;
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

    // A north-running line 0.5 m right of the vehicle (heading north, 3 m/s).
    private static List<Vec3> NorthLine(bool curve)
    {
        var pts = new List<Vec3>();
        int n = curve ? 21 : 2;
        for (int i = 0; i < n; i++) pts.Add(new Vec3(0.5, -100 + 200.0 * i / (n - 1), 0));
        return pts;
    }

    // What Swap A/B now produces: reversed order, headings turned 180°.
    private static List<Vec3> Swapped(List<Vec3> pts)
    {
        var r = new List<Vec3>();
        for (int i = pts.Count - 1; i >= 0; i--)
            r.Add(new Vec3(pts[i].Easting, pts[i].Northing, (pts[i].Heading + Math.PI) % (2 * Math.PI)));
        return r;
    }

    private double Steer(List<Vec3> pts, bool stanley)
    {
        ConfigurationStore.Instance.Guidance.IsPurePursuit = !stanley;
        var track = new Models.Track.Track
        {
            Name = "T", Points = pts,
            Type = pts.Count == 2 ? Models.Track.TrackType.ABLine : Models.Track.TrackType.Curve,
        };
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0, isOnBoundary: false);
        _pipeline.SetAutoSteerEngaged(true);
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 3 },
            FixQuality = 4,
            IsValid = true,
        });
        return Last.Guidance!.SteerAngle;
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SwappedTrack_SteersTheSameWay(bool curve, bool stanley)
    {
        var line = NorthLine(curve);
        line = Models.Guidance.CurveProcessing.CalculateHeadings(line);

        double before = Steer(line, stanley);
        double after = Steer(Swapped(line), stanley);
        TestContext.Out.WriteLine($"curve={curve} stanley={stanley}: before={before:F2}° after={after:F2}°");

        Assert.That(Math.Abs(before), Is.GreaterThan(0.1), "0.5 m off the line must produce a steer command");
        Assert.That(after, Is.EqualTo(before).Within(0.5),
            "Swapping A/B must not change where guidance steers");
    }
}
