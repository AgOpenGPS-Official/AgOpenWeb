// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Pins the redesigned Phase A behaviour of the auto motor calibration
/// step. Phase A now ramps <c>ConfigStore.AutoSteer.ProportionalGain</c>
/// at a fixed +5° setpoint and snapshots <c>PwmDisplay</c> from the
/// module at first motion. The original <c>ProportionalGain</c> must
/// always be restored on exit so the operator's tuning isn't lost.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class AutoMotorCalKpRampTests
{
    private IConfigurationService _configService = null!;
    private ConfigurationStore _store = null!;
    private IAutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);

        _configService = Substitute.For<IConfigurationService>();
        _configService.Store.Returns(_store);

        _autoSteer = Substitute.For<IAutoSteerService>();
    }

    /// <summary>
    /// Build the step with a mocked module-data accessor. The closure
    /// captures the latest <c>ProportionalGain</c> written by the wizard
    /// so the simulated motion responds to the ramp, not just to time.
    /// </summary>
    private AutoMotorCalibrationStepViewModel CreateStep(System.Func<int, SteerModuleData> moduleFor)
    {
        var step = new AutoMotorCalibrationStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        step.ReadModuleData = () => moduleFor(_store.AutoSteer.ProportionalGain);
        return step;
    }

    private static SteerModuleData ModuleAt(double angleDeg, byte pwm) =>
        new SteerModuleData(
            ActualSteerAngle: angleDeg, ImuHeading: 0, ImuRoll: 0,
            WorkSwitchActive: false, SteerSwitchActive: false, // bit 1 clear: module steering
            RemoteButtonPressed: false, VwasFusionActive: false,
            PwmDisplay: pwm);

    [Test]
    public async Task MotorCal_FirstMotionAtKp40_CapturesPwmAtThatMoment()
    {
        // Simulator's PID is approximately pwm = error * Kp / 10 with a
        // MinPWM floor. We're not modelling that exactly here — what we
        // care about is the *capture* semantic: when motion first
        // appears, DetectedMinPwm must equal LastSteerData.PwmDisplay at
        // that moment, not a derived number. Below Kp=40 the simulator
        // reports the wheel still at start; at Kp>=40 it has moved 2°
        // and is driving a duty cycle of 25.
        _store.AutoSteer.ProportionalGain = 12; // operator's tuning
        var step = CreateStep(kp =>
        {
            if (kp < 40) return ModuleAt(angleDeg: 0.0, pwm: (byte)(kp / 2));
            return ModuleAt(angleDeg: 2.0, pwm: 25);
        });

        await step.RunKpRampAsync();

        Assert.That(step.DetectedMinPwm, Is.EqualTo(25),
            "MinPWM must be the firmware-reported PWM at the moment of first motion");
        Assert.That(step.DetectedInvertMotor, Is.False);
        Assert.That(step.NoMovementDetected, Is.False);
        Assert.That(step.Phase, Is.EqualTo(CalibrationPhase.RampResult));

        // OriginalKp restored on exit.
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(12));
    }

    [Test]
    public async Task MotorCal_WrongDirectionFirstTry_FlipsInvertAndSucceeds()
    {
        // First-pass simulator: motor turns the wrong way at Kp>=40.
        // Second pass (after the wizard flips InvertMotor): motor now
        // turns correctly at Kp>=40 with PWM=30.
        _store.AutoSteer.ProportionalGain = 8;
        _store.AutoSteer.InvertMotor = false;

        var step = CreateStep(kp =>
        {
            if (!_store.AutoSteer.InvertMotor)
            {
                if (kp < 40) return ModuleAt(0.0, 0);
                return ModuleAt(-2.0, 25);  // wrong direction
            }
            else
            {
                if (kp < 40) return ModuleAt(0.0, 0);
                return ModuleAt(+2.5, 30);  // correct direction after flip
            }
        });

        await step.RunKpRampAsync();

        Assert.That(_store.AutoSteer.InvertMotor, Is.True,
            "ConfigStore.AutoSteer.InvertMotor must be flipped after a wrong-direction first pass");
        Assert.That(step.DetectedInvertMotor, Is.True);
        Assert.That(step.DetectedMinPwm, Is.EqualTo(30));
        Assert.That(step.Phase, Is.EqualTo(CalibrationPhase.RampResult));
        Assert.That(step.NoMovementDetected, Is.False);

        // OriginalKp restored.
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(8));
    }

    [Test]
    public async Task MotorCal_FoundMinPwm_IsTheSettingAfterward()
    {
        // Next right after the ramp's result keeps the MinPWM it found. (The step once saved
        // it only after a max angle part of its own, which the web wizard never ran.)
        _store.AutoSteer.ProportionalGain = 12;
        _store.AutoSteer.MinPwm = 10;
        var step = CreateStep(kp => kp < 40 ? ModuleAt(0.0, 0) : ModuleAt(2.0, 29));
        SetActive(step, true);

        await step.RunKpRampAsync();
        SetActive(step, false); // Next

        Assert.That(step.DetectedMinPwm, Is.EqualTo(29));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(29));
    }

    [Test]
    public async Task MotorCal_StalledWheels_DoNotSetMinPwmNearTheMaximum()
    {
        // The wheels moved past the threshold and then stalled against a load: the PWM read is
        // near the maximum, and as the minimum it would kick every small correction at full power.
        _store.AutoSteer.ProportionalGain = 12;
        _store.AutoSteer.MaxPwm = 180;
        _store.AutoSteer.MinPwm = 25;
        var step = CreateStep(kp => kp < 40 ? ModuleAt(0.0, 0) : ModuleAt(2.0, 170));
        SetActive(step, true);

        await step.RunKpRampAsync();
        SetActive(step, false); // Next

        Assert.That(step.DetectedMinPwm, Is.EqualTo(170));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(25), "a stalled reading is not the setting");
        Assert.That(step.PhaseResult, Does.Contain("Not saved"));
        Assert.That(step.PhaseResult, Does.Contain("Minimum PWM stays 25."), "the operator's own value, not the one the ramp ran on");
        Assert.That(step.PhaseResult, Does.Contain("run it at walking pace"));
    }

    // ── The ramp runs on a minimum PWM of its own (a tester's proportional valve, 18 Nov 2026) ──

    [Test]
    public async Task MotorCal_WithAHighMinimumSet_ReadsTheSteeringsOwnMinimum()
    {
        // Minimum To Move 64 was set (saved the day before, with the block's relief valve too
        // low) and the valve moves the wheels from PWM 48. The module's PWM is never under its
        // minimum PWM, so on the operator's value every run read 64, and 64 of Max Limit 105
        // was refused as stalled.
        _store.AutoSteer.ProportionalGain = 12;
        _store.AutoSteer.MaxPwm = 105;
        _store.AutoSteer.MinPwm = 64;
        var step = StepOnSteering(breakawayPwm: 48, out var lowestMinPwmSeen);
        SetActive(step, true);

        await step.RunKpRampAsync();
        SetActive(step, false); // Next

        Assert.Multiple(() =>
        {
            Assert.That(step.DetectedMinPwm, Is.InRange(45, 48), "what the wheels stop at, just under what moves them");
            Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(step.DetectedMinPwm));
            Assert.That(step.PhaseResult, Does.Not.Contain("Not saved"));
            Assert.That(lowestMinPwmSeen(), Is.EqualTo(AutoMotorCalibrationStepViewModel.RampMinPwm), "the ramp ran on its own minimum PWM");
            Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(12));
        });
    }

    [Test]
    public async Task MotorCal_AValvesMinimumNearHalfItsCoilLimit_IsSaved()
    {
        // The same valve on another tractor, Max Limit 89 (its coil's current): first movement
        // at 44 to 47. Half the maximum is 44, so its true minimum read as stalled.
        _store.AutoSteer.MaxPwm = 89;
        _store.AutoSteer.MinPwm = 30;
        var step = StepOnSteering(breakawayPwm: 46, out _);
        SetActive(step, true);

        await step.RunKpRampAsync();
        SetActive(step, false);

        Assert.That(step.DetectedMinPwm, Is.InRange(44, 46));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(step.DetectedMinPwm));
        Assert.That(step.PhaseResult, Does.Not.Contain("Not saved"));
    }

    [Test]
    public async Task MotorCal_AnOperatorsLowerMinimum_IsWhatTheRampRunsOn()
    {
        _store.AutoSteer.MaxPwm = 200;
        _store.AutoSteer.MinPwm = 2;
        var step = StepOnSteering(breakawayPwm: 30, out var lowestMinPwmSeen);

        await step.RunKpRampAsync();

        Assert.That(lowestMinPwmSeen(), Is.EqualTo(2));
        Assert.That(step.DetectedMinPwm, Is.InRange(26, 30));
    }

    [TestCase(57, 105, false)]
    [TestCase(69, 105, false)]
    [TestCase(70, 105, true)]
    [TestCase(47, 89, false)]
    [TestCase(170, 180, true)]
    [TestCase(169, 255, false)]
    [TestCase(170, 255, true)]
    public void IsStalledReading_cases(int pwm, int maxPwm, bool stalled) =>
        Assert.That(AutoMotorCalibrationStepViewModel.IsStalledReading(pwm, maxPwm), Is.EqualTo(stalled));

    /// <summary>
    /// A steering as the module drives it: its PWM is the gain times the error plus the minimum
    /// PWM (0 when the gain times the error is under 1), never over the maximum PWM, and the
    /// wheels turn while it is at least <paramref name="breakawayPwm"/>.
    /// </summary>
    private AutoMotorCalibrationStepViewModel StepOnSteering(int breakawayPwm, out System.Func<int> lowestMinPwmSeen)
    {
        var step = new AutoMotorCalibrationStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        double angle = 0, target = 0;
        int lowest = int.MaxValue;
        _autoSteer.When(a => a.SetFreeDriveAngle(Arg.Any<double>())).Do(c => target = c.Arg<double>());
        step.ReadModuleData = () =>
        {
            var cfg = _store.AutoSteer;
            lowest = System.Math.Min(lowest, cfg.MinPwm);
            int pwm = 0;
            for (int loop = 0; loop < 400; loop++) // the module's loops between two readings
            {
                double err = target - angle;
                int p = (int)(cfg.ProportionalGain * System.Math.Abs(err));
                pwm = p == 0 ? 0 : System.Math.Min(p + cfg.MinPwm, cfg.MaxPwm);
                if (pwm < breakawayPwm) break;
                angle += 0.01 * System.Math.Sign(err);
            }
            return ModuleAt(angle, (byte)pwm);
        };
        lowestMinPwmSeen = () => lowest;
        return step;
    }

    private static void SetActive(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel step, bool active) =>
        typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel).GetProperty("IsActive")!.SetValue(step, active);

    [Test]
    public async Task MotorCal_NoMotionAtMaxKp_RaisesFailure()
    {
        // Module reports the wheel stationary at every Kp setting —
        // hardware fault, low MaxPWM, or unsupplied motor. Wizard must
        // mark the failure and restore the operator's Kp.
        _store.AutoSteer.ProportionalGain = 42;
        var step = CreateStep(_ => ModuleAt(0.0, 0));

        await step.RunKpRampAsync();

        Assert.That(step.NoMovementDetected, Is.True);
        Assert.That(step.Phase, Is.EqualTo(CalibrationPhase.RampResult));
        Assert.That(step.PhaseResult, Does.Contain("No wheel movement"));
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(42),
            "OriginalKp must survive a failed ramp");
    }

    [Test]
    public async Task MotorCal_OnCancel_RestoresOriginalKp()
    {
        // Operator hits cancel mid-ramp. The internal CTS is exposed
        // via the OnLeaving hook, but at the unit-test level we trigger
        // cancel by overriding DelayFunc to throw OperationCanceled on
        // the second delay invocation.
        _store.AutoSteer.ProportionalGain = 17;
        _store.AutoSteer.MinPwm = 40;
        var step = new AutoMotorCalibrationStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.ReadModuleData = () => ModuleAt(0.0, 0);

        int delayCalls = 0;
        step.DelayFunc = (_, _) =>
        {
            delayCalls++;
            if (delayCalls >= 3)
                throw new System.OperationCanceledException();
            return Task.CompletedTask;
        };

        await step.RunKpRampAsync();

        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(17),
            "Cancellation must restore the operator's originalKp via the finally block");
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(40), "and the operator's minimum PWM, which the ramp runs under");
    }

    [Test]
    public async Task MotorCal_LeftMidRamp_PutsTheOperatorsValuesBackAtOnce()
    {
        // Cancel, or the screen in control gone: the host leaves the step and saves the profile
        // straight away (MainViewModel.EndRemoteSteerWizard), while the ramp's finally waits for
        // the host loop's next turn. Leaving must put the operator's gain and minimum PWM back
        // itself, or the ramp's own (gain 5, minimum PWM 5) were the ones saved.
        _store.AutoSteer.ProportionalGain = 17;
        _store.AutoSteer.MinPwm = 40;
        var step = new AutoMotorCalibrationStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.ReadModuleData = () => ModuleAt(0.0, 0);
        var waiting = new TaskCompletionSource();
        int delayCalls = 0;
        step.DelayFunc = (_, _) => ++delayCalls >= 3 ? waiting.Task : Task.CompletedTask;
        SetActive(step, true);

        var ramp = step.RunKpRampAsync();
        Assert.That(ramp.IsCompleted, Is.False, "the ramp waits on the host loop");
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(AutoMotorCalibrationStepViewModel.RampMinPwm));

        SetActive(step, false); // the wizard closed: the profile is saved right after this

        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(17), "the operator's gain, before the save");
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(40), "the operator's minimum PWM, before the save");

        waiting.SetResult(); // the ramp's turn comes: it stops and changes nothing more
        await ramp;
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(17));
        Assert.That(_store.AutoSteer.MinPwm, Is.EqualTo(40));
    }

    [Test]
    public async Task MotorCal_DetectsRunaway_AbortsBeforeRampCompletes()
    {
        // Defensive: if the wheel races more than 15° from the start
        // anchor during the ramp (e.g. WAS misconfigured), the wizard
        // must abort early rather than keep ramping Kp.
        //
        // Simulate: the start anchor reads 0° (the module arms at the
        // bottom of the ramp, Kp 5). Once the ramp climbs past it, the
        // wheel has shot to 20° — well past the 15° runaway limit.
        _store.AutoSteer.ProportionalGain = 0;
        var step = CreateStep(kp =>
        {
            if (kp >= 10) return ModuleAt(angleDeg: 20.0, pwm: 80);
            return ModuleAt(0.0, 0);
        });

        await step.RunKpRampAsync();

        Assert.That(step.NoMovementDetected, Is.True);
        Assert.That(step.PhaseResult, Does.Contain("Runaway"));
        Assert.That(step.Phase, Is.EqualTo(CalibrationPhase.RampResult));
        Assert.That(_store.AutoSteer.ProportionalGain, Is.EqualTo(0),
            "Runaway abort still restores originalKp");
    }
}
