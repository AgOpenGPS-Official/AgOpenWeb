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
/// #95: the cycle exposes the Pure Pursuit goal point in free-drive (autosteer off) so
/// the operator can see where the steering would aim before engaging — without
/// disturbing the steering state the engaged path carries between cycles.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class DisplayGoalPointTests
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

    // Vehicle at the field origin (0,0), heading north, stationary → look-ahead = hold (4 m).
    private static GpsData FixAtOrigin() => new()
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 0 },
        FixQuality = 4,
        IsValid = true,
    };

    private void SetNorthLine(double easting = 0) =>
        _pipeline.SetActiveTrack(
            Models.Track.Track.FromABLine("AB", new Vec3(easting, -100, 0), new Vec3(easting, 100, 0)),
            passNumber: 0, nudgeOffset: 0, isOnBoundary: false);

    [Test]
    public void FreeDrive_WithTrack_ReportsGoalAheadOnTheLine()
    {
        _pipeline.SetAutoSteerEngaged(false);
        SetNorthLine(easting: 1.0); // line 1 m right of the vehicle (within the pass)

        _gpsService.UpdateGpsData(FixAtOrigin());

        var g = Last.Guidance!;
        double hold = ConfigurationStore.Instance.Guidance.GoalPointLookAheadHold;
        Assert.Multiple(() =>
        {
            Assert.That(g.HasGoalPoint, Is.True, "Free-drive with an active track must report a goal");
            Assert.That(g.GoalPoint.Easting, Is.EqualTo(1.0).Within(0.01), "Goal lies on the line");
            Assert.That(g.GoalPoint.Northing, Is.EqualTo(hold).Within(0.01), "Goal is look-ahead ahead of the pivot");
        });
    }

    [Test]
    public void FreeDrive_WithoutTrack_ReportsNoGoal()
    {
        _pipeline.SetAutoSteerEngaged(false);

        _gpsService.UpdateGpsData(FixAtOrigin());

        Assert.That(Last.Guidance!.HasGoalPoint, Is.False);
    }

    [Test]
    public void FreeDrive_GoalDoesNotSeedEngagedSteeringState()
    {
        // The engaged path treats a null _trackGuidanceState as "fresh acquire" (global
        // nearest search, zero PP integral). The display goal must not populate it.
        _pipeline.SetAutoSteerEngaged(false);
        SetNorthLine();

        for (int i = 0; i < 3; i++) _gpsService.UpdateGpsData(FixAtOrigin());

        var fld = typeof(GpsPipelineService).GetField("_trackGuidanceState",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(fld, Is.Not.Null, "Reflection target _trackGuidanceState missing — pipeline internals changed");
        Assert.That(Last.Guidance!.HasGoalPoint, Is.True);
        Assert.That(fld!.GetValue(_pipeline), Is.Null,
            "Display-only goal must leave the engaged steering state untouched");
    }

    [Test]
    public void Snapshot_CarriesTheActiveTrack()
    {
        // #107: lets the VM tell a mirror of the current track's pass/nudge from a stale one.
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0, isOnBoundary: false);

        _gpsService.UpdateGpsData(FixAtOrigin());

        Assert.That(Last.Guidance!.ActiveTrack, Is.SameAs(track));
    }
}
