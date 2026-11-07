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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The closed-loop rig for the obstacle scenarios: the real GPS cycle (<see cref="GpsPipelineService"/>)
/// at 10 Hz steers a simple tractor (a bicycle model with a steering rate limit) with the angle
/// it asks for, on <see cref="ObstacleTestField"/>. It keeps the numbers the scenarios judge:
/// the nearest the tractor's axles or the implement came to any U-turn obstacle while AutoSteer
/// was on (the implement counts only for a Hard obstacle), how far the next U-turn obstacle was
/// when AutoSteer went off for one, and whether a turn was made at an obstacle.
/// </summary>
public sealed class ObstacleScenarioRig : IDisposable
{
    public const double Dt = 0.1, Wheelbase = 3.0, MaxSteerDeg = 35, SteerRateDeg = 35;
    public const double DefaultSpeed = 2.78; // 10 km/h
    public const double DefaultDistanceFromBoundary = 2; // the app's default

    private readonly GpsService _gps;
    private readonly ToolPositionService _tool;
    private readonly IGpsHeadingFusionService _headingFusion;
    private readonly List<ObstacleTestField.Obstacle> _obstacles;
    private GpsCycleResult? _last;
    private bool _wasPlanned;

    // The tractor: pivot (rear axle centre), heading (radians, clockwise from north), wheel angle (degrees, + right).
    private double _e, _n, _h, _steer;

