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
/// #106: the Headland on/off toggle only hid the line. Like AgOpenGPS (isHeadlandOn), off now
/// also disables headland section control, the hydraulic lift and the headland distance HUD.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HeadlandToggleTests
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

    private static List<Vec3> Square(double h) => new()
    {
        new(-h, -h, 0), new(h, -h, 0), new(h, h, 0), new(-h, h, 0),
    };

    private static Models.Boundary Boundary(double h)
    {
        var poly = new BoundaryPolygon();
        foreach (var p in Square(h)) poly.Points.Add(new BoundaryPoint(p.Easting, p.Northing, 0));
        poly.UpdateBounds();
        return new Models.Boundary { OuterBoundary = poly };
    }

    private void Cycle() => _gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
        FixQuality = 4,
        IsValid = true,
    });

    [TestCase(true)]
    [TestCase(false)]
    public void HeadlandDistanceHud_FollowsTheToggle(bool headlandOn)
    {
        _appState.FieldTools.IsHeadlandOn = headlandOn;
        _pipeline.SetBoundary(Boundary(200));
        _pipeline.SetHeadlandLine(Square(50));

        Cycle();

        Assert.That(Last.HeadlandProximityDistance.HasValue, Is.EqualTo(headlandOn));
    }

    [TestCase(true, 2)]   // tool in the headland band → raise
    [TestCase(false, 0)]  // headland off → lift off (AgOpenGPS)
    public void HydraulicLift_IsOffWithTheHeadland(bool headlandOn, int expected)
    {
        ConfigurationStore.Instance.Machine.HydraulicLiftEnabled = true;
        _appState.FieldTools.IsHeadlandOn = headlandOn;
        _appState.Field.CurrentBoundary = Boundary(200);

        var m = typeof(GpsPipelineService).GetMethod("ComputeHydLiftState",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(m, Is.Not.Null, "Reflection target ComputeHydLiftState missing");
        var state = (byte)m!.Invoke(_pipeline, new object?[] { new Vec3(0, 120, 0), 3.0, Square(100) })!;

        Assert.That(state, Is.EqualTo(expected));
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void HeadlandSectionControl_NeedsTheHeadlandOn(bool headlandOn, bool expectInHeadland)
    {
        var config = ConfigurationStore.Instance;
        config.Tool.IsHeadlandSectionControl = true;
        _appState.FieldTools.IsHeadlandOn = headlandOn;
        _appState.Field.CurrentBoundary = Boundary(200);
        _appState.Field.HeadlandLine = Square(100);
        var toolPosition = new ToolPositionService(config);
        var sections = new SectionControlService(toolPosition, new CoverageMapService(config), _appState, config);

        var m = typeof(SectionControlService).GetMethod("IsPointInHeadland", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(m, Is.Not.Null, "Reflection target IsPointInHeadland missing");
        var inHeadland = (bool)m!.Invoke(sections, new object[] { new Vec2(0, 150) })!;

        Assert.That(inHeadland, Is.EqualTo(expectInHeadland));
    }
}
