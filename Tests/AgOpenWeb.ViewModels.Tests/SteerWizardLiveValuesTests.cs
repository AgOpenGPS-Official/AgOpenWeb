// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.ViewModels.Wizards;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The web wizard types into the live settings (config.set), not into the step. Each step
/// checked the copy it took when it opened: a careless-operator scenario got past the
/// Dimensions step with a wheelbase of 0, the Gains step with Kp 0 and the Speed step with
/// the minimum above the maximum, and the right wheelbase typed after the wrong one was
/// refused with "Wheelbase must be at least 0.5 meters" until Back and Next again.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardLiveValuesTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static ConfigurationStore Store => ConfigurationStore.Instance;

    private static SteerWizardViewModel Wizard()
    {
        var c = Substitute.For<IConfigurationService>();
        c.Store.Returns(ConfigurationStore.Instance);
        return new SteerWizardViewModel(c, new InlineUiDispatcher(), Substitute.For<IAutoSteerService>());
    }

    private static T GoTo<T>(SteerWizardViewModel w) where T : WizardStepViewModel
    {
        var step = w.Steps.OfType<T>().Single();
        w.GoToStep(w.Steps.IndexOf(step));
        return step;
    }

    private static Task NextAsync(SteerWizardViewModel w) => ((IAsyncRelayCommand)w.NextCommand).ExecuteAsync(null);

    [Test]
    public async Task A_wheelbase_typed_in_as_0_is_refused()
    {
        var w = Wizard();
        var dims = GoTo<VehicleDimensionsStepViewModel>(w);

        Store.Vehicle.Wheelbase = 0; // config.set|vehicle.wheelbase:0
        await NextAsync(w);

        Assert.That(w.CurrentStep, Is.SameAs(dims));
        Assert.That(dims.ValidationMessage, Does.Contain("Wheelbase"));
    }

    [Test]
    public async Task The_right_wheelbase_typed_after_a_wrong_one_goes_on()
    {
        Store.Vehicle.Wheelbase = 0;
        var w = Wizard();
        var dims = GoTo<VehicleDimensionsStepViewModel>(w); // opens with 0

        Store.Vehicle.Wheelbase = 2.88;
        await NextAsync(w);

        Assert.That(w.CurrentStep, Is.Not.SameAs(dims), dims.ValidationMessage);
    }

    [Test]
    public async Task A_proportional_gain_typed_in_as_0_is_refused()
    {
        var w = Wizard();
        var gains = GoTo<SteeringGainsStepViewModel>(w);

        Store.AutoSteer.ProportionalGain = 0;
        await NextAsync(w);

        Assert.That(w.CurrentStep, Is.SameAs(gains));
        Assert.That(gains.ValidationMessage, Does.Contain("Proportional Gain"));
    }

    [Test]
    public async Task A_minimum_speed_above_the_maximum_is_refused()
    {
        var w = Wizard();
        var speed = GoTo<SpeedAndSensorsStepViewModel>(w);

        Store.AutoSteer.MinSteerSpeed = 10;
        Store.AutoSteer.MaxSteerSpeed = 5;
        await NextAsync(w);

        Assert.That(w.CurrentStep, Is.SameAs(speed));
        Assert.That(speed.ValidationMessage, Does.Contain("Max Steer Speed"));
    }

    [Test]
    public async Task An_antenna_height_typed_in_as_minus_1_is_refused()
    {
        var w = Wizard();
        var antenna = GoTo<AntennaSetupStepViewModel>(w);

        Store.Vehicle.AntennaHeight = -1;
        await NextAsync(w);

        Assert.That(w.CurrentStep, Is.SameAs(antenna));
        Assert.That(antenna.ValidationMessage, Does.Contain("Antenna height"));
    }
}
