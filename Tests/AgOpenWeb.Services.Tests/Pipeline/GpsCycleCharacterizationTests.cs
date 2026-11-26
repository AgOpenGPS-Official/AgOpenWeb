// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// What the GPS cycle (<see cref="GpsPipelineService"/>) puts out, fix by fix, on a whole drive,
/// held in <c>TestData/GpsCycle/*.txt.gz</c> (one line per fix; <c>gunzip -c</c> to read). The refactor in
/// <c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c> (item 8) splits the cycle into steps without
/// changing what it does: every fix of these drives must give the same result after it.
///
/// Each drive is closed loop, as on a tractor: the real cycle with its real guidance, U-turns,
/// sections and coverage, and a tractor that follows the steer angle the cycle asks for, on a
/// clock that moves only with the drive. The drive is run twice first; the two must agree to the
/// last digit, or the comparison would mean nothing. The record is then compared with the file:
/// what is on or off exactly, numbers to the millimetre (a little more is allowed, as the
/// maths library can differ in the last digit between machines).
///
/// A change to what the cycle puts out that is meant makes the files again, on purpose:
/// <c>AGOPENWEB_UPDATE_GPS_CYCLE=1 dotnet test --filter GpsCycleCharacterizationTests</c>, and
/// the diff is reviewed with the change.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore and Clock are process-wide
public class GpsCycleCharacterizationTests
{
    public enum Turns { Normal, SkipWorked, Alternate }

    public sealed record Drive(string Name, bool Curve, bool Headland, Turns Turns, bool HalfWorked, int Fixes);

    private static readonly Drive[] Drives =
    {
        new("ab-headland-normal", Curve: false, Headland: true, Turns.Normal, HalfWorked: false, Fixes: 2400),
        new("curve-headland-normal", Curve: true, Headland: true, Turns.Normal, HalfWorked: false, Fixes: 2400),
        new("curve-headland-skipworked", Curve: true, Headland: true, Turns.SkipWorked, HalfWorked: false, Fixes: 2400),
        new("ab-headland-skipworked", Curve: false, Headland: true, Turns.SkipWorked, HalfWorked: false, Fixes: 2400),
        new("ab-noheadland-alternate-halfworked", Curve: false, Headland: false, Turns.Alternate, HalfWorked: true, Fixes: 2400),
    };

    private static IEnumerable<TestCaseData> Cases() => Drives.Select(d => new TestCaseData(d).SetName(d.Name));

    [TestCaseSource(nameof(Cases))]
    public void The_cycle_puts_out_what_it_did_before_the_refactor(Drive drive)
    {
        var first = Run(drive);
        var second = Run(drive);
        Assert.That(second, Is.EqualTo(first), "the same drive twice gave different results: the run depends on something outside it");
        Assert.That(first.Count(l => l.Contains(" yt=1")), Is.GreaterThan(0), "the drive made no U-turn: it holds too little");

        string file = GoldenFile(drive.Name);
        if (Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_GPS_CYCLE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var w = new StreamWriter(new System.IO.Compression.GZipStream(File.Create(file), System.IO.Compression.CompressionLevel.SmallestSize)))
                foreach (var l in first) w.WriteLine(l);
            Assert.Pass($"wrote {first.Count} fixes to {file}");
        }

