// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
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
/// The host loop's settings reach the GPS cycle as inputs it takes at its start, under the state
/// lock, and never change the cycle's own state while a cycle runs (Plans/
/// MAINVIEWMODEL_REFACTOR_PLAN.md item 8: each piece of the cycle's state has one owner). A track
/// chosen or AutoSteer switched off from the host loop used to write the cycle's guidance state
/// directly: arriving during a cycle, the reset was undone by that cycle's guidance, and the next
/// line was steered from the old one's memory. The tests reach the middle of a cycle through
/// <see cref="GpsPipelineService.PoseEstimatorUpdated"/>, raised between the pose and the guidance.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CycleInputsTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;
    private Action? _midCycle;

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
        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();

        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
        _pipeline.PoseEstimatorUpdated += _ => { var a = _midCycle; _midCycle = null; a?.Invoke(); };
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private GpsCycleResult Last => _results[^1];

    // At the field origin, heading north at 2 m/s.
    private void Fix() => _gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 2 },
        FixQuality = 4,
        IsValid = true,
    });

    private static Models.Track.Track NorthLine(string name, double easting) =>
        Models.Track.Track.FromABLine(name, new Vec3(easting, -100, 0), new Vec3(easting, 100, 0));

    private object? SteeringMemory() => _pipeline.Guidance.SteeringMemory;

    [Test]
    public void AutoSteer_switched_off_during_a_cycle_still_clears_the_steering_memory()
    {
        _pipeline.SetActiveTrack(NorthLine("A", -3), passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        Fix();
        Assert.That(SteeringMemory(), Is.Not.Null, "engaged on a line, the guidance keeps its memory");

        _midCycle = () => _pipeline.SetAutoSteerEngaged(false);
        Fix(); // off from the host loop while this cycle steers
        Fix(); // the next cycle, AutoSteer off

        Assert.That(SteeringMemory(), Is.Null,
            "the next engage must search the line afresh; the memory kept from before was the old steering's");
    }

    [Test]
    public void A_track_chosen_during_a_cycle_is_steered_afresh()
    {
        _pipeline.SetActiveTrack(NorthLine("A", -3), passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        Fix();

        _midCycle = () => _pipeline.SetActiveTrack(NorthLine("B", 20), passNumber: 0, nudgeOffset: 0);
        Fix(); // track B chosen while this cycle steers on A

        object? memoryAtB = "not read";
        _midCycle = () => memoryAtB = SteeringMemory();
        Fix(); // B's first cycle: read before its guidance runs

        Assert.That(memoryAtB, Is.Null, "B's first cycle steered from A's nearest point and integral");
    }

#if DEBUG
    [Test]
    public void A_cycle_entered_while_another_runs_does_not_run()
    {
        _midCycle = Fix; // a second fix fed from inside the first cycle (synchronous mode)
        Fix();

        Assert.That(_results, Has.Count.EqualTo(1),
            "the cycle's state has one writer at a time; the nested cycle is refused (debug and test builds)");
    }
#endif

    [Test]
    public void A_cycle_reports_its_own_track_with_its_own_pass()
    {
        var a = NorthLine("A", -3);
        var b = NorthLine("B", 20);
        _pipeline.SetActiveTrack(a, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        Fix();

        _midCycle = () => _pipeline.SetActiveTrack(b, passNumber: 3, nudgeOffset: 0);
        Fix();
        var during = Last.Guidance!;
        Fix();
        var after = Last.Guidance!;

        Assert.Multiple(() =>
        {
            Assert.That(during.ActiveTrack, Is.SameAs(a));
            Assert.That(during.HowManyPathsAway, Is.EqualTo(0), "the cycle steered A: it reports A's pass, not B's");
            Assert.That(after.ActiveTrack, Is.SameAs(b));
            Assert.That(after.HowManyPathsAway, Is.EqualTo(3));
        });
    }
}