    public ObstacleScenarioRig(ObstacleTestField.Built field, string toolType, double toolWidth = 6, double toolOffset = 0, double hitchLength = 4)
    {
        Field = field;
        ToolWidth = toolWidth;
        ToolOffset = toolOffset;
        _obstacles = field.Obstacles.ToList();
        // AGOPENWEB_SCENARIO_VERBOSE=1 puts the U-turn services' own debug lines into the log.
        bool verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") is "1" or "true";
        ILogger<T> LoggerFor<T>() => verbose ? new CapturingLogger<T>(this) : NullLogger<T>.Instance;

        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = Wheelbase;
        c.Vehicle.AntennaPivot = 0;
        c.Vehicle.AntennaOffset = 0;
        c.Vehicle.AntennaHeight = 0;
        c.Tool.Width = toolWidth;
        c.Tool.Offset = toolOffset;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(toolWidth * 100));
        c.Tool.SetToolType(toolType);
        c.Tool.TrailingHitchLength = hitchLength;
        c.Guidance.UTurnRadius = 6;
        // AGOPENWEB_SCENARIO_DISTANCE overrides the U-turn distance from boundary (m) for a run.
        c.Guidance.UTurnDistanceFromBoundary = double.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_DISTANCE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dist) ? dist : DefaultDistanceFromBoundary;

        _gps = new GpsService();
        _gps.Start();
        _tool = new ToolPositionService(c);
        var coverage = new CoverageMapService(c);
        var sections = new SectionControlService(_tool, coverage, c);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(), Substitute.For<IUdpCommunicationService>(), _gps, c);
        _headingFusion = Substitute.For<IGpsHeadingFusionService>();
        _headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _headingFusion.IsReverse.Returns(_ => Reversing);
        _headingFusion.IsChangingDirection.Returns(false);
        Intents = new PipelineIntents();
        Pipeline = new GpsPipelineService(
            _gps, _tool, new TrackGuidanceService(), sections, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(LoggerFor<YouTurnCreationService>(), new PolygonOffsetService(), c),
                new YouTurnPathingService(LoggerFor<YouTurnPathingService>(), c),
                LoggerFor<YouTurnStateMachine>(), c),
            Substitute.For<IAudioService>(), Intents, _headingFusion,
            LoggerFor<GpsPipelineService>(), c, new PositionEstimator());

        Plane = new LocalPlane(new Wgs84(39.4225, -7.5319), new SharedFieldProperties());
        Pipeline.SetLocalPlane(Plane);
        Pipeline.SynchronousMode = true;
        Pipeline.CycleCompleted += r => _last = r;
        Pipeline.Start();

        Pipeline.SetHasActiveField(true);
        Pipeline.SetBoundary(field.Boundary);
        Pipeline.SetHeadlandLine(field.HeadlandLine);
        Pipeline.SetHeadlandOn(true);
        sections.SetFieldContext(new SectionFieldContext(field.Boundary, field.HeadlandLine, true, true));
        Pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: ObstacleTestField.HeadlandWidth, headlandDistance: ObstacleTestField.HeadlandWidth,
            isAlternateSkipMode: false);
        Pipeline.SetYouTurnEnabled(true);
    }

    public ObstacleTestField.Built Field { get; }
    public double ToolWidth { get; }
    /// <summary>The implement's lateral offset (m, + = right of the tractor), a mower conditioner's.</summary>
    public double ToolOffset { get; }
    public LocalPlane Plane { get; }
    public GpsPipelineService Pipeline { get; }
    public PipelineIntents Intents { get; }
    public GpsCycleResult Last => _last!;

    public double Speed { get; set; } = DefaultSpeed;
    /// <summary>The operator is backing up: the tractor moves backwards along its heading, wheels straight.</summary>
    public bool Reversing { get; set; }
    public bool Engaged { get; private set; }
    public double Time { get; private set; }
    public double E => _e;
    public double N => _n;
    public double Heading => _h;

    // What the run saw.
    private Dictionary<string, bool>? _hardAtTurnStart;
    public double WorstClearance { get; private set; } = double.MaxValue;
    /// <summary>The nearest while a turn was driven or after it ended: what the turn did, not what the pass runs beside.</summary>
    public double WorstClearanceInTurn { get; private set; } = double.MaxValue;
    public string? WorstObstacleInTurn { get; private set; }
    public string? WorstObstacle { get; private set; }
    /// <summary>The nearest the tractor's wheels came to a Soft U-turn obstacle while engaged: the
    /// planner keeps the pivot out of a Soft one, the wheels are the distance-from-boundary setting's job.</summary>
    public double WorstWheelsAtSoft { get; private set; } = double.MaxValue;
    /// <summary>How far the pivot strayed from the planned turn path while driving it.</summary>
    public double MaxPathError { get; private set; }
    public string? KickoutReason { get; private set; }
    public double? ObstacleAheadAtKickout { get; private set; }
    /// <summary>How far the next U-turn obstacle was the last time AutoSteer was given the job (engaged, or
    /// an obstacle or the U-turn changed under it): a kickout can't be expected earlier than that.</summary>
    public double RoomAtLastArm { get; private set; } = double.PositiveInfinity;
    public bool TurnedAtObstacle { get; private set; }
    public int Turns { get; private set; }
    public double? ArcStartedAt { get; private set; }
    public double? TurnEndedAt { get; private set; }
    public List<string> Log { get; } = new();

    public bool Executing => _last?.YouTurn?.IsExecuting == true;
    public bool Planned => _last?.YouTurn?.TurnPath != null;
    public bool PlannedAtObstacle => _last?.YouTurn is { TurnPath: not null, TurnAtObstacle: true };
    public int Pass => _last?.Guidance?.HowManyPathsAway ?? 0;

    public void UseTrack(Models.Track.Track track, int pass) => Pipeline.SetActiveTrack(track, pass, 0);

    public void Place(double e, double n, double headingRad)
    {
        _e = e; _n = n; _h = headingRad; _steer = 0;
    }

    public void Engage()
    {
        if (!Engaged) Arm("engaged");
        Engaged = true;
        Pipeline.SetAutoSteerEngaged(true);
    }

    public void Disengage()
    {
        Engaged = false;
        Pipeline.SetAutoSteerEngaged(false);
    }

    /// <summary>After an obstacle's choice, shape or the set of obstacles changed while driving.</summary>
    public void BoundaryChanged(string why)
    {
        Pipeline.SetBoundary(Field.Boundary);
        Arm(why);
    }

    public void AddObstacle(ObstacleTestField.Obstacle obstacle)
    {
        Field.Boundary.InnerBoundaries.Add(obstacle.Polygon);
        _obstacles.Add(obstacle);
        BoundaryChanged("obstacle added");
    }

    /// <summary>The operator (or a change) has just made the way ahead AutoSteer's problem.</summary>
    public void Arm(string why)
    {
        double ahead = ObstacleAhead();
        RoomAtLastArm = ahead;
        Note($"{why}: next U-turn obstacle {(double.IsInfinity(ahead) ? "none" : $"{ahead:F0} m")} ahead");
    }

    public void Note(string text) => Log.Add($"t={Time,5:F1}s ({_e,6:F0},{_n,6:F0}) {text}");

    /// <summary>One GPS fix and one tenth of a second of driving.</summary>
    public void Fix()
    {
        var ll = Plane.ConvertGeoCoordToWgs84(new GeoCoord(_n, _e));
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
        bool executing = r.YouTurn?.IsExecuting == true;
        bool planned = r.YouTurn?.TurnPath != null;
        if (planned && !_wasPlanned)
        {
            var path = r.YouTurn!.TurnPath!;
            Note($"turn planned {(r.YouTurn.TurnAtObstacle ? "at an obstacle" : "at the field's edge")}, " +
                $"{(r.YouTurn.IsTurnLeft ? "left" : "right")}, {path.Count} points, arc from ({r.YouTurn.ArcStart?.Easting ?? path[0].Easting:F0},{r.YouTurn.ArcStart?.Northing ?? path[0].Northing:F0}), " +
                $"path n {path.Min(p => p.Northing):F0}..{path.Max(p => p.Northing):F0} e {path.Min(p => p.Easting):F0}..{path.Max(p => p.Easting):F0}");
            // The planned path's nearest approach to every U-turn obstacle it comes near (the pivot, not the implement).
            foreach (var o in _obstacles.Where(o => o.IsUTurn))
            {
                double near = path.Min(p => ObstacleTestField.SignedDistance(o.Polygon, p.Easting, p.Northing));
                if (near < 2.5) Note($"  the path comes {near:F2} m from {o.Name}");
            }
        }
        if (!planned && _wasPlanned && !executing) Note("planned turn dropped");
        _wasPlanned = planned;
        if (executing && ArcStartedAt == null)
        {
            ArcStartedAt = Time; Turns++;
            Note(r.YouTurn!.TurnAtObstacle ? "turn starts at an obstacle" : "turn starts at the field's edge");
            // The turn is judged by the contract it was planned under: an obstacle made Hard while the
            // arc is being driven cannot be planned for again (the operator who does everything wrong).
            _hardAtTurnStart = _obstacles.ToDictionary(o => o.Name, o => o.IsHard);
        }
        if (!executing && ArcStartedAt != null) { ArcStartedAt = null; TurnEndedAt = Time; Note("turn over"); }
        if (executing && r.YouTurn!.TurnAtObstacle) TurnedAtObstacle = true;
        if (r.AutoSteerDisengagedThisCycle && Engaged)
        {
            Engaged = false;
            KickoutReason = r.DisengageReason;
            ObstacleAheadAtKickout = ObstacleAhead();
            Note($"AutoSteer off by the host, obstacle {ObstacleAheadAtKickout:F1} m ahead: {KickoutReason}");
        }

        // AutoSteer turns the wheels toward the angle asked, at the rate a steering valve can;
        // with it off, or backing up, the operator holds them straight.
        double want = Engaged && !Reversing ? Math.Clamp(Pipeline.SimulatorSteerAngle, -MaxSteerDeg, MaxSteerDeg) : 0;
        double step = SteerRateDeg * Dt;
        _steer += Math.Clamp(want - _steer, -step, step);
        double v = Reversing ? -Speed : Speed;
        _h += v / Wheelbase * Math.Tan(_steer * Math.PI / 180) * Dt;
        _e += Math.Sin(_h) * v * Dt;
        _n += Math.Cos(_h) * v * Dt;
        Time += Dt;

        if (Engaged)
        {
            var (d, name, wheelsAtSoft) = Clearance();
            bool inTurn = executing || TurnEndedAt != null;
            if (d < 0 && WorstClearance >= 0) Note($"inside {name} by {-d:F2} m" + (inTurn ? " in the turn" : " on the pass itself"));
            if (d < WorstClearance) { WorstClearance = d; WorstObstacle = name; }
            if (inTurn && d < WorstClearanceInTurn) { WorstClearanceInTurn = d; WorstObstacleInTurn = name; }
            WorstWheelsAtSoft = Math.Min(WorstWheelsAtSoft, wheelsAtSoft);
            if (executing && r.YouTurn!.TurnPath is { Count: > 1 } path)
                MaxPathError = Math.Max(MaxPathError, DistanceToPath(path, _e, _n));
        }
    }

    private static double DistanceToPath(IReadOnlyList<Vec3> path, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++)
        {
            double ax = path[i - 1].Easting, ay = path[i - 1].Northing, dx = path[i].Easting - ax, dy = path[i].Northing - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / len2, 0, 1) : 0;
            double px = ax + t * dx - e, py = ay + t * dy - n;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    public void Drive(double seconds)
    {
        for (int i = 0; i < (int)Math.Round(seconds / Dt); i++) Fix();
    }

    public void DriveUntil(Func<bool> done, double maxSeconds = 120)
    {
        for (int i = 0; i < (int)(maxSeconds / Dt) && !done(); i++) Fix();
    }

    /// <summary>
    /// The nearest point of the tractor and the implement to any U-turn obstacle, inside &lt; 0,
    /// by the planner's own contract: at a Hard obstacle the tractor's wheels (its axles, 2 m
    /// across) and the implement's working bar stay out of it; at a Soft obstacle the pivot
    /// does, the implement may cross, and how far the wheels swing past the pivot is what the
    /// U-turn distance from boundary is for (as at the field's edge in AgOpenGPS). The wheels'
    /// distance from a Soft obstacle comes back separately, for the report.
    /// </summary>
    public (double Distance, string? Obstacle, double WheelsAtSoft) Clearance()
    {
        var tractor = new List<(double E, double N)>();
        double se = Math.Sin(_h), ce = Math.Cos(_h);
        foreach (double along in new[] { 0, Wheelbase })
            foreach (double side in new[] { -1.0, 0, 1.0 })
                tractor.Add((_e + se * along + ce * side, _n + ce * along - se * side));
        var bar = new List<(double E, double N)>();
        var tool = _tool.ToolPosition;
        double th = _tool.ToolHeading;
        for (int k = 0; k <= 6; k++)
        {
            double side = ToolOffset - ToolWidth / 2 + ToolWidth * k / 6;
            bar.Add((tool.Easting + Math.Cos(th) * side, tool.Northing - Math.Sin(th) * side));
        }

        double best = double.MaxValue, wheelsAtSoft = double.MaxValue;
        string? worst = null;
        foreach (var o in _obstacles)
        {
            if (!o.IsUTurn) continue;
            if (Math.Abs(o.CentreE - _e) > 120 || Math.Abs(o.CentreN - _n) > 120) continue;
            double d;
            bool hard = _hardAtTurnStart != null && _hardAtTurnStart.TryGetValue(o.Name, out bool was) ? was : o.IsHard;
            if (hard)
                d = tractor.Concat(bar).Min(p => ObstacleTestField.SignedDistance(o.Polygon, p.E, p.N));
            else
            {
                d = ObstacleTestField.SignedDistance(o.Polygon, _e, _n);
                wheelsAtSoft = Math.Min(wheelsAtSoft, tractor.Min(p => ObstacleTestField.SignedDistance(o.Polygon, p.E, p.N)));
            }
            if (d < best) { best = d; worst = o.Name; }
        }
        return (best, worst, wheelsAtSoft);
    }

    /// <summary>How far the pivot would go straight ahead before it enters a U-turn obstacle.</summary>
    public double ObstacleAhead()
    {
        double se = Math.Sin(_h), ce = Math.Cos(_h);
        for (double d = 0; d < 200; d += 0.1)
        {
            double e = _e + se * d, n = _n + ce * d;
            foreach (var o in _obstacles)
                if (o.IsUTurn && o.Polygon.IsPointInside(e, n)) return d;
        }
        return double.PositiveInfinity;
    }

    public void Dispose()
    {
        Pipeline.Stop();
        _gps.Stop();
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>A service's own log lines, into the rig's log with the rig's clock.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ObstacleScenarioRig _rig;
        public CapturingLogger(ObstacleScenarioRig rig) => _rig = rig;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);
            if (text.StartsWith("[Guidance]")) return; // one line every fix
            _rig.Note($"  {typeof(T).Name}: {text}");
        }
    }
}
