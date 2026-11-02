// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Contour;
using AgOpenWeb.Services.YouTurn;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// Checklist 2.1 / 2.2 for the two steering paths that aren't the track's: contour following
/// (7.15) and the U-turn (3.1). The same pose, line, gains, speed and history go to
/// <see cref="ContourGuidance"/> / <see cref="YouTurnGuidanceService"/> and to
/// <see cref="AgOpenGpsContourAndTurnOracle"/> (the snapshot's code). The U-turn's Pure Pursuit
/// has guards of ours for the end of the path and an omega's fold (#337), so it is compared
/// where those don't fire: mid-path, close to it.
/// </summary>
[TestFixture]
public class ContourAndTurnSteeringMatchesAgOpenGpsTests
{
    private const double Wheelbase = 2.8, MaxSteer = 35, LookAhead = 4.0;
    private const double HeadingGain = 1.0, DistanceGain = 0.8, IntegralGain = 0.5, SideHill = 0.3;
    private const double Exact = 1e-9;

    private static readonly double[] Xtes = { -3, -1, -0.3, 0, 0.3, 1, 3 };
    private static readonly double[] HeadingErrorsDeg = { -30, -10, 0, 10, 30 };
    private static readonly double[] SpeedsKmh = { 1, 4, 8, 15 };

    private static ContourParams Params(bool stanley) => new(
        ToolWidth: 6, ToolOverlap: 0, ToolOffset: 0, UseStanley: stanley,
        StanleyHeadingErrorGain: HeadingGain, StanleyDistanceErrorGain: DistanceGain,
        Wheelbase: Wheelbase, MaxSteerAngle: MaxSteer, PurePursuitIntegralGain: IntegralGain,
        SideHillCompFactor: SideHill, GoalPointDistance: LookAhead);

    // A contour line north along x = 0, a point every metre, as a recorded strip gives.
    private static List<Vec3> StraightLine()
    {
        var pts = new List<Vec3>();
        for (int y = -100; y <= 100; y++) pts.Add(new Vec3(0, y, 0));
        return pts;
    }

    // A gentle S: x = 2 sin(y / 30), with each point's heading along the line.
    private static List<Vec3> WavyLine()
    {
        var pts = new List<Vec3>();
        for (int y = -100; y <= 100; y++)
        {
            double x = 2 * Math.Sin(y / 30.0);
            double dxdy = 2 / 30.0 * Math.Cos(y / 30.0);
            pts.Add(new Vec3(x, y, (Math.Atan2(dxdy, 1) + 2 * Math.PI) % (2 * Math.PI)));
        }
        return pts;
    }

    private static (Vec3 pivot, Vec3 steer, Vec2 fix) Pose(List<Vec3> line, int at, double xte, double headingErrDeg, bool against)
    {
        var p = line[at];
        double h = p.Heading + (against ? Math.PI : 0) + headingErrDeg * Math.PI / 180.0;
        h = (h + 2 * Math.PI) % (2 * Math.PI);
        // xte to the right of the line's own heading
        double e = p.Easting + Math.Cos(p.Heading) * xte, n = p.Northing - Math.Sin(p.Heading) * xte;
        var pivot = new Vec3(e, n, h);
        var steer = new Vec3(e + Math.Sin(h) * Wheelbase, n + Math.Cos(h) * Wheelbase, h);
        var fix = new Vec2(e + Math.Sin(h) * 0.8, n + Math.Cos(h) * 0.8);   // the antenna, 0.8 m ahead of the pivot
        return (pivot, steer, fix);
    }

