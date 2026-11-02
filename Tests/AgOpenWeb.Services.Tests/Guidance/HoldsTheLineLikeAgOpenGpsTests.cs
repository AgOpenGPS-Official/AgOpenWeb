// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// Checklist 2.4 / 2.5 / 2.6: the app, driving its own simulator, gets onto the line from 1, 3
/// and 10 m with no more overshoot than AgOpenGPS, holds a straight AB line at field speed
/// with a cross-track error as small as AgOpenGPS's, and holds a recorded curve with no more
/// wobble. Ours is the closed loop a tester sees: <see cref="GpsSimulationService"/> ticked at
/// the app's 30 Hz, each fix through the real <see cref="GpsPipelineService"/> (heading fusion,
/// antenna transform, look-ahead, track guidance), the cycle's steer angle back into the
/// simulator as <c>MainViewModel.ApplyResults</c> does. AgOpenGPS is
/// <see cref="AgOpenGpsSimDriveOracle"/>, its loop over its own code. Both run AgOpenGPS's
/// default vehicle settings and the simulator's true heading (the Dual source), so the
/// steering is what differs, not the setup. Both drive the same line: on the AB line, pass 0,
/// half the 6 m tool to the right of it in both apps; on the curve, the recorded line itself
/// (<see cref="TrackModel.NoPassOffset"/> here, as each app offsets a curve its own way and
/// that is checklist 2.12, not this).
///
/// The 100 Hz control loop (tool position, sections, PGN sends) does not feed back into the
/// steering, so it is not in the loop here.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HoldsTheLineLikeAgOpenGpsTests
{
    private const double FieldSpeedKmh = 10;
    private const double RunMetres = 300;
    private const double HoldFromMetre = 180; // the last 120 m of a run: settled, whatever the start
    private const double ToolWidth = 6;
    private const double PassEasting = ToolWidth / 2; // pass 0 of the AB line on x = 0

    private static readonly Wgs84 Origin = new(43.7128, -74.006);

    // ── The drives ─────────────────────────────────────────────────────────────────

    /// <summary>One sample of a drive: distance along the run, signed cross-track error, steer.</summary>
    public readonly record struct Sample(double Metres, double Xte, double Steer);

    /// <summary>The app's loop: its simulator, its pipeline, its settings set to AgOpenGPS's defaults.</summary>
    private sealed class PipelineDrive : IDisposable
    {
        private const double TickSeconds = 1.0 / 30.0; // MainViewModel._simulatorTimer

        private readonly GpsService _gps;
        private readonly GpsPipelineService _pipeline;
        private readonly GpsSimulationService _sim = new();
        private readonly LocalPlane _plane;
        private SimulatedGpsData? _last;
        private double _steer;

        public double Easting { get; private set; }
        public double Northing { get; private set; }
        public double HeadingRad => _sim.HeadingRadians;
        public double Steer => _steer;
        public Models.State.GpsCycleResult? Last { get; private set; }

        public PipelineDrive(TrackModel track, bool stanley)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var config = ConfigurationStore.Instance;
            config.Vehicle.Wheelbase = AgOpenGpsSimDriveOracle.Wheelbase;
            config.Vehicle.MaxSteerAngle = AgOpenGpsSimDriveOracle.MaxSteerAngle;
            config.Vehicle.AntennaPivot = 0;
            config.Vehicle.AntennaOffset = 0;
            config.Vehicle.AntennaHeight = 0;
            config.Guidance.IsPurePursuit = !stanley;
            config.Guidance.GoalPointLookAheadHold = AgOpenGpsSimDriveOracle.GoalPointLookAheadHold;
            config.Guidance.GoalPointLookAheadMult = AgOpenGpsSimDriveOracle.GoalPointLookAheadMult;
            config.Guidance.GoalPointAcquireFactor = AgOpenGpsSimDriveOracle.GoalPointAcquireFactor;
            config.Guidance.MinLookAheadDistance = 2;
            config.Guidance.PurePursuitIntegralGain = AgOpenGpsSimDriveOracle.PurePursuitIntegralGainAB;
            config.Guidance.StanleyDistanceErrorGain = AgOpenGpsSimDriveOracle.StanleyDistanceErrorGain;
            config.Guidance.StanleyHeadingErrorGain = AgOpenGpsSimDriveOracle.StanleyHeadingErrorGain;
            config.Guidance.StanleyIntegralGainAB = AgOpenGpsSimDriveOracle.StanleyIntegralGainAB;
            config.AutoSteer.SideHillCompensation = AgOpenGpsSimDriveOracle.SideHillCompFactor;
            config.AutoSteer.DeadzoneHeading = AgOpenGpsSimDriveOracle.DeadZoneHeading / 100.0;
            config.AutoSteer.DeadzoneDelay = AgOpenGpsSimDriveOracle.DeadZoneDelay;
            config.Tool.Width = ToolWidth;
            config.NumSections = 1;
            config.Tool.SetSectionWidth(0, ToolWidth * 100);
            config.Tool.Overlap = 0;
            config.Tool.Offset = 0;
            // The simulator's heading is the heading, as AgOpenGPS's Dual source takes it.
            config.Connections.IsDualGps = true;
            config.Connections.AutoDualFix = false;

            _gps = new GpsService();
            _gps.Start();
            var tool = new ToolPositionService(config);
            var coverage = new CoverageMapService(config);
            var sections = new SectionControlService(tool, coverage, config);
            var autoSteer = new AutoSteerService(new TrackGuidanceService(),
                Substitute.For<IUdpCommunicationService>(), _gps, config);
            var offset = new PolygonOffsetService();
            _pipeline = new GpsPipelineService(
                _gps, tool, new TrackGuidanceService(), sections, coverage, autoSteer,
                new YouTurnGuidanceService(),
                new YouTurnStateMachine(
                    new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, config),
                    new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                    NullLogger<YouTurnStateMachine>.Instance, config),
                Substitute.For<IAudioService>(),
                new PipelineIntents(),
                new GpsHeadingFusionService(config),
                NullLogger<GpsPipelineService>.Instance,
                config,
                new PositionEstimator());

            _plane = new LocalPlane(Origin, new SharedFieldProperties());
            _pipeline.SetLocalPlane(_plane);
            _pipeline.SynchronousMode = true;
            _pipeline.CycleCompleted += r => Last = r;
            _pipeline.Start();
            _pipeline.SetHasActiveField(true);
            _pipeline.SetSimulatorEnabled(true);
            _pipeline.SetActiveTrack(track, 0, 0);
            _pipeline.SetYouTurnEnabled(false);
            _pipeline.SetAutoSteerEngaged(true);

            _sim.GpsDataUpdated += (_, e) => _last = e.Data;
        }

        public void Start(double easting, double northing, double headingRad, double speedKmh)
        {
            _sim.Initialize(_plane.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting)));
            _sim.SetHeading(headingRad);
            _sim.StepDistance = speedKmh / 40.0; // MainViewModel.UpdateSimulatorSpeed
            Easting = easting;
            Northing = northing;
        }

        /// <summary>One simulator timer tick: move with the last cycle's steer, then run the cycle on the new fix.</summary>
        public void Tick()
        {
            _sim.Tick(_steer, TickSeconds);
            var d = _last!;
            var local = _plane.ConvertWgs84ToGeoCoord(d.Position);
            Easting = local.Easting;
            Northing = local.Northing;
            // MainViewModel.OnSimulatorGpsDataUpdated
            _gps.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position
                {
                    Latitude = d.Position.Latitude, Longitude = d.Position.Longitude, Altitude = d.Altitude,
                    Easting = local.Easting, Northing = local.Northing,
                    Heading = d.HeadingDegrees, Speed = d.SpeedKmh / 3.6,
                },
                FixQuality = 4, SatellitesInUse = d.SatellitesTracked, Hdop = d.Hdop, DifferentialAge = 0,
                HasDualHeading = true, ImuHeading = d.HeadingDegrees, ImuValid = true,
                SentenceType = GpsSentenceType.Simulator, Timestamp = Clock.Current.Now, IsValid = true,
            });
            // MainViewModel.ApplyResults: the sim steers by guidance while engaged on a line
            Assert.That(_pipeline.IsAutoSteerEngaged, Is.True, "AutoSteer dropped out during the drive");
            _steer = _pipeline.SimulatorSteerAngle;
        }

        public void Dispose()
        {
            _pipeline.Stop();
            _gps.Stop();
        }
    }

    // ── The lines ──────────────────────────────────────────────────────────────────

    private static readonly TrackModel AbLine = TrackModel.FromABLine("AB", new Vec3(0, -1000, 0), new Vec3(0, 1000, 0));

    /// <summary>
    /// A recorded curve: a point a metre, the heading swinging ±20° over a 314 m wavelength,
    /// so the bends have a radius of about 140 m, like a contour pass in a field.
    /// </summary>
    private static TrackModel RecordedCurve()
    {
        var pts = new List<Vec3>();
        double e = 0, n = -100;
        for (int i = 0; i <= 600; i++)
        {
            double h = 0.35 * Math.Sin(i / 50.0);
            pts.Add(new Vec3(e, n, (h + 2 * Math.PI) % (2 * Math.PI)));
            e += Math.Sin(h); n += Math.Cos(h);
        }
        var curve = TrackModel.FromCurve("Curve", pts);
        curve.NoPassOffset = true;
        return curve;
    }

    /// <summary>Signed distance from the line, left negative, from the pivot's true position.</summary>
    private static double XteOn(TrackModel track, double e, double n)
    {
        if (track.IsABLine) return e - PassEasting; // the AB line runs north on x = 0, its pass 0 on x = 3
        double best = double.MaxValue, signed = 0;
        var pts = track.Points;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double dx = pts[i + 1].Easting - pts[i].Easting, dy = pts[i + 1].Northing - pts[i].Northing;
            double len2 = dx * dx + dy * dy;
            double u = Math.Clamp(((e - pts[i].Easting) * dx + (n - pts[i].Northing) * dy) / len2, 0, 1);
            double px = pts[i].Easting + u * dx, py = pts[i].Northing + u * dy;
            double d2 = (e - px) * (e - px) + (n - py) * (n - py);
            if (d2 < best)
            {
                best = d2;
                signed = Math.Sqrt(d2) * Math.Sign((e - px) * dy - (n - py) * dx);
            }
        }
        return signed;
    }

    // ── Running and measuring ───────────────────────────────────────────────────────

    private static (double e, double n, double h) StartPose(TrackModel track, double offMetres)
    {
        if (track.IsABLine) return (PassEasting + offMetres, -150, 0);
        // On the curve 20 m in, at its heading, offset to its right
        var p = track.Points[20];
        return (p.Easting + Math.Cos(p.Heading) * offMetres, p.Northing - Math.Sin(p.Heading) * offMetres, p.Heading);
    }

    private static List<Sample> DriveOurs(TrackModel track, bool stanley, double offMetres)
    {
        using var drive = new PipelineDrive(track, stanley);
        var (e, n, h) = StartPose(track, offMetres);
        drive.Start(e, n, h, FieldSpeedKmh);
        var samples = new List<Sample>();
        double metres = 0, le = e, ln = n;
        while (metres < RunMetres)
        {
            drive.Tick();
            metres += Math.Sqrt((drive.Easting - le) * (drive.Easting - le) + (drive.Northing - ln) * (drive.Northing - ln));
            le = drive.Easting; ln = drive.Northing;
            samples.Add(new Sample(metres, XteOn(track, drive.Easting, drive.Northing), drive.Steer));
        }
        return samples;
    }

    private static List<Sample> DriveTheirs(TrackModel track, bool stanley, double offMetres)
    {
        var drive = new AgOpenGpsSimDriveOracle(new LocalPlane(Origin, new SharedFieldProperties()), track, stanley, PassEasting);
        var (e, n, h) = StartPose(track, offMetres);
        drive.Start(e, n, h, FieldSpeedKmh);
        var samples = new List<Sample>();
        double metres = 0, le = e, ln = n;
        while (metres < RunMetres)
        {
            drive.Tick();
            metres += Math.Sqrt((drive.Easting - le) * (drive.Easting - le) + (drive.Northing - ln) * (drive.Northing - ln));
            le = drive.Easting; ln = drive.Northing;
            samples.Add(new Sample(metres, XteOn(track, drive.Easting, drive.Northing), drive.SteerAngleSent));
        }
        return samples;
    }

    /// <summary>What a drive did, in the checklist's terms.</summary>
    public sealed record Figures(double Overshoot, double Settled, double HoldMax, double HoldRms, double Wobble)
    {
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "overshoot {0:F3} m, settled after {1:F0} m, held within {2:F3} m (rms {3:F3}), wobble {4:F3} m",
            Overshoot, Settled, HoldMax, HoldRms, Wobble);
    }

    private static Figures Measure(List<Sample> s, double startOff)
    {
        // Overshoot: the furthest the pivot went past the line, onto the other side from the start.
        double sign = Math.Sign(startOff);
        double overshoot = sign == 0 ? 0 : s.Max(x => Math.Max(0, -sign * x.Xte));
        // Settled: the metre after which it stays within 10 cm of the line.
        int lastOut = s.FindLastIndex(x => Math.Abs(x.Xte) > 0.10);
        double settled = lastOut < 0 ? 0 : s[lastOut].Metres;
        // Held: over the end of the run, settled whatever the start.
        var hold = s.Where(x => x.Metres >= HoldFromMetre).ToList();
        double holdMax = hold.Max(x => Math.Abs(x.Xte));
        double holdRms = Math.Sqrt(hold.Average(x => x.Xte * x.Xte));
        // Wobble: the largest swing of the XTE, peak to trough, within any 20 m of the held part.
        double wobble = 0;
        for (int i = 0; i < hold.Count; i++)
        {
            double lo = hold[i].Xte, hi = hold[i].Xte;
            for (int j = i; j < hold.Count && hold[j].Metres - hold[i].Metres <= 20; j++)
            {
                lo = Math.Min(lo, hold[j].Xte); hi = Math.Max(hi, hold[j].Xte);
            }
            wobble = Math.Max(wobble, hi - lo);
        }
        return new Figures(overshoot, settled, holdMax, holdRms, wobble);
    }

    private static (Figures ours, Figures theirs) Both(TrackModel track, bool stanley, double off)
        => (Measure(DriveOurs(track, stanley, off), off), Measure(DriveTheirs(track, stanley, off), off));

    private static string Compared(string what, Figures ours, Figures theirs)
        => $"{what}\n  ours:      {ours}\n  AgOpenGPS: {theirs}";

    // ── The checks ─────────────────────────────────────────────────────────────────

    private const double OvershootMargin = 0.05; // ours may overshoot 5 cm more than AgOpenGPS, no more
    private const double HoldMargin = 0.02;      // and sit 2 cm further off the line
    private const double SettledMargin = 10;     // and take 10 m longer to settle

    /// <summary>2.4: onto the line from 1, 3 and 10 m without overshoot beyond AgOpenGPS's.</summary>
    [TestCase(false, 1.0, TestName = "PurePursuit_OntoTheLineFrom1m_NoMoreOvershootThanAgOpenGps")]
    [TestCase(false, 3.0, TestName = "PurePursuit_OntoTheLineFrom3m_NoMoreOvershootThanAgOpenGps")]
    [TestCase(false, 10.0, TestName = "PurePursuit_OntoTheLineFrom10m_NoMoreOvershootThanAgOpenGps")]
    [TestCase(true, 1.0, TestName = "Stanley_OntoTheLineFrom1m_NoMoreOvershootThanAgOpenGps")]
    [TestCase(true, 3.0, TestName = "Stanley_OntoTheLineFrom3m_NoMoreOvershootThanAgOpenGps")]
    [TestCase(true, 10.0, TestName = "Stanley_OntoTheLineFrom10m_NoMoreOvershootThanAgOpenGps")]
    public void OntoTheLine(bool stanley, double off)
    {
        var (ours, theirs) = Both(AbLine, stanley, off);
        string report = Compared($"{(stanley ? "Stanley" : "Pure Pursuit")} from {off} m at {FieldSpeedKmh} km/h", ours, theirs);
        Assert.Multiple(() =>
        {
            Assert.That(ours.Overshoot, Is.LessThanOrEqualTo(theirs.Overshoot + OvershootMargin), report);
            Assert.That(ours.Settled, Is.LessThanOrEqualTo(theirs.Settled + SettledMargin), report);
            Assert.That(ours.Settled, Is.LessThan(HoldFromMetre), "must be on the line before the held part: " + report);
        });
    }

    /// <summary>2.5: holds a straight AB line at field speed with a cross-track error as small as AgOpenGPS's.</summary>
    [TestCase(false, TestName = "PurePursuit_HoldsTheAbLineAtFieldSpeed_LikeAgOpenGps")]
    [TestCase(true, TestName = "Stanley_HoldsTheAbLineAtFieldSpeed_LikeAgOpenGps")]
    public void HoldsTheAbLine(bool stanley)
    {
        var (ours, theirs) = Both(AbLine, stanley, 1.0);
        string report = Compared($"{(stanley ? "Stanley" : "Pure Pursuit")} on the AB line at {FieldSpeedKmh} km/h", ours, theirs);
        Assert.Multiple(() =>
        {
            Assert.That(ours.HoldMax, Is.LessThanOrEqualTo(theirs.HoldMax + HoldMargin), report);
            Assert.That(ours.HoldRms, Is.LessThanOrEqualTo(theirs.HoldRms + HoldMargin), report);
            Assert.That(ours.HoldMax, Is.LessThan(0.10), "within 10 cm of a straight line: " + report);
        });
    }

    /// <summary>2.6: holds a recorded curve at field speed with no wobble: no more than AgOpenGPS's, and small.</summary>
    [Test]
    public void PurePursuit_HoldsTheRecordedCurveAtFieldSpeed_NoMoreWobbleThanAgOpenGps()
    {
        var curve = RecordedCurve();
        var (ours, theirs) = Both(curve, false, 0.0);
        string report = Compared($"Pure Pursuit on the curve at {FieldSpeedKmh} km/h", ours, theirs);
        Assert.Multiple(() =>
        {
            Assert.That(ours.HoldMax, Is.LessThanOrEqualTo(theirs.HoldMax + HoldMargin), report);
            Assert.That(ours.Wobble, Is.LessThanOrEqualTo(theirs.Wobble + HoldMargin), report);
            Assert.That(ours.HoldMax, Is.LessThan(0.15), "within 15 cm of the curve: " + report);
        });
    }

    /// <summary>Stanley on the curve has no AgOpenGPS loop here (its curve Stanley picks segments its own way): held to the same absolute bounds.</summary>
    [Test]
    public void Stanley_HoldsTheRecordedCurveAtFieldSpeed()
    {
        var ours = Measure(DriveOurs(RecordedCurve(), true, 0.0), 0.0);
        Assert.Multiple(() =>
        {
            Assert.That(ours.HoldMax, Is.LessThan(0.15), ours.ToString());
            Assert.That(ours.Wobble, Is.LessThan(0.15), ours.ToString());
        });
    }

    /// <summary>One drive from 1 m, cycle by cycle: the pose, the steer and what the cycle reported.</summary>
    [Test, Explicit("prints a drive")]
    public void Trace()
    {
        using var drive = new PipelineDrive(AbLine, false);
        drive.Start(PassEasting + 1.0, -150, 0, FieldSpeedKmh);
        for (int i = 0; i < 400; i++)
        {
            drive.Tick();
            var g = drive.Last?.Guidance;
            if (i % 20 == 0)
                TestContext.Out.WriteLine($"{i,4} e {drive.Easting:F3} n {drive.Northing:F2} h {drive.HeadingRad * 180 / Math.PI:F2} steer {drive.Steer:F2} | cycle xte {g?.CrossTrackError:F3} steer {g?.SteerAngle:F2} has {g?.HasGuidance} engaged {drive.Last?.IsAutoSteerEngaged} reason {drive.Last?.DisengageReason} disp {g?.DisplayTrack?.Points[0].Easting:F2} goal {g?.GoalPoint.Easting:F2},{g?.GoalPoint.Northing:F2} label {g?.CurrentLineLabel} paths {g?.HowManyPathsAway}");
        }
    }

    /// <summary>The figures of every drive, both apps, for reading.</summary>
    [Test, Explicit("prints the figures")]
    public void Report()
    {
        foreach (bool stanley in new[] { false, true })
            foreach (double off in new[] { 1.0, 3.0, 10.0 })
            {
                var (ours, theirs) = Both(AbLine, stanley, off);
                TestContext.Out.WriteLine(Compared($"{(stanley ? "Stanley" : "Pure Pursuit")} AB from {off} m", ours, theirs));
            }
        var curve = RecordedCurve();
        {
            var (ours, theirs) = Both(curve, false, 0.0);
            TestContext.Out.WriteLine(Compared("Pure Pursuit curve", ours, theirs));
            TestContext.Out.WriteLine("Stanley curve\n  ours:      " + Measure(DriveOurs(curve, true, 0.0), 0.0));
        }
    }
}
