// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AutoSteer goes off, with the reason, when a Vehicle &amp; Tool setting it steers by
/// changes under it (the Vehicle &amp; Tool scenarios: 8 to 6 sections mid-pass swung a
/// tractor 3.3 m across its pass, a 2.5 m implement offset 2.8 m). Settings it does not
/// steer by, and a setter that announces a change without one, leave it on.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerOffWhenSettingsChangeTests
{
    private MainViewModel _vm = null!;
    private string? _said;
    private ConfigurationStore S => ConfigurationStore.Instance;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        S.NumSections = 8;
        for (int i = 0; i < 8; i++) S.Tool.SetSectionWidth(i, 100);
        var b = new MainViewModelBuilder
        {
            ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance),
        };
        _vm = b.Build();
        _vm.IsAutoSteerAvailable = true;
        _said = null;
        _vm.FailureReported += m => _said = m;
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
    }

    private static IEnumerable<TestCaseData> SteeringChanges()
    {
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.NumSections = 6), "implement").SetName("Fewer sections");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Tool.SetSectionWidth(3, 150)), "implement").SetName("A section width");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Tool.Offset = 2.5), "implement").SetName("Implement offset");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Tool.Overlap = 0.2), "implement").SetName("Overlap");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Tool.WorkingPointFromHitch = 3), "implement").SetName("Implement hitch");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Vehicle.ThreePointHitchLength = 0.7), "vehicle").SetName("Rear 3-point hitch");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Vehicle.AntennaOffset = 0.25), "vehicle").SetName("Antenna offset");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Vehicle.AntennaHeight = 1), "vehicle").SetName("Antenna height");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Vehicle.Wheelbase = 3.5), "vehicle").SetName("Wheelbase");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Connections.DualHeadingOffset = 90), "GPS").SetName("Dual heading offset");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Ahrs.IsRollInvert = !s.Ahrs.IsRollInvert), "roll").SetName("Roll inverted");
        yield return new TestCaseData((Action<ConfigurationStore>)(s => s.Ahrs.RollZero = 2), "roll").SetName("Roll zero");
    }

    [TestCaseSource(nameof(SteeringChanges))]
    public void A_setting_it_steers_by_turns_AutoSteer_off_and_says_why(Action<ConfigurationStore> change, string what)
    {
        change(S);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_said, Is.EqualTo($"AutoSteer off: {what} settings changed"));
    }

    [Test]
    public void The_same_value_again_leaves_it_on()
    {
        S.Tool.SetSectionWidth(3, 100);
        S.Tool.Offset = S.Tool.Offset;
        S.Vehicle.AntennaOffset = S.Vehicle.AntennaOffset;
        S.NumSections = 8;

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
        Assert.That(_said, Is.Null);
    }

    [Test]
    public void A_setting_it_does_not_steer_by_leaves_it_on()
    {
        S.Tool.LookAheadOnSetting = 2;
        S.Tool.SlowSpeedCutoff = 1.5;

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
        Assert.That(_said, Is.Null);
    }

    [Test]
    public void Off_already_nothing_is_said_and_it_engages_on_the_new_settings()
    {
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.False);

        S.Tool.Offset = 2.5;
        Assert.That(_said, Is.Null);

        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
    }
}
