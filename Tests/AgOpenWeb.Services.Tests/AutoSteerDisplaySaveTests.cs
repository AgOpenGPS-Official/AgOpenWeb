// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #479: the AutoSteer Display tab saves as soon as it changes, without saving the steering
/// settings, which wait for Send+Save.
/// </summary>
[TestFixture]
public class AutoSteerDisplaySaveTests
{
    private string _tempRoot = null!;
    private string _vehiclesDir = null!;
    private ConfigurationStore _store = null!;
    private ConfigurationService _configService = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AgOpenWeb-DisplaySave-" + Guid.NewGuid().ToString("N"));
        _vehiclesDir = Path.Combine(_tempRoot, "Vehicles");
        var toolsDir = Path.Combine(_tempRoot, "Tools");
        Directory.CreateDirectory(_vehiclesDir);
        Directory.CreateDirectory(toolsDir);

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        _store = new ConfigurationStore { ActiveVehicleProfileName = "Tractor" };
        _configService = new ConfigurationService(
            new TestVehicleProfileService(_vehiclesDir), new TestToolProfileService(toolsDir),
            settingsService, _store);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private AutoSteerConfigDto ReadFile() => JsonSerializer.Deserialize<AutoSteerConfigDto>(
        File.ReadAllText(Path.Combine(_vehiclesDir, "Tractor.AutoSteer.json")))!;

    [Test]
    public void DisplaySave_WritesTheBarSettings_AndLeavesUnsentSteeringAlone()
    {
        _store.AutoSteer.ProportionalGain = 10;
        _configService.SaveProfiles("Tractor", "Tool");

        _store.AutoSteer.ProportionalGain = 42;   // changed, not sent
        _store.AutoSteer.SteerBarEnabled = true;
        _store.AutoSteer.LightbarEnabled = false;
        _store.AutoSteer.CmPerPixel = 3;
        _configService.SaveAutoSteerDisplaySettings();

        var saved = ReadFile();
        Assert.That(saved.SteerBarEnabled, Is.True);
        Assert.That(saved.LightbarEnabled, Is.False);
        Assert.That(saved.CmPerPixel, Is.EqualTo(3));
        Assert.That(saved.ProportionalGain, Is.EqualTo(10));
    }

    [Test]
    public void DisplaySave_WithNoFileYet_WritesOne()
    {
        _store.AutoSteer.SteerBarEnabled = true;
        _configService.SaveAutoSteerDisplaySettings();

        Assert.That(ReadFile().SteerBarEnabled, Is.True);
    }

    [Test]
    public void DisplaySave_WritesTheRefNudgeStep_ApartFromTheNudgeDistance()
    {
        _store.AutoSteer.NudgeDistance = 30;
        _store.AutoSteer.RefNudgeDistance = 12;
        _configService.SaveAutoSteerDisplaySettings();

        var saved = ReadFile();
        Assert.That(saved.RefNudgeDistance, Is.EqualTo(12));
        Assert.That(saved.NudgeDistance, Is.EqualTo(30));
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
