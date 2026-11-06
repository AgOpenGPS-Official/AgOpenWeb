// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Next on every step finished the wizard with no test run and said nothing: the steer
/// sensor mounted the other way, the motor wired backwards, nothing caught it. The Finish
/// page now names the tests not run; Finish still works (the values as typed are kept).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardFinishWarningTests
{
    private ConfigurationStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
    }

    private SteerWizardViewModel Open()
    {
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(_store);
        return new SteerWizardViewModel(cfg, new InlineUiDispatcher(), Substitute.For<IAutoSteerService>());
    }

    private static T Step<T>(SteerWizardViewModel w) where T : class => w.Steps.OfType<T>().Single();

    private static void NextToTheEnd(SteerWizardViewModel w)
    {
        while (!w.IsOnLastStep)
        {
            int before = w.CurrentStepIndex;
            w.NextCommand.Execute(null);
            if (w.CurrentStepIndex == before) Assert.Inconclusive("couldn't reach the last step");
        }
    }

    [Test]
    public void Next_on_every_step_names_the_six_tests_not_run()
    {
        var w = Open();
        Step<HardwareInstalledStepViewModel>(w).HardwareLevel = 2;

        NextToTheEnd(w);
        string warning = Step<FinishStepViewModel>(w).TestResult;

        Assert.That(warning, Does.StartWith("Not tested: "));
        foreach (var name in new[] { "WAS zero", "roll zero", "motor direction test", "max steer angle test", "CPD circle", "Ackermann circle" })
            Assert.That(warning, Does.Contain(name));
        Assert.That(warning, Does.Contain("Go Back"));
        Assert.That(w.FinishCommand.CanExecute(null), Is.True, "a warning, not a refusal");
    }

    [Test]
    public void A_test_that_was_run_is_not_listed()
    {
        var w = Open();
        Step<HardwareInstalledStepViewModel>(w).HardwareLevel = 2;
        Step<AutoMotorCalibrationStepViewModel>(w).TestDone = true; // as the end of a test sets it
        Step<MaxSteeringAngleStepViewModel>(w).TestDone = true;

        NextToTheEnd(w);
        string warning = Step<FinishStepViewModel>(w).TestResult;

        Assert.That(warning, Does.Not.Contain("motor direction").And.Not.Contain("max steer angle"));
        Assert.That(warning, Does.Contain("the WAS zero").And.Contain("the CPD circle"));
    }

    [Test]
    public void GPS_only_has_no_tests_to_warn_about()
    {
        var w = Open();
        Step<HardwareInstalledStepViewModel>(w).HardwareLevel = 0;

        NextToTheEnd(w);

        Assert.That(Step<FinishStepViewModel>(w).TestResult, Is.Empty);
    }

    [Test]
    public void A_Finish_step_without_the_wizard_warns_about_nothing()
    {
        Assert.That(new FinishStepViewModel().TestResult, Is.Empty);
    }

    [Test]
    public async Task The_motor_test_that_ran_to_its_end_is_not_listed()
    {
        var a = _store.AutoSteer;
        a.ProportionalGain = 50;
        a.MinPwm = 25;
        a.MaxPwm = 180;
        var sim = new SimulatedSteer { MotorBackwards = false, Angle = 0 };
        var cfg = Substitute.For<IConfigurationService>();
        cfg.Store.Returns(_store);
        var step = new AutoMotorCalibrationStepViewModel(cfg, new InlineUiDispatcher(), sim.Service);
        step.DelayFunc = (ms, _) => { sim.Advance(ms); return Task.CompletedTask; };
        step.ReadModuleData = () => sim.Data;

        await step.RunKpRampAsync();

        Assert.That(step.NoMovementDetected, Is.False, step.PhaseResult);
        Assert.That(step.TestDone, Is.True,
            "the ramp is the whole motor test in the web wizard; it was marked done only in a part the web never runs");
    }
}
