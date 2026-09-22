// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.IO;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Contour;

namespace AgOpenWeb.Services.Tests.Contour;

/// <summary>#110: contour guidance, ported from AgOpenGPS CContour.</summary>
[TestFixture]
public class ContourGuidanceTests
{
    private const double North = 0, South = Math.PI;

    // 6 m tool, no overlap/offset, Pure Pursuit, 3 m wheelbase, 4 m look-ahead.
    private static ContourParams P(double offset = 0, bool stanley = false) => new(
        ToolWidth: 6, ToolOverlap: 0, ToolOffset: offset,
        UseStanley: stanley, StanleyHeadingErrorGain: 1, StanleyDistanceErrorGain: 0.8,
        Wheelbase: 3, MaxSteerAngle: 35, PurePursuitIntegralGain: 0,
        SideHillCompFactor: 0, GoalPointDistance: 4);

    // A pass recorded driving north along easting x, n from 0 to 200 m every 1 m.
    private static ContourGuidance WithNorthPass(double x = 0)
    {
        var c = new ContourGuidance();
        for (int n = 0; n <= 200; n++) c.Record(true, new Vec3(x, n, North), 0);
        c.Record(false, new Vec3(x, 200, North), 0);
        return c;
    }

    [Test]
    public void Recording_StartsWhenSectionsPaint_AndEndsWhenTheyStop()
    {
        var c = new ContourGuidance();
        c.Record(false, new Vec3(0, 0, North), 0);
        Assert.That(c.Strips, Is.Empty, "nothing recorded while no section paints");

        for (int n = 0; n < 10; n++) c.Record(true, new Vec3(0, n, North), 0);
        Assert.That(c.IsRecording, Is.True);
        c.Record(false, new Vec3(0, 10, North), 0);

        Assert.That(c.IsRecording, Is.False);
        Assert.That(c.Strips, Has.Count.EqualTo(1));
        Assert.That(c.Strips[0], Has.Count.EqualTo(10));
        Assert.That(c.PendingSave, Has.Count.EqualTo(1), "a finished strip is queued for Contour.txt");
    }

    [Test]
    public void ShortStrips_AreNotSaved()
    {
        var c = new ContourGuidance();
        for (int n = 0; n < 5; n++) c.Record(true, new Vec3(0, n, North), 0);
        c.Record(false, new Vec3(0, 5, North), 0);
        Assert.That(c.PendingSave, Is.Empty, "AgOpenGPS keeps strips of more than 5 points");
    }

    [Test]
    public void Points_AreShiftedByTheToolOffset_ToTheRight()
    {
        var c = new ContourGuidance();
        c.Record(true, new Vec3(10, 0, North), 1.5); // heading north: +offset is east
        Assert.That(c.Strips[0][0].Easting, Is.EqualTo(11.5).Within(1e-9));
        Assert.That(c.Strips[0][0].Northing, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void ComingBackBesideAPass_TheLineIsOneWidthOver()
    {
        var c = WithNorthPass(0);
        // Driving south 6 m east of the pass (the next pass over).
        c.BuildContourGuidanceLine(new Vec3(6.5, 150, South), South, P(), 0);

        Assert.That(c.Line.Count, Is.GreaterThanOrEqualTo(5));
        foreach (var pt in c.Line) Assert.That(pt.Easting, Is.EqualTo(6).Within(1e-6));
        Assert.That(c.StripNum, Is.EqualTo(0));
    }

    [Test]
    public void DrivingOnThePass_FollowsIt()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(0.5, 50, North), North, P(), 0);
        foreach (var pt in c.Line) Assert.That(pt.Easting, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void FarFromEveryStrip_NoLine()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(40, 50, North), North, P(), 0); // > 3 widths away
        Assert.That(c.Line, Is.Empty);
    }

