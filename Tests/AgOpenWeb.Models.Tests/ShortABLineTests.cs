// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using NUnit.Framework;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// AgOpenGPS's A+ line puts B 1 m from A (FormQuickAB.btnAPlus_Click). Read as it was, an offset pass
/// of it left out B (within 1 m of A) and had one point: no pass to steer anywhere but on the line
/// itself (a tester's field: A and B 0.96 m apart, AutoSteer held the wheels straight). Such a line is
/// read with B 100 m out along it, A and the heading as they were.
/// </summary>
[TestFixture]
public class ShortABLineTests
{
    private static double Deg(double rad) => (rad * 180.0 / Math.PI + 360.0) % 360.0;
    private static double Rad(double deg) => deg * Math.PI / 180.0;

    // The tester's line, as the field file has it: 0.961 m from A at 301.01°, its own heading 300.91°.
    private static List<Vec3> TestersLine(double storedHeading) => new()
    {
        new Vec3(-239.30, 53.00, storedHeading),
        new Vec3(-239.30 + Math.Sin(Rad(301.013)) * 0.961, 53.00 + Math.Cos(Rad(301.013)) * 0.961, storedHeading),
    };

    [Test]
    public void A_1_m_line_offsets_to_no_pass()
    {
        // What went wrong: the pass 300 m over has one point.
        var pass = CurveProcessing.CurvePass(TestersLine(Rad(300.913)), 300.0);
        Assert.That(pass.Points.Count, Is.LessThan(2));
    }

    [Test]
    public void A_short_line_gets_B_100_m_out_along_its_own_heading()
    {
        var line = TrackModel.LengthenShortABLine(TestersLine(Rad(300.913)), Rad(300.913));
        var a = line[0];
        var b = line[1];
        Assert.Multiple(() =>
        {
            Assert.That(a.Easting, Is.EqualTo(-239.30).Within(1e-9), "A stays");
            Assert.That(a.Northing, Is.EqualTo(53.00).Within(1e-9));
            Assert.That(GeometryMath.Distance(a, b), Is.EqualTo(TrackModel.ShortABLineNewLength).Within(1e-9));
            Assert.That(Deg(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)), Is.EqualTo(300.913).Within(1e-9),
                "along the line's own heading, not the 1 m between rounded points");
            Assert.That(Deg(a.Heading), Is.EqualTo(300.913).Within(1e-9));
            Assert.That(Deg(b.Heading), Is.EqualTo(300.913).Within(1e-9));
        });
        var pass = CurveProcessing.CurvePass(line, 300.0);
        Assert.That(pass.Points, Has.Count.EqualTo(2), "a pass 300 m over");
    }

    [Test]
    public void A_heading_that_does_not_agree_with_A_to_B_is_not_used()
    {
        var line = TrackModel.LengthenShortABLine(TestersLine(0), heading: 0);
        Assert.That(Deg(Math.Atan2(line[1].Easting - line[0].Easting, line[1].Northing - line[0].Northing)),
            Is.EqualTo(301.013).Within(1e-6), "A to B, a stored heading 59° off it is not the line's");
    }

    [Test]
    public void No_heading_takes_A_to_B()
    {
        var line = TrackModel.LengthenShortABLine(TestersLine(0));
        Assert.That(GeometryMath.Distance(line[0], line[1]), Is.EqualTo(TrackModel.ShortABLineNewLength).Within(1e-9));
        Assert.That(Deg(line[0].Heading), Is.EqualTo(301.013).Within(1e-6));
    }

    [Test]
    public void A_line_10_m_or_longer_a_curve_and_A_on_B_stay_as_they_are()
    {
        var longer = new List<Vec3> { new(0, 0, 0), new(0, 10, 0) };
        var curve = new List<Vec3> { new(0, 0, 0), new(0, 0.5, 0), new(0, 1, 0) };
        var onOneSpot = new List<Vec3> { new(5, 5, 0), new(5, 5, 0) };
        Assert.Multiple(() =>
        {
            Assert.That(TrackModel.LengthenShortABLine(longer), Is.SameAs(longer));
            Assert.That(TrackModel.LengthenShortABLine(curve), Is.SameAs(curve));
            Assert.That(TrackModel.LengthenShortABLine(onOneSpot), Is.SameAs(onOneSpot), "no direction to lengthen along");
        });
    }
}
