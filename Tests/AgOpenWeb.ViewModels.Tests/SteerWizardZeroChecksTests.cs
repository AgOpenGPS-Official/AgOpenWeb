// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Zero WAS and Zero Roll took whatever they read: the centre with the wheels turned 15°
/// (straight ahead then read -15° and AutoSteer held the wheels off), a 9° slope as level
/// (side-hill compensation then steered uphill on the flat), a second quick press as a
/// second correction (the module hadn't applied the first), and a stale reading with the
/// module unplugged. Now a big reading needs a second press, a press is ignored until the
/// module reports the new centre, and no reading means no zero; the step says which.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardZeroChecksTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static ConfigurationStore Store => ConfigurationStore.Instance;

    private static IConfigurationService Config()
    {
        var c = Substitute.For<IConfigurationService>();
        c.Store.Returns(ConfigurationStore.Instance);
        return c;
    }

    private sealed class Module
    {
        public IAutoSteerService Service { get; } = Substitute.For<IAutoSteerService>();
        public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>The module reports <paramref name="angle"/> now.</summary>
        public void Reads(double angle)
        {
            Service.LastSteerData.Returns(new SteerModuleData(angle, 0, 0, false, false, false, false, 0));
            Service.LastSteerDataUtc.Returns(Now);
        }

        public void Tick(double seconds) => Now = Now.AddSeconds(seconds);

        /// <summary>A state update, as the host raises one per fix.</summary>
        public void Update(double roll = 0) =>
            Service.StateUpdated += Raise.Event<EventHandler<VehicleStateSnapshot>>(Service, new VehicleStateSnapshot { Roll = roll });
    }

    private static WasCalibrationStepViewModel WasStep(Module m)
    {
        var step = new WasCalibrationStepViewModel(Config(), m.Service) { Clock = () => m.Now };
        step.IsActive = true;
        return step;
    }

    private static RollCalibrationStepViewModel RollStep(Module m)
    {
        var step = new RollCalibrationStepViewModel(Config(), m.Service) { Clock = () => m.Now };
        step.IsActive = true;
        return step;
    }

    [Test]
    public void Zero_WAS_with_the_wheels_turned_asks_first_and_takes_a_second_press()
    {
        Store.AutoSteer.WasOffset = 0;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(15);

        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(0), "the first press must not take a centre 15° off");
        Assert.That(step.TestResult, Does.Contain("15.0°").And.Contain("straight ahead"));

        m.Tick(3);
        m.Reads(15); // still reporting, still 15°
        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(WasCalibration.ZeroedOffset(0, 15, 110)), "the second press confirms");
        Assert.That(step.TestResult, Does.Contain("Centre set"));
    }

    [Test]
    public void Zero_WAS_asks_again_when_the_wheels_moved_between_the_presses()
    {
        Store.AutoSteer.WasOffset = 0;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(12);
        step.ZeroWasCommand.Execute(null);

        m.Tick(3);
        m.Reads(21); // the wheels moved: not the reading the operator was shown
        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(0), "21° was never shown, so it is asked about, not taken");
        Assert.That(step.TestResult, Does.Contain("21.0°").And.Contain("straight ahead"));
    }

    [Test]
    public void Zero_WAS_confirmation_expires()
    {
        Store.AutoSteer.WasOffset = 0;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(15);

        step.ZeroWasCommand.Execute(null);
        m.Tick(WasCalibrationStepViewModel.ConfirmWindow.TotalSeconds + 1);
        m.Reads(15);
        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(0), "a press long after the question is a new question");
        Assert.That(step.TestResult, Does.Contain("straight ahead"));
    }

    [Test]
    public void Zero_WAS_pressed_twice_quickly_corrects_once()
    {
        Store.AutoSteer.WasOffset = 0;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(2); // straight enough: no question

        step.ZeroWasCommand.Execute(null);
        int once = Store.AutoSteer.WasOffset;
        step.ZeroWasCommand.Execute(null); // the module hasn't answered: still reads 2

        Assert.That(once, Is.EqualTo(WasCalibration.ZeroedOffset(0, 2, 110)));
        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(once), "the second press must not add the same 2° again");
        Assert.That(step.TestResult, Does.Contain("wait"));
    }

    [Test]
    public void Zero_WAS_can_be_pressed_again_once_the_module_reports_the_new_centre()
    {
        Store.AutoSteer.WasOffset = 0;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(2);
        step.ZeroWasCommand.Execute(null);
        int once = Store.AutoSteer.WasOffset;

        m.Reads(0.1); // the module applied it
        m.Update();
        m.Reads(1); // the operator moved the wheel a little
        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(WasCalibration.ZeroedOffset(once, 1, 110)));
    }

    [Test]
    public void Zero_WAS_with_the_module_unplugged_does_nothing_and_says_so()
    {
        Store.AutoSteer.WasOffset = 100;
        Store.AutoSteer.CountsPerDegree = 110;
        var m = new Module();
        var step = WasStep(m);
        m.Reads(3);
        m.Tick(2.5); // nothing heard since

        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(100));
        Assert.That(step.TestResult, Does.Contain("steer module").And.Contain("power"));
    }

    [Test]
    public void Zero_WAS_before_any_reading_does_nothing_and_says_so()
    {
        Store.AutoSteer.WasOffset = 100;
        var m = new Module();
        var step = WasStep(m); // LastSteerDataUtc stays MinValue

        step.ZeroWasCommand.Execute(null);

        Assert.That(Store.AutoSteer.WasOffset, Is.EqualTo(100));
        Assert.That(step.TestResult, Does.Contain("No reading"));
    }

    [Test]
    public void Zero_Roll_on_a_slope_asks_first_and_takes_a_second_press()
    {
        Store.Ahrs.RollZero = 0;
        var m = new Module();
        var step = RollStep(m);
        m.Update(roll: 9);

        step.ZeroRollCommand.Execute(null);

        Assert.That(Store.Ahrs.RollZero, Is.EqualTo(0), "the first press must not take a 9° slope as level");
        Assert.That(step.TestResult, Does.Contain("9.0°").And.Contain("level"));

        m.Tick(3);
        step.ZeroRollCommand.Execute(null);

        Assert.That(Store.Ahrs.RollZero, Is.EqualTo(9).Within(0.01), "the second press confirms");
    }

    [Test]
    public void Zero_Roll_pressed_twice_quickly_corrects_once()
    {
        Store.Ahrs.RollZero = 0;
        var m = new Module();
        var step = RollStep(m);
        m.Update(roll: 1.2);

        step.ZeroRollCommand.Execute(null);
        step.ZeroRollCommand.Execute(null); // the parser hasn't applied it: still reads 1.2

        Assert.That(Store.Ahrs.RollZero, Is.EqualTo(1.2).Within(0.01), "not 2.4");
        Assert.That(step.TestResult, Does.Contain("wait"));
    }

    [Test]
    public void Zero_Roll_without_a_reading_does_nothing_and_says_so()
    {
        Store.Ahrs.RollZero = 0.5;
        var m = new Module();
        var step = RollStep(m);

        step.ZeroRollCommand.Execute(null);

        Assert.That(Store.Ahrs.RollZero, Is.EqualTo(0.5));
        Assert.That(step.TestResult, Does.Contain("No roll reading"));
    }
}