    [Test]
    public void TheLine_IsRebuiltOnlyEvery2Seconds()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(6.5, 150, South), South, P(), 0);
        int v = c.LineVersion;
        c.BuildContourGuidanceLine(new Vec3(6.5, 140, South), South, P(), 1.0);
        Assert.That(c.LineVersion, Is.EqualTo(v));
        c.BuildContourGuidanceLine(new Vec3(6.5, 140, South), South, P(), 2.5);
        Assert.That(c.LineVersion, Is.Not.EqualTo(v));
    }

    [Test]
    public void PurePursuit_SteersBackOntoTheLine()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(0.5, 50, North), North, P(), 0);

        // 0.5 m east (right) of the line heading north → steer left (negative), XTE 0.5 m.
        var s = c.DistanceFromContourLine(new Vec3(0.5, 50, North), new Vec3(0.5, 53, North),
            new Vec2(0.5, 50), North, P(), 10, false, true, 0);

        Assert.That(s, Is.Not.Null);
        Assert.That(Math.Abs(s!.Value.CrossTrackError), Is.EqualTo(0.5).Within(1e-6));
        Assert.That(s.Value.SteerAngle, Is.LessThan(0));
        Assert.That(s.Value.GoalPoint.Northing, Is.GreaterThan(50), "the goal point is ahead");
    }

    [Test]
    public void Stanley_SteersBackOntoTheLine()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(0.5, 50, North), North, P(stanley: true), 0);
        var s = c.DistanceFromContourLine(new Vec3(0.5, 50, North), new Vec3(0.5, 53, North),
            new Vec2(0.5, 50), North, P(stanley: true), 10, false, true, 0);
        Assert.That(s, Is.Not.Null);
        Assert.That(s!.Value.SteerAngle, Is.LessThan(0));
    }

    [Test]
    public void Lock_NeedsALine_AndKeepsTheStrip()
    {
        var c = WithNorthPass(0);
        for (int n = 0; n <= 200; n++) c.Record(true, new Vec3(12, n, North), 0); // a second pass
        c.Record(false, new Vec3(12, 200, North), 0);

        Assert.That(c.SetLockToLine(), Is.False, "no line yet → can't lock");

        c.BuildContourGuidanceLine(new Vec3(0.5, 50, North), North, P(), 0);
        Assert.That(c.StripNum, Is.EqualTo(0));
        Assert.That(c.SetLockToLine(), Is.True);

        // Now nearer the second pass, but locked → still the first strip.
        c.BuildContourGuidanceLine(new Vec3(9, 60, North), North, P(), 3);
        Assert.That(c.StripNum, Is.EqualTo(0));
        Assert.That(c.IsLocked, Is.True);
    }

    [Test]
    public void Reset_ForgetsEverything()
    {
        var c = WithNorthPass(0);
        c.BuildContourGuidanceLine(new Vec3(0.5, 50, North), North, P(), 0);
        c.Reset();
        Assert.That(c.Strips, Is.Empty);
        Assert.That(c.Line, Is.Empty);
        Assert.That(c.PendingSave, Is.Empty);
        Assert.That(c.StripNum, Is.EqualTo(-1));
    }

    [Test]
    public void ContourTxt_RoundTrips_InAgOpenGPSFormat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "contour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = new List<Vec3> { new(1, 2, 0.5), new(3, 4, 0.25) };
            var b = new List<Vec3> { new(5, 6, 1.0) };
            ContourFilesService.Append(dir, new[] { a });
            ContourFilesService.Append(dir, new[] { b });

            var lines = File.ReadAllLines(Path.Combine(dir, "Contour.txt"));
            Assert.That(lines[0], Is.EqualTo("$Contour"));
            Assert.That(lines[1], Is.EqualTo("2"));
            Assert.That(lines[2], Is.EqualTo("1.000,2.000,0.50000"));

            var loaded = ContourFilesService.Load(dir);
            Assert.That(loaded, Has.Count.EqualTo(2));
            Assert.That(loaded[0][1].Easting, Is.EqualTo(3));
            Assert.That(loaded[1][0].Heading, Is.EqualTo(1.0));
        }
        finally { Directory.Delete(dir, true); }
    }
}
