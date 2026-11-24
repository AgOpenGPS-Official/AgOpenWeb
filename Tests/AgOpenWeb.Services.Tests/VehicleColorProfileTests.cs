// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>The vehicle's colour on the map (Vehicle > Machine brand) is the vehicle
/// profile's, as AgOpenGPS's setDisplay_colorVehicle: a red tractor's profile draws it red. A profile
/// saved before it draws the picture as it is (white).</summary>
[TestFixture]
public class VehicleColorProfileTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-vehcolor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [TestCase(0xE82029u)]
    [TestCase(0x000000u)]
    [TestCase(0xFFFFFFu)]
    public void The_colour_is_saved_with_the_vehicle_profile(uint color)
    {
        var src = new ConfigurationStore();
        src.Vehicle.Color = color;
        VehicleProfileJsonService.Save(_dir, "Tractor", src);

        var dst = new ConfigurationStore();
        dst.Vehicle.Color = 0x319F3D; // the vehicle before
        Assert.That(VehicleProfileJsonService.Load(_dir, "Tractor", dst), Is.True);
        Assert.That(dst.Vehicle.Color, Is.EqualTo(color));
    }

    [Test]
    public void A_profile_saved_before_the_setting_draws_the_picture_as_it_is()
    {
        VehicleProfileJsonService.Save(_dir, "Old", new ConfigurationStore());
        var path = Path.Combine(_dir, "Old.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var vehicle = node["vehicle"]!.AsObject();
        Assert.That(vehicle.ContainsKey("color"), Is.True, "the setting is written");
        vehicle.Remove("color");
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();
        dst.Vehicle.Color = 0xE82029; // the vehicle that was active before
        Assert.That(VehicleProfileJsonService.Load(_dir, "Old", dst), Is.True);
        Assert.That(dst.Vehicle.Color, Is.EqualTo(VehicleConfig.DefaultColor));
    }

    [Test]
    public void Only_the_colour_is_kept_not_an_alpha()
    {
        var v = new VehicleConfig { Color = 0x80E82029 };
        Assert.That(v.Color, Is.EqualTo(0xE82029u));
    }

    [Test]
    public void A_new_vehicle_profile_is_drawn_as_the_picture_is()
    {
        var store = new ConfigurationStore();
        store.Vehicle.Color = 0xE82029;
        new TestVehicleProfileService(_dir).CreateDefaultProfile("New", store);
        Assert.That(store.Vehicle.Color, Is.EqualTo(VehicleConfig.DefaultColor));
    }

    private sealed class TestVehicleProfileService : VehicleProfileService
    {
        public TestVehicleProfileService(string dir)
            : base(NullLogger<VehicleProfileService>.Instance, dir) { }
    }
}
