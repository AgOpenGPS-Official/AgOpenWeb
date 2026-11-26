// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
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
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// AutoSteer at the seam of a boundary curve's lap, through the whole cycle. The lap ran
/// on 0.2 m past its seam and back (<see cref="CurveProcessing.CreateOffsetCurveWithInfo"/>), so a
/// tractor engaged a little off the line there was steered to a goal behind it and turned round.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ClosedPassSeamPipelineTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
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
        config.Vehicle.Wheelbase = Wheelbase;
        config.Tool.Width = 8;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 800);
        config.Tool.Overlap = 0;
        config.Tool.IsToolFrontFixed = false;
        config.Tool.IsToolRearFixed = true;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        // A fixed tool's working point -1.5 m from the rear axle: the tractor's hitch there, the tool on it (#547).
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = System.Math.Abs(-1.5);
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 8;

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
                    new Geometry.PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(_origin, new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
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

    // A round field of 30 m radius, its fence clockwise from the west point (the seam): the boundary
    // curve's first lap (pass 0) runs 4 m in, round a 26 m circle; at the seam it heads north.
    private const double FieldRadius = 30;
    private const double LapRadius = 26;

    private void Open()
    {
        const int n = 120;
        var fence = Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new Vec3(-FieldRadius * Math.Cos(a), FieldRadius * Math.Sin(a), 0);
        }).ToList();
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        });
        _pipeline.SetActiveTrack(new Models.Track.Track
        {
            Name = "Boundary Curve",
            Points = CurveProcessing.DensifyLoop(fence),
            Type = Models.Track.TrackType.Curve,
            IsClosed = true,
        }, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude,
                Heading = headingRad * 180.0 / Math.PI, Speed = SpeedKmh / 3.6, // m/s
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    // AutoSteer turned on with the tractor <paramref name="along"/> metres past the seam and
    // <paramref name="off"/> metres outside the lap (- inside), heading along it: it drives on round
    // the lap (40 m) and stays on it, never turning round.
    [Test]
    public void AutoSteer_engaged_at_the_seam_drives_on_round_the_lap(
        [Values(-3.0, -1.0, 0.0, 0.5, 2.0)] double along, [Values(-0.4, 0.4)] double off)
    {
        Open();
        double a0 = along / LapRadius, r0 = LapRadius + off;
        double e = -r0 * Math.Cos(a0), n = r0 * Math.Sin(a0), h = a0, step = SpeedKmh / 3.6 * 0.1;
        double worstHeading = 0, worstOff = 0;
        int cycles = (int)(40 / step);
        for (int i = 0; i < cycles; i++)
        {
            var r = Cycle(e, n, h);
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.False, r.DisengageReason);
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
            double a = Math.Atan2(n, -e); // where round the lap: its heading there is a
            worstHeading = Math.Max(worstHeading, Math.Abs(Math.IEEERemainder(h - a, 2 * Math.PI)) * 180 / Math.PI);
            worstOff = Math.Max(worstOff, Math.Abs(Math.Sqrt(e * e + n * n) - LapRadius));
        }
        double gone = Math.IEEERemainder(Math.Atan2(n, -e) - a0, 2 * Math.PI) * LapRadius;
        TestContext.Out.WriteLine($"driven {gone:F1} m round the lap, worst {worstHeading:F0}° off it, {worstOff:F2} m off it");
        Assert.Multiple(() =>
        {
            Assert.That(gone, Is.GreaterThan(35), "on round the lap");
            Assert.That(worstHeading, Is.LessThan(30), "never turned round");
            Assert.That(worstOff, Is.LessThan(1.0));
        });
    }
}
