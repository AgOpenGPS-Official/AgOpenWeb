// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Gps;

namespace AgValoniaGPS.Services.Tests.Gps;

[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GpsFixQualityValidatorTests
{
    [SetUp]
    public void SetUp()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.MinFixQuality = 2;   // require DGPS or better
        c.MaxHdop = 2.0;
        c.MaxDifferentialAge = 5.0;
    }

    [Test]
    public void Accepts_a_good_fix()
    {
        bool ok = GpsFixQualityValidator.IsAcceptable(
            fixQuality: 4, hdop: 0.8, differentialAge: 1.0, out var reason,
            ConfigurationStore.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(reason, Is.Null);
        });
    }

    [Test]
    public void Rejects_fix_quality_below_minimum()
    {
        bool ok = GpsFixQualityValidator.IsAcceptable(
            fixQuality: 1, hdop: 0.8, differentialAge: 1.0, out var reason,
            ConfigurationStore.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("Fix quality"));
            Assert.That(reason, Does.Contain("below minimum"));
        });
    }

    [Test]
    public void Rejects_hdop_above_maximum()
    {
        bool ok = GpsFixQualityValidator.IsAcceptable(
            fixQuality: 4, hdop: 3.0, differentialAge: 1.0, out var reason,
            ConfigurationStore.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("HDOP"));
            Assert.That(reason, Does.Contain("exceeds"));
        });
    }

    [Test]
    public void Rejects_differential_age_above_maximum()
    {
        bool ok = GpsFixQualityValidator.IsAcceptable(
            fixQuality: 4, hdop: 0.8, differentialAge: 10.0, out var reason,
            ConfigurationStore.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("Differential age"));
        });
    }

    [Test]
    public void Zero_differential_age_is_allowed()
    {
        // differentialAge=0 means "no differential fix", not "age expired".
        bool ok = GpsFixQualityValidator.IsAcceptable(
            fixQuality: 4, hdop: 0.8, differentialAge: 0.0, out var reason,
            ConfigurationStore.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(reason, Is.Null);
        });
    }
}
