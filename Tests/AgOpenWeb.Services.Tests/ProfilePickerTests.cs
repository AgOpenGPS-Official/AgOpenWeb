// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The Vehicle &amp; Tool picker's commands, as the careless operator of the Vehicle &amp; Tool
/// scenarios sent them: New keeps the profile in use and the next measurements overwrote it,
/// a name with a slash went into a sub-folder, one with <c>../</c> outside the Vehicles
/// folder, <c>PUMA</c> beside <c>Puma</c> was a second file on Linux only, and the refusals
/// said nothing.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class ProfilePickerTests
{
    private string _root = null!, _vehicles = null!, _tools = null!;
    private ConfigurationStore _store = null!;
    private ConfigurationService _cfg = null!;
    private AppSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "AgOpenWeb-Picker-" + Guid.NewGuid().ToString("N"));
        _vehicles = Path.Combine(_root, "Documents", "Vehicles");
        _tools = Path.Combine(_root, "Documents", "Tools");
        Directory.CreateDirectory(_vehicles);
        Directory.CreateDirectory(_tools);
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _settings = new AppSettings();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(_settings);
        _cfg = new ConfigurationService(
            new TestVehicleProfileService(_vehicles), new TestToolProfileService(_tools), settings, _store);

        // A Case Puma with the 8 m sprayer in use, and a vineyard tractor saved beside it.
        _store.Vehicle.Wheelbase = 2.10;
        _cfg.SaveProfiles("Vineyard", "Sprayer");
        _store.Vehicle.Wheelbase = 2.88;
        _cfg.SaveProfiles("Puma", "Sprayer");
        Assume.That(_cfg.LoadProfiles("Puma", "Sprayer"), Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string? Send(string cmd, string arg) => ProfilePicker.Apply(_store, _cfg, cmd, arg);

    private double SavedWheelbase(string vehicle)
    {
        var probe = new ConfigurationStore();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        var cfg = new ConfigurationService(new TestVehicleProfileService(_vehicles), new TestToolProfileService(_tools),
            settings, probe);
        Assume.That(cfg.LoadProfiles(vehicle, "Sprayer"), Is.True);
        return probe.Vehicle.Wheelbase;
    }

    [Test]
    public void New_saves_the_live_settings_under_the_new_name_and_puts_it_in_use()
    {
        _store.Vehicle.Wheelbase = 2.40;

        Assert.That(Send("profile.new", "vehicle\tPuma copy"), Is.Null);

        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("Puma copy"));
        Assert.That(_settings.LastUsedVehicleProfile, Is.EqualTo("Puma copy"));
        // The next measurement and Save go to the new profile; the Puma keeps its own.
        _store.Vehicle.Wheelbase = 2.50;
        _cfg.SaveProfiles(_store.ActiveVehicleProfileName, _store.ActiveToolProfileName);
        Assert.That(SavedWheelbase("Puma"), Is.EqualTo(2.88));
        Assert.That(SavedWheelbase("Puma copy"), Is.EqualTo(2.50));
    }

    [Test]
    public void New_tool_puts_the_new_tool_in_use()
    {
        Assert.That(Send("profile.new", "tool\tSprayer 12 m"), Is.Null);
        Assert.That(_store.ActiveToolProfileName, Is.EqualTo("Sprayer 12 m"));
        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("Puma"));
        Assert.That(File.Exists(Path.Combine(_tools, "Sprayer 12 m.json")), Is.True);
    }

    [TestCase("Puma 180/CVX")]
    [TestCase("../outside")]
    [TestCase("..\\outside")]
    [TestCase("C:")]
    [TestCase("What?")]
    [TestCase(" ")]
    [TestCase("Puma.")]
    public void A_name_that_is_no_file_name_is_refused_and_nothing_is_written(string name)
    {
        string? said = Send("profile.new", "vehicle\t" + name);

        Assert.That(said, Is.Not.Null.And.Not.Empty);
        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("Puma"));
        Assert.That(Directory.GetDirectories(_vehicles), Is.Empty, "no sub-folder");
        Assert.That(Directory.GetFiles(Path.Combine(_root, "Documents"), "*.json"), Is.Empty, "nothing beside the folder");
        Assert.That(Send("profile.rename", "vehicle\tVineyard\t" + name), Is.Not.Null);
        Assert.That(File.Exists(Path.Combine(_vehicles, "Vineyard.json")), Is.True);
    }

    [TestCase("Vineyard")]
    [TestCase("VINEYARD")]
    public void New_with_the_name_of_another_profile_is_refused_not_overwritten(string name)
    {
        _store.Vehicle.Wheelbase = 2.40;

        Assert.That(Send("profile.new", "vehicle\t" + name), Is.EqualTo("There is already a vehicle profile named 'Vineyard'"));
        Assert.That(SavedWheelbase("Vineyard"), Is.EqualTo(2.10));
        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("Puma"));
    }

    [Test]
    public void Rename_onto_another_profile_is_refused_a_case_only_rename_is_not()
    {
        Assert.That(Send("profile.rename", "vehicle\tVineyard\tpuma"), Is.EqualTo("There is already a vehicle profile named 'Puma'"));
        Assert.That(Send("profile.rename", "vehicle\tVineyard\tVineyard 80 hp"), Is.Null);
        Assert.That(Send("profile.rename", "vehicle\tPuma\tPUMA"), Is.Null);
        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("PUMA"));
    }

    [Test]
    public void Delete_says_why_it_is_refused()
    {
        Assert.That(Send("profile.delete", "vehicle\tPuma"), Is.EqualTo("The vehicle profile in use can't be deleted: load another one first"));
        Assert.That(Send("profile.delete", "vehicle\tGhost"), Is.EqualTo("There is no vehicle profile named 'Ghost'"));
        Assert.That(Send("profile.delete", "vehicle\t../Vehicles/Vineyard"), Is.Not.Null);
        Assert.That(File.Exists(Path.Combine(_vehicles, "Vineyard.json")), Is.True);

        Assert.That(Send("profile.delete", "vehicle\tVineyard"), Is.Null);
        Assert.That(File.Exists(Path.Combine(_vehicles, "Vineyard.json")), Is.False);
    }

    [Test]
    public void A_profile_the_picker_does_not_list_is_not_loaded()
    {
        Assert.That(Send("profile.load", "Ghost\tSprayer"), Is.EqualTo("There is no vehicle profile named 'Ghost'"));
        Assert.That(Send("profile.load", "../Vehicles/Vineyard\tSprayer"), Is.Not.Null);
        Assert.That(_store.ActiveVehicleProfileName, Is.EqualTo("Puma"));
        Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(2.88));

        Assert.That(Send("profile.load", "Vineyard\tSprayer"), Is.Null);
        Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(2.10));
    }

    [Test]
    public void A_combine_loaded_with_a_trailed_implement_gets_it_in_front_and_is_told()
    {
        _store.Vehicle.Type = VehicleType.Harvester;
        _cfg.SaveProfiles("Combine", "Sprayer");
        _store.Vehicle.Type = VehicleType.Tractor;
        _store.Tool.SetToolType("trailing");
        _cfg.SaveProfiles("Puma", "Mower");

        Assert.That(Send("profile.load", "Combine\tMower"), Is.EqualTo("Implement set to front: a combine takes only a front implement"));
        Assert.That(_store.Tool.IsToolFrontFixed, Is.True);
    }

    [TestCase("Case Puma 180 CVX", true)]
    [TestCase("Sprayer 8 m (8 x 1 m)", true)]
    [TestCase("Pulverizador 8 m", true)]
    [TestCase("Trator ç ã é", true)]
    [TestCase("", false)]
    [TestCase("a|b", false)]
    [TestCase("a\tb", false)]
    [TestCase(".hidden", false)]
    [TestCase(" Puma", false)]
    public void Profile_names(string name, bool ok)
    {
        Assert.That(ProfileNames.WhyNot(name) == null, Is.EqualTo(ok));
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
