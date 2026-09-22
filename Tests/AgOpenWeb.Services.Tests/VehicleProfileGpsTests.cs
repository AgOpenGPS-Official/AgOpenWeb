// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;

namespace AgOpenWeb.Services.Tests;

/// <summary>#112: the GPS / heading settings are saved with the vehicle profile
/// (AgOpenGPS keeps them per vehicle); before, they reset on every restart.</summary>
[TestFixture]
public class VehicleProfileGpsTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-vehgps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [Test]
    public void GpsSettings_RoundTripThroughTheVehicleProfile()
    {
        var src = new ConfigurationStore();
        var c = src.Connections;
        c.IsDualGps = true;
        c.DualHeadingOffset = 12.5;
        c.DualReverseDistance = 0.4;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 3.5;
        c.MinGpsStep = 0.08;
        c.FixToFixDistance = 0.7;
        c.HeadingFusionWeight = 0.45;
        c.ReverseDetection = false;
        c.RtkLostAlarm = false;
        c.RtkLostAction = 1;

        VehicleProfileJsonService.Save(_dir, "Rig", src);
        var dst = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(_dir, "Rig", dst), Is.True);

        var d = dst.Connections;
        Assert.Multiple(() =>
        {
            Assert.That(d.IsDualGps, Is.True);
            Assert.That(d.DualHeadingOffset, Is.EqualTo(12.5));
            Assert.That(d.DualReverseDistance, Is.EqualTo(0.4));
            Assert.That(d.AutoDualFix, Is.True);
            Assert.That(d.DualSwitchSpeed, Is.EqualTo(3.5));
            Assert.That(d.MinGpsStep, Is.EqualTo(0.08));
            Assert.That(d.FixToFixDistance, Is.EqualTo(0.7));
            Assert.That(d.HeadingFusionWeight, Is.EqualTo(0.45));
            Assert.That(d.ReverseDetection, Is.False);
            Assert.That(d.RtkLostAlarm, Is.False);
            Assert.That(d.RtkLostAction, Is.EqualTo(1));
        });
    }

    [Test]
    public void AProfileSavedBeforeTheGpsSection_LoadsTheDefaults()
    {
        var src = new ConfigurationStore();
        VehicleProfileJsonService.Save(_dir, "Old", src);
        var path = Path.Combine(_dir, "Old.json");
        var json = File.ReadAllText(path);
        int start = json.IndexOf("\"gps\"", StringComparison.Ordinal);
        int end = json.IndexOf('}', start) + 1;
        File.WriteAllText(path, json.Remove(start, end - start + 1)); // drop the section + comma

        var dst = new ConfigurationStore();
        dst.Connections.IsDualGps = true;          // whatever the previous vehicle had
        dst.Connections.DualHeadingOffset = 90;
        Assert.That(VehicleProfileJsonService.Load(_dir, "Old", dst), Is.True);

        Assert.That(dst.Connections.IsDualGps, Is.False);
        Assert.That(dst.Connections.DualHeadingOffset, Is.EqualTo(0), "AgOpenGPS default");
        Assert.That(dst.Connections.HeadingFusionWeight, Is.EqualTo(0.3), "AgOpenGPS default 30% GPS");
    }
}
