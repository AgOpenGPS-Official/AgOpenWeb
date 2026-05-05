// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Gps;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Tests for the antenna-to-pivot + roll correction transform. The
/// transform was previously embedded inside <c>GpsService.UpdateGpsData</c>
/// (with a (0,0)-skip guard that broke heading on the first non-zero
/// movement frame); now it lives in <see cref="AntennaToPivotTransform"/>
/// and is exercised here directly.
/// </summary>
[TestFixture]
public class RollCorrectionTests
{
    private VehicleConfig _vehicle = null!;

    [SetUp]
    public void Setup()
    {
        _vehicle = new VehicleConfig
        {
            AntennaHeight = 3.0,
            AntennaPivot = 0,
            AntennaOffset = 0,
        };
    }

    [Test]
    public void NoRoll_PositionUnchanged()
    {
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: 0);

        Assert.That(e, Is.EqualTo(100.0).Within(0.001));
        Assert.That(n, Is.EqualTo(200.0).Within(0.001));
    }

    [Test]
    public void RollRight_HeadingNorth_ShiftsPositionWest()
    {
        // Vehicle rolls right (positive roll) → antenna moves right.
        // Correction shifts position LEFT (west when heading north).
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: 10);

        double expectedOffset = Math.Sin(10.0 * Math.PI / 180.0) * 3.0;
        Assert.That(e, Is.LessThan(100.0),
            "Roll right heading north should decrease easting (shift west)");
        Assert.That(Math.Abs(e - 100.0), Is.EqualTo(expectedOffset).Within(0.05),
            "Correction magnitude should match sin(roll) * height");
        Assert.That(n, Is.EqualTo(200.0).Within(0.05),
            "Northing should be mostly unchanged for north heading");
    }

    [Test]
    public void RollLeft_HeadingNorth_ShiftsPositionEast()
    {
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: -10);

        Assert.That(e, Is.GreaterThan(100.0),
            "Roll left heading north should increase easting (shift east)");
    }

    [Test]
    public void RollRight_HeadingEast_ShiftsPositionNorth()
    {
        // Heading east → roll right pushes antenna south, correction goes north.
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: Math.PI / 2.0, _vehicle, imuRollDegrees: 10);

        Assert.That(n, Is.GreaterThan(200.0),
            "Roll right heading east should increase northing (shift north)");
    }

    [Test]
    public void ZeroAntennaHeight_NoCorrectionApplied()
    {
        _vehicle.AntennaHeight = 0;
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: 15);

        Assert.That(e, Is.EqualTo(100.0).Within(0.001),
            "No correction when antenna height is zero");
    }

    [Test]
    public void LargeAntennaHeight_LargerCorrection()
    {
        _vehicle.AntennaHeight = 3.0;
        double e1 = 100, n1 = 200;
        AntennaToPivotTransform.Apply(ref e1, ref n1, headingRadians: 0, _vehicle, imuRollDegrees: 5);
        double offset3m = Math.Abs(e1 - 100.0);

        _vehicle.AntennaHeight = 6.0;
        double e2 = 100, n2 = 200;
        AntennaToPivotTransform.Apply(ref e2, ref n2, headingRadians: 0, _vehicle, imuRollDegrees: 5);
        double offset6m = Math.Abs(e2 - 100.0);

        Assert.That(offset6m, Is.EqualTo(offset3m * 2).Within(0.01),
            "Double antenna height should double correction");
    }

    [Test]
    public void CorrectionMagnitude_MatchesFormula()
    {
        _vehicle.AntennaHeight = 4.0;
        double e = 500, n = 500;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: 15);

        double expected = Math.Sin(15.0 * Math.PI / 180.0) * 4.0; // ~1.035m
        double actualShift = Math.Sqrt(Math.Pow(e - 500, 2) + Math.Pow(n - 500, 2));

        Assert.That(actualShift, Is.EqualTo(expected).Within(0.05),
            $"Total shift should be sin(15)*4 = {expected:F3}m, got {actualShift:F3}m");
    }

    [Test]
    public void RollCorrectionCombinedWithAntennaPivot()
    {
        _vehicle.AntennaPivot = 2.0;   // 2m ahead
        _vehicle.AntennaHeight = 3.0;
        double e = 100, n = 200;
        AntennaToPivotTransform.Apply(ref e, ref n, headingRadians: 0, _vehicle, imuRollDegrees: 10);

        // Pivot offset moves position south (behind when heading north).
        // Roll correction moves position west.
        Assert.That(n, Is.LessThan(200.0), "Pivot should move position south");
        Assert.That(e, Is.LessThan(100.0), "Roll should move position west");
    }
}
