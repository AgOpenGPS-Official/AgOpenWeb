// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;
using AgOpenWeb.Services.Tool;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The hitch model (#547, NonTracked/HITCH_MODEL_PLAN.md): the tractor's hitch points are the
/// vehicle's (drawbar, rear 3-point, front 3-point, from the rear axle) and a fixed tool keeps its
/// working point's distance from its hitch, as ISO 11783-10 chains a connector and a device
/// element. Profile format 3; a format-2 tool (one number, rear axle → working point) is converted
/// against the vehicle loaded with it, so it stays where it was.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton + per-test temp dirs.
public class HitchModelTests
{
    private string _tempRoot = null!;
    private string _vehiclesDir = null!;
    private string _toolsDir = null!;
    private TestVehicleProfileService _vehicleService = null!;
    private TestToolProfileService _toolService = null!;
    private ISettingsService _settingsService = null!;
    private AppSettings _settings = null!;
    private ConfigurationService _configService = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AgOpenWeb-HitchModel-" + Guid.NewGuid().ToString("N"));
        _vehiclesDir = Path.Combine(_tempRoot, "Vehicles");
        _toolsDir = Path.Combine(_tempRoot, "Tools");
        Directory.CreateDirectory(_vehiclesDir);
        Directory.CreateDirectory(_toolsDir);

        _vehicleService = new TestVehicleProfileService(_vehiclesDir);
        _toolService = new TestToolProfileService(_toolsDir);

        _settings = new AppSettings();
        _settingsService = Substitute.For<ISettingsService>();
        _settingsService.Settings.Returns(_settings);

        ConfigurationStore.SetInstance(new ConfigurationStore());

        _configService = new ConfigurationService(_vehicleService, _toolService, _settingsService, ConfigurationStore.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---- Geometry: the tool hangs on the vehicle's hitch ----

    private static ToolPositionService Placed(ConfigurationStore store)
    {
        var service = new ToolPositionService(store);
        service.Update(new Vec3(0, 0, 0), 0); // heading 0 = +North
        return service;
    }

    // #547's case: the tractor's 3-point 1.2 m behind the axle, the tool's working point 0.8 m behind it.
    [Test]
    public void A_rear_tool_sits_at_the_tractors_hitch_plus_its_own_distance()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.SetToolType("rear");
        store.Vehicle.ThreePointHitchLength = 1.2;
        store.Tool.WorkingPointFromHitch = 0.8;
        store.Vehicle.HitchLength = 99.0; // the drawbar: not a rear-fixed tool's

        Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(-2.0).Within(1e-9));

        // Another tractor: only the vehicle's hitch changes, and the tool moves with it.
        store.Vehicle.ThreePointHitchLength = 1.5;
        Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(-2.3).Within(1e-9));
    }

    [Test]
    public void A_front_tool_sits_ahead_at_the_front_hitch_plus_its_own_distance()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.SetToolType("front");
        store.Vehicle.FrontHitchLength = 1.5;
        store.Tool.WorkingPointFromHitch = 0.5;
        store.Vehicle.ThreePointHitchLength = 99.0;

        Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void A_working_point_on_the_tractors_side_of_the_hitch_is_a_negative_distance()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.SetToolType("rear");
        store.Vehicle.ThreePointHitchLength = 1.2;
        store.Tool.WorkingPointFromHitch = -0.3;

        Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(-0.9).Within(1e-9));
    }

    [TestCase("trailing")]
    [TestCase("tbt")]
    public void A_trailed_tool_hangs_on_the_drawbar(string type)
    {
        var store = ConfigurationStore.Instance;
        store.Tool.SetToolType(type);
        store.Vehicle.HitchLength = 3.0;
        store.Vehicle.ThreePointHitchLength = 99.0;
        store.Tool.WorkingPointFromHitch = 99.0;

        Assert.That(Placed(store).HitchPosition.Northing, Is.EqualTo(-3.0).Within(1e-9));
    }

    // ---- Format 2 → 3: the tool stays where it was ----

    private void WriteV2Vehicle(string name, string vehicleJson) =>
        File.WriteAllText(Path.Combine(_vehiclesDir, name + ".json"),
            "{ \"formatVersion\": 2, \"vehicle\": { \"wheelbase\": 2.5" + vehicleJson + " } }");

    private void WriteV2Tool(string name, string type, double axleToWorkingPoint) =>
        File.WriteAllText(Path.Combine(_toolsDir, name + ".json"),
            "{ \"formatVersion\": 2, \"tool\": { \"width\": 6, \"hitchLength\": " +
            axleToWorkingPoint.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", \"isToolRearFixed\": " + (type == "rear" ? "true" : "false") +
            ", \"isToolFrontFixed\": " + (type == "front" ? "true" : "false") +
            ", \"isToolTrailing\": " + (type == "trailing" ? "true" : "false") + " } }");

    [Test]
    public void A_format_2_rear_tool_keeps_its_place_and_both_files_are_saved_as_format_3()
    {
        WriteV2Vehicle("Rig", ", \"hitchLength\": 1.1");
        WriteV2Tool("Rig", "rear", 2.0);

        Assert.That(_configService.LoadProfiles("Rig", "Rig"), Is.True);
        var store = ConfigurationStore.Instance;
        Assert.Multiple(() =>
        {
            Assert.That(store.Vehicle.HitchLength, Is.EqualTo(1.1).Within(1e-9), "the drawbar as it was");
            Assert.That(store.Vehicle.ThreePointHitchLength, Is.EqualTo(1.1).Within(1e-9), "the 3-point starts at the one hitch");
            Assert.That(store.Tool.WorkingPointFromHitch, Is.EqualTo(0.9).Within(1e-9));
            Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(-2.0).Within(1e-9), "where format 2 had it");
            Assert.That(store.ProfileMigrationNote, Does.Contain("0.90 m"));
            Assert.That(HitchMigration.FormatVersionOf(Path.Combine(_vehiclesDir, "Rig.json")), Is.EqualTo(3), "saved at once");
            Assert.That(HitchMigration.FormatVersionOf(Path.Combine(_toolsDir, "Rig.json")), Is.EqualTo(3), "saved at once");
        });

        // Read again (format 3 now): the same place, nothing converted.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var again = new ConfigurationService(_vehicleService, _toolService, _settingsService, ConfigurationStore.Instance);
        Assert.That(again.LoadProfiles("Rig", "Rig"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(ConfigurationStore.Instance.ProfileMigrationNote, Is.Null);
            Assert.That(Placed(ConfigurationStore.Instance).ToolPosition.Northing, Is.EqualTo(-2.0).Within(1e-9));
        });
    }

    [Test]
    public void A_format_2_front_tool_gives_the_vehicle_its_front_hitch()
    {
        WriteV2Vehicle("Rig", ", \"hitchLength\": 1.1");
        WriteV2Tool("Rig", "front", 3.0);

        Assert.That(_configService.LoadProfiles("Rig", "Rig"), Is.True);
        var store = ConfigurationStore.Instance;
        Assert.Multiple(() =>
        {
            Assert.That(store.Vehicle.FrontHitchLength, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(store.Tool.WorkingPointFromHitch, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(3.0).Within(1e-9), "where format 2 had it");
            Assert.That(store.ProfileMigrationNote, Does.Contain("front hitch"));
        });
    }

    [Test]
    public void A_vehicle_file_from_before_the_hitch_moved_takes_it_from_the_tool()
    {
        WriteV2Vehicle("Rig", "");
        WriteV2Tool("Rig", "trailing", 2.3);

        Assert.That(_configService.LoadProfiles("Rig", "Rig"), Is.True);
        var store = ConfigurationStore.Instance;
        Assert.Multiple(() =>
        {
            Assert.That(store.Vehicle.HitchLength, Is.EqualTo(2.3).Within(1e-9));
            Assert.That(store.Vehicle.ThreePointHitchLength, Is.EqualTo(2.3).Within(1e-9));
            Assert.That(Placed(store).HitchPosition.Northing, Is.EqualTo(-2.3).Within(1e-9));
        });
    }

    [Test]
    public void Format_3_round_trips_and_still_tells_an_older_build_where_the_tool_is()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.SetToolType("rear");
        store.Vehicle.HitchLength = 4.4;
        store.Vehicle.ThreePointHitchLength = 1.2;
        store.Vehicle.FrontHitchLength = 2.2;
        store.Tool.WorkingPointFromHitch = 0.8;
        _configService.SaveProfiles("Rig", "Rig");

        var reload = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(_vehiclesDir, "Rig", reload), Is.True);
        Assert.That(ToolProfileJsonService.Load(_toolsDir, "Rig", reload), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(reload.Vehicle.HitchLength, Is.EqualTo(4.4).Within(1e-9));
            Assert.That(reload.Vehicle.ThreePointHitchLength, Is.EqualTo(1.2).Within(1e-9));
            Assert.That(reload.Vehicle.FrontHitchLength, Is.EqualTo(2.2).Within(1e-9));
            Assert.That(reload.Tool.WorkingPointFromHitch, Is.EqualTo(0.8).Within(1e-9));
            Assert.That(reload.ProfileMigrationNote, Is.Null);
        });
        // An older build reads format 2's hitchLength: the axle → working point with this vehicle.
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_toolsDir, "Rig.json")));
        Assert.That(doc.RootElement.GetProperty("tool").GetProperty("hitchLength").GetDouble(), Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void A_profile_from_a_newer_build_is_not_overwritten()
    {
        var path = Path.Combine(_toolsDir, "Rig.json");
        const string newer = "{ \"formatVersion\": 4, \"tool\": { \"width\": 6, \"somethingNew\": 1 } }";
        File.WriteAllText(path, newer);
        ToolProfileJsonService.Save(_toolsDir, "Rig", ConfigurationStore.Instance);
        Assert.That(File.ReadAllText(path), Is.EqualTo(newer));
    }

    // AgOpenGPS: one hitch length, the fixed tool's working point at it (CTool). The vehicle's hitches
    // take it and the tool's distance is 0: the tool where AgOpenGPS has it.
    [TestCase(false, -2.0)]
    [TestCase(true, 2.0)]
    public void An_AgOpenGPS_profile_puts_the_tool_where_AgOpenGPS_has_it(bool front, double y)
    {
        File.WriteAllText(Path.Combine(_vehiclesDir, "Legacy.XML"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<configuration><userSettings><AgOpenGPS.Properties.Settings>" +
            "<setting name=\"setVehicle_hitchLength\"><value>-2.0</value></setting>" +
            "<setting name=\"setTool_isToolFront\"><value>" + (front ? "True" : "False") + "</value></setting>" +
            "<setting name=\"setTool_isToolRearFixed\"><value>" + (front ? "False" : "True") + "</value></setting>" +
            "</AgOpenGPS.Properties.Settings></userSettings></configuration>");

        var store = new ConfigurationStore();
        Assert.That(_vehicleService.Load("Legacy", store), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(store.Vehicle.HitchLength, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(store.Vehicle.ThreePointHitchLength, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(store.Vehicle.FrontHitchLength, Is.EqualTo(front ? 2.0 : 0.0).Within(1e-9));
            Assert.That(store.Tool.WorkingPointFromHitch, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(Placed(store).ToolPosition.Northing, Is.EqualTo(y).Within(1e-9));
        });
    }

    private sealed class TestVehicleProfileService : VehicleProfileService
    {
        public TestVehicleProfileService(string dir)
            : base(NullLogger<VehicleProfileService>.Instance, dir) { }
    }

    private sealed class TestToolProfileService : ToolProfileService
    {
        public TestToolProfileService(string dir)
            : base(NullLogger<ToolProfileService>.Instance, dir) { }
    }
}