        var expected = new List<string>();
        using (var r = new StreamReader(new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress)))
            for (string? l; (l = r.ReadLine()) != null;) expected.Add(l);
        Assert.That(first.Count, Is.EqualTo(expected.Count), "number of fixes");
        for (int i = 0; i < expected.Count; i++)
        {
            if (!Same(expected[i], first[i], out string why))
                Assert.Fail($"fix {i}: {why}\n  was: {expected[i]}\n  now: {first[i]}");
        }
    }

    // ── The drive ──────────────────────────────────────────────────────

    private const double FieldWidth = 100, FieldLength = 200, HeadlandWidth = 16, ToolWidth = 8;
    private const double TrackEasting = -30, SpeedMps = 10 / 3.6, FixSeconds = 0.1;
    private const int ControlTicksPerFix = 2;

    private static List<string> Run(Drive d)
    {
        var clock = new ManualClock();
        Clock.Set(clock);
        ConfigurationStore.SetInstance(new ConfigurationStore());
        try
        {
            var config = ConfigurationStore.Instance;
            config.Vehicle.Wheelbase = 3;
            config.Vehicle.AntennaPivot = 0;
            config.Vehicle.AntennaOffset = 0;
            config.Vehicle.AntennaHeight = 0;
            config.Tool.Width = ToolWidth;
            config.Tool.Overlap = 0;
            config.NumSections = 8;
            for (int i = 0; i < 8; i++) config.Tool.SetSectionWidth(i, (int)(ToolWidth / 8 * 100));
            config.Guidance.UTurnRadius = 8;
            config.Guidance.UTurnDistanceFromBoundary = 2;
            config.Machine.HydraulicLiftEnabled = true;

            var gps = new GpsService();
            gps.Start();
            var tool = new ToolPositionService(config);
            var coverage = new CoverageMapService(config);
            var sections = new SectionControlService(tool, coverage, config);
            var autoSteer = new AutoSteerService(new TrackGuidanceService(), Substitute.For<IUdpCommunicationService>(), gps, config);
            var headingFusion = Substitute.For<IGpsHeadingFusionService>();
            headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                    Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
                .Returns(ci => ci.ArgAt<double>(0));
            var offset = new PolygonOffsetService();
            var pipeline = new GpsPipelineService(
                gps, tool, new TrackGuidanceService(), sections, coverage, autoSteer,
                new YouTurnGuidanceService(),
                new YouTurnStateMachine(
                    new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, config),
                    new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                    NullLogger<YouTurnStateMachine>.Instance, config),
                Substitute.For<IAudioService>(), new PipelineIntents(), headingFusion,
                NullLogger<GpsPipelineService>.Instance, config, new PositionEstimator());

            var plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
            pipeline.SetLocalPlane(plane);
            pipeline.SynchronousMode = true;
            GpsCycleResult? last = null;
            pipeline.CycleCompleted += r => last = r;
            pipeline.Start();

            var boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon { Points = Ring(0).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
            };
            var headlandLine = d.Headland ? Ring(HeadlandWidth).Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList() : null;
            var track = d.Curve
                ? Models.Track.Track.FromCurve("Curve", CurveProcessing.CalculateHeadings(Enumerable.Range(0, 401) // headings as a field's load gives them
                    .Select(i => new Vec3(TrackEasting + 3 * Math.Sin(i / 40.0), -FieldLength + i, 0)).ToList()))
                : Models.Track.Track.FromABLine("AB", new Vec3(TrackEasting, -FieldLength, 0), new Vec3(TrackEasting, FieldLength, 0));

            pipeline.SetHasActiveField(true);
            pipeline.SetBoundary(boundary);
            pipeline.SetHeadlandLine(headlandLine);
            pipeline.SetHeadlandOn(d.Headland);
            sections.SetFieldContext(new SectionFieldContext(boundary, headlandLine, true, d.Headland));
            sections.SetAllAuto();
            coverage.SetFieldBounds(-FieldWidth / 2, FieldWidth / 2, -FieldLength / 2, FieldLength / 2);
            if (d.HalfWorked) coverage.MarkRectangleCovered(0, FieldWidth / 2, -FieldLength / 2, FieldLength / 2);
            pipeline.SetActiveTrack(track, 0, 0);
            pipeline.SetYouTurnConfig(
                uTurnSkipRows: d.Turns == Turns.Normal ? 0 : 1,
                isSkipWorkedMode: d.Turns == Turns.SkipWorked,
                headlandCalculatedWidth: d.Headland ? HeadlandWidth : 0,
                headlandDistance: d.Headland ? HeadlandWidth : 0,
                isAlternateSkipMode: d.Turns == Turns.Alternate);
            pipeline.SetYouTurnEnabled(true);
            pipeline.SetAutoSteerEngaged(true);

            // The tractor: on the track at the south end of the worked area, heading north.
            double e = TrackEasting, n = -FieldLength / 2 + HeadlandWidth + 4, heading = 0, steer = 0;
            var lines = new List<string>(d.Fixes);
            for (int fix = 0; fix < d.Fixes; fix++)
            {
                var ll = plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
                gps.UpdateGpsData(new GpsData
                {
                    CurrentPosition = new Position
                    {
                        Latitude = ll.Latitude, Longitude = ll.Longitude,
                        Heading = heading * 180 / Math.PI, Speed = SpeedMps,
                    },
                    FixQuality = 4, SatellitesInUse = 14, Hdop = 0.7, IsValid = true,
                });
                lines.Add(Record(fix, last));
                double command = last?.Guidance?.SteerAngle ?? 0;

                // Between fixes: the control loop's ticks and the tractor moving.
                for (int t = 0; t < ControlTicksPerFix; t++)
                {
                    double dt = FixSeconds / ControlTicksPerFix;
                    clock.Advance(dt);
                    steer += Math.Clamp(Math.Clamp(command, -35, 35) - steer, -40 * dt, 40 * dt);
                    heading += SpeedMps / config.Vehicle.Wheelbase * Math.Tan(steer * Math.PI / 180) * dt;
                    heading = (heading % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                    e += Math.Sin(heading) * SpeedMps * dt;
                    n += Math.Cos(heading) * SpeedMps * dt;
                    tool.Update(new Vec3(e, n, heading), heading);
                    sections.Update(tool.ToolPosition, tool.ToolHeading, heading, SpeedMps);
                }
            }
            pipeline.Stop();
            gps.Stop();
            return lines;
        }
        finally
        {
            Clock.Reset();
            ConfigurationStore.SetInstance(new ConfigurationStore());
        }
    }

    // The field's outline inset by `inset`, a point every metre.
    private static List<Vec2> Ring(double inset)
    {
        var r = new List<Vec2>();
        double x0 = -FieldWidth / 2 + inset, x1 = FieldWidth / 2 - inset;
        double y0 = -FieldLength / 2 + inset, y1 = FieldLength / 2 - inset;
        for (double x = x0; x < x1; x++) r.Add(new(x, y0));
        for (double y = y0; y < y1; y++) r.Add(new(x1, y));
        for (double x = x1; x > x0; x--) r.Add(new(x, y1));
        for (double y = y1; y > y0; y--) r.Add(new(x0, y));
        return r;
    }

    // ── One fix's record ───────────────────────────────────────────────

    private static string Record(int fix, GpsCycleResult? r)
    {
        if (r == null) return $"{fix} none";
        var s = new StringBuilder();
        s.Append(fix);
        s.Append(" pos=").Append(N(r.Easting)).Append(',').Append(N(r.Northing)).Append(',').Append(N(r.Heading));
        s.Append(" rev=").Append(B(r.IsReverse));
        s.Append(" tool=").Append(N(r.ToolEasting)).Append(',').Append(N(r.ToolNorthing)).Append(',').Append(N(r.ToolHeadingRadians));
        s.Append(" hitch=").Append(N(r.HitchEasting)).Append(',').Append(N(r.HitchNorthing));
        s.Append(" as=").Append(B(r.IsAutoSteerEngaged)).Append(B(r.AutoSteerDisengagedThisCycle));
        s.Append(" off=").Append(Q(r.DisengageReason));
        s.Append(" lift=").Append(r.HydLiftState);
        s.Append(" oob=").Append(B(r.IsOutOfBounds));
        var g = r.Guidance;
        if (g != null)
        {
            s.Append(" g=").Append(B(g.IsGuidanceActive)).Append(B(g.HasGuidance)).Append(B(g.IsHeadingSameWay)).Append(B(g.IsReverse));
            s.Append(" xte=").Append(N(g.CrossTrackError)).Append(" herr=").Append(N(g.HeadingError));
            s.Append(" steer=").Append(N(g.SteerAngle)).Append(" raw=").Append(g.SteerAngleRaw).Append(',').Append(g.DistanceOffRaw);
            s.Append(" goal=").Append(B(g.HasGoalPoint)).Append(N(g.GoalPoint.Easting)).Append(',').Append(N(g.GoalPoint.Northing));
            s.Append(" pass=").Append(g.HowManyPathsAway).Append(' ').Append(Q(g.CurrentLineLabel));
            s.Append(" turnpts=").Append(g.TurnPathPointCount);
        }
        var y = r.YouTurn;
        if (y != null)
        {
            s.Append(" yt=").Append(B(y.IsEnabled)).Append(B(y.IsTriggered)).Append(B(y.IsExecuting));
            s.Append(" ytpath=").Append(y.TurnPath?.Count ?? -1).Append(':').Append(Sum(y.TurnPath)).Append('@').Append(y.PathIndex);
            s.Append(" ytleft=").Append(B(y.IsTurnLeft)).Append(B(y.LastTurnWasLeft));
            s.Append(" ytdist=").Append(N(y.DistanceToHeadland)).Append(',').Append(N(y.DistanceToTrigger));
            s.Append(" ytn=").Append(y.YouTurnCounter).Append(" snake=").Append(y.SnakeIndex).Append('/').Append(y.SnakeSequence?.Count ?? -1);
            s.Append(" zone=").Append(y.CurrentZone);
        }
        s.Append(" sec=").Append(r.SectionStates == null ? "-" : string.Concat(r.SectionStates.Select(B)));
        s.Append(" col=").Append(r.SectionColorCodes == null ? "-" : string.Join("", r.SectionColorCodes));
        s.Append(" hl=").Append(r.HeadlandProximityDistance is double h ? N(h) : "-").Append(B(r.HeadlandProximityWarning));
        s.Append(" msg=").Append(Q(r.StatusMessage)).Append(" notice=").Append(Q(r.YouTurnNotice));
        return s.ToString();
    }

    private static string N(double v) => double.IsFinite(v) ? v.ToString("0.000", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
    private static string B(bool v) => v ? "1" : "0";
    private static string Q(string? v) => v == null ? "-" : "\"" + v.Replace(' ', '_') + "\"";
    private static string Sum(IReadOnlyList<Vec3>? path) => path == null ? "-" : N(path.Sum(p => p.Easting + 2 * p.Northing));

    // ── Comparison ─────────────────────────────────────────────────────

    /// <summary>Same record: every word the same, a number within a few millimetres.</summary>
    private static bool Same(string was, string now, out string why)
    {
        why = "";
        if (was == now) return true;
        var a = Tokens(was);
        var b = Tokens(now);
        if (a.Count != b.Count) { why = "a different record"; return false; }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] == b[i]) continue;
            if (double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(b[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                Math.Abs(x - y) <= 0.003 + 1e-6 * Math.Abs(x))
                continue;
            why = $"'{a[i]}' became '{b[i]}'";
            return false;
        }
        return true;
    }

    // Numbers apart from the words and signs around them.
    private static List<string> Tokens(string line)
    {
        var t = new List<string>();
        var cur = new StringBuilder();
        bool num = false;
        foreach (char c in line)
        {
            bool isNum = char.IsDigit(c) || c == '.' || (c == '-' && cur.Length == 0);
            if (cur.Length > 0 && isNum != num) { t.Add(cur.ToString()); cur.Clear(); }
            num = isNum;
            cur.Append(c);
        }
        if (cur.Length > 0) t.Add(cur.ToString());
        return t;
    }

    private static string GoldenFile(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "TestData", "GpsCycle", name + ".txt.gz");

    /// <summary>A clock that moves only when the drive moves it.</summary>
    private sealed class ManualClock : IClock
    {
        private long _ticks; // 100 ns
        private static readonly DateTime Start = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => _ticks += (long)Math.Round(seconds * 1e7);
        public DateTime Now => Start.AddTicks(_ticks);
        public DateTime UtcNow => Start.AddTicks(_ticks);
        public long GetTimestamp() => _ticks;
        public long Frequency => 10_000_000;
        public double ElapsedSeconds(long start, long end) => (end - start) / 1e7;
        public double ElapsedMs(long start, long end) => (end - start) / 1e4;
        public double TimeScale { get; set; } = 1.0;
    }
}
