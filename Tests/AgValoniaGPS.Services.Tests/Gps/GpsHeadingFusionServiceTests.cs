// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Gps;

namespace AgValoniaGPS.Services.Tests.Gps;

[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GpsHeadingFusionServiceTests
{
    private GpsHeadingFusionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new GpsHeadingFusionService();

        // Reset config to a known default state.
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = false;
        c.DualHeadingOffset = 0;
        c.DualSwitchSpeed = 2.0;
        c.MinGpsStep = 0.05;
        c.FixToFixDistance = 0.2;
        c.HeadingFusionWeight = 1.0; // all GPS, no IMU
    }

    [Test]
    public void Single_antenna_below_MinGpsStep_returns_raw_heading()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 1.0;

        double result = _service.FuseHeading(
            gpsHeading: 45.0, imuHeading: 0, imuValid: false,
            speedMs: 0.1, easting: 0, northing: 0);

        Assert.That(result, Is.EqualTo(45.0).Within(1e-9));
    }

    [Test]
    public void Single_antenna_above_MinGpsStep_uses_fix_to_fix_after_first_call()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 0.05;
        ConfigurationStore.Instance.Connections.FixToFixDistance = 0.2;

        // First call primes the fix-to-fix state; no previous position yet so returns raw.
        double first = _service.FuseHeading(
            gpsHeading: 0.0, imuHeading: 0, imuValid: false,
            speedMs: 10.0, easting: 0, northing: 0);

        Assert.That(first, Is.EqualTo(0.0).Within(1e-9),
            "first call has no previous position, returns raw heading");

        // Second call: moved 10m east — fix-to-fix heading = atan2(10, 0) = 90°.
        double second = _service.FuseHeading(
            gpsHeading: 0.0, imuHeading: 0, imuValid: false,
            speedMs: 10.0, easting: 10, northing: 0);

        Assert.That(second, Is.EqualTo(90.0).Within(1e-9),
            "fix-to-fix replaces raw heading when travelling faster than MinGpsStep");
    }

    [Test]
    public void Dual_GPS_mode_applies_offset_and_normalizes()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 10.0;

        double result = _service.FuseHeading(
            gpsHeading: 355.0, imuHeading: 0, imuValid: false,
            speedMs: 10.0, easting: 0, northing: 0);

        // 355 + 10 = 365 → normalize to 5.
        Assert.That(result, Is.EqualTo(5.0).Within(1e-9));
    }

    [Test]
    public void Dual_GPS_below_switch_speed_uses_fix_to_fix_when_available()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        ConfigurationStore.Instance.Connections.DualHeadingOffset = 0;
        ConfigurationStore.Instance.Connections.DualSwitchSpeed = 2.0;
        ConfigurationStore.Instance.Connections.FixToFixDistance = 0.2;

        // Prime fix-to-fix state at speed.
        _service.FuseHeading(gpsHeading: 90, imuHeading: 0, imuValid: false,
            speedMs: 5.0, easting: 0, northing: 0);

        // Now slow down — DualSwitchSpeed kicks in, fix-to-fix should override.
        double result = _service.FuseHeading(
            gpsHeading: 90, imuHeading: 0, imuValid: false,
            speedMs: 1.0, easting: 0, northing: 10);

        // Moved 10m north from origin → heading = atan2(0, 10) = 0°.
        Assert.That(result, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void Fix_to_fix_ignored_when_distance_below_threshold()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 0.05;
        ConfigurationStore.Instance.Connections.FixToFixDistance = 1.0;

        // Prime
        _service.FuseHeading(gpsHeading: 45, imuHeading: 0, imuValid: false,
            speedMs: 10.0, easting: 0, northing: 0);

        // Second call: moved only 0.5m (< FixToFixDistance) — fix-to-fix rejected,
        // raw heading stands.
        double result = _service.FuseHeading(
            gpsHeading: 45, imuHeading: 0, imuValid: false,
            speedMs: 10.0, easting: 0.5, northing: 0);

        Assert.That(result, Is.EqualTo(45.0).Within(1e-9));
    }

    [Test]
    public void IMU_fusion_blends_when_weight_is_partial_and_IMU_valid()
    {
        ConfigurationStore.Instance.Connections.HeadingFusionWeight = 0.5; // 50/50

        double result = _service.FuseHeading(
            gpsHeading: 80.0, imuHeading: 100.0, imuValid: true,
            speedMs: 0.01, easting: 0, northing: 0);

        // diff = imu - final = 100 - 80 = 20; final = 80 + 20 * 0.5 = 90.
        Assert.That(result, Is.EqualTo(90.0).Within(1e-9));
    }

    [Test]
    public void IMU_fusion_skipped_when_weight_is_1()
    {
        ConfigurationStore.Instance.Connections.HeadingFusionWeight = 1.0;

        double result = _service.FuseHeading(
            gpsHeading: 45.0, imuHeading: 180.0, imuValid: true,
            speedMs: 0.01, easting: 0, northing: 0);

        Assert.That(result, Is.EqualTo(45.0).Within(1e-9));
    }

    [Test]
    public void IMU_fusion_skipped_when_imu_invalid()
    {
        // Slider at 50% would normally blend, but the 65535 sentinel means
        // ImuValid=false — IMU branch must skip and return GPS heading as-is.
        ConfigurationStore.Instance.Connections.HeadingFusionWeight = 0.5;

        double result = _service.FuseHeading(
            gpsHeading: 80.0, imuHeading: 0, imuValid: false,
            speedMs: 0.01, easting: 0, northing: 0);

        Assert.That(result, Is.EqualTo(80.0).Within(1e-9));
    }

    [Test]
    public void Reset_clears_fix_to_fix_history()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 0.05;

        // Prime: establishes previous position.
        _service.FuseHeading(gpsHeading: 0, imuHeading: 0, imuValid: false,
            speedMs: 10, easting: 0, northing: 0);
        _service.Reset();

        // After reset, the next call should behave like the very first — no fix-to-fix,
        // even though easting moved.
        double result = _service.FuseHeading(
            gpsHeading: 0, imuHeading: 0, imuValid: false,
            speedMs: 10, easting: 10, northing: 0);

        Assert.That(result, Is.EqualTo(0.0).Within(1e-9));
    }
}
