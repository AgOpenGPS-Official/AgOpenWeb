// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// Checklist 2.1 / 2.2: the steer angle is AgOpenGPS's, number for number. The same pose,
/// line, gains, speed and history go to <see cref="TrackGuidanceService"/> and to
/// <see cref="AgOpenGpsSteeringOracle"/> (the snapshot's code), one cycle over a grid of
/// cross-track errors, heading errors, speeds and directions, and then over a whole pass
/// with the integrals and filters working. The one deliberate difference, Stanley's integral
/// offset mirrored when driving against the line (StanleyIntegralDirectionTests), is kept
/// out of the way by giving those cases no integral.
/// </summary>
[TestFixture]
public class SteeringMatchesAgOpenGpsTests
{
    private const double Wheelbase = 2.8;
    private const double MaxSteer = 35;
    private const double LookAhead = 4.0;
    private const double HeadingGain = 1.0, DistanceGain = 0.8, IntegralGain = 0.5;
    private const double SideHill = 0.3;
    private const double Exact = 1e-9;      // the same arithmetic in the same order

    private static readonly Vec3 LineA = new(0, -1000, 0), LineB = new(0, 1000, 0);
    private static readonly TrackModel AbLine = TrackModel.FromABLine("AB", LineA, LineB);

    private static readonly double[] Xtes = { -10, -3, -1, -0.3, 0, 0.3, 1, 3, 10 };
    private static readonly double[] HeadingErrorsDeg = { -30, -10, 0, 10, 30 };
    private static readonly double[] SpeedsKmh = { 1, 4, 8, 15 };

    private readonly TrackGuidanceService _service = new();

    private static (Vec3 pivot, Vec3 steer) Pose(double xte, double northing, double headingErrDeg, bool against)
    {
        double h = (against ? Math.PI : 0) + headingErrDeg * Math.PI / 180.0;
        h = (h + 2 * Math.PI) % (2 * Math.PI);
        var pivot = new Vec3(xte, northing, h);
        var steer = new Vec3(xte + Math.Sin(h) * Wheelbase, northing + Math.Cos(h) * Wheelbase, h);
        return (pivot, steer);
    }

    private TrackGuidanceInput OurInput(TrackModel track, Vec3 pivot, Vec3 steer, double speed, bool against,
        bool stanley, bool reverse, double roll, double integralGain, TrackGuidanceState? state, bool global) => new()
    {
        Track = track, PivotPosition = pivot, SteerPosition = steer, UseStanley = stanley,
        Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer, GoalPointDistance = LookAhead,
        SideHillCompFactor = SideHill, PurePursuitIntegralGain = stanley ? 0 : integralGain,
        StanleyHeadingErrorGain = HeadingGain, StanleyDistanceErrorGain = DistanceGain,
        StanleyIntegralGain = stanley ? integralGain : 0,
        FixHeading = pivot.Heading, AvgSpeed = speed, IsReverse = reverse, IsAutoSteerOn = true,
        ImuRoll = roll, IsHeadingSameWay = !against, PreviousState = state,
        FindGlobalNearest = global, CurrentLocationIndex = state?.CurrentLocationIndex ?? 0,
    };

    private static AgOpenGpsSteeringOracle.PurePursuitInput TheirPurePursuit(Vec3 pivot, double speed, bool against,
        bool reverse, double roll, double integralGain, TrackGuidanceState? s) => new()
    {
        PivotPosition = pivot, CurrentLinePtA = LineA, CurrentLinePtB = LineB, ABHeading = 0,
        IsHeadingSameWay = !against, Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer,
        PurePursuitIntegralGain = integralGain, GoalPointDistance = LookAhead, SideHillCompFactor = SideHill,
        FixHeading = pivot.Heading, AvgSpeed = speed, IsReverse = reverse, IsAutoSteerOn = true, ImuRoll = roll,
        PreviousIntegral = s?.Integral ?? 0, PreviousPivotDistanceError = s?.PivotDistanceError ?? 0,
        PreviousPivotDistanceErrorLast = s?.PivotDistanceErrorLast ?? 0, PreviousCounter = s?.Counter ?? 0,
    };

