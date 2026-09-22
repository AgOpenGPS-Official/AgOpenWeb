// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests.Gps;

/// <summary>#112: heading follows AgOpenGPS's "Fix" and "Dual" heading sources
/// (Position.designer.cs).</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GpsHeadingFusionServiceTests
{
    private GpsHeadingFusionService _service = null!;
    private const double Fast = 3.0; // m/s = 10.8 km/h, above the 1.5 km/h start speed

    [SetUp]
    public void SetUp()
    {
        _service = new GpsHeadingFusionService(ConfigurationStore.Instance);

        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = false;
        c.AutoDualFix = false;
        c.DualHeadingOffset = 0;
        c.DualSwitchSpeed = 2.0;   // km/h
        c.MinGpsStep = 0.05;       // m
        c.FixToFixDistance = 0.5;  // m
        c.HeadingFusionWeight = 0.3;
    }

    // Drive north from (0,0) one step at a time; returns the last heading.
    private double DriveNorth(int steps, double stepM = 0.3, double speedMs = Fast,
        double imu = 0, bool imuValid = false, double startN = 0, double gpsHeading = 0)
    {
        double h = double.NaN;
        for (int i = 0; i < steps; i++)
            h = _service.FuseHeading(gpsHeading, imu, imuValid, speedMs, 0, startN + i * stepM);
        return h;
    }

    [Test]
    public void BeforeAFirstHeading_TheSentenceHeadingPassesThrough()
    {
        double h = _service.FuseHeading(45, 0, false, 0.1, 0, 0);
        Assert.That(h, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void NoFirstHeadingBelow1Point5Kmh()
    {
        // 0.3 m/s = 1.08 km/h: fixes are moving north but too slowly to set a heading.
        double h = DriveNorth(6, speedMs: 0.3, gpsHeading: 45);
        Assert.That(h, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void SingleAntenna_HeadingFromFixToFix()
    {
        double h = DriveNorth(6);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));

        // Turn east.
        for (int i = 1; i <= 5; i++) h = _service.FuseHeading(0, 0, false, Fast, i * 0.3, 1.5);
        Assert.That(h, Is.EqualTo(90).Within(1e-6));
    }

    [Test]
    public void MovesShorterThanMinGpsStep_KeepTheHeading()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 1.0;
        double h = DriveNorth(4, stepM: 1.2);         // heading north set
        // Tiny sideways jitter under the min step must not swing it.
        h = _service.FuseHeading(0, 0, false, Fast, 0.5, 3.6 + 0.1);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void FusionWeight_IsTheGpsShareTimesPoint2_LikeAgOpenGPS()
    {
        Assert.That(GpsHeadingFusionService.FusionShareToWeight, Is.EqualTo(0.2));
        Assert.That(new ConnectionConfig().HeadingFusionWeight, Is.EqualTo(0.3),
            "default 30% GPS = AgOpenGPS fusionWeight 0.06");
    }

    [Test]
    public void Imu_IsSnappedToGpsAtStart_ThenSlowlyPulledOntoGps()
    {
        // IMU says 10°, travel is due north (0°). At start the offset snaps to -10°.
        double h = DriveNorth(3, imu: 10, imuValid: true);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));

        // IMU now drifts to 20° while travel stays north: output = IMU + offset, and
        // each GPS heading pulls the offset back by 6% (0.3 share × 0.2) of the error.
        h = DriveNorth(1, imu: 20, imuValid: true, startN: 0.9);
        // offset: -10° + (0 - (20-10)) × 0.06 = -10.6° → 20 - 10.6 = 9.4°
        Assert.That(h, Is.EqualTo(9.4).Within(1e-6));
    }

    [Test]
    public void WithImu_HeadingFollowsImuWhileStopped()
    {
        DriveNorth(3, imu: 10, imuValid: true);            // offset -10°
        double h = _service.FuseHeading(0, 40, true, 0, 0, 0.6); // stopped, IMU turned to 40°
        Assert.That(h, Is.EqualTo(30).Within(1e-6));
    }

    [Test]
    public void Dual_AppliesOffsetAndNormalizes()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.DualHeadingOffset = 10.0;

        double h = _service.FuseHeading(355, 0, false, Fast, 0, 0);
        Assert.That(h, Is.EqualTo(5).Within(1e-9));
    }

    [Test]
    public void Dual_StaysOnDualWhenAutoDualFixIsOff_EvenFast()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        double h = DriveNorth(6, gpsHeading: 30);   // travel north, dual says 30°
        Assert.That(h, Is.EqualTo(30).Within(1e-9));
    }

    [Test]
    public void Dual_SwitchesToFixAboveTheSwitchSpeed_InKmh()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 5.0; // km/h
        c.HeadingFusionWeight = 1.0; // 100% GPS → offset moves 20% per fix

        // 1.2 m/s = 4.32 km/h: below the switch speed → dual heading (30°).
        double slow = DriveNorth(6, speedMs: 1.2, gpsHeading: 30);
        Assert.That(slow, Is.EqualTo(30).Within(1e-9),
            "below the switch speed the dual heading is used (the old code did the opposite, and in m/s)");

        // 1.7 m/s = 6.12 km/h: above → Fix, with dual as the IMU pulled toward travel (0°).
        double fast = DriveNorth(10, speedMs: 1.7, gpsHeading: 30, startN: 1.8);
        Assert.That(fast, Is.LessThan(30).And.GreaterThan(0),
            "above the switch speed the fix heading pulls the dual-as-IMU heading toward travel");
    }

    [Test]
    public void Reset_KeepsTheHeading_ButForgetsStoredFixes()
    {
        DriveNorth(6);                          // heading north
        _service.Reset();

        // A fix in a new frame far away must not yield a heading toward it.
        double h = _service.FuseHeading(0, 0, false, Fast, 500, -300);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }
}