    [TestCase(false, TestName = "Contour_Stanley_OneCycle_StraightLine_IsAgOpenGps")]
    [TestCase(true, TestName = "Contour_Stanley_OneCycle_WavyLine_IsAgOpenGps")]
    public void Contour_Stanley_OneCycle_IsAgOpenGps(bool wavy)
    {
        var line = wavy ? WavyLine() : StraightLine();
        var wrong = new List<string>();
        int n = 0;
        foreach (double xte in Xtes)
            foreach (double hErr in HeadingErrorsDeg)
                foreach (double speed in SpeedsKmh)
                    foreach (bool against in new[] { false, true })
                        foreach (bool reverse in new[] { false, true })
                            foreach (int at in new[] { 40, 100, 160 })
                            {
                                var c = new ContourGuidance();
                                c.Line.AddRange(line);
                                var (pivot, steer, fix) = Pose(line, at, xte, hErr, against);
                                var ours = c.DistanceFromContourLine(pivot, steer, fix, pivot.Heading, Params(true), speed, reverse, true, 88888);
                                var theirs = AgOpenGpsContourAndTurnOracle.ContourStanley(line, pivot, steer, HeadingGain, DistanceGain, MaxSteer, speed, reverse);
                                n++;
                                if (ours == null || theirs == null)
                                {
                                    if (ours != null || theirs != null) wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse} at {at}: one side gave nothing");
                                    continue;
                                }
                                if (Math.Abs(ours.Value.SteerAngle - theirs.Value.steerAngle) > Exact || Math.Abs(ours.Value.CrossTrackError - theirs.Value.distance) > Exact)
                                    wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse} at {at}: ours {ours.Value.SteerAngle:F4}° / {ours.Value.CrossTrackError:F3} m, AgOpenGPS {theirs.Value.steerAngle:F4}° / {theirs.Value.distance:F3} m");
                            }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
    }

    [TestCase(false, TestName = "Contour_PurePursuit_OneCycle_StraightLine_IsAgOpenGps")]
    [TestCase(true, TestName = "Contour_PurePursuit_OneCycle_WavyLine_IsAgOpenGps")]
    public void Contour_PurePursuit_OneCycle_IsAgOpenGps(bool wavy)
    {
        var line = wavy ? WavyLine() : StraightLine();
        var wrong = new List<string>();
        int n = 0;
        foreach (double xte in Xtes)
            foreach (double hErr in HeadingErrorsDeg)
                foreach (double speed in SpeedsKmh)
                    foreach (bool against in new[] { false, true })
                        foreach (bool reverse in new[] { false, true })
                            foreach (double roll in new[] { 88888.0, 3.0 })
                            {
                                var c = new ContourGuidance();
                                c.Line.AddRange(line);
                                var (pivot, steer, fix) = Pose(line, 100, xte, hErr, against);
                                var ours = c.DistanceFromContourLine(pivot, steer, fix, pivot.Heading, Params(false), speed, reverse, true, roll);
                                var theirs = AgOpenGpsContourAndTurnOracle.CalculateGuidanceContour(new AgOpenGpsContourAndTurnOracle.ContourPurePursuitInput
                                {
                                    PivotPosition = pivot, FixPosition = new Vec3(fix.Easting, fix.Northing, pivot.Heading), ContourPoints = line,
                                    Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer, PurePursuitIntegralGain = IntegralGain, GoalPointDistance = LookAhead,
                                    SideHillCompFactor = SideHill, FixHeading = pivot.Heading, AvgSpeed = speed, IsReverse = reverse, IsAutoSteerOn = true, ImuRoll = roll,
                                });
                                n++;
                                if (ours == null) { wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse}: ours gave nothing"); continue; }
                                if (Math.Abs(ours.Value.SteerAngle - theirs.SteerAngle) > Exact || Math.Abs(ours.Value.CrossTrackError - theirs.DistanceFromCurrentLinePivot) > Exact)
                                    wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse} roll {roll}: ours {ours.Value.SteerAngle:F4}° / {ours.Value.CrossTrackError:F3} m, AgOpenGPS {theirs.SteerAngle:F4}° / {theirs.DistanceFromCurrentLinePivot:F3} m");
                            }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
    }

    /// <summary>
    /// A pass along the straight contour on a bicycle model, with a 2° bias on the wheels so
    /// the integral works; ours drives, both see the same poses, each keeps its own history.
    /// </summary>
    [TestCase(false, TestName = "Contour_PurePursuit_WholePass_WithTheIntegral_IsAgOpenGps")]
    [TestCase(true, TestName = "Contour_PurePursuit_WholePass_AgainstTheLine_IsAgOpenGps")]
    public void Contour_PurePursuit_WholePass_IsAgOpenGps(bool against)
    {
        const double speed = 7.0, biasDeg = 2.0, dt = 0.1;
        var line = StraightLine();
        var c = new ContourGuidance();
        c.Line.AddRange(line);
        double v = speed / 3.6;
        double x = 1.0, y = against ? 80 : -80;
        double h = against ? Math.PI : 0;
        double prevIntegral = 0, prevErr = 0, prevErrLast = 0; int prevCounter = 0;
        double worst = 0; int worstAt = -1;
        int steps = (int)(150 / (v * dt));
        for (int i = 0; i < steps; i++)
        {
            var pivot = new Vec3(x, y, h);
            var steer = new Vec3(x + Math.Sin(h) * Wheelbase, y + Math.Cos(h) * Wheelbase, h);
            var fix = new Vec2(x + Math.Sin(h) * 0.8, y + Math.Cos(h) * 0.8);
            var o = c.DistanceFromContourLine(pivot, steer, fix, h, Params(false), speed, false, true, 88888);
            var t = AgOpenGpsContourAndTurnOracle.CalculateGuidanceContour(new AgOpenGpsContourAndTurnOracle.ContourPurePursuitInput
            {
                PivotPosition = pivot, FixPosition = new Vec3(fix.Easting, fix.Northing, h), ContourPoints = line,
                Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer, PurePursuitIntegralGain = IntegralGain, GoalPointDistance = LookAhead,
                SideHillCompFactor = SideHill, FixHeading = h, AvgSpeed = speed, IsAutoSteerOn = true, ImuRoll = 88888,
                PreviousIntegral = prevIntegral, PreviousPivotDistanceError = prevErr, PreviousPivotDistanceErrorLast = prevErrLast, PreviousCounter = prevCounter,
            });
            prevIntegral = t.Integral; prevErr = t.PivotDistanceError; prevErrLast = t.PivotDistanceErrorLast; prevCounter = t.Counter;
            Assert.That(o, Is.Not.Null, $"no guidance at step {i}");
            double d = Math.Abs(o!.Value.SteerAngle - t.SteerAngle);
            if (d > worst) { worst = d; worstAt = i; }

            double wheel = (o.Value.SteerAngle + biasDeg) * Math.PI / 180.0;
            h += v * Math.Tan(wheel) / Wheelbase * dt;
            h = (h + 2 * Math.PI) % (2 * Math.PI);
            x += v * Math.Sin(h) * dt; y += v * Math.Cos(h) * dt;
        }
        Assert.That(worst, Is.LessThan(Exact), $"largest difference {worst:E2}° at step {worstAt} of {steps}");
        Assert.That(Math.Abs(x), Is.LessThan(0.5), "the pass should have ended near the line");
    }

    // A right-hand semicircle of radius 8 from (0,0) heading north, a point every 0.5 m, as the
    // turn planner lays it: the heading at each point is the path's own.
    private static List<Vec3> Semicircle()
    {
        const double R = 8;
        var pts = new List<Vec3>();
        int n = (int)(Math.PI * R / 0.5);
        for (int i = 0; i <= n; i++)
        {
            double th = Math.PI * i / n;
            pts.Add(new Vec3(R - R * Math.Cos(th), R * Math.Sin(th), th));
        }
        return pts;
    }

    private static (Vec3 pivot, Vec3 steer) TurnPose(List<Vec3> path, int at, double offset, double headingErrDeg)
    {
        var p = path[at];
        double h = (p.Heading + headingErrDeg * Math.PI / 180.0 + 2 * Math.PI) % (2 * Math.PI);
        double e = p.Easting + Math.Cos(p.Heading) * offset, n = p.Northing - Math.Sin(p.Heading) * offset;
        return (new Vec3(e, n, h), new Vec3(e + Math.Sin(h) * Wheelbase, n + Math.Cos(h) * Wheelbase, h));
    }

    private static YouTurnGuidanceInput OurTurn(List<Vec3> path, Vec3 pivot, Vec3 steer, bool stanley, double speed, bool reverse, int style) => new()
    {
        TurnPath = path, PivotPosition = pivot, SteerPosition = steer, Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer,
        UseStanley = stanley, StanleyHeadingErrorGain = HeadingGain, StanleyDistanceErrorGain = DistanceGain,
        GoalPointDistance = LookAhead, UTurnCompensation = 1.1, FixHeading = pivot.Heading, AvgSpeed = speed, IsReverse = reverse, UTurnStyle = style,
    };

    private static AgOpenGpsContourAndTurnOracle.YouTurnInput TheirTurn(List<Vec3> path, Vec3 pivot, Vec3 steer, bool stanley, double speed, bool reverse, int style) => new()
    {
        TurnPath = path, PivotPosition = pivot, SteerPosition = steer, Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer,
        UseStanley = stanley, StanleyHeadingErrorGain = HeadingGain, StanleyDistanceErrorGain = DistanceGain,
        GoalPointDistance = LookAhead, UTurnCompensation = 1.1, FixHeading = pivot.Heading, AvgSpeed = speed, IsReverse = reverse, UTurnStyle = style,
    };

    // Heading errors stay within ±30°: ours folds the error into ±90° as meant, AgOpenGPS's
    // fold has `< Math.PI` where it means `< -Math.PI`, so past 90° the two part company.
    // (The contour's Stanley keeps AgOpenGPS's fold as written, and matches it everywhere.)
    [Test]
    public void UTurn_Stanley_OneCycle_IsAgOpenGps()
    {
        var path = Semicircle();
        var svc = new YouTurnGuidanceService();
        var wrong = new List<string>();
        int n = 0, complete = 0;
        foreach (int at in new[] { 6, 12, 20, 30, 40 })
            foreach (double offset in new[] { -2.0, -0.5, 0, 0.5, 2.0 })
                foreach (double hErr in HeadingErrorsDeg)
                    foreach (double speed in SpeedsKmh)
                        foreach (bool reverse in new[] { false, true })
                            foreach (int style in new[] { 0, 1 })
                            {
                                var (pivot, steer) = TurnPose(path, at, offset, hErr);
                                var ours = svc.CalculateGuidance(OurTurn(path, pivot, steer, true, speed, reverse, style));
                                var theirs = AgOpenGpsContourAndTurnOracle.CalculateGuidance(TheirTurn(path, pivot, steer, true, speed, reverse, style));
                                n++;
                                if (ours.IsTurnComplete != theirs.IsTurnComplete)
                                {
                                    wrong.Add($"at {at} off {offset} hErr {hErr} v {speed} rev {reverse} style {style}: complete ours {ours.IsTurnComplete}, AgOpenGPS {theirs.IsTurnComplete}");
                                    continue;
                                }
                                if (ours.IsTurnComplete) { complete++; continue; }
                                if (Math.Abs(ours.SteerAngle - theirs.SteerAngle) > Exact || Math.Abs(ours.DistanceFromCurrentLine - theirs.DistanceFromCurrentLine) > Exact)
                                    wrong.Add($"at {at} off {offset} hErr {hErr} v {speed} rev {reverse} style {style}: ours {ours.SteerAngle:F4}° / {ours.DistanceFromCurrentLine:F3} m, AgOpenGPS {theirs.SteerAngle:F4}° / {theirs.DistanceFromCurrentLine:F3} m");
                            }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
        Assert.That(n - complete, Is.GreaterThan(n / 2), "most cases should steer, not complete the turn");
    }

    [Test]
    public void UTurn_PurePursuit_OneCycle_MidPath_IsAgOpenGps()
    {
        var path = Semicircle();
        var svc = new YouTurnGuidanceService();
        var wrong = new List<string>();
        int n = 0;
        foreach (int at in new[] { 8, 15, 25, 35 })
            foreach (double offset in new[] { -1.0, -0.3, 0, 0.3, 1.0 })
                foreach (double hErr in HeadingErrorsDeg)
                    foreach (double speed in SpeedsKmh)
                    {
                        var (pivot, steer) = TurnPose(path, at, offset, hErr);
                        var ours = svc.CalculateGuidance(OurTurn(path, pivot, steer, false, speed, false, 0));
                        var theirs = AgOpenGpsContourAndTurnOracle.CalculateGuidance(TheirTurn(path, pivot, steer, false, speed, false, 0));
                        n++;
                        if (theirs.IsTurnComplete) { wrong.Add($"at {at} off {offset} hErr {hErr}: AgOpenGPS completed the turn mid-path"); continue; }
                        if (ours.IsTurnComplete || ours.AntiTangentGuardFired) { wrong.Add($"at {at} off {offset} hErr {hErr}: ours completed or guarded mid-path"); continue; }
                        if (Math.Abs(ours.SteerAngle - theirs.SteerAngle) > Exact || Math.Abs(ours.DistanceFromCurrentLine - theirs.DistanceFromCurrentLine) > Exact)
                            wrong.Add($"at {at} off {offset} hErr {hErr} v {speed}: ours {ours.SteerAngle:F4}° / {ours.DistanceFromCurrentLine:F3} m, AgOpenGPS {theirs.SteerAngle:F4}° / {theirs.DistanceFromCurrentLine:F3} m");
                    }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
    }
}
