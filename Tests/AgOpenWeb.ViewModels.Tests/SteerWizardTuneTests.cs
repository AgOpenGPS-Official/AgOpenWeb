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
/// The steer wizard's wheel tuning step against <see cref="FakeSteering"/>: a hydraulic valve,
/// a Keya on the steering wheel and a motor, each with its own break-away, speed, delay and
/// play. The values it finds must fit the steering it measured, the operator's own values
/// must be back whenever a run ends without Use these values, and everything that can go wrong
/// in the cab (switch off, hands on the wheel, motor the wrong way, Stop) must stop the wheels.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardTuneTests
{
    private ConfigurationStore _store = null!;
    private IConfigurationService _configService = null!;
    private IAutoSteerService _autoSteer = null!;
    private FakeSteering _fake = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _configService = Substitute.For<IConfigurationService>();
        _configService.Store.Returns(_store);
        _fake = new FakeSteering(_store.AutoSteer);
        _autoSteer = Substitute.For<IAutoSteerService>();
        _autoSteer.When(x => x.EnableFreeDrive()).Do(_ => _fake.EnableFreeDrive());
        _autoSteer.When(x => x.DisableFreeDrive()).Do(_ => _fake.DisableFreeDrive());
        _autoSteer.When(x => x.SetFreeDriveAngle(Arg.Any<double>())).Do(c => _fake.SetFreeDriveAngle(c.Arg<double>()));
        _autoSteer.IsInFreeDriveMode.Returns(_ => _fake.FreeDriveOn);
        // The operator's values before the run, and a steer switch for the stop check.
        _store.AutoSteer.ProportionalGain = 50;
        _store.AutoSteer.MaxPwm = 180;
        _store.AutoSteer.MinPwm = 25;
        _store.AutoSteer.ExternalEnable = 1;
    }

    private TuneSteeringStepViewModel CreateStep()
    {
        var step = new TuneSteeringStepViewModel(_configService, new InlineUiDispatcher(), _autoSteer)
        {
            // As Task.Delay with a token: a cancelled test goes no further.
            DelayFunc = (ms, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _fake.Advance(ms);
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            NowMs = () => _fake.NowMs,
            ReadModuleData = _fake.Read,
        };
        return step;
    }

    /// <summary>The careful operator: switch off for the stop check, on again, Continue.</summary>
    private async Task RunAsync(TuneSteeringStepViewModel step)
    {
        _fake.Script = f => { if (f.Steering && step.PhaseDescription.StartsWith("Stop check: switch", StringComparison.Ordinal)) f.SwitchOn = false; };
        await step.RunStopCheckAsync();
        Assert.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        _fake.Script = null;
        _fake.SwitchOn = true;
        _fake.WatchFrom = _fake.Angle;
        await step.RunTuningAsync();
    }

    /// <summary>With the values found in the module, an 8° step: how far past the target, and how long.</summary>
    private (double Overshoot, double SettleSeconds) StepWithFoundValues(TuneSteeringStepViewModel step, double stepDeg = 8)
    {
        _store.AutoSteer.ProportionalGain = step.FoundKp;
        _store.AutoSteer.MaxPwm = step.FoundMaxPwm;
        _store.AutoSteer.MinPwm = step.FoundMinPwm;
        _fake.EnableFreeDrive();
        _fake.SetFreeDriveAngle(_fake.Angle);
        _fake.Advance(600);
        double start = _fake.Angle, target = start + stepDeg, peak = start, settledAt = 0;
        _fake.SetFreeDriveAngle(target);
        for (int t = 0; t < 4000; t += 5)
        {
            _fake.Advance(5);
            peak = Math.Max(peak, _fake.Angle);
            if (Math.Abs(_fake.Angle - target) > 0.5) settledAt = t + 5;
        }
        _fake.DisableFreeDrive();
        return (Math.Max(0, peak - target), settledAt / 1000.0);
    }

    // ------------------------------------------------------------ three kinds of steering

    [Test]
    public async Task Hydraulic_valve_values_fit_the_valve()
    {
        // A proportional valve: opens at PWM 62, 0.3°/s per PWM above it, answers 120 ms late.
        _fake.BreakawayRight = _fake.BreakawayLeft = 62;
        _fake.DegPerSecPerPwm = 0.3;
        _fake.DeadTimeMs = 120;
        _fake.LagMs = 60;
        _fake.LowHighDeg = 1.5;
        _fake.NoiseDeg = 0.03;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.MinRightPwm, Is.InRange(60, 66), "first movement, right");
        Assert.That(step.MinLeftPwm, Is.InRange(60, 66), "first movement, left");
        Assert.That(step.FoundMinPwm, Is.InRange(55, 62), "just under the PWM that opens the valve");
        // 25°/s needs 62 + 25 / 0.3 = 145.
        Assert.That(step.FoundMaxPwm, Is.InRange(130, 160), "Max Limit for 25°/s");
        var (over, settle) = StepWithFoundValues(step);
        Assert.That(over, Is.LessThan(1.0), $"Kp {step.FoundKp}: past the target on an 8° step");
        Assert.That(settle, Is.LessThan(1.5), $"Kp {step.FoundKp}: time to settle an 8° step");
        Assert.That(_fake.MaxExcursion, Is.LessThan(TuneSteeringStepViewModel.WindowDeg), "wheels stayed in their window");
    }

    [Test]
    public async Task Motor_on_the_steering_wheel_values_fit_the_motor()
    {
        // The scenario harness's motor: moves from PWM 28, quick to answer, AiO v4 firmware.
        _fake.BreakawayRight = _fake.BreakawayLeft = 28;
        _fake.DegPerSecPerPwm = 0.25;
        _fake.RateAtBreakaway = 1.0;
        _fake.LowHighDeg = 3.0;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.FoundMinPwm, Is.InRange(23, 28));
        // 25°/s needs about 28 + 24 / 0.25 = 124.
        Assert.That(step.FoundMaxPwm, Is.InRange(108, 138));
        Assert.That(step.FoundKp, Is.InRange(10, 147));
        var (over, settle) = StepWithFoundValues(step);
        Assert.That(over, Is.LessThan(1.0));
        Assert.That(settle, Is.LessThan(1.5));
    }

    [Test]
    public async Task Keya_with_play_values_fit_it()
    {
        // A Keya on the steering wheel: turns from a low PWM, fast, 40 ms late, 0.3° of play.
        _fake.BreakawayRight = _fake.BreakawayLeft = 9;
        _fake.DegPerSecPerPwm = 0.5;
        _fake.DeadTimeMs = 40;
        _fake.BacklashDeg = 0.3;
        _fake.LowHighDeg = 1.5;
        _fake.NoiseDeg = 0.02;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.FoundMinPwm, Is.InRange(5, 10));
        // The lowest Max Limit the module takes is 50, and that already turns the wheels 20°/s.
        Assert.That(step.FoundMaxPwm, Is.InRange(50, 75));
        var (over, settle) = StepWithFoundValues(step);
        Assert.That(over, Is.LessThan(1.2));
        Assert.That(settle, Is.LessThan(1.5));
    }

    [Test]
    public async Task Sides_that_differ_take_the_lower_side_and_say_so()
    {
        _fake.BreakawayRight = 40;
        _fake.BreakawayLeft = 58;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.MinRightPwm, Is.InRange(39, 43));
        Assert.That(step.MinLeftPwm, Is.InRange(57, 61));
        Assert.That(step.FoundMinPwm, Is.InRange(35, 40), "from the lower side");
        Assert.That(step.PhaseResult, Does.Contain("The two sides differ"));
    }

    [Test]
    public async Task A_valve_with_one_stiff_side_gets_a_gain_that_still_moves_that_side()
    {
        // A valve that answers late wants a low gain; with one Minimum To Move for both sides,
        // a low gain leaves the stiff side well short of small turns. The gain picked is the
        // one that keeps the wheels closest to where they are asked on both sides.
        _fake.BreakawayRight = 62;
        _fake.BreakawayLeft = 70;
        _fake.DegPerSecPerPwm = 0.3;
        _fake.DeadTimeMs = 100;
        _fake.LagMs = 60;
        _fake.LowHighDeg = 1.5;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.FoundMinPwm, Is.InRange(55, 62), "below the side that moves first");
        // The left side stops short by about (70 - Minimum To Move) / gain. A gain of 3 to 7,
        // which a late valve with even sides gets, would leave it 2° to 4° short.
        double shortLeft = (70.0 - step.FoundMinPwm) / step.FoundKp;
        Assert.That(shortLeft, Is.LessThan(1.5), step.PhaseResult);
        var (over, _) = StepWithFoundValues(step);
        Assert.That(over, Is.LessThan(1.2), step.PhaseResult);
    }

    [Test]
    public async Task A_very_late_valve_with_one_much_stiffer_side_ends_with_values()
    {
        // Late enough to swing at the first gain, so the lower ones are tried; at those a small
        // turn asks for less PWM than the stiff side needs, and the wheels do not move that
        // way. That is a poor gain (its result says so), not a hand on the wheel.
        _fake.BreakawayRight = 62;
        _fake.BreakawayLeft = 85;
        _fake.DegPerSecPerPwm = 0.3;
        _fake.DeadTimeMs = 350;
        _fake.LagMs = 150;
        _fake.LowHighDeg = 1.5;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.FoundMinPwm, Is.InRange(55, 62));
        Assert.That(step.PhaseResult, Does.Contain("The two sides differ"));
    }

    [Test]
    public async Task A_valve_that_is_fully_open_sets_the_limit_there()
    {
        // No more than 18°/s whatever the PWM: the wanted 25°/s can't be had.
        _fake.BreakawayRight = _fake.BreakawayLeft = 50;
        _fake.DegPerSecPerPwm = 0.3;
        _fake.MaxRate = 18;
        var step = CreateStep();

        await RunAsync(step);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        // 18°/s is reached at 50 + 18 / 0.3 = 110; the level above adds nothing.
        Assert.That(step.FoundMaxPwm, Is.InRange(100, 160));
        Assert.That(step.PhaseResult, Does.Contain("does not turn the wheels faster"));
    }

    [Test]
    public async Task Wanted_speed_changes_the_max_limit()
    {
        _fake.BreakawayRight = _fake.BreakawayLeft = 40;
        _fake.DegPerSecPerPwm = 0.25;
        var gentle = CreateStep();
        gentle.TargetWheelSpeed = 15;
        await RunAsync(gentle);
        int low = gentle.FoundMaxPwm;

        SetUp();
        _fake.BreakawayRight = _fake.BreakawayLeft = 40;
        _fake.DegPerSecPerPwm = 0.25;
        var fast = CreateStep();
        fast.TargetWheelSpeed = 35;
        await RunAsync(fast);

        // 15°/s needs about 100, 35°/s about 180.
        Assert.That(low, Is.InRange(85, 115));
        Assert.That(fast.FoundMaxPwm, Is.InRange(160, 200));
    }

    // ------------------------------------------------------------ nothing kept unless taken

    [Test]
    public async Task The_operators_values_are_back_until_use_these_values()
    {
        var step = CreateStep();
        await RunAsync(step);
        Assume.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);

        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25));
        Assert.That(_fake.FreeDriveOn, Is.False, "module released");

        step.AcceptCommand.Execute(null);

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Accepted));
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(step.FoundKp));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(step.FoundMaxPwm));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(step.FoundMinPwm));
    }

    [Test]
    public async Task Keep_the_old_values_keeps_them()
    {
        var step = CreateStep();
        await RunAsync(step);
        Assume.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);

        step.DiscardCommand.Execute(null);
        step.AcceptCommand.Execute(null); // nothing left to take

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle));
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25));
    }

    [Test]
    public async Task The_module_is_not_asked_to_save_settings_more_than_a_few_dozen_times()
    {
        // Every settings packet is an EEPROM write on the module.
        var step = CreateStep();
        await RunAsync(step);
        Assume.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
        Assert.That(step.SettingsWrites, Is.LessThanOrEqualTo(40));
    }

    // ------------------------------------------------------------ the stop check

    [Test]
    public async Task Nothing_moves_by_itself_before_the_stop_was_seen()
    {
        // The operator does nothing: the check runs out, no setting changed, wheels where they were.
        var step = CreateStep();
        _fake.WatchFrom = _fake.Angle;

        await step.RunStopCheckAsync();
        await step.RunTuningAsync(); // Continue without a passed check does nothing

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle));
        Assert.That(step.PhaseResult, Does.Contain("not seen to stop"));
        Assert.That(_fake.MaxExcursion, Is.LessThan(0.2));
        Assert.That(_fake.FreeDriveOn, Is.False);
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
    }

    [Test]
    public async Task A_kickout_sensor_passes_the_stop_check_with_hands_on_the_wheel()
    {
        _store.AutoSteer.ExternalEnable = 0;
        _store.AutoSteer.PressureSensorEnabled = true;
        _store.AutoSteer.PressureTripPoint = 30;
        _fake.KickoutSensor = true;
        var step = CreateStep();
        _fake.Script = f => { if (f.Steering && step.PhaseDescription.Contains("turn it by hand", StringComparison.Ordinal)) f.Hands = 4; };

        await step.RunStopCheckAsync();

        Assert.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        Assert.That(step.PhaseResult, Does.Contain("Let go of the steering wheel"));
        Assert.That(_fake.FreeDriveOn, Is.False);
    }

    [Test]
    public async Task With_no_stop_at_the_wheel_the_page_says_so_and_takes_its_stop_button()
    {
        _store.AutoSteer.ExternalEnable = 0;
        var step = CreateStep();
        string said = "";
        _fake.Script = f =>
        {
            if (!f.Steering || !step.PhaseDescription.Contains("press Stop now", StringComparison.Ordinal)) return;
            said = step.PhaseDescription;
            step.StopTestCommand.Execute(null);
        };

        await step.RunStopCheckAsync();

        Assert.That(said, Does.Contain("the only stop is the Stop button"));
        Assert.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
    }

    [Test]
    public async Task Wheels_that_run_away_in_the_stop_check_stop_it_within_a_few_degrees()
    {
        // Motor wired the other way, motor test skipped, and a Minimum To Move that moves the
        // wheels: holding them drives them away from where they are held.
        _fake.MotorReversed = true;
        _fake.BreakawayRight = _fake.BreakawayLeft = 20;
        _fake.NoiseDeg = 0.05;
        var step = CreateStep();
        step.IsActive = true; // on the page: the step follows the control loop
        // The control loop's StateUpdated, every module loop.
        _fake.Script = _ => _autoSteer.StateUpdated += Raise.Event<EventHandler<VehicleStateSnapshot>>(_autoSteer, default(VehicleStateSnapshot));

        await step.RunStopCheckAsync();

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle), step.PhaseResult);
        Assert.That(step.PhaseResult, Does.Contain("only asked to hold"));
        Assert.That(_fake.FreeDriveOn, Is.False, "module released");
        Assert.That(_fake.MaxExcursion, Is.LessThan(10.0), "the wheels were stopped soon after they began to run");
    }

    [Test]
    public async Task Wheels_not_straight_refuse_the_start()
    {
        _fake.SetWheel(15);
        var step = CreateStep();

        await step.RunStopCheckAsync();

        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle));
        Assert.That(step.PhaseResult, Does.Contain("Straighten the wheels"));
        Assert.That(_fake.FreeDriveOn, Is.False);
    }

    [Test]
    public async Task Switch_still_off_at_continue_asks_for_it_and_can_continue_again()
    {
        var step = CreateStep();
        _fake.Script = f => { if (f.Steering && step.PhaseDescription.StartsWith("Stop check: switch", StringComparison.Ordinal)) f.SwitchOn = false; };
        await step.RunStopCheckAsync();
        Assume.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        _fake.Script = null;

        await step.RunTuningAsync(); // switch still off

        Assert.That(step.Stage, Is.EqualTo(TuneStage.StopChecked));
        Assert.That(step.PhaseResult, Does.Contain("turn on the steer switch"));

        _fake.SwitchOn = true;
        await step.RunTuningAsync();
        Assert.That(step.Stage, Is.EqualTo(TuneStage.Result), step.PhaseResult);
    }

    // ------------------------------------------------------------ what stops a run

    private async Task<TuneSteeringStepViewModel> RunWithAsync(Action<FakeSteering, TuneSteeringStepViewModel> during)
    {
        var step = CreateStep();
        _fake.Script = f => { if (f.Steering && step.PhaseDescription.StartsWith("Stop check: switch", StringComparison.Ordinal)) f.SwitchOn = false; };
        await step.RunStopCheckAsync();
        Assume.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        _fake.SwitchOn = true;
        _fake.WatchFrom = _fake.Angle;
        _fake.Script = f => during(f, step);
        await step.RunTuningAsync();
        return step;
    }

    private void AssertStoppedClean(TuneSteeringStepViewModel step, string says)
    {
        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle), step.PhaseResult);
        Assert.That(step.PhaseResult, Does.Contain(says));
        Assert.That(step.PhaseResult, Does.Contain("Nothing was changed"));
        Assert.That(_fake.FreeDriveOn, Is.False, "module released");
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50), "Kp back");
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180), "Max Limit back");
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25), "Minimum To Move back");
    }

    [TestCase("Minimum To Move")]
    [TestCase("Max Limit")]
    [TestCase("Proportional Gain")]
    public async Task Stop_in_any_part_releases_the_module_and_puts_the_values_back(string part)
    {
        bool pressed = false;
        int loops = 0;
        var step = await RunWithAsync((f, s) =>
        {
            if (pressed || !s.PhaseDescription.StartsWith(part, StringComparison.Ordinal) || !f.Steering || ++loops < 40) return;
            pressed = true;
            s.StopTestCommand.Execute(null);
        });

        Assert.That(pressed, Is.True, "the run reached " + part);
        AssertStoppedClean(step, "Stopped.");
    }

    [TestCase("Minimum To Move")]
    [TestCase("Max Limit")]
    [TestCase("Proportional Gain")]
    public async Task Steer_switch_off_in_any_part_stops_the_run(string part)
    {
        // A second into the part (past its arming), with the module driving.
        int loops = 0;
        var step = await RunWithAsync((f, s) =>
        {
            if (s.PhaseDescription.StartsWith(part, StringComparison.Ordinal) && f.Steering && ++loops >= 40) f.SwitchOn = false;
        });

        Assert.That(loops, Is.GreaterThanOrEqualTo(40), "the run reached " + part);
        AssertStoppedClean(step, "stopped steering");
    }

    [Test]
    public async Task Steer_switch_off_while_a_part_arms_goes_back_to_continue()
    {
        var step = await RunWithAsync((f, s) =>
        {
            if (s.PhaseDescription.StartsWith("Max Limit", StringComparison.Ordinal) && f.FreeDriveOn) f.SwitchOn = false;
        });

        Assert.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        Assert.That(step.PhaseResult, Does.Contain("turn on the steer switch"));
        Assert.That(_fake.FreeDriveOn, Is.False);
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25));
    }

    [Test]
    public async Task Motor_the_wrong_way_stops_within_a_few_degrees()
    {
        _fake.MotorReversed = true; // and Invert Motor not set: the motor test was skipped
        var step = await RunWithAsync((_, _) => { });

        AssertStoppedClean(step, "turned the wrong way");
        Assert.That(_fake.MaxExcursion, Is.LessThan(6.0), "wheels stopped soon after going the wrong way");
    }

    [Test]
    public async Task Hands_on_the_wheel_from_the_start_ends_the_search_at_its_limit()
    {
        var step = await RunWithAsync((f, _) => f.Hands ??= f.Angle);

        AssertStoppedClean(step, "did not move up to PWM 200");
        Assert.That(_fake.PeakPwm, Is.LessThanOrEqualTo(205), "the search never drove harder than its limit");
    }

    [TestCase("Minimum To Move: looking", 4)]
    [TestCase("Minimum To Move: left, quick", 4)]
    [TestCase("Minimum To Move: left, slow", 4)]
    [TestCase("Minimum To Move: left, slow", 40)]
    [TestCase("Minimum To Move: right, quick", 20)]
    [TestCase("Minimum To Move: right, slow", 4)]
    [TestCase("Max Limit", 40)]
    [TestCase("Proportional Gain", 40)]
    [TestCase("Proportional Gain", 400)]
    public async Task A_hand_on_the_wheel_in_any_part_stops_the_run_within_seconds(string part, int afterLoops)
    {
        // No kickout sensor: the step itself has to notice that the wheels no longer answer.
        double grabbedAt = -1;
        int loops = 0, pushedMs = 0;
        var step = await RunWithAsync((f, s) =>
        {
            if (grabbedAt < 0 && s.PhaseDescription.StartsWith(part, StringComparison.Ordinal) && f.Steering && ++loops >= afterLoops)
            {
                grabbedAt = f.NowMs;
                f.Hands = f.Angle;
            }
            // Pushing: with a PWM that would move the wheels were they free.
            if (f.Hands != null && f.Steering && Math.Abs(f.Pwm) >= f.BreakawayRight)
                pushedMs += f.LoopMs;
        });

        Assert.That(grabbedAt, Is.GreaterThan(0), "the run reached " + part);
        Assert.That(step.Stage, Is.EqualTo(TuneStage.Idle), step.PhaseResult);
        Assert.That(step.PhaseResult, Does.Contain("on the wheel"));
        Assert.That(step.PhaseResult, Does.Contain("Nothing was changed"));
        Assert.That(_fake.FreeDriveOn, Is.False, "module released");
        Assert.That(_fake.NowMs - grabbedAt, Is.LessThan(9000), "seconds between the grab and the module let go");
        Assert.That(pushedMs, Is.LessThan(2600), "milliseconds the motor pushed against the hand");
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25));
    }

    [Test]
    public void A_pressure_or_current_sensor_with_no_trip_point_is_no_stop_at_the_wheel()
    {
        var a = new AutoSteerConfig { ExternalEnable = 0, PressureSensorEnabled = true, PressureTripPoint = 0 };
        Assert.That(TuneSteeringStepViewModel.StopKindFor(a), Is.EqualTo(TuneStopKind.Screen));
        a.PressureTripPoint = 30;
        Assert.That(TuneSteeringStepViewModel.StopKindFor(a), Is.EqualTo(TuneStopKind.Sensor));
        a.ExternalEnable = 1;
        Assert.That(TuneSteeringStepViewModel.StopKindFor(a), Is.EqualTo(TuneStopKind.SwitchOrSensor));
        var b = new AutoSteerConfig { ExternalEnable = 2 };
        Assert.That(TuneSteeringStepViewModel.StopKindFor(b), Is.EqualTo(TuneStopKind.Switch));
    }

    [Test]
    public async Task Leaving_the_step_in_the_middle_stops_it()
    {
        bool left = false;
        var step = CreateStep();
        typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel).GetProperty("IsActive")!
            .SetValue(step, true);
        _fake.Script = f => { if (f.Steering && step.PhaseDescription.StartsWith("Stop check: switch", StringComparison.Ordinal)) f.SwitchOn = false; };
        await step.RunStopCheckAsync();
        Assume.That(step.Stage, Is.EqualTo(TuneStage.StopChecked), step.PhaseResult);
        _fake.SwitchOn = true;
        _fake.Script = f =>
        {
            if (left || !step.PhaseDescription.StartsWith("Max Limit", StringComparison.Ordinal) || !f.Steering) return;
            left = true;
            typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel).GetProperty("IsActive")!
                .SetValue(step, false); // Next / Back / the wizard closing
        };

        await step.RunTuningAsync();

        Assert.That(left, Is.True);
        Assert.That(_fake.FreeDriveOn, Is.False);
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(50));
        Assert.That(_store.AutoSteer.MaxPwm, Is.EqualTo(180));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25));
    }

    [Test]
    public async Task The_motor_never_drives_after_the_module_was_let_go()
    {
        var step = CreateStep();
        await RunAsync(step);
        Assert.That(_fake.DroveWhileOffMs, Is.EqualTo(0));
    }

    /// <summary>What the step finds on a range of steerings, and how long it takes (run by hand).</summary>
    [Test, Explicit]
    public async Task Report()
    {
        var plants = new (string Name, Action<FakeSteering> Make)[]
        {
            ("motor 28, quick, LHD 3", f => { f.BreakawayRight = f.BreakawayLeft = 28; f.RateAtBreakaway = 1; }),
            ("valve 62, 120 ms late", f => { f.BreakawayRight = f.BreakawayLeft = 62; f.DegPerSecPerPwm = 0.3; f.DeadTimeMs = 120; f.LagMs = 60; f.LowHighDeg = 1.5; f.NoiseDeg = 0.03; }),
            ("valve 90, 200 ms late, slow", f => { f.BreakawayRight = 88; f.BreakawayLeft = 95; f.DegPerSecPerPwm = 0.2; f.DeadTimeMs = 200; f.LagMs = 100; f.LowHighDeg = 1.5; f.NoiseDeg = 0.05; }),
            ("keya 9, play 0.3", f => { f.BreakawayRight = f.BreakawayLeft = 9; f.DegPerSecPerPwm = 0.5; f.DeadTimeMs = 40; f.BacklashDeg = 0.3; f.LowHighDeg = 1.5; f.NoiseDeg = 0.02; }),
            ("motor 35, play 1.0, 60 ms", f => { f.BreakawayRight = f.BreakawayLeft = 35; f.DegPerSecPerPwm = 0.35; f.DeadTimeMs = 60; f.BacklashDeg = 1.0; f.NoiseDeg = 0.05; }),
            ("valve capped 18 deg/s", f => { f.BreakawayRight = f.BreakawayLeft = 50; f.DegPerSecPerPwm = 0.3; f.MaxRate = 18; f.DeadTimeMs = 100; }),
            ("very late valve 350 ms", f => { f.BreakawayRight = f.BreakawayLeft = 70; f.DegPerSecPerPwm = 0.3; f.DeadTimeMs = 350; f.LagMs = 150; f.LowHighDeg = 1.5; }),
            ("valve 62 right, 68 left, 100 ms late", f => { f.BreakawayRight = 62; f.BreakawayLeft = 68; f.DegPerSecPerPwm = 0.3; f.DeadTimeMs = 100; f.LagMs = 80; f.MaxRate = 40; f.LowHighDeg = 1.5; f.NoiseDeg = 0.03; }),
            ("keya 9, slow, play 0.4", f => { f.BreakawayRight = f.BreakawayLeft = 9; f.DegPerSecPerPwm = 0.13; f.DeadTimeMs = 50; f.LagMs = 40; f.BacklashDeg = 0.4; f.LowHighDeg = 1.5; f.NoiseDeg = 0.02; }),
            ("very late valve, 62 right, 85 left", f => { f.BreakawayRight = 62; f.BreakawayLeft = 85; f.DegPerSecPerPwm = 0.3; f.DeadTimeMs = 350; f.LagMs = 150; f.LowHighDeg = 1.5; }),
            ("noisy sensor 0.12", f => { f.BreakawayRight = f.BreakawayLeft = 45; f.DegPerSecPerPwm = 0.3; f.DeadTimeMs = 80; f.NoiseDeg = 0.12; }),
        };
        foreach (var (name, make) in plants)
        {
            SetUp();
            make(_fake);
            var step = CreateStep();
            double t0 = _fake.NowMs;
            await RunAsync(step);
            double took = (_fake.NowMs - t0) / 1000;
            TestContext.Out.WriteLine($"== {name}: {step.Stage} in {took:F0} s, {step.SettingsWrites} settings writes, wheels up to {_fake.MaxExcursion:F1} deg from the start, peak PWM {_fake.PeakPwm}");
            TestContext.Out.WriteLine(step.PhaseResult);
            if (step.Stage == TuneStage.Result)
            {
                var (o4, s4) = StepWithFoundValues(step, 2);
                var (o8, s8) = StepWithFoundValues(step, 8);
                TestContext.Out.WriteLine($"   with these: 2 deg step {o4:F2} past, {s4:F2} s; 8 deg step {o8:F2} past, {s8:F2} s");
            }
        }
    }

    // ------------------------------------------------------------ the arithmetic

    [Test]
    public void Median_of_three_ignores_the_odd_one()
    {
        Assert.That(SteerTuneMath.Median(new[] { 31, 30, 58 }), Is.EqualTo(31));
        Assert.That(SteerTuneMath.Median(new[] { 30, 34 }), Is.EqualTo(32));
    }

    [Test]
    public void Slope_is_the_speed_of_a_steady_turn()
    {
        var pts = Enumerable.Range(0, 10).Select(i => (X: i * 0.03, Y: 1.5 + i * 0.03 * 22.0)).ToList();
        Assert.That(SteerTuneMath.Slope(pts), Is.EqualTo(22.0).Within(1e-6));
    }

    [Test]
    public void Step_response_reads_overshoot_crossings_and_settling()
    {
        // 4° step: up in 0.2 s, 0.8° past, back to the target and still from 0.4 s.
        var s = new List<(double T, double Moved)>();
        for (int i = 0; i <= 10; i++) s.Add((i * 0.02, i * 0.48));          // 0 … 4.8 at 0.2 s
        for (int i = 1; i <= 10; i++) s.Add((0.2 + i * 0.02, 4.8 - i * 0.08)); // back to 4.0 at 0.4 s
        for (int i = 1; i <= 20; i++) s.Add((0.4 + i * 0.02, 4.0));

        var r = SteerTuneMath.StepResponse(s, 4.0);

        Assert.That(r.OvershootDeg, Is.EqualTo(0.8).Within(1e-6));
        Assert.That(r.Crossings, Is.EqualTo(1));
        Assert.That(r.Reached, Is.True);
        Assert.That(r.SettleSeconds, Is.EqualTo(0.34).Within(0.03));
        Assert.That(r.FinalErrorDeg, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void Swinging_across_the_target_counts_every_crossing()
    {
        var s = new List<(double T, double Moved)>();
        for (int i = 0; i < 60; i++) s.Add((i * 0.02, 4.0 + Math.Sin(i * 0.5) * 1.0));
        Assert.That(SteerTuneMath.Crossings(s, 4.0), Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Pwm_for_the_wanted_speed_lies_between_the_two_levels_around_it()
    {
        var levels = new List<(int Pwm, double Speed)> { (80, 10), (100, 20), (125, 30) };
        Assert.That(SteerTuneMath.PwmForSpeed(levels, 25, 60), Is.EqualTo(113));
        // First level already fast enough: on the line from the PWM that starts the wheels.
        Assert.That(SteerTuneMath.PwmForSpeed(new List<(int, double)> { (100, 40) }, 20, 60), Is.EqualTo(80));
    }
}
