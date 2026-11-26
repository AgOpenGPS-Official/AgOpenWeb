// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// AgOpenGPS's simulator driving a line, as an oracle: the closed loop of its
/// <c>timerSim_Tick</c> (Controls.Designer.cs), <c>CSim.OnGpsDataUpdated</c>, the steering part
/// of <c>UpdateFixPosition</c> (Position.designer.cs) and <c>CVehicle.UpdateGoalPointDistance</c>,
/// over the steering routines in <see cref="AgOpenGpsSteeringOracle"/>. The vehicle is
/// <see cref="GpsSimulationService"/>, the port of AgOpenGPS.Core's, stepped one whole step a
/// tick as its 93 ms timer does. Names and order are AgOpenGPS's; the vehicle settings are its
/// defaults (Settings.cs). The heading is the simulator's own, as with the Dual heading source.
///
/// What the loop keeps of AgOpenGPS: the sim gets 90 % of the steer angle
/// (<c>guidanceLineSteerAngle * 0.01 * 0.9</c>) and holds the last one in the dead zone; the
/// speed is averaged (<c>AverageTheSpeed</c>); the look-ahead uses the previous cycle's XTE.
/// </summary>
public sealed class AgOpenGpsSimDriveOracle
{
    public const double Wheelbase = 3.3;               // setVehicle_wheelbase
    public const double MaxSteerAngle = 30;            // setVehicle_maxSteerAngle
    public const double GoalPointLookAheadHold = 3;    // setVehicle_goalPointLookAheadHold
    public const double GoalPointLookAheadMult = 1.5;  // setVehicle_goalPointLookAheadMult
    public const double GoalPointAcquireFactor = 0.9;  // setVehicle_goalPointAcquireFactor
    public const double PurePursuitIntegralGainAB = 0; // purePursuitIntegralGainAB
    public const double StanleyDistanceErrorGain = 1;  // stanleyDistanceErrorGain
    public const double StanleyHeadingErrorGain = 1;   // stanleyHeadingErrorGain
    public const double StanleyIntegralGainAB = 0;     // stanleyIntegralGainAB
    public const int DeadZoneHeading = 10;             // setAS_deadZoneHeading (hundredths of a degree)
    public const int DeadZoneDelay = 5;                // setAS_deadZoneDelay (seconds)
    public const double SideHillCompFactor = 0;

    public const double TickSeconds = GpsSimulationService.StepSeconds; // 93 ms timerSim

    private readonly GpsSimulationService _sim = new();
    private readonly LocalPlane _plane;
    private readonly Models.Track.Track _track;
    private readonly bool _stanley;
    private readonly double _distAway;

    // FormGPS / CVehicle state
    private double _avgSpeed;
    private double _lastSimGuidanceAngle;
    private short _guidanceLineSteerAngle;
    private double _modeActualXTE;
    private double _actualSteerAngleDegrees;
    private int _deadZoneDelayCounter;
    private bool _isInDeadZone;
    private double _oneSecond;

    // the steering routines' own history (CABLine / CABCurve fields)
    private double _integral, _pivotDistanceError, _pivotDistanceErrorLast;
    private int _counter;
    private double _xTrackSteerCorrection, _distSteerError, _lastDistSteerError;
    private int _currentLocationIndex;
    private bool _findGlobalNearest = true;

    public double Easting { get; private set; }
    public double Northing { get; private set; }
    public double HeadingRad => _sim.HeadingRadians;
    public double SteerAngleSent => _guidanceLineSteerAngle * 0.01;
    public double SteerAngleAverage => _sim.SteerAngleAverage;

    /// <param name="distAway">For an AB line, the pass driven: its distance right of the
    /// reference line (<c>BuildNewOffsetList</c>; AgOpenGPS puts pass 0 half a width over).
    /// A curve is driven on its own points.</param>
    public AgOpenGpsSimDriveOracle(LocalPlane plane, Models.Track.Track track, bool stanley, double distAway = 0)
    {
        _plane = plane;
        _track = track;
        _stanley = stanley;
        _distAway = distAway;
    }