    private static AgOpenGpsSteeringOracle.StanleyInput TheirStanley(Vec3 pivot, Vec3 steer, double speed,
        bool reverse, double roll, double integralGain, TrackGuidanceState? s) => new()
    {
        PivotPosition = pivot, SteerPosition = steer, StanleyHeadingErrorGain = HeadingGain,
        StanleyDistanceErrorGain = DistanceGain, StanleyIntegralGainAB = integralGain, MaxSteerAngle = MaxSteer,
        SideHillCompFactor = SideHill, AvgSpeed = speed, IsReverse = reverse, IsAutoSteerOn = true, ImuRoll = roll,
        PreviousIntegral = s?.Integral ?? 0, PreviousXTrackSteerCorrection = s?.XTrackSteerCorrection ?? 0,
        PreviousDistSteerError = s?.DistSteerError ?? 0, PreviousLastDistSteerError = s?.LastDistSteerError ?? 0,
        PreviousCounter = s?.Counter ?? 0, PreviousPivotDistanceError = s?.PivotDistanceError ?? 0,
    };

    private static IEnumerable<(double xte, double hErr, double speed, bool against, bool reverse, double roll)> Grid()
    {
        foreach (double xte in Xtes)
            foreach (double hErr in HeadingErrorsDeg)
                foreach (double speed in SpeedsKmh)
                    foreach (bool against in new[] { false, true })
                        foreach (bool reverse in new[] { false, true })
                            foreach (double roll in new[] { 88888.0, 3.0 })
                                yield return (xte, hErr, speed, against, reverse, roll);
    }

