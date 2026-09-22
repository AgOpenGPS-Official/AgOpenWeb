// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Windows.Input;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #108: the web wizard edits values straight into the ConfigurationStore (config.set); each
/// step used to write its entry-time copy back on Next/Back, reverting those edits. Steps now
/// write back only values they changed themselves (actions, wizard.set, native bindings).
/// Driven through the real SteerWizardViewModel navigation, like the web.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardWriteBackTests
{
    private ConfigurationStore _store = null!;
    private SteerWizardViewModel _wizard = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _store.Vehicle.Wheelbase = 2.5;
        _store.Vehicle.TrackWidth = 1.8;
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(_store);
        _wizard = new SteerWizardViewModel(cfg, new AgOpenWeb.Services.Threading.InlineUiDispatcher());
    }

    private void GoToDimensions()
    {
        while (_wizard.CurrentStep is not VehicleDimensionsStepViewModel)
        {
            int before = _wizard.CurrentStepIndex;
            _wizard.NextCommand.Execute(null);
            Assert.That(_wizard.CurrentStepIndex, Is.GreaterThan(before), "wizard didn't advance");
        }
    }

    [Test]
    public void StoreEdit_WhileOnStep_SurvivesNext()
    {
        GoToDimensions();

        _store.Vehicle.Wheelbase = 3.3; // web: config.set|vehicle.wheelbase:3.3

        _wizard.NextCommand.Execute(null);

        Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(3.3), "Next must not revert the web edit");
        Assert.That(_store.Vehicle.TrackWidth, Is.EqualTo(1.8));
    }

    [Test]
    public void StoreEdit_WhileOnStep_SurvivesBack()
    {
        GoToDimensions();

        _store.Vehicle.TrackWidth = 2.2;

        _wizard.BackCommand.Execute(null);

        Assert.That(_store.Vehicle.TrackWidth, Is.EqualTo(2.2), "Back must not revert the web edit");
    }

    [Test]
    public void StepEdit_IsStillWrittenBack()
    {
        // wizard.set / a native binding changes the step's own property → persisted on leave.
        GoToDimensions();
        var step = (VehicleDimensionsStepViewModel)_wizard.CurrentStep!;

        step.Wheelbase = 2.9;

        _wizard.NextCommand.Execute(null);

        Assert.That(_store.Vehicle.Wheelbase, Is.EqualTo(2.9));
        Assert.That(_store.Vehicle.TrackWidth, Is.EqualTo(1.8), "untouched values aren't rewritten");
    }

    [Test]
    public void StepActionResult_IsWrittenBack()
    {
        // Zero WAS is an action on the step's own copy; it must still persist.
        var auto = Substitute.For<IAutoSteerService>();
        auto.LastSteerData.Returns(new AgOpenWeb.Models.SteerModuleData(
            ActualSteerAngle: 2.0, ImuHeading: 0, ImuRoll: 0, WorkSwitchActive: false,
            SteerSwitchActive: false, RemoteButtonPressed: false, VwasFusionActive: false, PwmDisplay: 0));
        _store.AutoSteer.CountsPerDegree = 100;
        _store.AutoSteer.WasOffset = 0;
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(_store);
        var wizard = new SteerWizardViewModel(cfg, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), auto);
        while (wizard.CurrentStep is not WasCalibrationStepViewModel)
        {
            int before = wizard.CurrentStepIndex;
            wizard.SkipCommand.CanExecute(null);
            wizard.NextCommand.Execute(null);
            if (wizard.CurrentStepIndex == before) Assert.Inconclusive("couldn't navigate to the WAS step");
        }
        var was = (WasCalibrationStepViewModel)wizard.CurrentStep!;

        ((ICommand)was.ZeroWasCommand).Execute(null);
        wizard.NextCommand.Execute(null);

        Assert.That(_store.AutoSteer.WasOffset, Is.EqualTo(-200), "Zero WAS result persists on Next");
    }
}
