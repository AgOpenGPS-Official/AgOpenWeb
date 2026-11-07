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
/// The motor test is where the wizard learns which way the motor turns; until then, and in
/// the max-angle test if the motor test was skipped, nothing may hold or centre the wheels at
/// a real gain, because with the motor wired the other way every hold drives them away. A
/// steer scenario run (careless operator, motor wired backwards) saw the wheels swing from
/// 12° to the lock when the motor test armed at 0°, 10° the wrong way while Invert Motor was
/// on its way to the module, and the max-angle test drive them into the opposite lock and
/// push there for 5 s. These tests drive the steps against a small simulated steer loop:
/// the module's P controller with its minimum PWM, a motor with a break-away PWM, the locks,
/// and settings that reach the module 150 ms after they change (the config debounce).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardMotorDirectionTests
{
    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var a = ConfigurationStore.Instance.AutoSteer;
        a.ProportionalGain = 50;
        a.MinPwm = 25;
        a.MaxPwm = 180;
        a.InvertMotor = false;
    }

    private static IConfigurationService Config()
    {
        var c = Substitute.For<IConfigurationService>();
        c.Store.Returns(ConfigurationStore.Instance);
        return c;
    }

    private static AutoMotorCalibrationStepViewModel MotorStep(SimulatedSteer sim)
    {
        var step = new AutoMotorCalibrationStepViewModel(Config(), new InlineUiDispatcher(), sim.Service);
        step.DelayFunc = (ms, _) => { sim.Advance(ms); return Task.CompletedTask; };
        step.ReadModuleData = () => sim.Data;
        return step;
    }

    private static MaxSteeringAngleStepViewModel MaxStep(SimulatedSteer sim)
    {
        var step = new MaxSteeringAngleStepViewModel(Config(), new InlineUiDispatcher(), sim.Service);
        step.DelayFunc = (ms, _) => { sim.Advance(ms); return Task.CompletedTask; };
        step.ReadWasAngle = () => sim.Angle;
        return step;
    }

    [Test]
    public async Task Motor_test_with_the_motor_wired_backwards_does_not_swing_the_wheels()
    {
        var sim = new SimulatedSteer { MotorBackwards = true, Angle = 12 };
        var step = MotorStep(sim);

        await step.RunKpRampAsync();

        Assert.That(step.DetectedInvertMotor, Is.True, step.PhaseResult);
        Assert.That(ConfigurationStore.Instance.AutoSteer.InvertMotor, Is.True);
        Assert.That(sim.MaxAngle, Is.LessThan(20),
            "a 5° nudge: the wheels must stay near where they were (they swung to the lock)");
        Assert.That(sim.MinAngle, Is.GreaterThan(-2), "afterwards they are brought back to centre, no further");
        Assert.That(sim.MsAtLockDriving, Is.Zero);
        Assert.That(ConfigurationStore.Instance.AutoSteer.ProportionalGain, Is.EqualTo(50));
    }

    [Test]
    public async Task Motor_test_with_the_motor_wired_backwards_moves_little_the_wrong_way()
    {
        var sim = new SimulatedSteer { MotorBackwards = true, Angle = 0 };
        var step = MotorStep(sim);

        await step.RunKpRampAsync();

        Assert.That(step.DetectedInvertMotor, Is.True, step.PhaseResult);
        Assert.That(sim.MinAngle, Is.GreaterThan(-3),
            "the wrong-way move before the flip must stop at about the 1° that detects it (it went 10°)");
    }

    [Test]
    public async Task Motor_test_turns_Invert_Motor_off_for_a_motor_rewired_since()
    {
        // Calibrated as inverted, then the motor leads were swapped back.
        ConfigurationStore.Instance.AutoSteer.InvertMotor = true;
        var sim = new SimulatedSteer { MotorBackwards = false, Angle = 0 };
        var step = MotorStep(sim);

        await step.RunKpRampAsync();

        Assert.That(step.NoMovementDetected, Is.False, step.PhaseResult);
        Assert.That(ConfigurationStore.Instance.AutoSteer.InvertMotor, Is.False);
        Assert.That(step.DetectedInvertMotor, Is.False);
        Assert.That(sim.MaxDistanceFrom(0), Is.LessThan(8));
    }

    [Test]
    public async Task Motor_test_with_a_normal_motor_still_finds_it_and_centres()
    {
        var sim = new SimulatedSteer { MotorBackwards = false, Angle = 0 };
        var step = MotorStep(sim);

        await step.RunKpRampAsync();

        Assert.That(step.DetectedInvertMotor, Is.False, step.PhaseResult);
        Assert.That(step.DetectedMinPwm, Is.InRange(sim.BreakawayPwm - 2, sim.BreakawayPwm + 3),
            "the PWM the wheels need to move (it read 44 for a motor that moves from 28)");
        Assert.That(Math.Abs(sim.Angle), Is.LessThanOrEqualTo(SwitchGatedWizardStep.CenterToleranceDeg + 0.5),
            "a motor seen turning the right way is brought back to centre");
        Assert.That(sim.FreeDrive, Is.False);
    }

    [Test]
    public async Task Max_angle_test_with_the_motor_wired_backwards_stops_and_leaves_the_wheels()
    {
        // The motor test was skipped (Next), so Invert Motor is still wrong.
        var sim = new SimulatedSteer { MotorBackwards = true, Angle = 0 };
        var step = MaxStep(sim);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Is.EqualTo(MaxSteeringAngleStepViewModel.WrongWayText));
        Assert.That(sim.MaxDistanceFrom(0), Is.LessThan(8),
            "it drove the wheels into the opposite lock and pushed there for 5 s");
        Assert.That(sim.MsAtLockDriving, Is.Zero);
        Assert.That(sim.FreeDrive, Is.False);
        Assert.That(step.CalibrationCompleted, Is.False);
    }

    [Test]
    public async Task Max_angle_test_with_a_normal_motor_still_finds_both_locks()
    {
        var sim = new SimulatedSteer { MotorBackwards = false, Angle = 0, LockDeg = 35 };
        var step = MaxStep(sim);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.CalibrationCompleted, Is.True, step.PhaseResult);
        Assert.That(step.DetectedMaxAngleRight, Is.EqualTo(35).Within(0.5));
        Assert.That(step.DetectedMaxAngleLeft, Is.EqualTo(35).Within(0.5));
        Assert.That(Math.Abs(sim.Angle), Is.LessThanOrEqualTo(SwitchGatedWizardStep.CenterToleranceDeg + 0.5));
    }

    [Test]
    public async Task Arming_holds_the_wheels_where_they_are()
    {
        // Wheels left 12° off centre: arming used to command 0° at the operator's gain.
        var sim = new SimulatedSteer { MotorBackwards = false, Angle = 12 };
        var step = MaxStep(sim);
        double? atArm = null;
        // The first full-lock command comes right after arming: note the angle then.
        sim.OnSetAngle = deg => { if (Math.Abs(deg) > 30 && atArm == null) atArm = sim.Angle; };

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(atArm, Is.EqualTo(12).Within(1), "the wheels moved while the module armed");
    }

    // The operator held the wheel straight through the motor test: the ramp went on to Kp 200,
    // eight seconds of full power against their hands, and then said no movement up to 200.
    [Test]
    public async Task Motor_test_stops_soon_when_the_wheels_are_held()
    {
        var sim = new SimulatedSteer { Angle = 0, Hands = 0 };
        var step = MotorStep(sim);

        await step.RunKpRampAsync();

        Assert.That(step.NoMovementDetected, Is.True);
        Assert.That(step.PhaseResult, Does.Contain("don't move at full power").And.Contain("Hands on the wheel"));
        Assert.That(sim.MsDrivingAgainstHands, Is.LessThan(2500),
            "a second at full power says it all; it used to push for the whole ramp");
        Assert.That(sim.FreeDrive, Is.False, "the motor is released");
        Assert.That(ConfigurationStore.Instance.AutoSteer.ProportionalGain, Is.EqualTo(50), "the operator's Kp is back");
        Assert.That(step.CalibrationCompleted, Is.False);
    }
}

