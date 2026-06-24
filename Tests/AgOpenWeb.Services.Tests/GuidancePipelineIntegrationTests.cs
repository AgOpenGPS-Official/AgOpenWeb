using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Integration tests that wire real services together in closed loops.
/// No mocking — these verify the full guidance pipeline end-to-end.
/// </summary>
[TestFixture]
[NonParallelizable]
public class GuidancePipelineIntegrationTests
{
    private GeoConversion _geo = null!;
    private GpsSimulationService _sim = null!;
    private TrackGuidanceService _guidance = null!;

    private const double OriginLat = 61.4978;
    private const double OriginLon = 23.7610;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _geo = new GeoConversion(OriginLat, OriginLon);
        _sim = new GpsSimulationService();
        _guidance = new TrackGuidanceService();
    }

    #region GPS → Guidance Closed Loop

    [Test]
    public void ABLine_VehicleConvergesToTrack()
    {
        var track = TrackModel.FromABLine("North", new Vec3(0, 0, 0), new Vec3(0, 200, 0));

        var startWgs = _geo.ToWgs84(new Vec2(5, 0));
        _sim.Initialize(new Wgs84(startWgs.lat, startWgs.lon));
        _sim.SetHeading(0);
        _sim.StepDistance = 0.5;

        TrackGuidanceState? state = null;
        double lastXte = 5.0;

        for (int i = 0; i < 200; i++)
        {
            var local = _geo.ToLocal(_sim.CurrentPosition.Latitude, _sim.CurrentPosition.Longitude);
            double heading = _sim.HeadingRadians;

            var input = new TrackGuidanceInput
            {
                Track = track,
                PivotPosition = new Vec3(local.Easting, local.Northing, heading),
                SteerPosition = new Vec3(
                    local.Easting + Math.Sin(heading) * 2.5,
                    local.Northing + Math.Cos(heading) * 2.5, heading),
                UseStanley = false, Wheelbase = 2.5, MaxSteerAngle = 35,
                GoalPointDistance = 5, FixHeading = heading, AvgSpeed = 20,
                IsHeadingSameWay = true, IsAutoSteerOn = true,
                FindGlobalNearest = state == null,
                PreviousState = state,
                CurrentLocationIndex = state?.CurrentLocationIndex ?? 0
            };

            var output = _guidance.CalculateGuidance(input);
            state = output.State;
            _sim.Tick(output.SteerAngle);
            lastXte = Math.Abs(output.CrossTrackError);
        }

        Assert.That(lastXte, Is.LessThan(1.0),
            $"Vehicle should converge within 1m, but XTE={lastXte:F2}m");
    }

    [Test]
    public void ABLine_Stanley_AlsoConverges()
    {
        var track = TrackModel.FromABLine("North", new Vec3(0, 0, 0), new Vec3(0, 200, 0));

        var startWgs = _geo.ToWgs84(new Vec2(3, 0));
        _sim.Initialize(new Wgs84(startWgs.lat, startWgs.lon));
        _sim.SetHeading(0);
        _sim.StepDistance = 0.5;

        TrackGuidanceState? state = null;
        double lastXte = 3.0;

        for (int i = 0; i < 200; i++)
        {
            var local = _geo.ToLocal(_sim.CurrentPosition.Latitude, _sim.CurrentPosition.Longitude);
            double heading = _sim.HeadingRadians;

            var input = new TrackGuidanceInput
            {
                Track = track,
                PivotPosition = new Vec3(local.Easting, local.Northing, heading),
                SteerPosition = new Vec3(
                    local.Easting + Math.Sin(heading) * 2.5,
                    local.Northing + Math.Cos(heading) * 2.5, heading),
                UseStanley = true, Wheelbase = 2.5, MaxSteerAngle = 35,
                GoalPointDistance = 5, StanleyHeadingErrorGain = 1.0,
                StanleyDistanceErrorGain = 0.8, FixHeading = heading, AvgSpeed = 20,
                IsHeadingSameWay = true, IsAutoSteerOn = true,
                FindGlobalNearest = state == null,
                PreviousState = state,
                CurrentLocationIndex = state?.CurrentLocationIndex ?? 0
            };

            var output = _guidance.CalculateGuidance(input);
            state = output.State;
            _sim.Tick(output.SteerAngle);
            lastXte = Math.Abs(output.CrossTrackError);
        }

        Assert.That(lastXte, Is.LessThan(1.0),
            $"Stanley should converge within 1m, but XTE={lastXte:F2}m");
    }

    [Test]
    public void CurveFollowing_VehicleTracksArc()
    {
        var points = new List<Vec3>();
        double radius = 50;
        for (int i = 0; i <= 30; i++)
        {
            double angle = i * (Math.PI / 2) / 30;
            points.Add(new Vec3(radius * Math.Sin(angle), radius * (1 - Math.Cos(angle)), angle));
        }
        var track = TrackModel.FromCurve("Arc", points);

        var startWgs = _geo.ToWgs84(new Vec2(1, -2));
        _sim.Initialize(new Wgs84(startWgs.lat, startWgs.lon));
        _sim.SetHeading(0);
        _sim.StepDistance = 0.3;

        TrackGuidanceState? state = null;
        int onTrackCount = 0;

        for (int i = 0; i < 150; i++)
        {
            var local = _geo.ToLocal(_sim.CurrentPosition.Latitude, _sim.CurrentPosition.Longitude);
            double heading = _sim.HeadingRadians;

            var input = new TrackGuidanceInput
            {
                Track = track,
                PivotPosition = new Vec3(local.Easting, local.Northing, heading),
                SteerPosition = new Vec3(
                    local.Easting + Math.Sin(heading) * 2.5,
                    local.Northing + Math.Cos(heading) * 2.5, heading),
                UseStanley = false, Wheelbase = 2.5, MaxSteerAngle = 35,
                GoalPointDistance = 4, FixHeading = heading, AvgSpeed = 12,
                IsHeadingSameWay = true, IsAutoSteerOn = true,
                FindGlobalNearest = state == null,
                PreviousState = state,
                CurrentLocationIndex = state?.CurrentLocationIndex ?? 0
            };

            var output = _guidance.CalculateGuidance(input);
            state = output.State;
            _sim.Tick(output.SteerAngle);
            if (Math.Abs(output.CrossTrackError) < 2.0) onTrackCount++;
        }

        double pct = onTrackCount / 150.0 * 100;
        Assert.That(pct, Is.GreaterThanOrEqualTo(50),
            $"Vehicle should be on-track >=50%, was {pct:F0}%");
    }

    #endregion

    #region U-Turn Path Following

    [Test]
    public void UTurn_CreateAndFollow_CompletesSuccessfully()
    {
        var guidanceService = new YouTurnGuidanceService();

        var path = new List<Vec3>();
        for (int i = 0; i < 10; i++)
            path.Add(new Vec3(0, i, 0));
        for (int i = 1; i <= 10; i++)
        {
            double angle = i * Math.PI / 10;
            path.Add(new Vec3(5 * (1 - Math.Cos(angle)), 10 + 5 * Math.Sin(angle), angle));
        }
        for (int i = 0; i < 10; i++)
            path.Add(new Vec3(10, 15 - i, Math.PI));

        bool turnComplete = false;
        bool sawGoalAtPathEnd = false;
        // Drive the pivot through the path and a few meters past the endpoint along
        // its heading. Two assertions matter under the post-#337 contract:
        //   1. While the pivot is following the path (and even at the last point),
        //      the guidance service must NOT report IsTurnComplete — it has to keep
        //      publishing a forward-projected goal so the steering controller has
        //      something to chase during the headland traverse. An earlier bail on
        //      `B >= ptCount-1 && A > halfway` was the regression that froze the
        //      goal dot at the path end and caused exit-of-turn wiggle.
        //   2. Once the pivot drifts >4m off-path, the off-path safety net fires
        //      and reports completion. Real production completion is owned by
        //      YouTurnStateMachine.Tick's closest-approach detection (which uses
        //      the actual tractor position and runs at 5m); this safety net is
        //      a fallback for the case where the state machine isn't in the loop.
        var lastPathPoint = path[path.Count - 1];
        for (int i = 0; i < 100 && !turnComplete; i++)
        {
            Vec3 pos;
            if (i < path.Count)
            {
                pos = path[i];
            }
            else
            {
                // Continue forward along the endpoint heading past the last path point.
                double extra = (i - path.Count + 1) * 1.0; // 1 m per step
                pos = new Vec3(
                    lastPathPoint.Easting + Math.Sin(lastPathPoint.Heading) * extra,
                    lastPathPoint.Northing + Math.Cos(lastPathPoint.Heading) * extra,
                    lastPathPoint.Heading);
            }

            var output = guidanceService.CalculateGuidance(new AgOpenWeb.Models.YouTurn.YouTurnGuidanceInput
            {
                TurnPath = path, PivotPosition = pos,
                SteerPosition = new Vec3(pos.Easting + Math.Sin(pos.Heading) * 2.5,
                    pos.Northing + Math.Cos(pos.Heading) * 2.5, pos.Heading),
                Wheelbase = 2.5, MaxSteerAngle = 35, UseStanley = false,
                GoalPointDistance = 3, UTurnCompensation = 1.0,
                FixHeading = pos.Heading, AvgSpeed = 5, IsReverse = false, UTurnStyle = 0
            });

            // While still following the path (or right at its end), goal must keep
            // flowing — no premature completion that would freeze the goal dot.
            if (i == path.Count - 1 && !output.IsTurnComplete)
            {
                sawGoalAtPathEnd = output.GoalPoint.Easting != 0 || output.GoalPoint.Northing != 0;
            }

            turnComplete = output.IsTurnComplete;
            Assert.That(double.IsNaN(output.SteerAngle), Is.False);
        }

        Assert.That(sawGoalAtPathEnd, Is.True,
            "Goal point must keep publishing when pivot reaches the last path point (no early bail freezing the dot)");
        Assert.That(turnComplete, Is.True, "U-turn should complete once pivot drifts off-path");
    }

    #endregion

    #region NMEA → GpsService Pipeline

    [Test]
    public void NmeaPipeline_RealSentence_ProducesValidPosition()
    {
        var gpsService = new GpsService();
        var parser = new NmeaParserServiceFast(gpsService);
        GpsData? received = null;
        gpsService.GpsDataUpdated += (s, d) => received = d;

        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(
            BuildPanda(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0));
        parser.ParseBuffer(bytes, bytes.Length);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.IsValid, Is.True);
        Assert.That(received.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    [Test]
    public void NmeaPipeline_MultipleUpdates_TracksPosition()
    {
        var gpsService = new GpsService();
        var parser = new NmeaParserServiceFast(gpsService);
        var lats = new List<double>();
        gpsService.GpsDataUpdated += (s, d) => { if (d.IsValid) lats.Add(d.CurrentPosition.Latitude); };

        for (int i = 0; i < 5; i++)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(
                BuildPanda(4807.038 + i * 0.001, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0));
            parser.ParseBuffer(bytes, bytes.Length);
        }

        Assert.That(lats.Count, Is.EqualTo(5));
        for (int i = 1; i < lats.Count; i++)
            Assert.That(lats[i], Is.GreaterThan(lats[i - 1]));
    }

    #endregion

    #region Helpers

    private static string BuildPanda(double lat, string latDir, double lon, string lonDir,
        int fix, int sats, double hdop, double alt, double diffAge, double speed, double heading)
    {
        // Heading wire format = (int)(degrees * 10), per AiO firmware.
        int headingX10 = (int)System.Math.Round(heading * 10.0);
        string body = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},{10},0,0,0",
            lat, latDir, lon, lonDir, fix, sats, hdop, alt, diffAge, speed, headingX10);
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return $"${body}*{checksum:X2}";
    }

    #endregion
}