    public void Start(double easting, double northing, double headingRad, double speedKmh)
    {
        _sim.Initialize(_plane.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting)));
        _sim.SetHeading(headingRad);
        _sim.StepDistance = speedKmh / 40.0; // btnSimSpeed: 4 * stepDistance * 10 km/h
        _avgSpeed = speedKmh;
        Easting = easting;
        Northing = northing;
    }

    /// <summary>One simulator timer tick: move, then steer from the new fix.</summary>
    public void Tick()
    {
        // timerSim_Tick
        if (_isInDeadZone)
            _sim.Tick(_lastSimGuidanceAngle, TickSeconds);
        else
        {
            _lastSimGuidanceAngle = _guidanceLineSteerAngle * 0.01 * 0.9;
            _sim.Tick(_lastSimGuidanceAngle, TickSeconds);
        }

        // CSim.OnGpsDataUpdated
        _actualSteerAngleDegrees = _sim.SteerAngleAverage;
        double vtgSpeed = Math.Abs(Math.Round(4.0 * _sim.StepDistance * 10.0, 2));
        _avgSpeed = (_avgSpeed * 0.75) + (vtgSpeed * 0.25);
        var fix = _plane.ConvertWgs84ToGeoCoord(_sim.CurrentPosition);
        Easting = fix.Easting;
        Northing = fix.Northing;
        double fixHeading = _sim.HeadingRadians;

        // UpdateFixPosition: antenna on the pivot, the steer axle a wheelbase ahead
        var pivotAxlePos = new Vec3(Easting, Northing, fixHeading);
        var steerAxlePos = new Vec3(Easting + Math.Sin(fixHeading) * Wheelbase,
            Northing + Math.Cos(fixHeading) * Wheelbase, fixHeading);

        if (_track.IsABLine) SteerAB(pivotAxlePos, steerAxlePos);
        else SteerCurve(pivotAxlePos);

        // tmrWatchdog's one-second block: vehicle.deadZoneDelayCounter++
        _oneSecond += TickSeconds;
        if (_oneSecond >= 1.0)
        {
            _oneSecond -= 1.0;
            _deadZoneDelayCounter++;
        }

        // delay on dead zone (status 1, not reversing)
        if (Math.Abs(_guidanceLineSteerAngle - _actualSteerAngleDegrees * 100) < DeadZoneHeading)
        {
            if (_deadZoneDelayCounter > DeadZoneDelay)
                _isInDeadZone = true;
        }
        else
        {
            _deadZoneDelayCounter = 0;
            _isInDeadZone = false;
        }
    }

    private double UpdateGoalPointDistance()
    {
        double xTE = Math.Abs(_modeActualXTE);
        double goalPointDistance = _avgSpeed * 0.05 * GoalPointLookAheadMult;

        double LoekiAheadHold = GoalPointLookAheadHold;
        double LoekiAheadAcquire = GoalPointLookAheadHold * GoalPointAcquireFactor;

        if (xTE <= 0.1)
        {
            goalPointDistance *= LoekiAheadHold;
            goalPointDistance += LoekiAheadHold;
        }
        else if (xTE > 0.1 && xTE < 0.4)
        {
            xTE -= 0.1;

            LoekiAheadHold = (1 - (xTE / 0.3)) * (LoekiAheadHold - LoekiAheadAcquire);
            LoekiAheadHold += LoekiAheadAcquire;

            goalPointDistance *= LoekiAheadHold;
            goalPointDistance += LoekiAheadHold;
        }
        else
        {
            goalPointDistance *= LoekiAheadAcquire;
            goalPointDistance += LoekiAheadAcquire;
        }

        if (goalPointDistance < 2) goalPointDistance = 2;
        return goalPointDistance;
    }

    private void SteerAB(Vec3 pivotAxlePos, Vec3 steerAxlePos)
    {
        Vec3 ptA = _track.Points[0], ptB = _track.Points[1];
        double abHeading = Math.Atan2(ptB.Easting - ptA.Easting, ptB.Northing - ptA.Northing);
        if (abHeading < 0) abHeading += 2 * Math.PI;
        // BuildNewOffsetList, TrackMode.AB: the pass is the line moved distAway to the right
        var curPtA = new Vec3((Math.Cos(-abHeading) * _distAway) + ptA.Easting, (Math.Sin(-abHeading) * _distAway) + ptA.Northing, abHeading);
        var curPtB = new Vec3((Math.Cos(-abHeading) * _distAway) + ptB.Easting, (Math.Sin(-abHeading) * _distAway) + ptB.Northing, abHeading);
        bool isHeadingSameWay = Math.PI - Math.Abs(Math.Abs(pivotAxlePos.Heading - abHeading) - Math.PI) < Math.PI / 2;

        if (_stanley)
        {
            var o = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(curPtA, curPtB, new AgOpenGpsSteeringOracle.StanleyInput
            {
                PivotPosition = pivotAxlePos, SteerPosition = steerAxlePos,
                StanleyHeadingErrorGain = StanleyHeadingErrorGain, StanleyDistanceErrorGain = StanleyDistanceErrorGain,
                StanleyIntegralGainAB = StanleyIntegralGainAB, MaxSteerAngle = MaxSteerAngle,
                SideHillCompFactor = SideHillCompFactor, AvgSpeed = _avgSpeed, IsAutoSteerOn = true,
                PreviousIntegral = _integral, PreviousXTrackSteerCorrection = _xTrackSteerCorrection,
                PreviousDistSteerError = _distSteerError, PreviousLastDistSteerError = _lastDistSteerError,
                PreviousCounter = _counter, PreviousPivotDistanceError = _pivotDistanceError,
            }, isHeadingSameWay);
            _integral = o.Integral; _xTrackSteerCorrection = o.XTrackSteerCorrection;
            _distSteerError = o.DistSteerError; _lastDistSteerError = o.LastDistSteerError;
            _counter = o.Counter; _pivotDistanceError = o.PivotDistanceError;
            _guidanceLineSteerAngle = o.GuidanceLineSteerAngle;
            _modeActualXTE = o.ModeActualXTE;
        }
        else
        {
            var o = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(new AgOpenGpsSteeringOracle.PurePursuitInput
            {
                PivotPosition = pivotAxlePos, CurrentLinePtA = curPtA, CurrentLinePtB = curPtB, ABHeading = abHeading,
                IsHeadingSameWay = isHeadingSameWay, Wheelbase = Wheelbase, MaxSteerAngle = MaxSteerAngle,
                PurePursuitIntegralGain = PurePursuitIntegralGainAB, GoalPointDistance = UpdateGoalPointDistance(),
                SideHillCompFactor = SideHillCompFactor, FixHeading = pivotAxlePos.Heading, AvgSpeed = _avgSpeed,
                IsAutoSteerOn = true,
                PreviousIntegral = _integral, PreviousPivotDistanceError = _pivotDistanceError,
                PreviousPivotDistanceErrorLast = _pivotDistanceErrorLast, PreviousCounter = _counter,
            });
            _integral = o.Integral; _pivotDistanceError = o.PivotDistanceError;
            _pivotDistanceErrorLast = o.PivotDistanceErrorLast; _counter = o.Counter;
            _guidanceLineSteerAngle = o.GuidanceLineSteerAngle;
            _modeActualXTE = o.ModeActualXTE;
        }
    }

    private void SteerCurve(Vec3 pivotAxlePos)
    {
        // CABCurve.GetCurrentCurveLine, Pure Pursuit, on the curve's own points (pass 0)
        var o = AgOpenGpsSteeringOracle.CalculateGuidanceCurve(new AgOpenGpsSteeringOracle.CurvePurePursuitInput
        {
            PivotPosition = pivotAxlePos, CurvePoints = _track.Points, CurrentLocationIndex = _currentLocationIndex,
            FindGlobalNearestPoint = _findGlobalNearest, IsHeadingSameWay = true,
            Wheelbase = Wheelbase, MaxSteerAngle = MaxSteerAngle, PurePursuitIntegralGain = PurePursuitIntegralGainAB,
            GoalPointDistance = UpdateGoalPointDistance(), SideHillCompFactor = SideHillCompFactor,
            FixHeading = pivotAxlePos.Heading, AvgSpeed = _avgSpeed, IsAutoSteerOn = true,
            PreviousIntegral = _integral, PreviousPivotDistanceError = _pivotDistanceError,
            PreviousPivotDistanceErrorLast = _pivotDistanceErrorLast, PreviousCounter = _counter,
        });
        _findGlobalNearest = o.FindGlobalNearestPoint; _currentLocationIndex = o.CurrentLocationIndex;
        _integral = o.Integral; _pivotDistanceError = o.PivotDistanceError;
        _pivotDistanceErrorLast = o.PivotDistanceErrorLast; _counter = o.Counter;
        _guidanceLineSteerAngle = o.GuidanceLineSteerAngle;
        _modeActualXTE = o.ModeActualXTE;
    }
}