/// <summary>
/// A steer module and its motor in 10 ms ticks: the AiO firmware's P controller (Kp × error,
/// plus MinPWM, capped by the low/high PWM with the low cap below 3°, reversed by Invert
/// Motor), a motor that needs <see cref="BreakawayPwm"/> to move and then turns at
/// 0.25°/s per PWM step above it, the locks, and the settings taking effect 150 ms after the
/// store changes them (AutoSteerService's debounce). Free Drive on = the module steers to the
/// commanded angle; off = the motor stops.
/// </summary>
internal sealed class SimulatedSteer
{
    public IAutoSteerService Service { get; } = Substitute.For<IAutoSteerService>();
    public bool MotorBackwards { get; init; }
    public double Angle { get; set; }
    public double LockDeg { get; init; } = 38;
    public int BreakawayPwm { get; init; } = 28;
    public bool FreeDrive { get; private set; }
    public double SetPoint { get; private set; }
    public double MinAngle { get; private set; } = double.MaxValue;
    public double MaxAngle { get; private set; } = double.MinValue;
    public int MsAtLockDriving { get; private set; }
    /// <summary>The operator's hands on the wheel: it stays there whatever the motor does.</summary>
    public double? Hands { get; set; }
    /// <summary>How long the motor drove (above its break-away PWM) against the hands.</summary>
    public int MsDrivingAgainstHands { get; private set; }
    public Action<double>? OnSetAngle { get; set; }
    private int _pwm;
    private long _now;
    private readonly List<(long At, int Kp, int Min, int Max, bool Inv)> _sent = new();

