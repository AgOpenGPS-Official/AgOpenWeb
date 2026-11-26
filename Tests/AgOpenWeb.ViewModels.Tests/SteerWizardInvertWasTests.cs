// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Turning Invert WAS on after Zero WAS moved the centre: the firmware subtracts the offset
/// for an inverted sensor, so straight ahead read twice the offset off and had to be zeroed
/// again (the step didn't say so). The toggle now flips the offset with it, and the WAS step
/// follows the live settings so that leaving it doesn't write the old offset back.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardInvertWasTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static ConfigurationStore Store => ConfigurationStore.Instance;

    private static WasCalibrationStepViewModel ActiveStep()
    {
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(Store);
        var step = new WasCalibrationStepViewModel(cfg, Substitute.For<IAutoSteerService>());
        step.IsActive = true;
        return step;
    }

    [Test]
    public void Invert_WAS_toggled_after_a_zero_keeps_the_flipped_offset_when_the_step_is_left()
    {
        Store.AutoSteer.InvertWas = false;
        Store.AutoSteer.WasOffset = 0;
        var step = ActiveStep();
        step.WasOffset = -260; // what Zero WAS writes: the step's own change

        // The web's Invert WAS toggle: config.set|autosteer.invertWas, into the live settings.
        Store.AutoSteer.SetInvertWasKeepingCentre(true);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(260), "the toggle flips the offset");
        Assert.That(step.WasOffset, Is.EqualTo(260), "the step shows the live offset");
        Assert.That(step.InvertWas, Is.True, "and the live toggle");

        step.IsActive = false; // Next, Back or Close

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(260), "leaving the step must not write -260 back");
        Assert.That(Store.AutoSteer.InvertWas, Is.True);
    }

    [Test]
    public void The_step_stops_following_the_settings_once_left()
    {
        Store.AutoSteer.WasOffset = 10;
        var step = ActiveStep();
        step.IsActive = false;

        Store.AutoSteer.WasOffset = 20;

        Assert.That(step.WasOffset, Is.EqualTo(10));
    }
}
