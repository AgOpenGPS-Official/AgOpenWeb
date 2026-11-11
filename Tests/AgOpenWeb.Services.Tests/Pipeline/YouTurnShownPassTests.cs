// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
/// While a U-turn is driven, the pass shown (the highlighted line, the tool's band, the
/// guidelines, the pass label) is the one the turn lands on, as AgOpenGPS, which moves to the
/// next pass when the turn starts (CYouTurn.YouTurnTrigger). It used to stay on the pass just
/// worked until the turn completed, with the tractor already driving along the next one. The
/// pass worked (HowManyPathsAway) still moves on only when the turn completes, so a turn backed
/// out of keeps its pass (#588).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class YouTurnShownPassTests
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

    private YouTurnWorkingState CycleYouTurn()
    {
        var fld = typeof(GpsPipelineService).GetField("_youTurn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(fld, Is.Not.Null, "Reflection target _youTurn missing — pipeline internals changed");
        return (YouTurnWorkingState)fld!.GetValue(_pipeline)!;
    }

    // A line going north whose pass 0 is where the tractor stands (E = 0): passes are 6 m apart.
    private static Models.Track.Track Line() =>
        Models.Track.Track.FromABLine("AB", new Vec3(-3, -100, 0), new Vec3(-3, 100, 0));

    private void Fix() => _gpsService.UpdateGpsData(new GpsData
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006, Heading = 0, Speed = 2 },
        FixQuality = 4,
        IsValid = true,
    });

    // From just behind the tractor up the pass for 30 m, round to the right on a 6 m radius,
    // and 50 m back down pass 2, a point every metre.
    private static List<Vec3> TurnToPassTwo()
    {
        var path = new List<Vec3>();
        for (int n = -2; n < 30; n++) path.Add(new Vec3(0, n, 0));
        for (int i = 0; i <= 18; i++)
        {
            double a = System.Math.PI * i / 18;
            path.Add(new Vec3(6 - 6 * System.Math.Cos(a), 30 + 6 * System.Math.Sin(a), a));
        }
        for (int n = 29; n >= -20; n--) path.Add(new Vec3(12, n, System.Math.PI));
        return path;
    }

    /// <summary>One cycle on pass 0 with AutoSteer on and a turn to pass 2 (12 m to the right),
    /// planned or being driven.</summary>
    private GuidanceSnapshot CycleWithTurn(bool driven)
    {
        _pipeline.SetActiveTrack(Line(), passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        Fix(); // the cycle takes the track: a new track drops any turn
        var turn = CycleYouTurn();
        turn.TurnPath = TurnToPassTwo();
        turn.ReturnPassTargetPath = 2;
        turn.IsTriggered = driven;
        turn.IsExecuting = driven;
        Fix();
        Assert.That(turn.IsExecuting, Is.EqualTo(driven), "control: the cycle left the turn as seeded");
        return _results[^1].Guidance!;
    }

    [Test]
    public void A_turn_being_driven_shows_the_pass_it_lands_on()
    {
        var g = CycleWithTurn(driven: true);
        Assert.Multiple(() =>
        {
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(12).Within(1e-6), "the highlighted line");
            Assert.That(g.ToolPass![0].Easting, Is.EqualTo(12).Within(1e-6), "the tool's band");
            Assert.That(g.CurrentLineLabel, Is.EqualTo("↓3R"), "the label: pass 2, driven back down");
            Assert.That(g.HowManyPathsAway, Is.EqualTo(0), "the pass worked moves on when the turn completes");
        });
    }

    [Test]
    public void A_turn_planned_but_not_driven_yet_keeps_the_pass_worked_shown()
    {
        var g = CycleWithTurn(driven: false);
        Assert.Multiple(() =>
        {
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(0).Within(1e-6));
            Assert.That(g.ToolPass![0].Easting, Is.EqualTo(0).Within(1e-6));
            Assert.That(g.CurrentLineLabel, Is.EqualTo("↑1R"));
            Assert.That(g.HowManyPathsAway, Is.EqualTo(0));
        });
    }

    [Test]
    public void With_a_tool_offset_the_line_shown_is_the_one_steered_on_the_landing_pass()
    {
        // The tool 0.5 m to the right: going north the tractor steers 0.5 m left of the pass,
        // coming back down pass 2 it steers 0.5 m to the other side of it (#446).
        ConfigurationStore.Instance.Tool.Offset = 0.5;
        var g = CycleWithTurn(driven: true);
        Assert.Multiple(() =>
        {
            Assert.That(g.DisplayTrack!.Points[0].Easting, Is.EqualTo(12.5).Within(1e-6));
            Assert.That(g.ToolPass![0].Easting, Is.EqualTo(12).Within(1e-6), "the tool runs on the pass itself");
        });
    }

    [Test]
    public void The_guidelines_lie_either_side_of_the_landing_pass()
    {
        ConfigurationStore.Instance.Display.ExtraGuidelines = true;
        ConfigurationStore.Instance.Display.ExtraGuidelinesCount = 1;
        var g = CycleWithTurn(driven: true);
        var eastings = new List<double>();
        foreach (var line in g.GuideLines!) eastings.Add(System.Math.Round(line[0].Easting, 6));
        Assert.That(eastings, Is.EquivalentTo(new[] { 6.0, 18.0 }));
    }

    [Test]
    public void With_a_tool_offset_the_guidelines_lie_where_their_passes_are_steered()
    {
        // The tool 0.5 m to the right. The landing pass 2 is driven back down; its neighbours 1
        // and 3 are driven north, as pass 0 was: steered 0.5 m to the left of each pass.
        ConfigurationStore.Instance.Tool.Offset = 0.5;
        ConfigurationStore.Instance.Display.ExtraGuidelines = true;
        ConfigurationStore.Instance.Display.ExtraGuidelinesCount = 1;
        var g = CycleWithTurn(driven: true);
        var eastings = new List<double>();
        foreach (var line in g.GuideLines!) eastings.Add(System.Math.Round(line[0].Easting, 6));
        Assert.That(eastings, Is.EquivalentTo(new[] { 5.5, 17.5 }));
    }
}
