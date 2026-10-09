// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
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
        var state = (byte)m!.Invoke(_pipeline, new object?[] { new Vec3(0, 120, 0), 0.0, 3.0, Square(100) })!;

        Assert.That(state, Is.EqualTo(expected));
    }

    [TestCase(0.0, 1)]  // no look-ahead: tool still in the worked area → down
    [TestCase(2.0, 2)]  // 3 m/s × 2 s = 6 m ahead crosses the headland line → up early
    public void HydraulicLift_LooksAheadByMachineLookAhead(double lookAheadSec, int expected)
    {
        ConfigurationStore.Instance.Machine.HydraulicLiftEnabled = true;
        ConfigurationStore.Instance.Machine.LookAhead = lookAheadSec;
        _appState.FieldTools.IsHeadlandOn = true;
        _appState.Field.CurrentBoundary = Boundary(200);

        var m = typeof(GpsPipelineService).GetMethod("ComputeHydLiftState",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Tool 4 m short of the headland line (cultivated area = ±100), heading north at 3 m/s.
        var state = (byte)m.Invoke(_pipeline, new object?[] { new Vec3(0, 96, 0), 0.0, 3.0, Square(100) })!;

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

    /// <summary>
    /// The headland is everything outside the headland line, including past the outer
    /// boundary (AgOpenGPS CHead). A tool swinging over the boundary in a U-turn used to
    /// have its outer sections released by the headland gate there and, with "Off outside
    /// boundary" off, kept in bounds by the straddle rule: they painted a half-disc over
    /// the boundary at the turn's apex.
    /// </summary>
    [Test]
    public void HeadlandSectionControl_CountsOutsideTheBoundaryAsHeadland()
    {
        var config = ConfigurationStore.Instance;
        config.Tool.IsHeadlandSectionControl = true;
        _appState.FieldTools.IsHeadlandOn = true;
        _appState.Field.CurrentBoundary = Boundary(200);
        _appState.Field.HeadlandLine = Square(100);
        var sections = new SectionControlService(new ToolPositionService(config), new CoverageMapService(config), _appState, config);

        var m = typeof(SectionControlService).GetMethod("IsPointInHeadland", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.That((bool)m.Invoke(sections, new object[] { new Vec2(0, 150) })!, Is.True, "between the line and the boundary");
        Assert.That((bool)m.Invoke(sections, new object[] { new Vec2(0, 250) })!, Is.True, "past the boundary");
        Assert.That((bool)m.Invoke(sections, new object[] { new Vec2(0, 50) })!, Is.False, "inside the line");
    }

    /// <summary>
    /// The apex of a U-turn that reaches the boundary, replayed from a bug-report log
    /// (2026-10-09, Test1 field: headland 32 m, 16 m tool, 8 m turn radius, 1.94 m/s). The
    /// tool runs along the edge with its outer sections past it; with the headland on,
    /// every section stays off through the arc.
    /// </summary>
    [Test]
    public void ToolSwingingPastTheBoundary_KeepsSectionsOff_WithTheHeadlandOn()
    {
        var config = ConfigurationStore.Instance;
        config.Tool.Width = 16;
        config.NumSections = 16;
        for (int i = 0; i < 16; i++) config.Tool.SetSectionWidth(i, 100);
        config.Tool.IsHeadlandSectionControl = true;
        config.Tool.IsSectionOffWhenOut = false; // a straddling section counts as in bounds (#110)
        _appState.FieldTools.IsHeadlandOn = true;

        var outer = new List<Vec2> { new(-353.2, -31.1), new(688.7, -92.8), new(725.9, 443.5), new(-266.9, 607.5) };
        var poly = new BoundaryPolygon();
        foreach (var p in outer) poly.Points.Add(new BoundaryPoint(p.Easting, p.Northing, 0));
        poly.UpdateBounds();
        _appState.Field.CurrentBoundary = new Models.Boundary { OuterBoundary = poly };
        var offset = new AgOpenWeb.Services.Geometry.PolygonOffsetService();
        _appState.Field.HeadlandLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 32)!);

        var coverage = new CoverageMapService(config);
        coverage.SetFieldBounds(-400, 800, -150, 700);
        var sections = new SectionControlService(new ToolPositionService(config), coverage, _appState, config);
        sections.MasterState = SectionMasterState.Auto;
        sections.SetAllAuto();

        int maxOn = 0;
        foreach (var (e, n, toolHeading, vehicleHeadingDeg) in ApexFrames)
        {
            double vh = vehicleHeadingDeg * Math.PI / 180;
            for (int tick = 0; tick < 3; tick++) // 30 Hz log, 100 Hz control loop
                sections.Update(new Vec3(e, n, toolHeading), toolHeading, vh, 1.94);
            maxOn = Math.Max(maxOn, sections.SectionStates.Take(16).Count(st => st.IsOn));
        }

        Assert.That(maxOn, Is.Zero, "a section came on in the headland zone / past the boundary during the arc");
    }

    // tool easting, northing, tool heading (rad), vehicle heading (deg)
    private static readonly (double, double, double, double)[] ApexFrames =
    {
        (113.448, 533.152, 0.0367, 1.99),
        (113.475, 533.318, 0.0236, 1.15),
        (113.500, 533.485, 0.0104, 0.33),
        (113.532, 533.672, -0.0082, 359.41),
        (113.554, 533.844, 6.2604, 358.53),
        (113.574, 534.007, 6.2458, 357.61),
        (113.599, 534.194, 6.2259, 356.62),
        (113.615, 534.367, 6.2098, 355.69),
        (113.628, 534.532, 6.1940, 354.70),
        (113.638, 534.701, 6.1786, 353.73),
        (113.652, 534.890, 6.1577, 352.76),
        (113.657, 535.058, 6.1414, 351.73),
        (113.658, 535.228, 6.1257, 350.74),
        (113.663, 535.419, 6.1039, 349.69),
        (113.658, 535.591, 6.0874, 348.68),
        (113.651, 535.757, 6.0712, 347.65),
        (113.646, 535.945, 6.0496, 346.58),
        (113.633, 536.119, 6.0320, 345.54),
        (113.618, 536.285, 6.0154, 344.48),
        (113.600, 536.451, 5.9987, 343.38),
        (113.581, 536.646, 5.9752, 342.32),
        (113.557, 536.809, 5.9582, 341.23),
        (113.530, 536.975, 5.9408, 340.13),
        (113.502, 537.164, 5.9174, 339.00),
        (113.468, 537.332, 5.8992, 337.89),
        (113.432, 537.497, 5.8813, 336.75),
        (113.394, 537.683, 5.8576, 335.58),
        (113.350, 537.851, 5.8390, 334.47),
        (113.306, 538.010, 5.8208, 333.29),
        (113.256, 538.174, 5.8023, 332.10),
        (113.203, 538.361, 5.7772, 330.97),
        (113.150, 538.518, 5.7582, 329.77),
        (113.092, 538.677, 5.7395, 328.57),
        (113.029, 538.859, 5.7138, 327.34),
        (112.963, 539.019, 5.6942, 326.14),
        (112.896, 539.173, 5.6750, 324.92),
        (112.823, 539.349, 5.6493, 323.65),
        (112.747, 539.508, 5.6287, 322.43),
        (112.671, 539.657, 5.6092, 321.18),
        (112.591, 539.804, 5.5898, 319.91),
        (112.503, 539.978, 5.5623, 318.67),
        (112.418, 540.123, 5.5422, 317.38),
        (112.328, 540.268, 5.5217, 316.03),
        (112.226, 540.437, 5.4940, 314.76),
        (112.131, 540.578, 5.4730, 313.47),
        (112.033, 540.715, 5.4528, 312.16),
        (111.926, 540.872, 5.4253, 310.81),
        (111.818, 541.011, 5.4030, 309.50),
        (111.711, 541.143, 5.3819, 308.16),
        (111.600, 541.271, 5.3613, 306.78),
        (111.476, 541.422, 5.3323, 305.49),
        (111.363, 541.545, 5.3103, 304.10),
        (111.241, 541.668, 5.2892, 302.72),
        (111.108, 541.810, 5.2595, 301.32),
        (110.981, 541.928, 5.2373, 299.95),
        (110.855, 542.040, 5.2157, 298.58),
        (110.714, 542.170, 5.1867, 297.15),
        (110.577, 542.282, 5.1633, 295.77),
        (110.444, 542.384, 5.1414, 294.37),
        (110.307, 542.484, 5.1194, 292.92),
        (110.150, 542.604, 5.0890, 291.55),
        (110.012, 542.697, 5.0665, 290.07),
        (109.863, 542.789, 5.0438, 288.63),
        (109.698, 542.897, 5.0130, 287.20),
        (109.548, 542.982, 4.9897, 285.77),
        (109.398, 543.061, 4.9673, 284.34),
        (109.246, 543.137, 4.9445, 282.91),
        (109.074, 543.228, 4.9131, 281.43),
        (108.917, 543.296, 4.8908, 279.98),
        (108.758, 543.361, 4.8678, 278.50),
        (108.577, 543.438, 4.8369, 277.07),
        (108.415, 543.496, 4.8133, 275.62),
        (108.253, 543.547, 4.7906, 274.17),
        (108.069, 543.611, 4.7594, 272.69),
        (107.902, 543.654, 4.7369, 271.25),
        (107.735, 543.695, 4.7135, 269.81),
        (107.550, 543.743, 4.6833, 268.33),
        (107.377, 543.775, 4.6596, 266.93),
        (107.209, 543.803, 4.6363, 265.45),
        (107.038, 543.824, 4.6141, 263.98),
        (106.846, 543.852, 4.5841, 262.61),
        (106.676, 543.868, 4.5603, 261.19),
        (106.508, 543.877, 4.5384, 259.70),
        (106.310, 543.889, 4.5086, 258.34),
        (106.140, 543.892, 4.4851, 256.90),
        (105.968, 543.886, 4.4636, 255.47),
        (105.774, 543.884, 4.4345, 254.10),
        (105.601, 543.872, 4.4114, 252.73),
        (105.431, 543.855, 4.3895, 251.34),
        (105.261, 543.832, 4.3685, 249.99),
        (105.069, 543.812, 4.3394, 248.66),
        (104.904, 543.782, 4.3186, 247.28),
        (104.732, 543.747, 4.2972, 245.97),
        (104.543, 543.711, 4.2692, 244.65),
        (104.377, 543.668, 4.2485, 243.37),
        (104.216, 543.621, 4.2289, 242.07),
        (104.028, 543.571, 4.2015, 240.77),
        (103.864, 543.515, 4.1815, 239.58),
        (103.707, 543.458, 4.1616, 238.31),
        (103.549, 543.395, 4.1428, 237.07),
        (103.368, 543.326, 4.1173, 235.91),
        (103.215, 543.258, 4.0982, 234.69),
        (103.061, 543.183, 4.0800, 233.52),
        (102.886, 543.103, 4.0549, 232.39),
        (102.736, 543.023, 4.0367, 231.26),
        (102.590, 542.940, 4.0195, 230.15),
        (102.425, 542.849, 3.9965, 229.04),
        (102.278, 542.757, 3.9788, 227.99),
        (102.138, 542.666, 3.9618, 226.90),
        (101.999, 542.568, 3.9460, 225.85),
        (101.836, 542.461, 3.9232, 224.85),
        (101.704, 542.361, 3.9078, 223.85),
        (101.574, 542.258, 3.8929, 222.83),
        (101.417, 542.138, 3.8715, 221.88),
        (101.290, 542.030, 3.8566, 220.95),
        (101.165, 541.920, 3.8423, 220.04),
        (101.043, 541.808, 3.8283, 219.13),
        (100.899, 541.680, 3.8083, 218.23),
        (100.779, 541.560, 3.7948, 217.36),
        (100.664, 541.441, 3.7819, 216.50),
        (100.528, 541.306, 3.7637, 215.70),
        (100.416, 541.184, 3.7506, 214.87),
        (100.306, 541.057, 3.7384, 214.06),
        (100.177, 540.914, 3.7212, 213.27),
    };
}
