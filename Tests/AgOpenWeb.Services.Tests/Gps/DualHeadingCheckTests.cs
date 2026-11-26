// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests.Gps;

/// <summary>
/// The dual heading held against the direction the antenna moves. In the Vehicle &amp; Tool
/// scenarios a heading offset 90° out (antennas front to back, set up as side by side) took
/// the tractor out of its pass with AutoSteer on: guidance steered for a tractor driving
/// across its line. Reversing, tight turns and slow creeping must not read as that.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class DualHeadingCheckTests
{
    private GpsHeadingFusionService _f = null!;
    private const double WorkSpeed = 2.5; // m/s = 9 km/h
    private const double Step = 0.25;     // m between fixes: 10 Hz at 9 km/h

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.AutoDualFix = false;
        c.DualHeadingOffset = 0;
        c.DualReverseDistance = 0.25;
        _f = new GpsHeadingFusionService(ConfigurationStore.Instance);
    }

    // Antenna moves along `travelDeg` (0 = north) while the receiver reports `sentenceDeg`.
    private void Drive(double metres, double travelDeg, double sentenceDeg, double speed = WorkSpeed,
        double startE = 0, double startN = 0, bool dual = true)
    {
        double t = travelDeg * Math.PI / 180;
        for (double s = 0; s <= metres; s += Step)
            _f.FuseHeading(sentenceDeg, 0, false, speed, startE + s * Math.Sin(t), startN + s * Math.Cos(t), dual);
    }

    [Test]
    public void Offset_90_degrees_out_is_found_within_a_few_metres()
    {
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 90;   // the tractor faces north

        Drive(3, travelDeg: 0, sentenceDeg: 0);
        Assert.That(_f.IsDualHeadingOffTravel, Is.False, "one check is not enough");
        Drive(4, travelDeg: 0, sentenceDeg: 0, startN: 3.25);

        Assert.That(_f.IsDualHeadingOffTravel, Is.True);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.EqualTo(90).Within(1));
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(-90)]
    public void A_right_offset_reads_along_the_travel(double mounting)
    {
        // Antennas side by side read 90° to one side of the way the tractor faces: the
        // offset that puts them right.
        ConfigurationStore.Instance.Connections.DualHeadingOffset = mounting;
        Drive(20, travelDeg: 30, sentenceDeg: 30 - mounting);

        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.EqualTo(0).Within(1));
    }

    [Test]
    public void Reversing_reads_along_the_travel()
    {
        Drive(20, travelDeg: 180, sentenceDeg: 0);   // facing north, backing south

        Assert.That(_f.IsReverse, Is.True);
        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
    }

    [Test]
    public void A_tight_turn_with_the_antenna_ahead_of_the_axle_is_not_off()
    {
        // Rear axle on a 4 m circle at 6 km/h; antenna 2 m ahead of it: it moves up to ~27°
        // sideways of where the tractor faces.
        const double r = 4, ahead = 2, v = 6 / 3.6;
        for (double s = 0; s < 2 * Math.PI * r; s += 0.17)
        {
            double a = s / r;                                  // turning right from north
            double h = a;                                      // heading, rad
            double e = r - r * Math.Cos(a) + ahead * Math.Sin(h);
            double n = r * Math.Sin(a) + ahead * Math.Cos(h);
            _f.FuseHeading(h * 180 / Math.PI, 0, false, v, e, n, true);
            Assert.That(_f.IsDualHeadingOffTravel, Is.False, $"at {s:F1} m");
        }
        Assert.That(_f.DualHeadingOffTravelDeg, Is.LessThan(35));
    }

    [Test]
    public void Creeping_and_standing_still_are_not_judged()
    {
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 90;
        Drive(10, travelDeg: 0, sentenceDeg: 0, speed: 0.4);   // 1.4 km/h

        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.NaN);
    }

    [Test]
    public void A_position_jump_is_not_travel()
    {
        Drive(1, travelDeg: 0, sentenceDeg: 0);
        Drive(1, travelDeg: 0, sentenceDeg: 0, startE: 40);    // the simulator moved, say
        Drive(1, travelDeg: 0, sentenceDeg: 0, startE: 80);

        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
    }

    [Test]
    public void A_new_offset_is_judged_afresh_and_the_right_one_clears_it()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.DualHeadingOffset = 90;
        Drive(8, travelDeg: 0, sentenceDeg: 0);
        Assume.That(_f.IsDualHeadingOffTravel, Is.True);

        c.DualHeadingOffset = 0;
        _f.FuseHeading(0, 0, false, WorkSpeed, 0, 8.25, true);
        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.NaN);

        Drive(8, travelDeg: 0, sentenceDeg: 0, startN: 8.5);
        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.EqualTo(0).Within(1));
    }

    [Test]
    public void Without_a_dual_heading_there_is_nothing_to_judge()
    {
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 90;
        Drive(8, travelDeg: 0, sentenceDeg: 0);
        Assume.That(_f.IsDualHeadingOffTravel, Is.True);

        Drive(1, travelDeg: 0, sentenceDeg: 0, startN: 8.25, dual: false);   // $PANDA
        Assert.That(_f.IsDualHeadingOffTravel, Is.False);
        Assert.That(_f.DualHeadingOffTravelDeg, Is.NaN);
    }

    [Test]
    public void Also_with_auto_dual_fix()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 1.2;
        c.DualHeadingOffset = 90;

        Drive(8, travelDeg: 0, sentenceDeg: 0);

        Assert.That(_f.IsDualHeadingOffTravel, Is.True);
    }
}
