// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Settings take effect as they are typed and reach the profile with Save Profile or a switch
/// to another profile; closing AgOpenWeb lost them (the Vehicle &amp; Tool scenarios: an antenna
/// height typed and the app closed came back as the old one). AgOpenGPS saves its vehicle and
/// tool settings when it closes (FormGPS_FormClosing), so the exit save does too.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ProfilesSavedAtExitTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [Test]
    public void The_profiles_in_use_are_saved_at_exit_with_no_field_open()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        ConfigurationStore.Instance.ActiveVehicleProfileName = "Puma";
        ConfigurationStore.Instance.ActiveToolProfileName = "Sprayer";

        Assert.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(1)), Is.True);

        b.ConfigurationService.Received(1).SaveProfiles("Puma", "Sprayer");
    }

    [Test]
    public void With_no_profile_loaded_nothing_is_saved()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        ConfigurationStore.Instance.ActiveVehicleProfileName = "";

        vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(1));

        b.ConfigurationService.DidNotReceive().SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
    }
}
