using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Robustness matrix for U-turn generation: boundary shape (straight / convex /
/// concave) × approach angle (90°..30°) × track type (curve vs 2-point AB) × turn
/// style (omega / sagitta). Each case must run the real generator (not the straight
/// SimpleFallback) and keep the entry leg on the current pass.
///
/// Geometry: a wide field whose TOP edge carries a centred bump (none/up/down). The
/// track passes through the edge crossing point at heading φ = 90° − θ, so it meets
/// the (locally horizontal) edge at angle θ. The track ENDS a few metres short of the
/// edge (the hard "curve stops at the field edge" case).
/// </summary>
[TestFixture]
public class CurveTurnRobustnessTests
{
    public enum Shape { Straight, Convex, Concave }

    private YouTurnCreationService _creation = null!;
    private PolygonOffsetService _offset = null!;
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
        c.Tool.SetSectionWidth(0, 800); // 8 m
        c.Guidance.UTurnDistanceFromBoundary = 1.0;
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnSmoothing = 3;
        c.Guidance.UTurnExtension = 10;

        _offset = new PolygonOffsetService();
        _log = new CaptureLogger();
        _creation = new YouTurnCreationService(_log, _offset, c);
    }

    private const double EdgeBase = 50.0;
    private static double Bump(Shape shape, double x)
    {
        double g = 10.0 * Math.Exp(-(x / 25.0) * (x / 25.0)); // centred at x=0, zero slope there
        return shape switch { Shape.Convex => g, Shape.Concave => -g, _ => 0.0 };
    }

    private (Boundary boundary, List<Vec3> headland, double crossY) MakeField(Shape shape)
    {
        const double W = 120, yBot = -80;
        var pts = new List<Vec2> { new(-W, yBot), new(W, yBot) };
        for (double x = W; x >= -W - 1e-9; x -= 4.0)
            pts.Add(new Vec2(x, EdgeBase + Bump(shape, x)));

        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = pts.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = false
            }
        };

        var inset = _offset.CreateInwardOffset(pts, 10.0) ?? pts;
        var headland = _offset.CalculatePointHeadings(inset);
        return (boundary, headland, EdgeBase + Bump(shape, 0));
    }

    private static (List<Vec3> pts, double headingPhi, Vec3 vehicle) MakeTrack(double angleDeg, bool ab, double crossY)
    {
        double phi = (90.0 - angleDeg) * Math.PI / 180.0; // heading from north
        double se = Math.Sin(phi), cn = Math.Cos(phi);
        Vec2 cross = new(0, crossY);
        Vec3 At(double s, double head) // s metres along heading from the crossing (negative = into field)
        {
            double e = cross.Easting + se * s;
            double n = cross.Northing + cn * s;
            return new Vec3(e, n, head);
        }

        var list = new List<Vec3>();
        if (ab)
        {
            list.Add(At(-120, phi));
            list.Add(At(-5, phi)); // ends 5 m short of the edge
        }
        else
        {
            var perpE = cn; var perpN = -se; // perpendicular to heading
            var raw = new List<Vec3>();
            for (double s = -120; s <= -5 + 1e-9; s += 2.0)
            {
                double w = 6.0 * Math.Sin(s / 25.0); // lateral wiggle => genuine curve
                var b = At(s, 0);
                raw.Add(new Vec3(b.Easting + perpE * w, b.Northing + perpN * w, 0));
            }
            for (int i = 0; i < raw.Count - 1; i++)
            {
                double dE = raw[i + 1].Easting - raw[i].Easting;
                double dN = raw[i + 1].Northing - raw[i].Northing;
                double h = Math.Atan2(dE, dN); if (h < 0) h += Math.PI * 2;
                raw[i] = new Vec3(raw[i].Easting, raw[i].Northing, h);
            }
            raw[^1] = new Vec3(raw[^1].Easting, raw[^1].Northing, raw[^2].Heading);
            list = raw;
        }

        // vehicle at a realistic trigger distance back along the track, heading phi
        var veh = At(-60, phi);
        return (list, phi, veh);
    }

    private static double PerpDistToPolyline(IReadOnlyList<Vec3> line, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0; i < line.Count - 1; i++)
            best = Math.Min(best, GeometryMath.PointToSegmentDistance(
                e, n, line[i].Easting, line[i].Northing, line[i + 1].Easting, line[i + 1].Northing));
        return best;
    }

    [Test, Combinatorial]
    public void Generates_NotFallback_Aligned(
        [Values] Shape shape,
        [Values(90, 75, 60, 45, 30)] int angle,
        [Values(false, true)] bool ab,
        [Values(0, 2)] int style)
    {
        ConfigurationStore.Instance.Guidance.UTurnStyle = style;

        var (boundary, headland, crossY) = MakeField(shape);
        var (pts, phi, veh) = MakeTrack(angle, ab, crossY);
        var track = ab
            ? Models.Track.Track.FromABLine("ab", pts[0], pts[1])
            : Models.Track.Track.FromCurve("c", pts);

        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };

        var result = _creation.CreateTurnPath(
            new Position { Easting = veh.Easting, Northing = veh.Northing },
            track, headingRadians: phi, abHeading: phi,
            boundary, headland, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 5);

        string tag = $"{shape} angle={angle} {(ab ? "AB" : "curve")} style={style}";
        if (result.UsedFallback || result.Path == null)
            foreach (var m in _log.Messages.Where(m => m.Contains("[YouTurn]"))) TestContext.WriteLine($"[{tag}] {m}");

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10), $"{tag}: should plot a turn");
        Assert.That(result.UsedFallback, Is.False, $"{tag}: should run the generator, not the straight fallback");

        double maxEntryDist = 0;
        for (int i = 0; i < Math.Min(5, result.Path!.Count); i++)
            maxEntryDist = Math.Max(maxEntryDist, PerpDistToPolyline(pts, result.Path[i].Easting, result.Path[i].Northing));
        Assert.That(maxEntryDist, Is.LessThan(0.3), $"{tag}: entry leg should lie on the pass (max perp {maxEntryDist:F2} m)");
    }
}
