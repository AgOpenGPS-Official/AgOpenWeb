// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Tool;

namespace AgOpenWeb.Services.Tests.Tool;

/// <summary>
/// Checklist 11.3 / 11.4: where the tool is, for every tool type, as AgOpenGPS puts it. The
/// oracle is AgOpenGPS's hitch / tank / tool block from Position.designer.cs, ported line for
/// line with its own signed lengths (behind is negative; the pivot distance positive with the
/// tool behind its wheels). Both are driven along the same path, a straight, a tight turn and
/// a straight, and the tool's position and heading are compared at every step.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ToolPositionMatchesAgOpenGpsTests
{
    /// <summary>AgOpenGPS Position.designer.cs, the "#region" that places the hitch, tank and tool.</summary>
    private sealed class Oracle
    {
        public double hitchLength, tankTrailingHitchLength, trailingHitchLength, trailingToolToPivotLength;
        public bool isToolTrailing, isToolTBT;
        public Vec3 tankPos, toolPivotPos, toolPos;
        public Vec2 hitchPos;

        public void Step(Vec3 pivot, double fixHeading, double distanceCurrentStepFix, int startCounter)
        {
            const double twoPI = Math.PI * 2;
            // pn.fix is the antenna; with the antenna on the pivot (AntennaPivot 0) it is the pivot.
            hitchPos = new Vec2(pivot.Easting + (Math.Sin(fixHeading) * hitchLength),
                                pivot.Northing + (Math.Cos(fixHeading) * hitchLength));

            if (isToolTrailing)
            {
                double over;
                if (isToolTBT)
                {
                    if (distanceCurrentStepFix != 0)
                    {
                        tankPos.Heading = Math.Atan2(hitchPos.Easting - tankPos.Easting, hitchPos.Northing - tankPos.Northing);
                        if (tankPos.Heading < 0) tankPos.Heading += twoPI;
                    }

                    over = Math.Abs(Math.PI - Math.Abs(Math.Abs(tankPos.Heading - fixHeading) - Math.PI));

                    if (over < 2.0 && startCounter > 50)
                    {
                        tankPos.Easting = hitchPos.Easting + (Math.Sin(tankPos.Heading) * (tankTrailingHitchLength));
                        tankPos.Northing = hitchPos.Northing + (Math.Cos(tankPos.Heading) * (tankTrailingHitchLength));
                    }

                    if (over > 2.0 | startCounter < 51)
                    {
                        tankPos.Heading = fixHeading;
                        tankPos.Easting = hitchPos.Easting + (Math.Sin(tankPos.Heading) * (tankTrailingHitchLength));
                        tankPos.Northing = hitchPos.Northing + (Math.Cos(tankPos.Heading) * (tankTrailingHitchLength));
                    }
                }
                else
                {
                    tankPos.Heading = fixHeading;
                    tankPos.Easting = hitchPos.Easting;
                    tankPos.Northing = hitchPos.Northing;
                }

                if (distanceCurrentStepFix != 0)
                {
                    toolPivotPos.Heading = Math.Atan2(tankPos.Easting - toolPivotPos.Easting, tankPos.Northing - toolPivotPos.Northing);
                    if (toolPivotPos.Heading < 0) toolPivotPos.Heading += twoPI;
                }

                over = Math.Abs(Math.PI - Math.Abs(Math.Abs(toolPivotPos.Heading - tankPos.Heading) - Math.PI));

                if (over < 1.9 && startCounter > 50)
                {
                    toolPivotPos.Easting = tankPos.Easting + (Math.Sin(toolPivotPos.Heading) * (trailingHitchLength));
                    toolPivotPos.Northing = tankPos.Northing + (Math.Cos(toolPivotPos.Heading) * (trailingHitchLength));
                }

                if (over > 1.9 | startCounter < 51)
                {
                    toolPivotPos.Heading = tankPos.Heading;
                    toolPivotPos.Easting = tankPos.Easting + (Math.Sin(toolPivotPos.Heading) * (trailingHitchLength));
                    toolPivotPos.Northing = tankPos.Northing + (Math.Cos(toolPivotPos.Heading) * (trailingHitchLength));
                }

                toolPos.Heading = toolPivotPos.Heading;
                toolPos.Easting = tankPos.Easting +
                    (Math.Sin(toolPivotPos.Heading) * (trailingHitchLength - trailingToolToPivotLength));
                toolPos.Northing = tankPos.Northing +
                    (Math.Cos(toolPivotPos.Heading) * (trailingHitchLength - trailingToolToPivotLength));
            }
            else
            {
                toolPivotPos = new Vec3(hitchPos.Easting, hitchPos.Northing, fixHeading);
                toolPos = new Vec3(hitchPos.Easting, hitchPos.Northing, fixHeading);
            }
        }
    }

    private const double Hitch = 2.0, Tank = 3.0, Trailing = 4.0;

    /// <summary>A straight, a 180° turn of radius 8 m (tight enough to swing a trailed tool far
    /// out), a straight; 0.2 m a step, as at 7 km/h and 10 Hz.</summary>
    private static IEnumerable<(Vec3 pivot, double heading)> Path(double r = 8.0)
    {
        double e = 0, n = 0, h = 0;
        const double step = 0.2;
        for (int i = 0; i < 150; i++) { n += step; yield return (new Vec3(e, n, h), h); }
        int turnSteps = (int)(Math.PI * r / step);
        for (int i = 0; i < turnSteps; i++)
        {
            h -= step / r;                                   // turning left
            e += Math.Sin(h) * step; n += Math.Cos(h) * step;
            yield return (new Vec3(e, n, (h + 2 * Math.PI) % (2 * Math.PI)), (h + 2 * Math.PI) % (2 * Math.PI));
        }
        for (int i = 0; i < 150; i++) { e += Math.Sin(h) * step; n += Math.Cos(h) * step; yield return (new Vec3(e, n, (h + 2 * Math.PI) % (2 * Math.PI)), (h + 2 * Math.PI) % (2 * Math.PI)); }
    }

    private static void Drive(string mount, double pivotDistance, double turnRadius = 8.0)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.AntennaPivot = 0;
        c.Vehicle.HitchLength = Hitch;
        c.Tool.HitchLength = Hitch;
        c.Tool.TankTrailingHitchLength = Tank;
        c.Tool.TrailingHitchLength = Trailing;
        c.Tool.TrailingToolToPivotLength = pivotDistance;
        c.Tool.IsToolFrontFixed = mount == "front";
        c.Tool.IsToolRearFixed = mount == "rear";
        c.Tool.IsToolTrailing = mount is "trailing" or "tbt";
        c.Tool.IsToolTBT = mount == "tbt";
        var ours = new ToolPositionService(c);

        var aog = new Oracle
        {
            // AgOpenGPS's signs: the hitch ahead (+) only for a front tool; trailing lengths behind (−).
            hitchLength = mount == "front" ? Hitch : -Hitch,
            tankTrailingHitchLength = -Tank,
            trailingHitchLength = -Trailing,
            trailingToolToPivotLength = pivotDistance,
            isToolTrailing = mount is "trailing" or "tbt",
            isToolTBT = mount == "tbt",
        };

        int step = 0;
        double worst = 0, worstHeading = 0, worstMap = 0; int worstAt = -1, worstMapAt = -1;
        foreach (var (pivot, heading) in Path(turnRadius))
        {
            ours.Update(pivot, heading);
            if (mount is "trailing" or "tbt")
            {
                // The map's implement (MainViewModel's render pull) is placed from the hitch and
                // the headings with TrailedToolCentre: it must land on the tool position, every step.
                var (me, mn) = ToolPositionService.TrailedToolCentre(ours.HitchPosition.Easting, ours.HitchPosition.Northing,
                    ours.TankPosition.Heading, ours.ToolHeading, c.Tool);
                double dm = Math.Sqrt(Math.Pow(me - ours.ToolPosition.Easting, 2) + Math.Pow(mn - ours.ToolPosition.Northing, 2));
                if (dm > worstMap) { worstMap = dm; worstMapAt = step; }
            }
            aog.Step(pivot, heading, distanceCurrentStepFix: 0.2, startCounter: step);
            if (step >= 60)                                  // both past their start-up snap
            {
                double d = Math.Sqrt(Math.Pow(ours.ToolPosition.Easting - aog.toolPos.Easting, 2) +
                                     Math.Pow(ours.ToolPosition.Northing - aog.toolPos.Northing, 2));
                double dh = Math.Abs(Math.PI - Math.Abs(Math.Abs(ours.ToolHeading - aog.toolPos.Heading) - Math.PI));
                if (d > worst) { worst = d; worstAt = step; }
                worstHeading = Math.Max(worstHeading, dh);
            }
            step++;
        }
        Assert.Multiple(() =>
        {
            Assert.That(worst, Is.LessThan(1e-6), $"{mount}, pivot {pivotDistance}: tool {worst:F3} m from AgOpenGPS's at step {worstAt}");
            Assert.That(worstHeading, Is.LessThan(1e-6), $"{mount}: tool heading off by {worstHeading:F4} rad");
            Assert.That(worstMap, Is.LessThan(1e-9), $"{mount}, pivot {pivotDistance}: the map's implement {worstMap:F3} m from the tool position at step {worstMapAt}");
        });
    }

    [Test] public void RearFixed_IsAgOpenGps() => Drive("rear", 0);
    [Test] public void FrontFixed_IsAgOpenGps() => Drive("front", 0);

    [TestCase(0.0)]
    [TestCase(1.0, TestName = "Trailing_ToolBehindItsWheels_IsAgOpenGps")]
    [TestCase(-1.0, TestName = "Trailing_ToolAheadOfItsWheels_IsAgOpenGps")]
    public void Trailing_IsAgOpenGps(double pivotDistance) => Drive("trailing", pivotDistance);

    [TestCase(0.0)]
    [TestCase(0.5, TestName = "Tbt_ToolBehindItsWheels_IsAgOpenGps")]
    public void Tbt_IsAgOpenGps(double pivotDistance) => Drive("tbt", pivotDistance);

    // A turn of 1 m radius, nearly on the spot: the trailed tool swings past AgOpenGPS's
    // spring-back angles (1.9 rad tool against what pulls it, 2.0 rad TBT tank against the tractor).
    [TestCase("trailing", TestName = "Trailing_Jackknifed_SpringsBackAsAgOpenGps")]
    [TestCase("tbt", TestName = "Tbt_Jackknifed_SpringsBackAsAgOpenGps")]
    public void Jackknifed_SpringsBackAsAgOpenGps(string mount) => Drive(mount, 0.5, turnRadius: 1.0);

    [Test]
    public void Trailing_ToolBehindItsWheels_WorksBehindThePivot()
    {
        // The plain meaning, without the oracle: "Behind" 1 m on a 4 m trailing hitch puts the
        // working centre 5 m behind the hitch, 1 m behind the tool's wheels.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.HitchLength = 2; c.Tool.TrailingHitchLength = 4; c.Tool.TrailingToolToPivotLength = 1;
        c.Tool.IsToolTrailing = true; c.Tool.IsToolFrontFixed = c.Tool.IsToolRearFixed = c.Tool.IsToolTBT = false;
        var t = new ToolPositionService(c);
        t.Update(new Vec3(0, 0, 0), 0);
        Assert.That(t.ToolPivotPosition.Northing, Is.EqualTo(-6).Within(1e-9), "the wheels, 4 m behind the 2 m hitch");
        Assert.That(t.ToolPosition.Northing, Is.EqualTo(-7).Within(1e-9), "the working centre, 1 m behind the wheels");
    }
}
