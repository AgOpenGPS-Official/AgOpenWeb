// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Sections switched on with a machine module heard, but some sections have no relay in the pin
/// map (PGN 236; the default has sections 1 to 6): the operator is told which, once, and where to
/// set them. Those sections show on and paint coverage while no valve opens (the machine module
/// harness's SECTION-WITHOUT-RELAY).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SectionWithoutRelayTests
{
    private MainViewModel _vm = null!;
    private readonly List<string> _said = new();

    private void Build(int sections, bool moduleHeard)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var store = ConfigurationStore.Instance;
        store.NumSections = sections;
        for (int i = 0; i < sections; i++) store.Tool.SetSectionWidth(i, 100);
        _vm = new MainViewModelBuilder().Build();
        _said.Clear();
        _vm.FailureReported += m => _said.Add(m);
        _vm.State.Connections.IsMachineDataOk = moduleHeard;
    }

    [Test]
    public void Eight_sections_on_the_default_pins_are_told_once()
    {
        Build(8, moduleHeard: true);
        _vm.ToggleSectionMasterCommand!.Execute(null);
        Assert.That(_said, Has.Count.EqualTo(1));
        Assert.That(_said[0], Does.Contain("section(s) 7, 8").And.Contain("Pin Config"));

        _vm.ToggleSectionMasterCommand!.Execute(null);   // off
        _vm.ToggleSectionMasterCommand!.Execute(null);   // on again: said already
        Assert.That(_said, Has.Count.EqualTo(1));
    }

    [Test]
    public void Every_section_with_a_relay_says_nothing()
    {
        Build(6, moduleHeard: true);
        _vm.ToggleSectionMasterCommand!.Execute(null);
        Assert.That(_said, Is.Empty);
    }

    [Test]
    public void No_machine_module_heard_says_nothing()
    {
        Build(8, moduleHeard: false);   // the sections may go by the PGN 239/229 bits, not the pin map
        _vm.ToggleSectionMasterCommand!.Execute(null);
        Assert.That(_said, Is.Empty);
    }

    [Test]
    public void A_changed_pin_map_is_checked_again()
    {
        Build(8, moduleHeard: true);
        _vm.ToggleSectionMasterCommand!.Execute(null);
        ConfigurationStore.Instance.Machine.SetPinAssignment(6, PinFunction.Section7);
        _vm.ToggleSectionMasterCommand!.Execute(null);
        _vm.ToggleSectionMasterCommand!.Execute(null);
        Assert.That(_said, Has.Count.EqualTo(2));
        Assert.That(_said[1], Does.Contain("section(s) 8").And.Not.Contain("7,"));
    }
}
