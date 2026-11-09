// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Closing the field switches every section off, the ones switched on by hand too, as
/// AgOpenGPS's JobClose does (manual and auto off, AllSectionsAndButtonsToState(Off)). Only
/// AutoSteer went off: sections on in Manual kept reaching the module every tick, and with no
/// field the section bar was gone, so the operator could not switch them off (web client
/// flows F6; F7 opens another field, which closes this one the same way).
/// </summary>
[TestFixture]
public class CloseFieldSectionsOffTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-closesec-" + Guid.NewGuid().ToString("N"));
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();
        var dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(dir);
        var field = new Field { Name = "F", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task Sections_on_in_manual_go_off_with_the_field()
    {
        _vm.IsManualSectionMode = true;
        _b.SectionControlService.ClearReceivedCalls();

        await _vm.CloseFieldAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsManualSectionMode, Is.False, "Manual off");
            Assert.That(_vm.IsSectionMasterOn, Is.False, "Auto off");
        });
        _b.SectionControlService.Received().SetAllSections(SectionButtonState.Off);
    }

    [Test]
    public async Task A_section_on_by_its_own_button_goes_off_with_the_field()
    {
        var states = new[] { new SectionControlState { Index = 0 }, new SectionControlState { Index = 1 } };
        states[1].ButtonState = SectionButtonState.On;
        _b.SectionControlService.SectionStates.Returns(states);
        Assume.That(_vm.IsSectionMasterOn || _vm.IsManualSectionMode, Is.False);
        _b.SectionControlService.ClearReceivedCalls();

        await _vm.CloseFieldAsync();

        _b.SectionControlService.Received().SetAllSections(SectionButtonState.Off);
    }

    [Test]
    public async Task With_everything_off_closing_changes_nothing_in_the_sections()
    {
        _b.SectionControlService.ClearReceivedCalls();

        await _vm.CloseFieldAsync();

        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }
}
