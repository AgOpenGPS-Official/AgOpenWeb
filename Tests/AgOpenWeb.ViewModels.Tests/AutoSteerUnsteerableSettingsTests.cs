// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AutoSteer does not engage on settings the guidance cannot steer with, however they got
/// into the store (a profile saved before the settings had ranges): a wheelbase of 0 never
/// steers, a negative one steers away from the line, an overlap as wide as the implement
/// puts every pass on one line (the Vehicle &amp; Tool scenarios).
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerUnsteerableSettingsTests
{
    private MainViewModel _vm = null!;
    private string? _said;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var b = new MainViewModelBuilder
        {
            ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance),
        };
        _vm = b.Build();
        _vm.IsAutoSteerAvailable = true;
        _said = null;
        _vm.FailureReported += m => _said = m;
    }

    [TestCase(0.0)]
    [TestCase(-2.88)]
    [TestCase(double.NaN)]
    public void No_engage_without_a_wheelbase(double wheelbase)
    {
        ConfigurationStore.Instance.Vehicle.Wheelbase = wheelbase;

        _vm.ToggleAutoSteerCommand!.Execute(null);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Does.Contain("wheelbase"));
    }

    [Test]
    public void No_engage_when_the_overlap_is_as_wide_as_the_implement()
    {
        var s = ConfigurationStore.Instance;
        s.Tool.Overlap = s.ActualToolWidth;

        _vm.ToggleAutoSteerCommand!.Execute(null);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Does.Contain("overlap"));
    }

    [Test]
    public void A_real_machine_engages_and_stopping_is_always_allowed()
    {
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assert.That(_vm.IsAutoSteerEngaged, Is.True);

        // A setting going bad under it does not stop the button from turning it off.
        ConfigurationStore.Instance.Vehicle.Wheelbase = 0;
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }
}
