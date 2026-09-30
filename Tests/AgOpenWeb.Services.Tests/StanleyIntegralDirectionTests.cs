// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Stanley's integral must correct a steady lateral bias in BOTH travel directions. It is
/// accumulated from the vehicle-relative pivot distance (sign-flipped when driving against
/// the line) but was applied as a line-relative offset (always toward heading + 90°), so
/// against the line it shifted the steer line toward the tractor and cancelled the
/// correction — XTE held in one direction and drifted in the other, worse with more
/// integral gain. Inherited from AgOpenGPS StanleyGuidanceABLine; in AgOpenWeb the same
/// routine also serves curves.
/// </summary>
[TestFixture]
public class StanleyIntegralDirectionTests
{
    private const double Wheelbase = 2.8;
    private const double SpeedKmh = 7.0;
    private const double BiasDeg = 2.0;     // steady mechanical / side-hill bias on the wheels
    private const double Dt = 0.1;          // 10 Hz, like the GPS cycle

    /// <summary>Drive one pass with a bicycle model; return mean |XTE| (m) over the last 100 m.
    /// <paramref name="xteOf"/> gives the true distance from the track for a position
    /// (default: the AB line x = 0).</summary>
    internal static double DrivePass(TrackModel track, bool againstLine, double integralGain,
        Func<double, double, double>? xteOf = null, (double x, double y, double h)? start = null)
    {
        xteOf ??= (px, _) => Math.Abs(px);
        var svc = new TrackGuidanceService();
        double v = SpeedKmh / 3.6;
        double y = againstLine ? 900 : -900, x = 0.3;               // start 30 cm off the line
        double h = againstLine ? Math.PI : 0;
        if (start is { } st) { x = st.x; y = st.y; h = st.h; }
        TrackGuidanceState? state = null;
        var xtes = new List<double>();
        int steps = (int)(290 / (v * Dt));                          // a 290 m pass
        for (int i = 0; i < steps; i++)
        {
            var pivot = new Vec3(x, y, h);
            var steer = new Vec3(x + Math.Sin(h) * Wheelbase, y + Math.Cos(h) * Wheelbase, h);
            var output = svc.CalculateGuidance(new TrackGuidanceInput
            {
                Track = track, PivotPosition = pivot, SteerPosition = steer, UseStanley = true,
                Wheelbase = Wheelbase, MaxSteerAngle = 35, StanleyHeadingErrorGain = 1.0,
                StanleyDistanceErrorGain = 0.8, StanleyIntegralGain = integralGain,
                FixHeading = h, AvgSpeed = SpeedKmh, IsAutoSteerOn = true, ImuRoll = 88888,
                PreviousState = state, IsHeadingSameWay = !againstLine,
            });
            state = output.State;
            double wheel = (output.SteerAngle + BiasDeg) * Math.PI / 180.0;
            h += v * Math.Tan(wheel) / Wheelbase * Dt;
            x += v * Math.Sin(h) * Dt; y += v * Math.Cos(h) * Dt;
            if (i * v * Dt > 190) xtes.Add(xteOf(x, y));
        }
        return xtes.Average();
    }

    private static readonly TrackModel AbLine = TrackModel.FromABLine("AB", new Vec3(0, -1000, 0), new Vec3(0, 1000, 0));

    // A 500 m-radius arc bending right (east), centred at (500, 0): x = 500 − √(R² − y²).
    private const double R = 500;
    private static readonly TrackModel Curve = BuildCurve();
    private static TrackModel BuildCurve()
    {
        var pts = new List<Vec3>();
        for (double y = -400; y <= 400; y += 2)
        {
            double x = 500 - Math.Sqrt(R * R - y * y);
            double h = Math.Atan2(-y / Math.Sqrt(R * R - y * y), 1.0);   // dx/dy for heading
            h = Math.Atan2(-y, Math.Sqrt(R * R - y * y));
            pts.Add(new Vec3(x, y, (h + 2 * Math.PI) % (2 * Math.PI)));
        }
        return new TrackModel { Name = "Curve", Points = pts, Type = TrackType.Curve };
    }
    private static double CurveXte(double x, double y) => Math.Abs(Math.Sqrt((x - 500) * (x - 500) + y * y) - R);
    private static (double, double, double) CurveStart(bool against)
    {
        double y = against ? 150 : -150;
        double x = 500 - Math.Sqrt(R * R - y * y) + 0.3;
        double h = Math.Atan2(-y, Math.Sqrt(R * R - y * y));
        if (against) h += Math.PI;
        return (x, y, (h + 2 * Math.PI) % (2 * Math.PI));
    }

    [TestCase(0.5)]
    [TestCase(1.0)]
    public void AbLine_integral_removes_the_bias_in_both_directions(double gain)
    {
        double without = DrivePass(AbLine, false, 0);
        double with = DrivePass(AbLine, false, gain);
        double against = DrivePass(AbLine, true, gain);
        Assert.Multiple(() =>
        {
            Assert.That(with, Is.LessThan(without / 4), "integral corrects the bias driving with the line");
            Assert.That(against, Is.LessThan(without / 4),
                $"integral must correct the bias driving AGAINST the line too (got {against * 100:F1} cm, no-integral {without * 100:F1} cm)");
        });
    }

    [Test]
    public void Curve_integral_removes_the_bias_in_both_directions()
    {
        double without = DrivePass(Curve, false, 0, CurveXte, CurveStart(false));
        double with = DrivePass(Curve, false, 1.0, CurveXte, CurveStart(false));
        double against = DrivePass(Curve, true, 1.0, CurveXte, CurveStart(true));
        TestContext.Out.WriteLine($"curve: no-integral {without * 100:F2} cm, with {with * 100:F2} cm, against {against * 100:F2} cm");
        Assert.That(with, Is.LessThan(without / 2));
        Assert.That(against, Is.LessThan(without / 2), $"against the curve: {against * 100:F1} cm");
    }

    [Test, Explicit("diagnostic table")]
    public void Table()
    {
        foreach (double g in new[] { 0.0, 0.5, 1.0, 2.0 })
            TestContext.Out.WriteLine($"gain {g}: with {DrivePass(AbLine, false, g) * 100:F2} cm, against {DrivePass(AbLine, true, g) * 100:F2} cm");
    }
}
