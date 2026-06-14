using System;
using System.Collections.Generic;
using System.Linq;

using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Guidance;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Services.Geometry;
using AgValoniaGPS.Services.YouTurn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgValoniaGPS.Services.Tests.YouTurn;

/// <summary>
/// Local repro for the curved-AB U-turn work. Verifies the curve generator
/// actually RUNS (not the straight SimpleFallback) and that the entry/exit legs
/// lie on the real offset passes — at BOTH field ends (heading same way and
/// opposite). Lets us iterate the geometry without a device.
/// </summary>
[TestFixture]
public class CurveTurnAlignmentTests
{
    private YouTurnCreationService _creation = null!;
    private CaptureLogger _log = null!;

    private sealed class CaptureLogger : ILogger<YouTurnCreationService>
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Messages.Add($"{logLevel}: {formatter(state, ex)}");
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800); // 8 m implement -> ActualToolWidth = 8
        c.Guidance.UTurnDistanceFromBoundary = 1.0;
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnSmoothing = 3;
        c.Guidance.UTurnExtension = 10;
        c.Guidance.UTurnStyle = 0; // Albin/omega

        _log = new CaptureLogger();
        _creation = new YouTurnCreationService(_log, new PolygonOffsetService());
    }

    // A gentle S-ish curve running south->north, points every 2 m, with headings.
    private static List<Vec3> CurvePoints(double yStart, double yEnd)
    {
        var pts = new List<Vec3>();
        for (double y = yStart; y <= yEnd + 1e-9; y += 2.0)
        {
            double x = 8.0 * Math.Sin(y / 60.0); // curves in x
            pts.Add(new Vec3(x, y, 0));
        }
        // headings = atan2(dE, dN) toward the next point
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double dE = pts[i + 1].Easting - pts[i].Easting;
            double dN = pts[i + 1].Northing - pts[i].Northing;
            double h = Math.Atan2(dE, dN);
            if (h < 0) h += Math.PI * 2;
            pts[i] = new Vec3(pts[i].Easting, pts[i].Northing, h);
        }
        pts[^1] = new Vec3(pts[^1].Easting, pts[^1].Northing, pts[^2].Heading);
        return pts;
    }

    private static Boundary Square() => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = false
        }
    };

    private static List<Vec3> HeadlandSquare() => new()
    {
        new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0)
    };

    // A wavy (non-straight) closed boundary so the turn boundary the arc seats against
    // is itself curved — probes whether the turn generalises beyond straight field edges.
    private static List<Vec2> WavyRing(double radius)
    {
        var pts = new List<Vec2>();
        for (int deg = 0; deg < 360; deg += 6)
        {
            double a = deg * Math.PI / 180.0;
            double r = radius + 6.0 * Math.Sin(3 * a); // lobed, not a plain circle
            pts.Add(new Vec2(r * Math.Sin(a), r * Math.Cos(a)));
        }
        return pts;
    }

    private static Boundary WavyBoundary() => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = WavyRing(50).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = false
        }
    };

    private static List<Vec3> WavyHeadland() =>
        WavyRing(40).Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    private static double PerpDistToPolyline(List<Vec3> line, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0; i < line.Count - 1; i++)
        {
            double d = GeometryMath.PointToSegmentDistance(
                e, n, line[i].Easting, line[i].Northing, line[i + 1].Easting, line[i + 1].Northing);
            if (d < best) best = d;
        }
        return best;
    }

    // Curve EXTENDS past the boundary (y -100..100, boundary ±50): the generator
    // should find the boundary crossing and produce a real curve turn (no fallback).
    [Test]
    public void NorthEnd_CurveCrossesBoundary_RunsCurvePath_NotFallback()
    {
        var curve = CurvePoints(-100, 100);
        var track = Models.Track.Track.FromCurve("c", curve);

        // vehicle near the north headland, on the curve, heading north (same way)
        var near = curve.First(p => p.Northing >= 38);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: near.Heading, abHeading: near.Heading,
            Square(), HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False,
            "curve crosses the boundary, so the curve generator should run — NOT the straight fallback");

        // The entry leg (first ~5 m of the path) must lie on the current pass (the base curve).
        double maxEntryDist = 0;
        for (int i = 0; i < Math.Min(5, result.Path!.Count); i++)
            maxEntryDist = Math.Max(maxEntryDist, PerpDistToPolyline(curve, result.Path[i].Easting, result.Path[i].Northing));
        Assert.That(maxEntryDist, Is.LessThan(0.3),
            $"entry leg should lie on the current curved pass (max perp dist {maxEntryDist:F2} m)");
    }

    [Test]
    public void SouthEnd_CurveCrossesBoundary_RunsCurvePath_NotFallback()
    {
        var curve = CurvePoints(-100, 100);
        var track = Models.Track.Track.FromCurve("c", curve);

        // vehicle near the south headland, heading south (opposite way)
        var near = curve.First(p => p.Northing >= -38);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = false, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        double southHeading = near.Heading + Math.PI;
        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: southHeading, abHeading: near.Heading,
            Square(), HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False,
            "curve crosses the boundary, so the curve generator should run — NOT the straight fallback");

        double maxEntryDist = 0;
        for (int i = 0; i < Math.Min(5, result.Path!.Count); i++)
            maxEntryDist = Math.Max(maxEntryDist, PerpDistToPolyline(curve, result.Path[i].Easting, result.Path[i].Northing));
        Assert.That(maxEntryDist, Is.LessThan(0.3),
            $"entry leg should lie on the current curved pass (max perp dist {maxEntryDist:F2} m)");
    }

    // Curve ENDS inside the field (y -45..45, turn boundary ±49): the recorded curve
    // never crosses the turn boundary, so today the generator fails and the orchestration
    // drops to the straight SimpleFallback — the misaligned turn the operator sees. These
    // two tests reproduce that and should turn GREEN once the curve is made to reach the
    // boundary (and stay aligned at BOTH ends).
    // style: 0 = Albin/omega, 2 = Sagitta (the operator's style). Both must run the
    // curve generator (not the fallback) AND keep the entry leg on the current pass,
    // at BOTH field ends, even when the recorded curve ends at the field edge.
    [TestCase(true, 0, TestName = "NorthEnd_FieldEdge_Omega")]
    [TestCase(false, 0, TestName = "SouthEnd_FieldEdge_Omega")]
    [TestCase(true, 2, TestName = "NorthEnd_FieldEdge_Sagitta")]
    [TestCase(false, 2, TestName = "SouthEnd_FieldEdge_Sagitta")]
    public void CurveEndsAtFieldEdge_RunsCurvePath_Aligned(bool sameWay, int style)
    {
        ConfigurationStore.Instance.Guidance.UTurnStyle = style;

        var curve = CurvePoints(-45, 45);
        var track = Models.Track.Track.FromCurve("c", curve);

        var near = curve.First(p => p.Northing >= 0);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        double heading = sameWay ? near.Heading : near.Heading + Math.PI;
        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: heading, abHeading: near.Heading,
            Square(), HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        foreach (var m in _log.Messages.Where(m => m.Contains("[YouTurn]"))) TestContext.WriteLine(m);
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False,
            "even when the curve ends at the field edge, the curve generator should run (not the straight fallback)");

        double maxEntryDist = 0;
        for (int i = 0; i < Math.Min(5, result.Path!.Count); i++)
            maxEntryDist = Math.Max(maxEntryDist, PerpDistToPolyline(curve, result.Path[i].Easting, result.Path[i].Northing));
        Assert.That(maxEntryDist, Is.LessThan(0.3),
            $"entry leg should lie on the current curved pass (max perp dist {maxEntryDist:F2} m)");
    }

    // The cyan NextTrack curve must be offset-then-EXTENDED exactly like the U-turn
    // exit leg (BuildNewOffsetCurveList) and the post-turn magenta line, so the green
    // exit leg lands on it with no gap. Regression for the exit-side gap the operator
    // saw: the cyan next-track curve was the bare offset (only as long as the recorded
    // pass), so it stopped short of the exit leg's end — and the magenta line that
    // replaced it after the turn was longer.
    [Test]
    public void NextTrackCurve_IsExtended_ToMeetExitLeg()
    {
        var curve = CurvePoints(-45, 45); // recorded pass spans y in [-45, 45]
        var track = Models.Track.Track.FromCurve("c", curve);

        var pathing = new YouTurnPathingService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTurnPathingService>.Instance);
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        pathing.ComputeNextTrack(track, abHeading: 0, guidance, turn,
            uTurnSkipRows: 0, isSkipWorkedMode: false, selectedTrack: null);

        Assert.That(turn.NextTrack, Is.Not.Null, "next track should be computed");
        var pts = turn.NextTrack!.Points;
        double maxN = pts.Max(p => p.Northing);
        double minN = pts.Min(p => p.Northing);

        // A bare (non-extended) offset of the recorded pass also spans ~[-45, 45]; the
        // extended curve must reach well past BOTH ends so the exit leg meets the cyan line.
        Assert.That(maxN, Is.GreaterThan(60),
            $"cyan NextTrack curve should extend past the north field edge (max N {maxN:F1} m)");
        Assert.That(minN, Is.LessThan(-60),
            $"cyan NextTrack curve should extend past the south field edge (min N {minN:F1} m)");
    }

    // The EXIT leg must lie on the cyan/post-turn next pass (basePoints offset by the
    // pass-number distance) — the line the tractor actually drives after completion.
    // Reproduces the lateral offset the operator saw: cyan ~1 m off the green exit leg.
    [TestCase(true, 0, TestName = "ExitLeg_OnNextPass_North_Omega")]
    [TestCase(false, 0, TestName = "ExitLeg_OnNextPass_South_Omega")]
    [TestCase(true, 2, TestName = "ExitLeg_OnNextPass_North_Sagitta")]
    [TestCase(false, 2, TestName = "ExitLeg_OnNextPass_South_Sagitta")]
    [TestCase(true, 0, 0.5, TestName = "ExitLeg_OnNextPass_North_Omega_ToolOffset")]
    [TestCase(false, 0, 0.5, TestName = "ExitLeg_OnNextPass_South_Omega_ToolOffset")]
    [TestCase(true, 2, 0.5, TestName = "ExitLeg_OnNextPass_North_Sagitta_ToolOffset")]
    [TestCase(false, 2, 0.5, TestName = "ExitLeg_OnNextPass_South_Sagitta_ToolOffset")]
    public void ExitLeg_LiesOnNextPass(bool sameWay, int style, double toolOffset = 0.0)
    {
        ConfigurationStore.Instance.Guidance.UTurnStyle = style;
        ConfigurationStore.Instance.Tool.Offset = toolOffset;

        var curve = CurvePoints(-100, 100);
        var track = Models.Track.Track.FromCurve("c", curve);

        var near = curve.First(p => p.Northing >= 38);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        // The post-turn / cyan pass: basePoints offset by the pass-number distance.
        double width = ConfigurationStore.Instance.ActualToolWidth - ConfigurationStore.Instance.Tool.Overlap;
        bool positiveOffset = turn.IsTurnLeft ^ guidance.IsHeadingSameWay;
        int offsetChange = positiveOffset ? 1 : -1; // skip 0 -> move 1 pass
        double nextDistAway = width * (guidance.HowManyPathsAway + offsetChange);
        var nextPass = CurveProcessing.ExtendCurveEnds(CurveProcessing.CreateOffsetCurve(curve, nextDistAway));

        double heading = sameWay ? near.Heading : near.Heading + Math.PI;
        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: heading, abHeading: near.Heading,
            Square(), HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False, "curve generator should run");

        var path = result.Path!;
        // Exit leg = last few points of the turn path.
        double maxExitDist = 0;
        for (int i = Math.Max(0, path.Count - 5); i < path.Count; i++)
            maxExitDist = Math.Max(maxExitDist, PerpDistToPolyline(nextPass, path[i].Easting, path[i].Northing));
        TestContext.Out.WriteLine($"max exit-leg perp dist to cyan/next pass = {maxExitDist:F3} m");

        Assert.That(maxExitDist, Is.LessThan(0.3),
            $"exit leg should lie on the cyan/post-turn next pass (max perp dist {maxExitDist:F2} m)");
    }

    // Recorded curve ends WELL INSIDE the field (y -30..30, headland ±40, boundary ±50)
    // so the whole turn region sits on the ExtendCurveEnds extension — like the operator's
    // real field (curve ends ~16 m before the headland). The exit leg must still land on
    // the cyan/pass-number next pass.
    // The NOISY variant perturbs the recorded curve's end-point heading (the value the old
    // SignedPerpDistanceToCurve latched onto out on the extension). That projection skewed
    // the exit offset by metres (~4 m at 20°) — the operator's lateral exit shift. The
    // pass-number offset is immune because it never measures the arc end.
    [TestCase(true, 0, 0.0, TestName = "ExitLeg_CurveEndsInside_North_Omega")]
    [TestCase(false, 0, 0.0, TestName = "ExitLeg_CurveEndsInside_South_Omega")]
    [TestCase(true, 2, 0.0, TestName = "ExitLeg_CurveEndsInside_North_Sagitta")]
    [TestCase(false, 2, 0.0, TestName = "ExitLeg_CurveEndsInside_South_Sagitta")]
    [TestCase(true, 0, 20.0, TestName = "ExitLeg_NoisyEnd_North_Omega")]
    [TestCase(false, 0, 20.0, TestName = "ExitLeg_NoisyEnd_South_Omega")]
    [TestCase(true, 2, 20.0, TestName = "ExitLeg_NoisyEnd_North_Sagitta")]
    [TestCase(false, 2, 20.0, TestName = "ExitLeg_NoisyEnd_South_Sagitta")]
    public void ExitLeg_CurveEndsInside_LandsOnNextPass(bool sameWay, int style, double endHeadingNoiseDeg)
    {
        ConfigurationStore.Instance.Guidance.UTurnStyle = style;

        var curve = CurvePoints(-30, 30); // ends 10 m short of the headland, 20 m short of boundary
        if (endHeadingNoiseDeg != 0.0)
        {
            double noise = endHeadingNoiseDeg * Math.PI / 180.0;
            curve[0] = new Vec3(curve[0].Easting, curve[0].Northing, curve[0].Heading + noise);
            curve[^1] = new Vec3(curve[^1].Easting, curve[^1].Northing, curve[^1].Heading + noise);
        }
        var track = Models.Track.Track.FromCurve("c", curve);

        var near = curve.First(p => p.Northing >= 28);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        double width = ConfigurationStore.Instance.ActualToolWidth - ConfigurationStore.Instance.Tool.Overlap;
        bool positiveOffset = turn.IsTurnLeft ^ guidance.IsHeadingSameWay;
        int offsetChange = positiveOffset ? 1 : -1;
        double nextDistAway = width * (guidance.HowManyPathsAway + offsetChange);
        var cyan = CurveProcessing.ExtendCurveEnds(CurveProcessing.CreateOffsetCurve(curve, nextDistAway));

        double heading = sameWay ? near.Heading : near.Heading + Math.PI;
        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: heading, abHeading: near.Heading,
            Square(), HeadlandSquare(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False, "curve generator should run");

        var path = result.Path!;
        double exitToCyan = 0;
        for (int i = Math.Max(0, path.Count - 5); i < path.Count; i++)
            exitToCyan = Math.Max(exitToCyan, PerpDistToPolyline(cyan, path[i].Easting, path[i].Northing));
        TestContext.Out.WriteLine($"noise={endHeadingNoiseDeg}deg: max exit-leg -> cyan(pass-number) = {exitToCyan:F3} m");

        Assert.That(exitToCyan, Is.LessThan(0.3),
            $"exit leg must lie on the cyan/post-turn next pass regardless of recorded-curve end noise (was {exitToCyan:F2} m)");
    }

    // Curved track meeting a CURVED (wavy/lobed) boundary, both ends, both styles.
    // The arc seats against a curved turn boundary instead of a straight field edge.
    [TestCase(true, 0, TestName = "North_CurvedBoundary_Omega")]
    [TestCase(false, 0, TestName = "South_CurvedBoundary_Omega")]
    [TestCase(true, 2, TestName = "North_CurvedBoundary_Sagitta")]
    [TestCase(false, 2, TestName = "South_CurvedBoundary_Sagitta")]
    public void CurvedTrack_MeetsCurvedBoundary_RunsCurvePath_Aligned(bool sameWay, int style)
    {
        ConfigurationStore.Instance.Guidance.UTurnStyle = style;

        var curve = CurvePoints(-45, 45);
        var track = Models.Track.Track.FromCurve("c", curve);

        var near = curve.First(p => p.Northing >= 0);
        var pos = new Position { Easting = near.Easting, Northing = near.Northing };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        double heading = sameWay ? near.Heading : near.Heading + Math.PI;
        var result = _creation.CreateTurnPath(
            pos, track, headingRadians: heading, abHeading: near.Heading,
            WavyBoundary(), WavyHeadland(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        foreach (var m in _log.Messages.Where(m => m.Contains("[YouTurn]"))) TestContext.WriteLine(m);
        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), "should plot a turn");
        Assert.That(result.UsedFallback, Is.False,
            "a curved boundary should still drive the curve generator, not the straight fallback");

        double maxEntryDist = 0;
        for (int i = 0; i < Math.Min(5, result.Path!.Count); i++)
            maxEntryDist = Math.Max(maxEntryDist, PerpDistToPolyline(curve, result.Path[i].Easting, result.Path[i].Northing));
        Assert.That(maxEntryDist, Is.LessThan(0.3),
            $"entry leg should lie on the current curved pass (max perp dist {maxEntryDist:F2} m)");
    }
}
