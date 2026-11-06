// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Cancel kept what the finished steps set in the live settings (Invert Motor on, the WAS
/// and roll zeros, a wheelbase typed in) without saving it: the module ran it until the
/// next restart, then it was gone. Cancel now puts the settings back as they were when the
/// wizard opened; Finish keeps and saves them.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardCancelTests
{
    private ConfigurationStore _store = null!;
    private IConfigurationService _cfg = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _cfg = Substitute.For<IConfigurationService>();
        _cfg.Store.Returns(_store);
    }

    private SteerWizardViewModel Open() =>
        new(_cfg, new InlineUiDispatcher(), Substitute.For<IAutoSteerService>());

    private void TheStepsAndTheBoxesChangeThings()
    {
        // As the steps' actions and the web's config.set do: straight into the live settings.
        _store.Vehicle.Wheelbase = 3.1;
        _store.Vehicle.AntennaHeight = 2.9;
        _store.Vehicle.MaxSteerAngle = 42;
        _store.AutoSteer.InvertMotor = true;
        _store.AutoSteer.WasOffset = -260;
        _store.AutoSteer.CountsPerDegree = 97;
        _store.AutoSteer.ProportionalGain = 80;
        _store.Ahrs.RollZero = 1.7;
        _store.Guidance.PurePursuitIntegralGain = 0.3;
    }

    [Test]
    public void Cancel_puts_the_settings_back_as_they_were_when_the_wizard_opened()
    {
        _store.Vehicle.Wheelbase = 2.88;
        _store.Vehicle.AntennaHeight = 3.2;
        _store.Vehicle.MaxSteerAngle = 35;
        _store.AutoSteer.InvertMotor = false;
        _store.AutoSteer.WasOffset = 3;
        _store.AutoSteer.CountsPerDegree = 110;
        _store.AutoSteer.ProportionalGain = 50;
        _store.Ahrs.RollZero = 0;
        _store.Guidance.PurePursuitIntegralGain = 0;
        _store.HasUnsavedChanges = false;
        var wizard = Open();

        TheStepsAndTheBoxesChangeThings();
        wizard.CancelCommand.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(2.88));
            Assert.That(_store.Vehicle.AntennaHeight, Is.EqualTo(3.2));
            Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(35));
            Assert.That(_store.AutoSteer.InvertMotor, Is.False);
            Assert.That(_store.AutoSteer.WasOffset, Is.EqualTo(3));
            Assert.That(_store.AutoSteer.CountsPerDegree, Is.EqualTo(110));
            Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
            Assert.That(_store.Ahrs.RollZero, Is.EqualTo(0));
            Assert.That(_store.Guidance.PurePursuitIntegralGain, Is.EqualTo(0));
            Assert.That(_store.HasUnsavedChanges, Is.False, "nothing is left to save");
        });
        _cfg.DidNotReceive().SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void Finish_keeps_and_saves_what_the_wizard_set()
    {
        _store.AutoSteer.InvertMotor = false;
        var wizard = Open();
        TheStepsAndTheBoxesChangeThings();

        while (!wizard.IsOnLastStep)
        {
            int before = wizard.CurrentStepIndex;
            wizard.NextCommand.Execute(null);
            if (wizard.CurrentStepIndex == before) Assert.Inconclusive("couldn't reach the last step");
        }
        wizard.FinishCommand.Execute(null);

        Assert.That(_store.AutoSteer.InvertMotor, Is.True);
        Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(3.1));
        _cfg.Received(1).SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void Closing_the_wizard_without_Cancel_keeps_the_live_settings()
    {
        // The browser went away (MainViewModel.EndRemoteSteerWizard): the operator didn't
        // choose to discard, and the next wizard starts from what is there.
        var wizard = Open();
        TheStepsAndTheBoxesChangeThings();

        wizard.Close();

        Assert.That(_store.AutoSteer.InvertMotor, Is.True);
    }
}
