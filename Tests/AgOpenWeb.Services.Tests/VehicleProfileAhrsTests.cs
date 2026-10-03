// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Tests that AHRS/IMU roll calibration (RollZero, RollFilter, IsRollInvert)
/// persists through the vehicle profile JSON format.
/// </summary>
[TestFixture]
public class VehicleProfileAhrsTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-vehahrs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Test]
    public void AhrsSettings_RoundTripThroughTheVehicleProfile()
    {
        var src = new ConfigurationStore();
        src.Ahrs.RollZero = -168.2;
        src.Ahrs.RollFilter = 0.35;
        src.Ahrs.IsRollInvert = true;

        VehicleProfileJsonService.Save(_dir, "Tractor", src);
        var dst = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(_dir, "Tractor", dst), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(dst.Ahrs.RollZero, Is.EqualTo(-168.2));
            Assert.That(dst.Ahrs.RollFilter, Is.EqualTo(0.35));
            Assert.That(dst.Ahrs.IsRollInvert, Is.True);
        });
    }

    [Test]
    public void AProfileSavedBeforeTheAhrsSection_LoadsTheDefaults()
    {
        var src = new ConfigurationStore();
        VehicleProfileJsonService.Save(_dir, "Old", src);
        var path = Path.Combine(_dir, "Old.json");
        var json = File.ReadAllText(path);
        int start = json.IndexOf("\"ahrs\"", StringComparison.Ordinal);
        int end = json.IndexOf('}', start) + 1;
        File.WriteAllText(path, json.Remove(start, end - start + 1)); // drop the section + comma

        var dst = new ConfigurationStore();
        dst.Ahrs.RollZero = 12.3; // existing non-default in store
        dst.Ahrs.RollFilter = 0.5;
        dst.Ahrs.IsRollInvert = true;

        Assert.That(VehicleProfileJsonService.Load(_dir, "Old", dst), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(dst.Ahrs.RollZero, Is.EqualTo(0.0), "Default RollZero");
            Assert.That(dst.Ahrs.RollFilter, Is.EqualTo(0.0), "Default RollFilter");
            Assert.That(dst.Ahrs.IsRollInvert, Is.False, "Default IsRollInvert");
        });
    }
}