    [Test]
    public void PurePursuit_AbLine_OneCycle_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (var (xte, hErr, speed, against, reverse, roll) in Grid())
        {
            var (pivot, steer) = Pose(xte, 0, hErr, against);
            var ours = _service.CalculateGuidance(OurInput(AbLine, pivot, steer, speed, against, false, reverse, roll, IntegralGain, null, true));
            var theirs = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(TheirPurePursuit(pivot, speed, against, reverse, roll, IntegralGain, null));
            n++;
            if (Math.Abs(ours.SteerAngle - theirs.SteerAngle) > Exact || Math.Abs(ours.CrossTrackError - theirs.DistanceFromCurrentLinePivot) > Exact)
                wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse} roll {roll}: ours {ours.SteerAngle:F4}° / {ours.CrossTrackError:F3} m, AgOpenGPS {theirs.SteerAngle:F4}° / {theirs.DistanceFromCurrentLinePivot:F3} m");
        }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
    }

    [Test]
    public void Stanley_AbLine_OneCycle_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (var (xte, hErr, speed, against, reverse, roll) in Grid())
        {
            var (pivot, steer) = Pose(xte, 0, hErr, against);
            var ours = _service.CalculateGuidance(OurInput(AbLine, pivot, steer, speed, against, true, reverse, roll, IntegralGain, null, true));
            var theirs = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(LineA, LineB, TheirStanley(pivot, steer, speed, reverse, roll, IntegralGain, null), !against);
            n++;
            if (Math.Abs(ours.SteerAngle - theirs.SteerAngle) > Exact || Math.Abs(ours.CrossTrackError - theirs.DistanceFromCurrentLinePivot) > Exact)
                wrong.Add($"xte {xte} hErr {hErr} v {speed} against {against} rev {reverse} roll {roll}: ours {ours.SteerAngle:F4}° / {ours.CrossTrackError:F3} m, AgOpenGPS {theirs.SteerAngle:F4}° / {theirs.DistanceFromCurrentLinePivot:F3} m");
        }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} cases differ:\n" + string.Join("\n", wrong.Take(12)));
    }

    /// <summary>
    /// A pass driven with a bicycle model from 1 m off the line, with a 2° bias on the wheels
    /// so the integrals have work to do. Both controllers see the same poses (ours drives) and
    /// each keeps its own history: integral, filters, counters.
    /// </summary>
    private void DriveAPass(bool stanley, bool against, double integralGain, double tolerance)
    {
        const double speed = 7.0, biasDeg = 2.0, dt = 0.1;
        double v = speed / 3.6;
        double x = 1.0, y = against ? 150 : -150;
        double h = against ? Math.PI : 0;
        TrackGuidanceState? ours = null;
        TrackGuidanceState? theirs = null;
        double worst = 0; int worstAt = -1;
        int steps = (int)(300 / (v * dt));
        for (int i = 0; i < steps; i++)
        {
            var pivot = new Vec3(x, y, h);
            var steer = new Vec3(x + Math.Sin(h) * Wheelbase, y + Math.Cos(h) * Wheelbase, h);
            var o = _service.CalculateGuidance(OurInput(AbLine, pivot, steer, speed, against, stanley, false, 88888, integralGain, ours, ours == null));
            double theirAngle;
            if (stanley)
            {
                var t = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(LineA, LineB, TheirStanley(pivot, steer, speed, false, 88888, integralGain, theirs), !against);
                theirs = new TrackGuidanceState
                {
                    Integral = t.Integral, XTrackSteerCorrection = t.XTrackSteerCorrection, DistSteerError = t.DistSteerError,
                    LastDistSteerError = t.LastDistSteerError, Counter = t.Counter, PivotDistanceError = t.PivotDistanceError,
                };
                theirAngle = t.SteerAngle;
            }
            else
            {
                var t = AgOpenGpsSteeringOracle.CalculateGuidanceABLine(TheirPurePursuit(pivot, speed, against, false, 88888, integralGain, theirs));
                theirs = new TrackGuidanceState
                {
                    Integral = t.Integral, PivotDistanceError = t.PivotDistanceError,
                    PivotDistanceErrorLast = t.PivotDistanceErrorLast, Counter = t.Counter,
                };
                theirAngle = t.SteerAngle;
            }
            ours = o.State;
            double d = Math.Abs(o.SteerAngle - theirAngle);
            if (d > worst) { worst = d; worstAt = i; }

            double wheel = (o.SteerAngle + biasDeg) * Math.PI / 180.0;
            h += v * Math.Tan(wheel) / Wheelbase * dt;
            h = (h + 2 * Math.PI) % (2 * Math.PI);
            x += v * Math.Sin(h) * dt; y += v * Math.Cos(h) * dt;
        }
        Assert.That(worst, Is.LessThan(tolerance), $"largest difference {worst:E2}° at step {worstAt} of {steps}");
        Assert.That(Math.Abs(x), Is.LessThan(0.5), "the pass should have ended near the line");
    }

    [TestCase(false, TestName = "PurePursuit_AbLine_WholePass_WithTheIntegral_IsAgOpenGps")]
    [TestCase(true, TestName = "PurePursuit_AbLine_WholePass_AgainstTheLine_IsAgOpenGps")]
    public void PurePursuit_AbLine_WholePass(bool against) => DriveAPass(false, against, IntegralGain, Exact);

    [Test]
    public void Stanley_AbLine_WholePass_WithTheIntegral_IsAgOpenGps() => DriveAPass(true, false, IntegralGain, Exact);

    // Against the line the integral offset is mirrored on purpose (StanleyIntegralDirectionTests),
    // so this pass runs without it: everything else must still be AgOpenGPS's.
    [Test]
    public void Stanley_AbLine_WholePass_AgainstTheLine_IsAgOpenGps() => DriveAPass(true, true, 0, Exact);

    /// <summary>
    /// Pure Pursuit on a curve: the goal point walk and the steering are AgOpenGPS's; only the
    /// nearest-segment search is ours (#422), so on a smooth arc the two may sit on neighbouring
    /// segments and the angle may differ in the last decimals.
    /// </summary>
    [Test]
    public void PurePursuit_Curve_WholePass_IsAgOpenGps()
    {
        const double R = 500, speed = 7.0, dt = 0.1;
        var pts = new List<Vec3>();
        for (double py = -400; py <= 400; py += 2)
        {
            double px = 500 - Math.Sqrt(R * R - py * py);
            double ph = (Math.Atan2(-py, Math.Sqrt(R * R - py * py)) + 2 * Math.PI) % (2 * Math.PI);
            pts.Add(new Vec3(px, py, ph));
        }
        var curve = new TrackModel { Name = "Curve", Points = pts, Type = TrackType.Curve };

        double v = speed / 3.6;
        double y0 = -150;
        double x0 = 500 - Math.Sqrt(R * R - y0 * y0) + 1.0;
        double x = x0, y = y0, h = (Math.Atan2(-y0, Math.Sqrt(R * R - y0 * y0)) + 2 * Math.PI) % (2 * Math.PI);
        TrackGuidanceState? ours = null;
        var theirs = new AgOpenGpsSteeringOracle.CurvePurePursuitInput();
        bool theirGlobal = true; int theirIndex = 0;
        double worst = 0; int worstAt = -1;
        int steps = (int)(280 / (v * dt));
        for (int i = 0; i < steps; i++)
        {
            var pivot = new Vec3(x, y, h);
            var steer = new Vec3(x + Math.Sin(h) * Wheelbase, y + Math.Cos(h) * Wheelbase, h);
            var o = _service.CalculateGuidance(OurInput(curve, pivot, steer, speed, false, false, false, 88888, IntegralGain, ours, ours == null));
            var t = AgOpenGpsSteeringOracle.CalculateGuidanceCurve(new AgOpenGpsSteeringOracle.CurvePurePursuitInput
            {
                PivotPosition = pivot, CurvePoints = pts, CurrentLocationIndex = theirIndex, FindGlobalNearestPoint = theirGlobal,
                IsHeadingSameWay = true, Wheelbase = Wheelbase, MaxSteerAngle = MaxSteer, PurePursuitIntegralGain = IntegralGain,
                GoalPointDistance = LookAhead, SideHillCompFactor = SideHill, FixHeading = h, AvgSpeed = speed,
                IsAutoSteerOn = true, ImuRoll = 88888,
                PreviousIntegral = theirs.PreviousIntegral, PreviousPivotDistanceError = theirs.PreviousPivotDistanceError,
                PreviousPivotDistanceErrorLast = theirs.PreviousPivotDistanceErrorLast, PreviousCounter = theirs.PreviousCounter,
            });
            theirGlobal = t.FindGlobalNearestPoint; theirIndex = t.CurrentLocationIndex;
            theirs.PreviousIntegral = t.Integral; theirs.PreviousPivotDistanceError = t.PivotDistanceError;
            theirs.PreviousPivotDistanceErrorLast = t.PivotDistanceErrorLast; theirs.PreviousCounter = t.Counter;
            ours = o.State;
            ours.CurrentLocationIndex = o.CurrentLocationIndex;

            double d = Math.Abs(o.SteerAngle - t.SteerAngle);
            if (d > worst) { worst = d; worstAt = i; }

            double wheel = o.SteerAngle * Math.PI / 180.0;
            h += v * Math.Tan(wheel) / Wheelbase * dt;
            h = (h + 2 * Math.PI) % (2 * Math.PI);
            x += v * Math.Sin(h) * dt; y += v * Math.Cos(h) * dt;
        }
        Assert.That(worst, Is.LessThan(0.01), $"largest difference {worst:F4}° at step {worstAt} of {steps}");
    }
}
