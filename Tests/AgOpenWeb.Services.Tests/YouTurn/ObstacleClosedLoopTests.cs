// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn obstacle (a ditch across the pass) driven in closed loop: the real GPS cycle
/// (<see cref="GpsPipelineService"/>) at 10 Hz steers a simple tractor (a bicycle model with a
/// steering rate limit) with the angle it asks for, and an operator does the things that lose a
/// turn: aborts it, switches U-turn or AutoSteer off and on, asks for a manual turn too close,
/// snaps or nudges, engages late, changes the obstacle's choice while driving. Whatever happens,
/// while AutoSteer is on neither the tractor nor the implement enters the ditch, and when
/// AutoSteer goes off for the ditch, there is room to stop before it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ObstacleClosedLoopTests
{
    // 200 x 400 m, a 20 m headland, a 6 m rear-fixed tool, 10 km/h, U-turn radius 6 m, 1 m from
    // the boundary; the ditch 60 m wide and 20 m long, 40 m north of mid-field.
    private const double Speed = 2.78, Dt = 0.1, Wheelbase = 3.0, MaxSteerDeg = 35, SteerRateDeg = 35;
    private const double DitchSouth = 40, DitchNorth = 60, DitchWest = -30, DitchEast = 30;
    private const double ToolWidth = 6;

    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private ToolPositionService _tool = null!;
    private PipelineIntents _intents = null!;
    private LocalPlane _plane = null!;
    private Boundary _boundary = null!;
    private BoundaryPolygon _ditch = null!;
    private Models.Track.Track _track = null!;
    private GpsCycleResult? _last;

    // The tractor: its pivot (rear axle centre), heading (radians, clockwise from north) and the
    // wheel angle (degrees, + to the right).
    private double _e, _n, _h, _steer;
    private bool _engaged;

    // What the run saw.
    private double _worstWhileEngaged = double.MaxValue; // nearest tractor or implement point to the ditch, inside < 0
    private double? _ditchAheadAtKickout;
    private string? _kickoutReason;
    private bool _turnedAtDitch;
    private int _startPass;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = Wheelbase;
        c.Vehicle.AntennaPivot = 0;
        c.Vehicle.AntennaOffset = 0;
        c.Vehicle.AntennaHeight = 0;
        c.Tool.Width = ToolWidth;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        c.Guidance.UTurnRadius = 6;
        c.Guidance.UTurnDistanceFromBoundary = 1;

        _gps = new GpsService();
        _gps.Start();
        _tool = new ToolPositionService(c);
        var coverage = new CoverageMapService(c);
        var sections = new SectionControlService(_tool, coverage, c);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(), Substitute.For<IUdpCommunicationService>(), _gps, c);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        var offset = new PolygonOffsetService();
        _intents = new PipelineIntents();
        _pipeline = new GpsPipelineService(
            _gps, _tool, new TrackGuidanceService(), sections, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
                NullLogger<YouTurnStateMachine>.Instance, c),
            Substitute.For<IAudioService>(), _intents, headingFusion,
            NullLogger<GpsPipelineService>.Instance, c, new PositionEstimator());

        _plane = new LocalPlane(new Wgs84(39.42, -7.53), new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.SynchronousMode = true;
        _pipeline.CycleCompleted += r => _last = r;
        _pipeline.Start();

        _ditch = Rect(DitchWest, DitchSouth, DitchEast, DitchNorth);
        _ditch.AtObstacle = ObstacleAction.Turn;
        _ditch.IsHard = true;
        _boundary = new Boundary { OuterBoundary = Rect(-100, -200, 100, 200) };
        _boundary.InnerBoundaries.Add(_ditch);
        var headland = Rect(-80, -180, 80, 180).Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(_boundary);
        _pipeline.SetHeadlandLine(headland);
        _pipeline.SetHeadlandOn(true);
        sections.SetFieldContext(new SectionFieldContext(_boundary, headland, true, true));
        _pipeline.SetActiveTrack(_track, 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: 20, headlandDistance: 20, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(true);

        // On pass 0 (half a pass east of the AB line), heading north, 150 m south of the ditch.
        _e = ToolWidth / 2;
        _n = -110;
        _h = 0;
        _steer = 0;
        _engaged = false;
        _last = null;
        _worstWhileEngaged = double.MaxValue;
        _ditchAheadAtKickout = null;
        _kickoutReason = null;
        _turnedAtDitch = false;
        _startPass = 0;
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gps.Stop();
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private static BoundaryPolygon Rect(double e0, double n0, double e1, double n1)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e1, n0), (e1, n1), (e0, n1) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private void Engage()
    {
        _engaged = true;
        _pipeline.SetAutoSteerEngaged(true);
    }

    private void Disengage()
    {
        _engaged = false;
        _pipeline.SetAutoSteerEngaged(false);
    }

    /// <summary>One GPS fix and one tenth of a second of driving.</summary>
    private void Fix()
    {
        var ll = _plane.ConvertGeoCoordToWgs84(new GeoCoord(_n, _e));
        _gps.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = ll.Latitude, Longitude = ll.Longitude,
                Heading = _h * 180 / Math.PI, Speed = Speed,
            },
            FixQuality = 4, SatellitesInUse = 14, Hdop = 0.7, IsValid = true,
        });
        _tool.Update(new Vec3(_e, _n, _h), _h);

        var r = _last!;
        if (r.YouTurn is { TurnAtObstacle: true }) _turnedAtDitch = true;
        if (r.AutoSteerDisengagedThisCycle && _engaged)
        {
            _engaged = false;
            _kickoutReason = r.DisengageReason;
            _ditchAheadAtKickout = DitchAhead();
        }

        // AutoSteer turns the wheels toward the angle asked, at the rate a steering valve can;
        // with it off the operator holds them straight.
        double want = _engaged ? Math.Clamp(_pipeline.SimulatorSteerAngle, -MaxSteerDeg, MaxSteerDeg) : 0;
        double step = SteerRateDeg * Dt;
        _steer += Math.Clamp(want - _steer, -step, step);
        _h += Speed / Wheelbase * Math.Tan(_steer * Math.PI / 180) * Dt;
        _e += Math.Sin(_h) * Speed * Dt;
        _n += Math.Cos(_h) * Speed * Dt;

        if (_engaged) _worstWhileEngaged = Math.Min(_worstWhileEngaged, Clearance());
    }

    private void Drive(double seconds)
    {
        for (int i = 0; i < (int)Math.Round(seconds / Dt); i++) Fix();
    }

    private void DriveUntil(Func<bool> done, double maxSeconds = 120)
    {
        for (int i = 0; i < (int)(maxSeconds / Dt) && !done(); i++) Fix();
    }

    private bool Executing => _last?.YouTurn?.IsExecuting == true;
    private bool Planned => _last?.YouTurn?.TurnPath != null;
    private int Pass => _last?.Guidance?.HowManyPathsAway ?? 0;

    // The tractor's wheels (its axles, 2 m across) and the implement's working bar, the nearest
    // of them to the ditch; inside < 0. The bonnet ahead of the front axle is the operator's
    // U-turn distance from boundary, as at the field's edge: the turn keeps the pivot that far out.
    private double Clearance()
    {
        var pts = new List<(double E, double N)>();
        double se = Math.Sin(_h), ce = Math.Cos(_h);
        foreach (double along in new[] { 0, Wheelbase })
            foreach (double side in new[] { -1.0, 0, 1.0 })
                pts.Add((_e + se * along + ce * side, _n + ce * along - se * side));
        var tool = _tool.ToolPosition;
        double th = _tool.ToolHeading;
        for (int k = 0; k <= 6; k++)
        {
            double side = -ToolWidth / 2 + ToolWidth * k / 6;
            pts.Add((tool.Easting + Math.Cos(th) * side, tool.Northing - Math.Sin(th) * side));
        }
        return pts.Min(p => SignedDistanceToDitch(p.E, p.N));
    }

    private static double SignedDistanceToDitch(double e, double n)
    {
        double dx = Math.Max(DitchWest - e, Math.Max(0, e - DitchEast));
        double dy = Math.Max(DitchSouth - n, Math.Max(0, n - DitchNorth));
        bool inside = e > DitchWest && e < DitchEast && n > DitchSouth && n < DitchNorth;
        return inside ? -Math.Min(Math.Min(e - DitchWest, DitchEast - e), Math.Min(n - DitchSouth, DitchNorth - n))
            : Math.Sqrt(dx * dx + dy * dy);
    }

    // How far the tractor's pivot would go straight ahead before it reaches the ditch.
    private double DitchAhead()
    {
        double se = Math.Sin(_h), ce = Math.Cos(_h);
        for (double d = 0; d < 200; d += 0.1)
            if (SignedDistanceToDitch(_e + se * d, _n + ce * d) <= 0) return d;
        return double.PositiveInfinity;
    }

    /// <param name="stopRoom">The least distance (m) to the ditch AutoSteer may go off at.</param>
    private void AssertNeverSteeredIntoTheDitch(double stopRoom = 10)
    {
        string nearest = _worstWhileEngaged == double.MaxValue ? "-" : $"{_worstWhileEngaged:F2} m";
        TestContext.Out.WriteLine($"nearest the ditch while engaged {nearest}; " +
            (_kickoutReason != null ? $"AutoSteer off {_ditchAheadAtKickout:F1} m before it: {_kickoutReason}" : "AutoSteer stayed on") +
            $"; turned at the ditch: {_turnedAtDitch}");
        Assert.That(_worstWhileEngaged, Is.GreaterThanOrEqualTo(0), "AutoSteer steered the tractor or the implement into the ditch");
        if (_kickoutReason != null && _ditchAheadAtKickout is double ahead && !double.IsInfinity(ahead))
            Assert.That(ahead, Is.GreaterThanOrEqualTo(stopRoom), "AutoSteer went off too late to stop before the ditch");
    }

    /// <summary>Drive until the turn has been made, or AutoSteer is off, then a little more.</summary>
    private void FinishThePass()
    {
        DriveUntil(() => !_engaged || (Pass != _startPass && !Executing));
        Drive(5);
    }

    [TestCase("rear", 0)]
    [TestCase("rear", 1)]
    [TestCase("trailing", 0)]
    [TestCase("trailing", 1)]
    [TestCase("tbt", 0)]
    public void TheNormalPass_TurnsBeforeTheDitch(string tool, int skip)
    {
        var c = ConfigurationStore.Instance;
        c.Tool.SetToolType(tool);
        c.Tool.TrailingHitchLength = 4;
        _pipeline.SetYouTurnConfig(uTurnSkipRows: skip, isSkipWorkedMode: false,
            headlandCalculatedWidth: 20, headlandDistance: 20, isAlternateSkipMode: false);
        Engage();
        _startPass = 0;
        FinishThePass();

        AssertNeverSteeredIntoTheDitch();
        Assert.That(_turnedAtDitch, Is.True);
        Assert.That(_kickoutReason, Is.Null);
        Assert.That(Pass, Is.Not.EqualTo(0), "on the next pass");
    }

    [TestCase(20)]
    [TestCase(14)]
    [TestCase(8)]
    public void AbortingThePlannedTurn_CloseToTheDitch_NeverDrivesIntoIt(double metresBefore)
    {
        Engage();
        DriveUntil(() => _n >= DitchSouth - metresBefore);
        _intents.RequestClearYouTurn();
        FinishThePass();
        // A turn cleared 8 m before the ditch: its turn line stands 4 m off it (the metre plus the
        // 6 m implement's reach), no arc fits in the 4 m left, and AutoSteer goes off at once, with
        // the room there is.
        AssertNeverSteeredIntoTheDitch(Math.Min(10, metresBefore));
    }

    [TestCase(1.0)]
    [TestCase(3.0)]
    public void UTurnSwitchedOffMidArc_NeverDrivesIntoTheDitch(double secondsIntoTheArc)
    {
        Engage();
        DriveUntil(() => Executing);
        Drive(secondsIntoTheArc);
        _pipeline.SetYouTurnEnabled(false);
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [TestCase(0.0)]
    [TestCase(2.0)]
    [TestCase(5.0)]
    public void AutoSteerOffForASecondMidArc_NeverDrivesIntoTheDitch(double secondsIntoTheArc)
    {
        Engage();
        DriveUntil(() => Executing);
        Drive(secondsIntoTheArc);
        Disengage();
        Drive(1);
        Engage();
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [TestCase(30)]
    [TestCase(20)]
    [TestCase(12)]
    public void EngagedLate_CloseToTheDitch_NeverDrivesIntoIt(double metresBefore)
    {
        DriveUntil(() => _n >= DitchSouth - metresBefore);
        Engage();
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [TestCase(25)]
    [TestCase(15)]
    [TestCase(9)]
    [TestCase(3)]
    public void AManualUTurn_CloseToTheDitch_NeverDrivesIntoIt(double metresBefore)
    {
        Engage();
        DriveUntil(() => _n >= DitchSouth - 4 - metresBefore); // the tractor's front this far before it
        _intents.RequestManualYouTurn(turnLeft: true);
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ASnapToTheNextPass_CloseToTheDitch_NeverDrivesIntoIt(bool left)
    {
        Engage();
        DriveUntil(() => _n >= DitchSouth - 25);
        _intents.RequestGuidanceSnap(left);
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [TestCase(0.1)]
    [TestCase(3.0)]
    public void ANudge_CloseToTheDitch_NeverDrivesIntoIt(double metres)
    {
        Engage();
        DriveUntil(() => _n >= DitchSouth - 25);
        _intents.RequestGuidanceNudge(metres);
        Drive(30);
        AssertNeverSteeredIntoTheDitch();
    }

    [Test]
    public void TheDitchMadeAUTurn_AfterTheTurnWasPlannedAtTheFieldsEdge_IsTurnedAt()
    {
        _ditch.AtObstacle = ObstacleAction.DriveThrough;
        Engage();
        DriveUntil(() => Planned);
        Assert.That(_last!.YouTurn!.TurnAtObstacle, Is.False, "planned at the field's edge first");
        DriveUntil(() => _n >= -20);

        _ditch.AtObstacle = ObstacleAction.Turn; // chosen in the boundary list while driving
        _pipeline.SetBoundary(_boundary);
        _startPass = 0;
        FinishThePass();

        AssertNeverSteeredIntoTheDitch();
        Assert.That(_turnedAtDitch, Is.True, "planned again, before the ditch");
    }

    [Test]
    public void TheDitchMadeDriveThrough_AfterTheTurnWasPlanned_IsDrivenThrough()
    {
        Engage();
        DriveUntil(() => Planned);
        Assert.That(_last!.YouTurn!.TurnAtObstacle, Is.True);
        DriveUntil(() => _n >= -20);

        _ditch.AtObstacle = ObstacleAction.DriveThrough;
        _pipeline.SetBoundary(_boundary);
        DriveUntil(() => _n >= DitchNorth + 5, maxSeconds: 60);

        Assert.That(_n, Is.GreaterThanOrEqualTo(DitchNorth + 5), "driven on across it");
        Assert.That(_engaged, Is.True);
        Assert.That(_last!.YouTurn!.TurnAtObstacle, Is.False);
    }

    [Test]
    public void WithUTurnOff_AutoSteerGoesOffBeforeTheDitch_WithRoomToStop()
    {
        _pipeline.SetYouTurnEnabled(false);
        Engage();
        DriveUntil(() => !_engaged || _n > DitchNorth);

        AssertNeverSteeredIntoTheDitch();
        Assert.That(_kickoutReason, Does.Contain("Inner 1"));
    }
}
