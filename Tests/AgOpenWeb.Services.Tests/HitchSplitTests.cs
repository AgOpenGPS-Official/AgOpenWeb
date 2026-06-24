// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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
/// Vehicle/Tool config split: the overloaded Tool.HitchLength was split into
/// Vehicle.HitchLength (#1, rear axle → tractor hitch pin, used by trailing/TBT)
/// and Tool.HitchLength (#2/#3, axle → rigid working center, used by fixed tools).
/// These tests pin the two halves: the geometry branch by tool type, and the
/// persistence/migration that seeds Vehicle.HitchLength on pre-split profiles.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton + per-test temp dirs.
public class HitchSplitTests
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
        _tempRoot = Path.Combine(Path.GetTempPath(), "AgOpenWeb-HitchSplit-" + Guid.NewGuid().ToString("N"));
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

    // ---- Geometry: the hitch reference must branch by tool type ----

    [Test]
    public void Geometry_RigidTool_UsesToolHitchLength_IgnoresVehicle()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.IsToolRearFixed = true;
        store.Tool.IsToolTrailing = false;
        store.Tool.IsToolTBT = false;
        store.Tool.IsToolFrontFixed = false;
        store.Tool.Offset = 0;
        store.Tool.HitchLength = 3.0;   // rigid working center — should drive the hitch
        store.Vehicle.HitchLength = 99.0; // vehicle pin — must be ignored for rigid

        var service = new ToolPositionService(store);
        service.Update(new Vec3(0, 0, 0), 0); // heading 0 = +North

        // Rear tool → hitch is behind the pivot by Tool.HitchLength.
        Assert.That(service.HitchPosition.Northing, Is.EqualTo(-3.0).Within(1e-6));
        Assert.That(service.HitchPosition.Easting, Is.EqualTo(0.0).Within(1e-6));
    }

    [Test]
    public void Geometry_TrailingTool_UsesVehicleHitchLength_IgnoresTool()
    {
        var store = ConfigurationStore.Instance;
        store.Tool.IsToolRearFixed = false;
        store.Tool.IsToolTrailing = true;
        store.Tool.IsToolTBT = false;
        store.Tool.IsToolFrontFixed = false;
        store.Tool.Offset = 0;
        store.Tool.HitchLength = 99.0;   // must be ignored for trailing
        store.Vehicle.HitchLength = 3.0; // tractor hitch pin — should drive the hitch

        var service = new ToolPositionService(store);
        service.Update(new Vec3(0, 0, 0), 0);

        // Trailing → hitch (the tractor pin) is behind the pivot by Vehicle.HitchLength.
        Assert.That(service.HitchPosition.Northing, Is.EqualTo(-3.0).Within(1e-6));
        Assert.That(service.HitchPosition.Easting, Is.EqualTo(0.0).Within(1e-6));
    }

    // ---- Persistence + migration ----

    [Test]
    public void Migration_PreSplitVehicleFile_SeedsVehicleHitchFromTool()
    {
        // A pre-split v2 vehicle file with NO hitchLength, paired with a tool
        // file that still carries the legacy hitch under Tool.HitchLength.
        File.WriteAllText(Path.Combine(_vehiclesDir, "Combo.json"),
            "{\n  \"formatVersion\": 2,\n  \"vehicle\": { \"wheelbase\": 2.5 }\n}");

        var toolSource = new ConfigurationStore();
        toolSource.Tool.HitchLength = 2.3;
        ToolProfileJsonService.Save(_toolsDir, "Combo", toolSource);

        Assert.That(_configService.LoadProfiles("Combo", "Combo"), Is.True);
        Assert.That(ConfigurationStore.Instance.Vehicle.HitchLength, Is.EqualTo(2.3).Within(1e-6),
            "Pre-split vehicle file (no hitchLength) should be seeded from the legacy Tool.HitchLength");
    }

    [Test]
    public void Migration_VehicleFileWithHitch_NotClobberedByTool()
    {
        File.WriteAllText(Path.Combine(_vehiclesDir, "Combo.json"),
            "{\n  \"formatVersion\": 2,\n  \"vehicle\": { \"wheelbase\": 2.5, \"hitchLength\": 1.1 }\n}");

        var toolSource = new ConfigurationStore();
        toolSource.Tool.HitchLength = 9.9;
        ToolProfileJsonService.Save(_toolsDir, "Combo", toolSource);

        Assert.That(_configService.LoadProfiles("Combo", "Combo"), Is.True);
        Assert.That(ConfigurationStore.Instance.Vehicle.HitchLength, Is.EqualTo(1.1).Within(1e-6),
            "An explicit vehicle hitchLength must win — no migration overwrite");
    }

    [Test]
    public void RoundTrip_VehicleAndToolHitch_PersistIndependently()
    {
        var store = ConfigurationStore.Instance;
        store.Vehicle.HitchLength = 4.4;
        store.Tool.HitchLength = 6.6;

        _configService.SaveProfiles("Rig", "Rig");

        var reload = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(_vehiclesDir, "Rig", reload), Is.True);
        Assert.That(ToolProfileJsonService.Load(_toolsDir, "Rig", reload), Is.True);

        Assert.That(reload.Vehicle.HitchLength, Is.EqualTo(4.4).Within(1e-6));
        Assert.That(reload.Tool.HitchLength, Is.EqualTo(6.6).Within(1e-6));
    }

    [Test]
    public void XmlImport_VehicleHitchKey_PopulatesBothVehicleAndTool()
    {
        // AOG's single setVehicle_hitchLength is the tractor pin (vehicle) and
        // doubles as the rigid working center (tool); import populates both.
        File.WriteAllText(Path.Combine(_vehiclesDir, "Legacy.XML"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<configuration><userSettings><AgOpenGPS.Properties.Settings>" +
            "<setting name=\"setVehicle_hitchLength\"><value>2.0</value></setting>" +
            "</AgOpenGPS.Properties.Settings></userSettings></configuration>");

        var store = new ConfigurationStore();
        Assert.That(_vehicleService.Load("Legacy", store), Is.True);
        Assert.That(store.Vehicle.HitchLength, Is.EqualTo(2.0).Within(1e-6));
        Assert.That(store.Tool.HitchLength, Is.EqualTo(2.0).Within(1e-6));
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
