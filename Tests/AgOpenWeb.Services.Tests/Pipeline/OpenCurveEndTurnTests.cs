// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.Guidance;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// #694 (Graeme): a Bnd. Curve made the long way round, from A along the fence to B, leaves out
/// the 47 m of fence between them, and every pass has that opening. A and B are the line's ends:
/// it is not extended there, and the opening is a virtual boundary the U-turn turns at as at a
/// headland (<see cref="CurveEndOpening"/>): at B onto the next pass back towards A, at A onto the
/// one after. Where no turn is made there (auto U-turn off), AutoSteer goes off at the end, as
/// AgOpenGPS's "past end of curve", instead of looping back to the line. Driven in closed loop:
/// the app's simulator, the real <see cref="GpsPipelineService"/>, the steer angle fed back, on the
/// reporter's field (fence, headland and curve from the dump), his tool (6 m, offset 1.5 m), turn
/// radius (6 m) and the pass he drove (10R, pass 9).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class OpenCurveEndTurnTests
{
    private const double ToolWidth = 6;
    private const double SpeedKmh = 10;
    private static readonly Wgs84 Origin = new(55.27030725780243, -7.1667352803176785);

    private static readonly Lazy<(List<Vec2> Fence, List<Vec3> Headland, TrackModel Curve)> Field = new(() => LoadField("issue694_open_curve_field.json"));

    // His second try (2026-11-09): A on the south-west side, B on the south-east, the south corner
    // left out between them.
    private static readonly Lazy<(List<Vec2> Fence, List<Vec3> Headland, TrackModel Curve)> CornerField = new(() => LoadField("issue694_corner_curve_field.json"));

    private static (List<Vec2> Fence, List<Vec3> Headland, TrackModel Curve) LoadField(string file)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Pipeline", "Fixtures", file);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        List<(double E, double N)> Read(string name) => doc.RootElement.GetProperty(name).EnumerateArray()
            .Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
        var fence = Read("fence").Select(p => new Vec2(p.E, p.N)).ToList();
        var headland = Read("headland").Select(p => new Vec3(p.E, p.N, 0)).ToList();
        var curve = TrackModel.FromCurve("Boundary Curve",
            CurveProcessing.CalculateHeadings(Read("curve").Select(p => new Vec3(p.E, p.N, 0)).ToList()));
        return (fence, headland, curve);
    }

    /// <summary>One fix of a drive.</summary>
    public readonly record struct Sample(int Tick, double E, double N, double HeadingDeg, int Pass, bool InTurn, bool Engaged, string? Reason);

    private sealed class Drive : IDisposable
    {
        private const double TickSeconds = 1.0 / 30.0;
        private readonly GpsService _gps;
        private readonly GpsPipelineService _pipeline;
        private readonly GpsSimulationService _sim = new();
        private readonly LocalPlane _plane;
        private SimulatedGpsData? _last;
        private double _steer;
        public Models.State.GpsCycleResult? Last { get; private set; }
        public readonly List<Sample> Samples = new();
        public string? DisengageReason;

        public Drive(TrackModel track, int pass, bool youTurn, Boundary boundary, List<Vec3> headland)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Vehicle.AntennaPivot = 0;
            c.Vehicle.AntennaOffset = 0;
            c.Vehicle.AntennaHeight = 0;
            c.Guidance.GoalPointLookAheadHold = 4;
            c.Guidance.UTurnRadius = 6;
            c.Guidance.UTurnExtension = 20;
            c.Guidance.UTurnDistanceFromBoundary = 4;
            c.Guidance.UTurnStyle = 0;
            c.Guidance.UTurnSmoothing = 14;
            c.Tool.Width = ToolWidth;
            c.NumSections = 2;
            c.Tool.SetSectionWidth(0, 300);
            c.Tool.SetSectionWidth(1, 300);
            c.Tool.Overlap = 0;
            c.Tool.Offset = 1.5;
            c.Connections.IsDualGps = true;
            c.Connections.AutoDualFix = false;

            _gps = new GpsService();
            _gps.Start();
            var tool = new ToolPositionService(c);
            var coverage = new CoverageMapService(c);
            var sections = new SectionControlService(tool, coverage, c);
            var autoSteer = new AutoSteerService(new TrackGuidanceService(),
                Substitute.For<IUdpCommunicationService>(), _gps, c);
            var offset = new PolygonOffsetService();
            _pipeline = new GpsPipelineService(
                _gps, tool, new TrackGuidanceService(), sections, coverage, autoSteer,
                new YouTurnGuidanceService(),
                new YouTurnStateMachine(
                    new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
                    new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
                    NullLogger<YouTurnStateMachine>.Instance, c),
                Substitute.For<IAudioService>(),
                new PipelineIntents(),
                new GpsHeadingFusionService(c),
                NullLogger<GpsPipelineService>.Instance,
                c,
                new PositionEstimator());

            _plane = new LocalPlane(Origin, new SharedFieldProperties());
            _pipeline.SetLocalPlane(_plane);
            _pipeline.SynchronousMode = true;
            _pipeline.CycleCompleted += r =>
            {
                Last = r;
                if (r.AutoSteerDisengagedThisCycle) DisengageReason = r.DisengageReason;
            };
            _pipeline.Start();
            _pipeline.SetHasActiveField(true);
            _pipeline.SetSimulatorEnabled(true);
            _pipeline.SetBoundary(boundary);
            _pipeline.SetHeadlandLine(headland);
            _pipeline.SetHeadlandOn(true);
            _pipeline.SetYouTurnConfig(0, false, 12, 12, false);
            _pipeline.SetActiveTrack(track, pass, 0);
            _pipeline.SetYouTurnEnabled(youTurn);
            _pipeline.SetAutoSteerEngaged(true);
            _sim.GpsDataUpdated += (_, e) => _last = e.Data;
        }

        public void Start(double e, double n, double headingRad)
        {
            _sim.Initialize(_plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e)));
            _sim.SetHeading(headingRad);
            _sim.StepDistance = SpeedKmh / 40.0;
        }

        /// <summary>Drive until AutoSteer goes off or <paramref name="ticks"/> pass.</summary>
        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                _sim.Tick(_steer, TickSeconds);
                var d = _last!;
                var local = _plane.ConvertWgs84ToGeoCoord(d.Position);
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
                var r = Last!;
                Samples.Add(new Sample(i, local.Easting, local.Northing, d.HeadingDegrees,
                    r.Guidance?.HowManyPathsAway ?? int.MinValue, r.YouTurn?.IsExecuting ?? false, _pipeline.IsAutoSteerEngaged,
                    r.StatusMessage));
                if (!_pipeline.IsAutoSteerEngaged) break;
                _steer = _pipeline.SimulatorSteerAngle;
            }
        }

        public void Dispose()
        {
            _pipeline.Stop();
            _gps.Stop();
        }
    }

    private static Boundary FieldBoundary() => BoundaryOf(Field.Value.Fence);

    private static Boundary BoundaryOf(List<Vec2> fence) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        },
    };

    // Where the reporter engaged: on pass 10R (9), heading north-west (his log's first fix).
    private const double StartE = -27.215, StartN = 51.600, StartHeadingDeg = 318.70;

    // The reporter's pass and its two ends, as the reference offset gives them.
    private static (Vec3 A, Vec3 B) PassEnds(int pass) => PassEnds(Field.Value.Curve, pass);

    private static (Vec3 A, Vec3 B) PassEnds(TrackModel curve, int pass)
    {
        var made = CurveProcessing.CurvePass(curve.Points, Models.Track.PassGeometry.Offset(curve, pass, ToolWidth), false, 6);
        return (made.Points[0], made.Points[^1]);
    }

    private static double Dist(double e, double n, Vec3 p) => Math.Sqrt((e - p.Easting) * (e - p.Easting) + (n - p.Northing) * (n - p.Northing));

    // Each U-turn of a drive: the pass it left, the pass it landed on, where it began.
    private static List<(int From, int To, Sample Start)> Turns(List<Sample> samples)
    {
        var turns = new List<(int, int, Sample)>();
        Sample? start = null;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].InTurn && !samples[i - 1].InTurn) start = samples[i];
            if (samples[i].Pass != samples[i - 1].Pass && start is { } s)
            {
                turns.Add((samples[i - 1].Pass, samples[i].Pass, s));
                start = null;
            }
        }
        return turns;
    }

    [Test]
    public void The_curve_has_facing_ends_and_its_pass_ends_there()
    {
        var curve = Field.Value.Curve;
        Assert.That(CurveProcessing.EndsFacingEachOther(curve.Points), Is.EqualTo((true, true)), "A and B face each other across the opening");
        var made = CurveProcessing.CurvePass(curve.Points, Models.Track.PassGeometry.Offset(curve, 9, ToolWidth));
        var line = CurveProcessing.ExtendPass(made);
        Assert.Multiple(() =>
        {
            Assert.That(line[0], Is.EqualTo(made.Points[0]), "no extension at A: the line ends there");
            Assert.That(line[^1], Is.EqualTo(made.Points[^1]), "nor at B");
            Assert.That(CurveEndOpening.Build(curve.Points, FieldBoundary(), Field.Value.Headland, 12), Is.Not.Null,
                "the opening is a virtual boundary for the U-turn");
        });
    }

    [Test]
    public void With_auto_U_turn_the_opening_is_turned_at_like_a_headland_at_both_ends()
    {
        // Pass 9 the way he drove it, round to its end at A; the turn there lands on the next
        // pass, which is driven all the way round to its end at B and turns there, and so on.
        using var drive = new Drive(Field.Value.Curve, 9, youTurn: true, FieldBoundary(), Field.Value.Headland);
        drive.Start(StartE, StartN, StartHeadingDeg * Math.PI / 180);
        drive.Run(30 * 400);

        var turns = Turns(drive.Samples);
        Assert.That(drive.Samples.All(s => s.Engaged), Is.True, $"AutoSteer stayed on ({drive.DisengageReason})");
        Assert.That(turns.Count, Is.GreaterThanOrEqualTo(3), "three turns: at A, at B, at A");
        Assert.Multiple(() =>
        {
            for (int k = 0; k < 3; k++)
            {
                var (from, to, start) = turns[k];
                Assert.That(Math.Abs(to - from), Is.EqualTo(1), $"turn {k + 1}: onto the next pass");
                var (a, b) = PassEnds(from);
                // He drives pass 9 from B round to A: the first and third turns are at A, the second at B.
                var end = k % 2 == 0 ? a : b;
                var other = k % 2 == 0 ? b : a;
                double toEnd = Dist(start.E, start.N, end);
                Assert.That(toEnd, Is.LessThan(40), $"turn {k + 1} starts {toEnd:F0} m from the pass's end there");
                Assert.That(toEnd, Is.LessThan(Dist(start.E, start.N, other)), $"turn {k + 1}: at that end, not across the opening");
            }
            // Between turns the tractor drives the whole pass round the field.
            Assert.That(turns[1].Start.Tick - turns[0].Start.Tick, Is.GreaterThan(30 * 100), "a whole pass between the turns at A and at B");
        });
    }

    [Test]
    public void Without_a_U_turn_AutoSteer_goes_off_at_the_end_instead_of_looping_back()
    {
        using var drive = new Drive(Field.Value.Curve, 9, youTurn: false, FieldBoundary(), Field.Value.Headland);
        drive.Start(StartE, StartN, StartHeadingDeg * Math.PI / 180);
        drive.Run(30 * 200);

        var last = drive.Samples[^1];
        var (a, _) = PassEnds(9);
        var run = drive.Samples.Where(s => Dist(s.E, s.N, a) < 25).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(last.Engaged, Is.False, "AutoSteer went off");
            Assert.That(drive.DisengageReason, Is.EqualTo(GpsPipelineService.PastEndOfCurveReason));
            Assert.That(Dist(last.E, last.N, a), Is.LessThan(12), "at the end, about a look-ahead short of it");
            // The last 25 m (past the corner before it) straight on to the end: no turn back towards the line.
            double h0 = run[0].HeadingDeg;
            Assert.That(run.Max(s => Math.Abs(((s.HeadingDeg - h0 + 540) % 360) - 180)), Is.LessThan(15), "no turn back");
        });
    }

    [Test]
    public void A_and_B_on_two_sides_of_a_corner_are_the_lines_ends()
    {
        var (fence, headland, curve) = CornerField.Value;
        var made = CurveProcessing.CurvePass(curve.Points, Models.Track.PassGeometry.Offset(curve, 4, ToolWidth));
        var line = CurveProcessing.ExtendPass(made);
        Assert.Multiple(() =>
        {
            Assert.That(CurveProcessing.EndsFacingEachOther(curve.Points), Is.EqualTo((true, true)), "run on, A and B meet at the corner");
            Assert.That(line[0], Is.EqualTo(made.Points[0]), "no extension at A");
            Assert.That(line[^1], Is.EqualTo(made.Points[^1]), "nor at B");
            Assert.That(CurveEndOpening.Build(curve.Points, BoundaryOf(fence), headland, 12), Is.Not.Null,
                "the corner left out is a virtual boundary for the U-turn");
        });
    }

    [Test]
    public void With_A_and_B_on_two_sides_of_a_corner_the_tractor_keeps_its_pass_and_turns_at_B()
    {
        // His pass 5R (4) on the south-east side, heading south-west towards B. Run on, the pass
        // went past B across the corner left out, where the other side's passes crossed it, and
        // the turn was made at the field's corner. Now it stays on its pass to B and turns there
        // onto the next one.
        var (fence, headland, curve) = CornerField.Value;
        const int pass = 4;
        var made = CurveProcessing.CurvePass(curve.Points, Models.Track.PassGeometry.Offset(curve, pass, ToolWidth), false, 6);
        // Start 80 m back from B along the pass, heading for B.
        int i = made.Points.Count - 1;
        double back = 0;
        while (i > 0 && back < 80)
        {
            back += Dist(made.Points[i].Easting, made.Points[i].Northing, made.Points[i - 1]);
            i--;
        }
        var from = made.Points[i];
        var to = made.Points[Math.Min(i + 5, made.Points.Count - 1)];
        double heading = Math.Atan2(to.Easting - from.Easting, to.Northing - from.Northing);

        using var drive = new Drive(curve, pass, youTurn: true, BoundaryOf(fence), headland);
        drive.Start(from.Easting, from.Northing, heading);
        drive.Run(30 * 120);

        var turns = Turns(drive.Samples);
        var (_, b) = PassEnds(curve, pass);
        var beforeTurn = drive.Samples.TakeWhile(s => !s.InTurn).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(beforeTurn.All(s => s.Pass == pass), Is.True,
                "on its pass until the turn: " + string.Join(",", beforeTurn.Select(s => s.Pass).Distinct()));
            Assert.That(drive.Samples.All(s => s.Engaged), Is.True, $"AutoSteer stayed on ({drive.DisengageReason})");
            Assert.That(turns, Is.Not.Empty, "a U-turn at B");
            if (turns.Count > 0)
            {
                Assert.That(Math.Abs(turns[0].To - turns[0].From), Is.EqualTo(1), "onto the next pass");
                // The turn starts before B, the way the pass runs there: not past it, as when the
                // pass ran on across the opening and turned at the field's corner 30 m on.
                var nearB = made.Points[^6];
                double dE = b.Easting - nearB.Easting, dN = b.Northing - nearB.Northing, len = Math.Sqrt(dE * dE + dN * dN);
                double pastB = ((turns[0].Start.E - b.Easting) * dE + (turns[0].Start.N - b.Northing) * dN) / len;
                Assert.That(pastB, Is.LessThan(0), $"the turn starts before B ({pastB:F1} m past it)");
                Assert.That(Dist(turns[0].Start.E, turns[0].Start.N, b), Is.LessThan(40), "at B");
            }
        });
    }
}
