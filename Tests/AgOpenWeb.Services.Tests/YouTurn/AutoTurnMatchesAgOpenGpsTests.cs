// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Checklist 3.2 / 3.4 / 3.5: the automatic turn fits inside the boundary with the Distance from
/// boundary kept, the turn radius changes it as in AgOpenGPS, and the K-turn path. The same
/// field, pass, tractor and settings go to our planner (<see cref="YouTurnStateMachine"/>, as the
/// pipeline drives it) and to <see cref="AgOpenGpsYouTurnOracle"/>, AgOpenGPS's AB-line turn
/// builder ported from the snapshot. Compared in the checklist's terms: how close to the fence the
/// turn comes, where it enters and leaves, its tightest radius; and point for point where the
/// two build the same shape (the wide turn, the K-turn). The U turn on a narrow tool is Brian's
/// sagitta here (from the AgOpenGPS dev fork, not in the snapshot) and the Dubins omega in the
/// snapshot: the same figures, not the same points.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class AutoTurnMatchesAgOpenGpsTests
{
    private const double Half = 60;          // a 120 × 120 m square field
    private const double HeadlandWidth = 16;
    private const double PivotNorthing = -20;

    private static double PassX(double width, int n) => n * width + width / 2;

    private static List<Vec2> SquareFence()
    {
        var ring = new List<Vec2> { new(-Half, -Half), new(Half, -Half), new(Half, Half), new(-Half, Half) };
        var points = new List<Vec2>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double len = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
            int n = (int)Math.Ceiling(len / 2.0);
            for (int k = 0; k < n; k++)
                points.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * k / n, a.Northing + (b.Northing - a.Northing) * k / n));
        }
        return points;
    }

    /// <summary>The rig: tool width, turn radius, distance from boundary, style (0 U, 1 K).</summary>
    public readonly record struct Rig(double Width, double Radius, double Setback, int Style)
    {
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "{0} m tool, radius {1}, {2} m from the boundary, {3}", Width, Radius, Setback, Style == 1 ? "K-turn" : "U-turn");
    }

    /// <summary>Keeps the planner's log lines, so a test can see a fallback being taken.</summary>
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Lines.Add(formatter(state, exception));
        }
    }

    private static List<string> _plannerLog = new();

    // ── Our planner ────────────────────────────────────────────────────────────────

    private static (List<Vec3> path, bool turnLeft) PlanOurs(Rig rig, List<Vec2> fence)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, rig.Width * 100);
        c.Guidance.UTurnRadius = rig.Radius;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = rig.Setback;
        c.Guidance.UTurnStyle = rig.Style;

        var offset = new PolygonOffsetService();
        var log = new RecordingLogger<YouTurnCreationService>();
        _plannerLog = log.Lines;
        var creation = new YouTurnCreationService(log, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var headland = offset.CalculatePointHeadings(offset.CreateInwardOffset(fence, HeadlandWidth)!);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = PassX(rig.Width, 0), Northing = PivotNorthing, Heading = 0, Speed = 2.78 },
            track, boundary, headland,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: HeadlandWidth, HeadlandDistance: HeadlandWidth);
        sm.Tick(ctx, guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null.And.Count.GreaterThan(10), $"ours planned a turn: {rig}");
        return (turn.TurnPath!, turn.IsTurnLeft);
    }

    /// <summary>
    /// A tool offset more than half the pass spacing (a side-shift mower: 3 m tool, 2.5 m to the
    /// right): the tractor's next line is on the other side of the turn, 2 m against the turn
    /// direction, because the tool must move 3 m with it. AgOpenGPS keeps the sign of its
    /// turnOffset (w ± 2 × offset) and a negative one flips the end of the turn (CYouTurn);
    /// ours took its absolute value and landed every such turn 4 m from the line it then
    /// steered (field job J8/J9 TURN-EXIT-OFF-PASS: every other turn, the implement 3 m off).
    /// </summary>
    [TestCase(2.5, 0, TestName = "ToolOffset_2_5_on_3m_U_LandsOnTheNextLine")]
    [TestCase(-2.5, 0, TestName = "ToolOffset_minus_2_5_on_3m_U_LandsOnTheNextLine")]
    [TestCase(0.5, 0, TestName = "ToolOffset_0_5_on_3m_U_LandsOnTheNextLine")]
    public void A_turn_with_a_large_tool_offset_lands_on_the_next_line(double toolOffset, int style)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.Tool.Offset = toolOffset;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 300);
        c.Guidance.UTurnRadius = 6;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2;
        c.Guidance.UTurnStyle = style;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        var fence = SquareFence();
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var headland = offset.CalculatePointHeadings(offset.CreateInwardOffset(fence, HeadlandWidth)!);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        // Heading north on pass 0: the tractor steers beside the pass so the tool runs on it.
        double x = Models.Track.PassGeometry.Offset(track, 0, 3.0, 0, Models.Track.PassGeometry.ToolShift(toolOffset, true));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = x, Northing = PivotNorthing, Heading = 0, Speed = 2.78 },
            track, boundary, headland,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: HeadlandWidth, HeadlandDistance: HeadlandWidth);
        sm.Tick(ctx, guidance, turn);

        Assert.That(turn.TurnPath, Is.Not.Null.And.Count.GreaterThan(10), "a turn is planned");
        Assert.That(turn.NextTrack, Is.Not.Null, "the next line is known");
        // The line the tractor steers after the turn (the cyan line): AB lines run north, so
        // its easting is the line.
        double nextX = turn.NextTrack!.Points[0].Easting;
        double exitX = turn.TurnPath![^1].Easting;
        TestContext.Out.WriteLine($"tractor x {x:F2}, next line x {nextX:F2}, turn ends at x {exitX:F2}, turn left {turn.IsTurnLeft}");
        Assert.That(exitX, Is.EqualTo(nextX).Within(0.3), "the turn ends on the line the tractor steers next");
    }

    private static List<Vec3> PlanTheirs(Rig rig, List<Vec2> fence, bool turnLeft)
    {
        var oracle = new AgOpenGpsYouTurnOracle(fence)
        {
            youTurnRadius = rig.Radius,
            uturnDistanceFromBoundary = rig.Setback,
            uTurnStyle = rig.Style,
            toolWidth = rig.Width,
            toolOverlap = 0,
            toolOffset = 0,
        };
        var path = oracle.Build(new Vec3(PassX(rig.Width, 0), PivotNorthing, 0), new Vec3(0, -100, 0), new Vec3(0, 100, 0), 0, turnLeft);
        Assert.That(path, Has.Count.GreaterThan(10), $"AgOpenGPS built a turn: {rig}\n  {oracle.Trace}");
        return path;
    }

    // ── Figures ────────────────────────────────────────────────────────────────────

    /// <summary>A turn in the checklist's terms.</summary>
    public sealed record Figures(double ClosestToFence, double Apex, double EntryX, double ExitX, double TightestRadius, int Points)
    {
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "closest to the fence {0:F2} m, apex at {1:F2} N, enters at x {2:F2}, leaves at x {3:F2}, tightest radius {4:F1} m, {5} points",
            ClosestToFence, Apex, EntryX, ExitX, TightestRadius, Points);
    }

    private static double DistanceInsideFence(Vec3 p) => Half - Math.Max(Math.Abs(p.Easting), Math.Abs(p.Northing));

    private static Figures Measure(List<Vec3> path)
    {
        // Tightest radius: the circle through points about 2 m apart, the smallest along the path.
        double tightest = double.MaxValue;
        int step = Math.Max(1, (int)Math.Round(2.0 / AverageSpacing(path)));
        int at = -1;
        for (int i = step; i + step < path.Count; i++)
        {
            double r = Circumradius(path[i - step], path[i], path[i + step]);
            if (r < tightest) { tightest = r; at = i; }
        }
        if (at >= 0)
        {
            var sb = new System.Text.StringBuilder($"    tightest at point {at} of {path.Count}:");
            for (int k = Math.Max(0, at - step); k <= Math.Min(path.Count - 1, at + step); k++)
                sb.Append(string.Format(CultureInfo.InvariantCulture, " ({0:F2},{1:F2})", path[k].Easting, path[k].Northing));
            TestContext.Out.WriteLine(sb.ToString());
        }
        return new Figures(path.Min(DistanceInsideFence), path.Max(p => p.Northing), path[0].Easting, path[^1].Easting, tightest, path.Count);
    }

    /// <summary>The points around index <paramref name="at"/>, for reading a path.</summary>
    private static string Around(List<Vec3> path, int at, int span)
    {
        var sb = new System.Text.StringBuilder();
        for (int k = Math.Max(0, at - span); k <= Math.Min(path.Count - 1, at + span); k++)
            sb.Append(string.Format(CultureInfo.InvariantCulture, " ({0:F2},{1:F2})", path[k].Easting, path[k].Northing));
        return sb.ToString();
    }

    private static double AverageSpacing(List<Vec3> path)
    {
        double total = 0;
        for (int i = 1; i < path.Count; i++) total += Math.Sqrt(Sq(path[i], path[i - 1]));
        return total / (path.Count - 1);
    }

    private static double Sq(Vec3 a, Vec3 b) => (a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing);

    private static double Circumradius(Vec3 a, Vec3 b, Vec3 c)
    {
        double ab = Math.Sqrt(Sq(a, b)), bc = Math.Sqrt(Sq(b, c)), ca = Math.Sqrt(Sq(c, a));
        double cross = Math.Abs((b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting));
        return cross < 1e-9 ? double.MaxValue : ab * bc * ca / (2 * cross);
    }

    /// <summary>The furthest any point of <paramref name="a"/> is from the polyline <paramref name="b"/>.</summary>
    private static double FurthestFrom(List<Vec3> a, List<Vec3> b)
    {
        double worst = 0;
        foreach (var p in a)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < b.Count; i++)
            {
                double dx = b[i + 1].Easting - b[i].Easting, dy = b[i + 1].Northing - b[i].Northing;
                double len2 = dx * dx + dy * dy;
                double u = len2 < 1e-12 ? 0 : Math.Clamp(((p.Easting - b[i].Easting) * dx + (p.Northing - b[i].Northing) * dy) / len2, 0, 1);
                double px = b[i].Easting + u * dx, py = b[i].Northing + u * dy;
                best = Math.Min(best, (p.Easting - px) * (p.Easting - px) + (p.Northing - py) * (p.Northing - py));
            }
            worst = Math.Max(worst, best);
        }
        return Math.Sqrt(worst);
    }

    /// <summary>
    /// How far the two paths are from each other where they both exist: AgOpenGPS's whole path
    /// off ours, and ours off AgOpenGPS's over the part level with it (our entry leg is longer:
    /// 16 m of lead-in against AgOpenGPS's 12 points, 4 m on a K-turn; that is 3.1, not these rows).
    /// </summary>
    private static (double oursOffTheirs, double theirsOffOurs) Apart(List<Vec3> ourPath, List<Vec3> theirPath)
    {
        // AgOpenGPS's path starts with its lead-in on the pass; ours from there on is level with it.
        double south = theirPath[0].Northing;
        var ourTurnPart = ourPath.Where(p => p.Northing >= south).ToList();
        return (FurthestFrom(ourTurnPart, theirPath), FurthestFrom(theirPath, ourPath));
    }

    private static (Figures ours, Figures theirs, List<Vec3> ourPath, List<Vec3> theirPath, bool turnLeft) Both(Rig rig)
    {
        var fence = SquareFence();
        var (ourPath, turnLeft) = PlanOurs(rig, fence);
        var theirPath = PlanTheirs(rig, fence, turnLeft);
        return (Measure(ourPath), Measure(theirPath), ourPath, theirPath, turnLeft);
    }

    private static string Compared(Rig rig, Figures ours, Figures theirs)
        => $"{rig}\n  ours:      {ours}\n  AgOpenGPS: {theirs}";

    // ── The checks ─────────────────────────────────────────────────────────────────

    private static readonly Rig[] Rigs =
    {
        new(6, 8, 1, 0), new(6, 8, 2, 0), new(6, 8, 5, 0),
        new(6, 5, 2, 0), new(6, 12, 2, 0),
        new(21, 8, 1, 0), new(21, 8, 2, 0), new(21, 8, 5, 0),
        new(21, 5, 2, 0), new(21, 12, 2, 0),
        new(6, 8, 2, 1), new(21, 8, 2, 1),
    };

    /// <summary>3.2: inside the boundary, and the Distance from boundary kept, as AgOpenGPS keeps it.</summary>
    [TestCaseSource(nameof(Rigs))]
    public void DistanceFromBoundaryIsKept(Rig rig)
    {
        var (ours, theirs, _, _, _) = Both(rig);
        string report = Compared(rig, ours, theirs);
        Assert.Multiple(() =>
        {
            Assert.That(ours.ClosestToFence, Is.GreaterThanOrEqualTo(rig.Setback - 0.15), "never nearer the fence than the setting: " + report);
            Assert.That(ours.ClosestToFence, Is.EqualTo(theirs.ClosestToFence).Within(0.5), "as close to the fence as AgOpenGPS: " + report);
            Assert.That(ours.Apex, Is.EqualTo(theirs.Apex).Within(0.5), "the turn's apex where AgOpenGPS puts it: " + report);
        });
    }

    /// <summary>3.4: the radius is the radius; it sets the tightest bend of the turn, as in AgOpenGPS.</summary>
    [TestCaseSource(nameof(Rigs))]
    public void TurnRadiusSetsTheTurn(Rig rig)
    {
        var (ours, theirs, _, _, turnLeft) = Both(rig);
        string report = Compared(rig, ours, theirs);
        int target = turnLeft ? -1 : 1;
        Assert.Multiple(() =>
        {
            Assert.That(ours.TightestRadius, Is.EqualTo(rig.Radius).Within(rig.Radius * 0.25), "the tightest bend is the set radius: " + report);
            Assert.That(ours.TightestRadius, Is.EqualTo(theirs.TightestRadius).Within(rig.Radius * 0.25), "as tight as AgOpenGPS: " + report);
            Assert.That(ours.EntryX, Is.EqualTo(PassX(rig.Width, 0)).Within(0.5), "enters from the current pass: " + report);
            if (rig.Style == 1)
                Assert.That(ours.ExitX, Is.EqualTo(theirs.ExitX).Within(0.3), "the K-turn's tail ends where AgOpenGPS's does: " + report);
            else
                Assert.That(ours.ExitX, Is.EqualTo(PassX(rig.Width, target)).Within(0.5), "leaves onto the next pass: " + report);
        });
    }

    /// <summary>3.5 (and 3.3): where both build the same shape, the K-turn and the wide turn, the paths lie on each other.</summary>
    [TestCase(21, 8, 2, 0, TestName = "WideTurn_IsAgOpenGpsPath")]
    [TestCase(21, 5, 2, 0, TestName = "WideTurn_Radius5_IsAgOpenGpsPath")]
    [TestCase(21, 5, 5, 0, TestName = "WideTurn_Radius5_Setback5_IsAgOpenGpsPath")]
    [TestCase(6, 8, 2, 1, TestName = "KTurn_IsAgOpenGpsPath")]
    [TestCase(21, 8, 2, 1, TestName = "KTurn_WideTool_IsAgOpenGpsPath")]
    public void SameShape_PathsLieOnEachOther(double width, double radius, double setback, int style)
    {
        var rig = new Rig(width, radius, setback, style);
        var (ours, theirs, ourPath, theirPath, _) = Both(rig);
        var (oursOffTheirs, theirsOffOurs) = Apart(ourPath, theirPath);
        string report = Compared(rig, ours, theirs) + $"\n  ours off AgOpenGPS's path by up to {oursOffTheirs:F2} m, AgOpenGPS's off ours by up to {theirsOffOurs:F2} m";
        Assert.Multiple(() =>
        {
            Assert.That(oursOffTheirs, Is.LessThan(0.3), report);
            Assert.That(theirsOffOurs, Is.LessThan(0.3), report);
        });
    }

    /// <summary>The figures of every rig, both apps, for reading.</summary>
    [Test, Explicit("prints the figures")]
    public void Report()
    {
        foreach (var rig in Rigs)
        {
            var (ours, theirs, ourPath, theirPath, _) = Both(rig);
            double south = theirPath.Min(p => p.Northing);
            var ourTurnPart = ourPath.Where(p => p.Northing >= south).ToList();
            int apexAt = ourPath.IndexOf(ourPath.OrderByDescending(p => p.Northing).First());
            TestContext.Out.WriteLine($"    our apex, point {apexAt} of {ourPath.Count}:{Around(ourPath, apexAt, 5)}");
            TestContext.Out.WriteLine(Compared(rig, ours, theirs)
                + $"\n  ours off AgOpenGPS's path by up to {FurthestFrom(ourPath, theirPath):F2} m (its turn part {FurthestFrom(ourTurnPart, theirPath):F2} m), AgOpenGPS's off ours by up to {FurthestFrom(theirPath, ourPath):F2} m"
                + (_plannerLog.Count > 0 ? "\n  planner: " + string.Join(" | ", _plannerLog) : ""));
        }
    }
}