    public SimulatedSteer()
    {
        Service.When(s => s.EnableFreeDrive()).Do(_ => { FreeDrive = true; SetPoint = 0; });
        Service.When(s => s.DisableFreeDrive()).Do(_ => { FreeDrive = false; SetPoint = 0; });
        Service.When(s => s.SetFreeDriveAngle(Arg.Any<double>())).Do(c =>
        {
            SetPoint = Math.Clamp(c.Arg<double>(), -40, 40);
            OnSetAngle?.Invoke(SetPoint);
        });
        Service.IsInFreeDriveMode.Returns(_ => FreeDrive);
        Service.LastSteerData.Returns(_ => Data);
        var a = ConfigurationStore.Instance.AutoSteer;
        _sent.Add((-1000, a.ProportionalGain, a.MinPwm, a.MaxPwm, a.InvertMotor)); // the module's settings at the start
    }

    /// <summary>PGN 253 as the step reads it: bit 1 set when the module is not steering.</summary>
    public SteerModuleData Data => SteerModuleData.Empty with
    {
        ActualSteerAngle = Angle,
        PwmDisplay = (byte)Math.Min(255, Math.Abs(_pwm)),
        SteerSwitchActive = !FreeDrive,
    };

    public double MaxDistanceFrom(double deg) => Math.Max(Math.Abs(MaxAngle - deg), Math.Abs(MinAngle - deg));

    public void Advance(int ms)
    {
        for (int t = 0; t < ms; t += 10) Tick();
    }

    private void Tick()
    {
        _now += 10;
        var a = ConfigurationStore.Instance.AutoSteer;
        var latest = (_now, a.ProportionalGain, a.MinPwm, a.MaxPwm, a.InvertMotor);
        if (_sent[^1].Kp != latest.ProportionalGain || _sent[^1].Min != latest.MinPwm
            || _sent[^1].Max != latest.MaxPwm || _sent[^1].Inv != latest.InvertMotor)
            _sent.Add(latest);
        var applied = _sent.Last(x => x.At <= _now - 150);

        if (!FreeDrive)
            _pwm = 0;
        else
        {
            double err = SetPoint - Angle;
            int pwm = (int)(applied.Kp * err);
            if (pwm > 0) pwm += applied.Min; else if (pwm < 0) pwm -= applied.Min;
            double low = applied.Min * 1.2;
            int cap = Math.Abs(err) < 3 ? (int)(Math.Abs(err) * (applied.Max - low) / 3 + low) : applied.Max;
            pwm = Math.Clamp(pwm, -cap, cap);
            if (applied.Inv) pwm = -pwm;
            _pwm = pwm;
        }

        int over = Math.Abs(_pwm) - BreakawayPwm;
        if (Hands is { } hand)
        {
            Angle = Math.Clamp(hand, -LockDeg, LockDeg);
            if (over > 0) MsDrivingAgainstHands += 10;
        }
        else if (over > 0)
        {
            double dir = Math.Sign(_pwm) * (MotorBackwards ? -1 : 1);
            Angle = Math.Clamp(Angle + dir * (over + 4) * 0.25 * 0.01, -LockDeg, LockDeg);
            if (Math.Abs(Angle) >= LockDeg - 1e-9 && Math.Sign(Angle) == Math.Sign(dir)) MsAtLockDriving += 10;
        }
        MinAngle = Math.Min(MinAngle, Angle);
        MaxAngle = Math.Max(MaxAngle, Angle);
    }
}
