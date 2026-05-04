// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// One-time v1 → v2 split migration (#346). On first launch with the
/// new build, every pre-#346 combined Vehicles/&lt;name&gt;.json file
/// must be rewritten as a v2 vehicle file with a paired same-name v2
/// tool file under Tools/.
/// </summary>
[TestFixture]
[NonParallelizable] // Touches per-test temp dirs; safer to serialize
public class ProfileMigrationTests
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
        _tempRoot = Path.Combine(Path.GetTempPath(), "AgValoniaGPS-MigrationTests-" + Guid.NewGuid().ToString("N"));
        _vehiclesDir = Path.Combine(_tempRoot, "Vehicles");
        _toolsDir = Path.Combine(_tempRoot, "Tools");
        Directory.CreateDirectory(_vehiclesDir);
        Directory.CreateDirectory(_toolsDir);

        _vehicleService = new TestVehicleProfileService(_vehiclesDir);
        _toolService = new TestToolProfileService(_toolsDir);

        _settings = new AppSettings();
        _settingsService = Substitute.For<ISettingsService>();
        _settingsService.Settings.Returns(_settings);

        _configService = new ConfigurationService(_vehicleService, _toolService, _settingsService);

        // Isolate the singleton so production code's ConfigurationStore.Instance
        // doesn't bleed test state.
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Test]
    public void NoV1Profiles_ReturnsFalse()
    {
        Assert.That(_configService.MigrateV1ProfilesIfNeeded(), Is.False);
    }

    [Test]
    public void ToolsDirectoryNotEmpty_SkipsMigration()
    {
        // Pre-existing tool file → assumed already migrated.
        File.WriteAllText(Path.Combine(_toolsDir, "Existing.json"), "{\"formatVersion\":2}");
        WriteV1Profile("OldTractor");

        Assert.That(_configService.MigrateV1ProfilesIfNeeded(), Is.False);
        Assert.That(File.Exists(Path.Combine(_toolsDir, "OldTractor.json")), Is.False,
            "Migration should not run when Tools/ is non-empty");
    }

    [Test]
    public void V1Profile_BecomesPairedV2VehicleAndTool()
    {
        WriteV1Profile("MyTractor", toolWidth: 7.5, antennaHeight: 2.4, minCoverage: 65);

        bool migrated = _configService.MigrateV1ProfilesIfNeeded();

        Assert.That(migrated, Is.True);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "MyTractor.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(_toolsDir, "MyTractor.json")), Is.True);

        // Round-trip the result through the new readers; values from the
        // original v1 file must be preserved on both sides.
        var roundTrip = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(_vehiclesDir, "MyTractor", roundTrip), Is.True);
        Assert.That(roundTrip.Vehicle.AntennaHeight, Is.EqualTo(2.4).Within(0.001));

        Assert.That(ToolProfileJsonService.Load(_toolsDir, "MyTractor", roundTrip), Is.True);
        Assert.That(roundTrip.Tool.Width, Is.EqualTo(7.5).Within(0.001));
        Assert.That(roundTrip.Tool.MinCoverage, Is.EqualTo(65));
    }

    [Test]
    public void V1Profile_OverwritesVehicleFileWithV2Schema()
    {
        WriteV1Profile("MyTractor");

        _configService.MigrateV1ProfilesIfNeeded();

        // The vehicle file should now have FormatVersion = 2 and no Tool block.
        var json = File.ReadAllText(Path.Combine(_vehiclesDir, "MyTractor.json"));
        Assert.That(json, Does.Contain("\"formatVersion\": 2"));
        Assert.That(json, Does.Not.Contain("\"tool\":"),
            "v2 vehicle file must not carry the Tool block (it lives in Tools/<name>.json)");
        Assert.That(json, Does.Not.Contain("\"sections\":"),
            "v2 vehicle file must not carry the Sections block");
    }

    [Test]
    public void LastUsedToolProfile_PairedFromVehicleProfileOnFirstMigration()
    {
        _settings.LastUsedVehicleProfile = "MyTractor";
        Assert.That(_settings.LastUsedToolProfile, Is.Empty);
        WriteV1Profile("MyTractor");

        _configService.MigrateV1ProfilesIfNeeded();

        Assert.That(_settings.LastUsedToolProfile, Is.EqualTo("MyTractor"),
            "Migration should pair LastUsedToolProfile to the existing LastUsedVehicleProfile");
    }

    [Test]
    public void V2Profile_NotMigratedAgain()
    {
        // Hand-write a v2 vehicle file. (Tools/ stays empty so the
        // top-level guard doesn't bail out.)
        File.WriteAllText(Path.Combine(_vehiclesDir, "Already.json"),
            "{\n  \"formatVersion\": 2,\n  \"vehicle\": null\n}");

        bool migrated = _configService.MigrateV1ProfilesIfNeeded();

        Assert.That(migrated, Is.False, "v2 files shouldn't be re-migrated");
        Assert.That(File.Exists(Path.Combine(_toolsDir, "Already.json")), Is.False,
            "v2 vehicle without paired tool stays alone — picker dialog handles missing tool");
    }

    private void WriteV1Profile(string name, double toolWidth = 6.0, double antennaHeight = 3.0, int minCoverage = 100)
    {
        var store = new ConfigurationStore();
        store.Vehicle.AntennaHeight = antennaHeight;
        store.Tool.Width = toolWidth;
        store.Tool.MinCoverage = minCoverage;
        store.NumSections = 3;
        for (int i = 0; i < 3; i++)
            store.Tool.SetSectionWidth(i, toolWidth * 100.0 / 3.0);

        ProfileJsonServiceV1.Save(_vehiclesDir, name, store);
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
