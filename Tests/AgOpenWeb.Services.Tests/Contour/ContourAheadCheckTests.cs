// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Contour;

namespace AgOpenWeb.Services.Tests.Contour;

/// <summary>
/// The contour line ahead that can't be driven as a lap (turns back, runs onto sprayed ground,
/// ends), found early enough for the operator to take over (P6 on Olival: the laps where the
/// field narrows).
/// </summary>
[TestFixture]
public class ContourAheadCheckTests
{
    private const double North = 0, South = Math.PI;

    /// <summary>A line north along x = 0 from n = 0 to <paramref name="length"/>, a point a metre.</summary>
    private static List<Vec3> NorthLine(double length)
    {
        var l = new List<Vec3>();
        for (double n = 0; n <= length + 1e-9; n += 1) l.Add(new Vec3(0, n, North));
        return l;
    }

    /// <summary>North for <paramref name="leg"/> m, a half circle of <paramref name="radius"/> to
    /// the east, then south again: a hairpin.</summary>
    private static List<Vec3> Hairpin(double leg, double radius)
    {
        var l = NorthLine(leg);
        int steps = (int)Math.Ceiling(Math.PI * radius);
        for (int i = 1; i <= steps; i++)
        {
            double a = Math.PI * i / steps;
            l.Add(new Vec3(radius - radius * Math.Cos(a), leg + radius * Math.Sin(a), North + a));
        }
        for (double n = leg - 1; n >= 0; n -= 1) l.Add(new Vec3(2 * radius, n, South));
        return l;
    }

    /// <summary>North for <paramref name="leg"/> m, then on at <paramref name="turnDeg"/>° to the right.</summary>
    private static List<Vec3> Corner(double leg, double turnDeg, double after = 60)
    {
        var l = NorthLine(leg);
        double h = turnDeg * Math.PI / 180;
        for (double s = 1; s <= after; s += 1) l.Add(new Vec3(Math.Sin(h) * s, leg + Math.Cos(h) * s, h));
        return l;
    }

    [Test]
    public void StraightLine_IsClear()
    {
        var r = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 10), North, 40, (_, _) => false);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [TestCase(72)]
    [TestCase(105)]
    public void ACornerOfTheField_IsClear(double turnDeg)
    {
        // Olival's corners turn 72° and 105°: contour drives them.
        var r = ContourAheadCheck.Check(Corner(30, turnDeg), new Vec2(0, 5), North, 60, null);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [Test]
    public void AHairpin_IsFoundAtTheTurn_BeforeTheTractorGetsThere()
    {
        // The lap turns back 3 m round where the field narrows, 40 m ahead of the tractor.
        var r = ContourAheadCheck.Check(Hairpin(50, 3), new Vec2(0, 10), North, 50, null);
        Assert.That(r.What, Is.EqualTo(ContourAhead.TurnsBack));
        Assert.That(r.AtM, Is.InRange(38.0, 46.0), "the distance is to the turn, not to the stretch before it");
    }

    [TestCase(12, false)]  // the last lap round a water pivot: the rig (8 m) drives it
    [TestCase(5, true)]    // tighter than the rig turns
    public void ACircle_TurnsBackOnlyWhenTighterThanTheRigTurns(double radius, bool turnsBack)
    {
        var l = new List<Vec3>();
        for (double s = 0; s <= 120; s += 1)
        {
            double a = s / radius;
            l.Add(new Vec3(radius - radius * Math.Cos(a), radius * Math.Sin(a), a));
        }
        var r = ContourAheadCheck.Check(l, new Vec2(0, 0), North, 40, null, turnRadiusM: 8);
        Assert.That(r.What, Is.EqualTo(turnsBack ? ContourAhead.TurnsBack : ContourAhead.Clear));
    }

    [Test]
    public void AHairpin_IsTighterThanTheRigTurns()
    {
        var r = ContourAheadCheck.Check(Hairpin(50, 3), new Vec2(0, 10), North, 50, null, turnRadiusM: 8);
        Assert.That(r.What, Is.EqualTo(ContourAhead.TurnsBack));
    }

    [Test]
    public void AHairpin_PastTheReach_IsNotYetReported()
    {
        var r = ContourAheadCheck.Check(Hairpin(100, 3), new Vec2(0, 10), North, 40, null);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [Test]
    public void AShortSpikeInTheLine_IsNoHairpin()
    {
        // Two pieces of line joined at a corner can leave a spike a metre back: the direction
        // is taken over 5 m, so it doesn't count.
        var l = NorthLine(30);
        l.Add(new Vec3(0, 29.2, South));
        for (double n = 31; n <= 80; n += 1) l.Add(new Vec3(0, n, North));
        var r = ContourAheadCheck.Check(l, new Vec2(0, 5), North, 60, null);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [Test]
    public void TheLineOnSprayedGround_IsFoundWhereItStarts()
    {
        // From n = 35 the line runs over the last lap.
        var r = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 10), North, 40, (_, n) => n >= 35);
        Assert.That(r.What, Is.EqualTo(ContourAhead.OnCoverage));
        Assert.That(r.AtM, Is.EqualTo(25).Within(1.0));
    }

    [Test]
    public void SprayedGroundUnderTheTractor_DoesNotCount()
    {
        // The implement sprays the line just behind and beside the tractor.
        var r = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 10), North, 40, (_, n) => n < 15);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [Test]
    public void AFewSprayedPoints_DoNotCount()
    {
        // Less than 3 m of the line over sprayed ground (a corner of the last lap).
        var r = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 10), North, 40, (_, n) => n >= 30 && n <= 31.5);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Clear));
    }

    [Test]
    public void ALineThatStopsShort_Ends()
    {
        var r = ContourAheadCheck.Check(NorthLine(30), new Vec2(0, 10), North, 40, (_, _) => false);
        Assert.That(r.What, Is.EqualTo(ContourAhead.Ends));
        Assert.That(r.AtM, Is.EqualTo(20).Within(0.5));
    }

    [Test]
    public void DrivenAgainstTheLinesOrder_LooksTheWayTheTractorDrives()
    {
        // The line's points run north, the tractor drives south: ahead is towards n = 0.
        var r = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 100), South, 40, (_, n) => n <= 70);
        Assert.That(r.What, Is.EqualTo(ContourAhead.OnCoverage));
        Assert.That(r.AtM, Is.EqualTo(30).Within(1.0));

        var end = ContourAheadCheck.Check(NorthLine(120), new Vec2(0, 25), South, 40, null);
        Assert.That(end.What, Is.EqualTo(ContourAhead.Ends));
        Assert.That(end.AtM, Is.EqualTo(25).Within(0.5));
    }

    [Test]
    public void TheNearestTrouble_IsReported()
    {
        // Sprayed ground from 20 m ahead, the hairpin further on.
        var r = ContourAheadCheck.Check(Hairpin(50, 3), new Vec2(0, 10), North, 50, (_, n) => n >= 30 && n < 49);
        Assert.That(r.What, Is.EqualTo(ContourAhead.OnCoverage));
    }

    [TestCase(2, 40)]
    [TestCase(10, 41.7)]
    [TestCase(30, 60)]
    public void Reach_IsFifteenSecondsOfDriving_Between40And60m(double kmh, double metres)
    {
        Assert.That(ContourAheadCheck.Reach(kmh), Is.EqualTo(metres).Within(0.1));
    }
}
