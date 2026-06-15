// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Phase 6 of #346: Rename / Delete on the profile services and the
/// active-profile policy applied in ConfigurationService.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ProfileRenameDeleteTests
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
        _tempRoot = Path.Combine(Path.GetTempPath(), "AgValoniaGPS-RenameDelete-" + Guid.NewGuid().ToString("N"));
        _vehiclesDir = Path.Combine(_tempRoot, "Vehicles");
        _toolsDir = Path.Combine(_tempRoot, "Tools");
        Directory.CreateDirectory(_vehiclesDir);
        Directory.CreateDirectory(_toolsDir);

        _vehicleService = new TestVehicleProfileService(_vehiclesDir);
        _toolService = new TestToolProfileService(_toolsDir);

        _settings = new AppSettings();
        _settingsService = Substitute.For<ISettingsService>();
        _settingsService.Settings.Returns(_settings);

        _configService = new ConfigurationService(_vehicleService, _toolService, _settingsService, ConfigurationStore.Instance);
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── Vehicle service direct tests ───────────────────────────────────

    [Test]
    public void Rename_RoundTrips()
    {
        WriteVehicleFile("Old");
        Assert.That(_vehicleService.Rename("Old", "New"), Is.True);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "Old.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "New.json")), Is.True);
    }

    [Test]
    public void Rename_CaseOnlyAllowed()
    {
        WriteVehicleFile("MyTractor");
        Assert.That(_vehicleService.Rename("MyTractor", "mytractor"), Is.True);
        // On case-insensitive filesystems both names refer to the same
        // file; assert the *content* survived (the file exists).
        var path = Directory.GetFiles(_vehiclesDir, "*.json")[0];
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public void Rename_CollisionBlocked()
    {
        WriteVehicleFile("A");
        WriteVehicleFile("B");
        Assert.That(_vehicleService.Rename("A", "B"), Is.False);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "A.json")), Is.True,
            "Source must be preserved when target exists");
    }

    [Test]
    public void Rename_MissingSourceReturnsFalse()
    {
        Assert.That(_vehicleService.Rename("DoesNotExist", "Whatever"), Is.False);
    }

    [Test]
    public void Delete_FileGoneAndReturnsTrue()
    {
        WriteVehicleFile("ToDelete");
        Assert.That(_vehicleService.Delete("ToDelete"), Is.True);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "ToDelete.json")), Is.False);
    }

    [Test]
    public void Delete_MissingFileReturnsFalse()
    {
        Assert.That(_vehicleService.Delete("DoesNotExist"), Is.False);
    }

    // ── Tool service mirrors the vehicle service ───────────────────────

    [Test]
    public void Tool_Rename_RoundTrips()
    {
        WriteToolFile("Old");
        Assert.That(_toolService.Rename("Old", "New"), Is.True);
        Assert.That(File.Exists(Path.Combine(_toolsDir, "New.json")), Is.True);
    }

    [Test]
    public void Tool_Delete_FileGone()
    {
        WriteToolFile("ToDelete");
        Assert.That(_toolService.Delete("ToDelete"), Is.True);
        Assert.That(File.Exists(Path.Combine(_toolsDir, "ToDelete.json")), Is.False);
    }

    // ── ConfigurationService policy: active profile ────────────────────

    [Test]
    public void DeleteVehicleProfile_BlockedWhenActive()
    {
        WriteVehicleFile("ActiveOne");
        _configService.Store.ActiveVehicleProfileName = "ActiveOne";

        Assert.That(_configService.DeleteVehicleProfile("ActiveOne"), Is.False);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "ActiveOne.json")), Is.True);
    }

    [Test]
    public void DeleteVehicleProfile_AllowedWhenNotActive()
    {
        WriteVehicleFile("ActiveOne");
        WriteVehicleFile("OtherOne");
        _configService.Store.ActiveVehicleProfileName = "ActiveOne";

        Assert.That(_configService.DeleteVehicleProfile("OtherOne"), Is.True);
        Assert.That(File.Exists(Path.Combine(_vehiclesDir, "OtherOne.json")), Is.False);
    }

    [Test]
    public void DeleteToolProfile_BlockedWhenActive()
    {
        WriteToolFile("ActiveTool");
        _configService.Store.ActiveToolProfileName = "ActiveTool";

        Assert.That(_configService.DeleteToolProfile("ActiveTool"), Is.False);
        Assert.That(File.Exists(Path.Combine(_toolsDir, "ActiveTool.json")), Is.True);
    }

    [Test]
    public void RenameVehicleProfile_UpdatesActivePointerWhenActive()
    {
        WriteVehicleFile("OldName");
        _configService.Store.ActiveVehicleProfileName = "OldName";
        _settings.LastUsedVehicleProfile = "OldName";

        Assert.That(_configService.RenameVehicleProfile("OldName", "NewName"), Is.True);
        Assert.That(_configService.Store.ActiveVehicleProfileName, Is.EqualTo("NewName"));
        Assert.That(_settings.LastUsedVehicleProfile, Is.EqualTo("NewName"),
            "LastUsedVehicleProfile must follow the rename of the active profile");
    }

    [Test]
    public void RenameVehicleProfile_LeavesActiveAloneWhenRenamingNonActive()
    {
        WriteVehicleFile("Other");
        _configService.Store.ActiveVehicleProfileName = "Active";
        _settings.LastUsedVehicleProfile = "Active";

        Assert.That(_configService.RenameVehicleProfile("Other", "Renamed"), Is.True);
        Assert.That(_configService.Store.ActiveVehicleProfileName, Is.EqualTo("Active"));
        Assert.That(_settings.LastUsedVehicleProfile, Is.EqualTo("Active"));
    }

    [Test]
    public void RenameToolProfile_UpdatesActivePointerWhenActive()
    {
        WriteToolFile("OldTool");
        _configService.Store.ActiveToolProfileName = "OldTool";
        _settings.LastUsedToolProfile = "OldTool";

        Assert.That(_configService.RenameToolProfile("OldTool", "NewTool"), Is.True);
        Assert.That(_configService.Store.ActiveToolProfileName, Is.EqualTo("NewTool"));
        Assert.That(_settings.LastUsedToolProfile, Is.EqualTo("NewTool"));
    }

    private void WriteVehicleFile(string name)
        => File.WriteAllText(Path.Combine(_vehiclesDir, $"{name}.json"), "{\"formatVersion\":2}");

    private void WriteToolFile(string name)
        => File.WriteAllText(Path.Combine(_toolsDir, $"{name}.json"), "{\"formatVersion\":2}");

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
