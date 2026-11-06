// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
using AgOpenWeb.Services.Gps;
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
/// AutoSteer engaged at a standstill (allowed: you pull away with it on), then the tractor
/// drives off with a dual heading offset 90° out. In the Vehicle &amp; Tool scenarios guidance
/// steered for a tractor driving across its line and took it out of the pass. Now the cycle
/// stops AutoSteer within a few metres and says why. Real GPS cycle, real heading fusion.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class DualHeadingAcrossTravelTests
{
    private const double Lat0 = 43.7128, Lon0 = -74.006;
    private const double MetresPerDegLat = 111_132.0;

    private GpsService _gps = null!;
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
        config.Connections.IsDualGps = true;
        config.Connections.DualHeadingOffset = 0;

        _gps = new GpsService();
        _gps.Start();
        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gps, config);

        _pipeline = new GpsPipelineService(
            _gps, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            new GpsHeadingFusionService(config),
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());
        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(Lat0, Lon0), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gps.Stop();
    }

    // Receiver heading 0 (antenna baseline north); the antenna moves north, or south when
    // `backwards`, 0.25 m per fix at 9 km/h. Returns the first disengage, or null.
    private GpsCycleResult? Drive(double metres, bool backwards = false)
    {
        _pipeline.SetAutoSteerEngaged(true);
        for (double n = 0; n <= metres; n += 0.25)
        {
            _gps.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position
                {
                    Latitude = Lat0 + (backwards ? -n : n) / MetresPerDegLat,
                    Longitude = Lon0,
                    Heading = 0,
                    Speed = 2.5,
                },
                FixQuality = 4,
                HasDualHeading = true,
                IsValid = true,
            });
            if (_results.LastOrDefault() is { AutoSteerDisengagedThisCycle: true } off) return off;
        }
        return null;
    }

    [Test]
    public void Offset_90_degrees_out_stops_AutoSteer_within_a_few_metres_and_says_why()
    {
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 90;

        var off = Drive(30);

        Assert.That(off, Is.Not.Null, "AutoSteer kept steering a tractor it saw driving across its line");
        Assert.That(off!.DisengageReason, Is.EqualTo(
            "AutoSteer disengaged - the dual antenna heading is 90° off the direction of travel: check its Heading offset (GPS tab)"));
        double travelled = (off.Latitude - Lat0) * MetresPerDegLat;
        Assert.That(travelled, Is.LessThan(6), "found within a few metres");
    }

    [Test]
    public void A_right_offset_keeps_AutoSteer_on()
    {
        Assert.That(Drive(30), Is.Null);
    }

    [Test]
    public void Reversing_keeps_AutoSteer_on()
    {
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 0;
        Assert.That(Drive(30, backwards: true), Is.Null);
        Assert.That(_results[^1].IsReverse, Is.True);
    }
}
