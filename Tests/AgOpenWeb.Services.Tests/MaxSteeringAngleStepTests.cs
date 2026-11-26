// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Pins the new max-steering-angle wizard step. The split-out step
/// must:
///   1. Detect a WAS plateau (consecutive samples within
///      PlateauThresholdDeg) and capture that angle, rather than read
///      a fixed-time snapshot mid-motion.
///   2. Capture both sides of the lock and persist
///      <c>min(left, right) * 0.9</c> as MaxSteerAngle on completion.
///   3. Honour cooperative cancellation by exiting the loop and
///      restoring free-drive state.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class MaxSteeringAngleStepTests
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
        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty);
    }

    [Test]
    public async Task PlateauDetector_FiresOnStableSamples_AndPersistsMaxAngle()
    {
        // Simulator-style WAS: ramp up to ±35° then plateau. The
        // detector should fire once it sees PlateauStableSamples
        // consecutive samples within PlateauThresholdDeg of each other.
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        // Right-lock sequence: 10, 20, 30, 35, 35, 35, 35, 35, 35, 35
        // then center, then left: -10, -25, -30, -30, -30, -30, -30, -30
        var rightSequence = new[] { 10.0, 20.0, 30.0, 35.0, 35.0, 35.0, 35.0, 35.0, 35.0, 35.0 };
        var leftSequence = new[] { -10.0, -25.0, -30.0, -30.0, -30.0, -30.0, -30.0, -30.0 };

        int call = 0;
        bool measuringLeft = false;
        step.ReadWasAngle = () =>
        {
            // Transition to "left" sequence the first time we hit
            // Phase.MeasuringLeft. (See the SetFreeDriveAngle stub below.)
            if (measuringLeft)
            {
                var v = leftSequence[System.Math.Min(call - rightSequence.Length, leftSequence.Length - 1)];
                call++;
                return v;
            }
            else
            {
                var v = rightSequence[System.Math.Min(call, rightSequence.Length - 1)];
                call++;
                if (call >= rightSequence.Length)
                {
                    // Reset call counter for the left-side polling.
                    measuringLeft = true;
                    call = rightSequence.Length;
                }
                return v;
            }
        };

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.DetectedMaxAngleRight, Is.EqualTo(35.0).Within(0.5));
        Assert.That(step.DetectedMaxAngleLeft, Is.EqualTo(30.0).Within(0.5));
        // min(35, 30) * 0.9 = 27 (truncated).
        Assert.That(step.MaxSteerAngle, Is.EqualTo(27));
        Assert.That(step.CalibrationCompleted, Is.True);
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
    }

    [Test]
    public async Task NoPlateau_TimesOut_WithoutHanging_AndIsNoLock()
    {
        // If the WAS never settles (e.g. simulator with no clamp, runaway
        // hydraulics), the detector must time out rather than hang forever.
        // The wizard caps the poll loop at PlateauTimeoutMs internally.
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        // Drifts the way it is commanded and never plateaus (a wheel drifting the
        // other way stops the test: the motor direction is wrong).
        double angle = 0, commanded = 0;
        _autoSteer.When(a => a.SetFreeDriveAngle(Arg.Any<double>())).Do(c =>
        {
            if (System.Math.Sign(c.Arg<double>()) != System.Math.Sign(commanded)) angle = 0;
            commanded = c.Arg<double>();
        });
        step.ReadWasAngle = () =>
        {
            angle += 0.5 * System.Math.Sign(commanded);
            return angle;
        };

        await step.RunMaxAngleMeasurementAsync();

        // The point: it returned without hanging. Wheels still turning when the time ran
        // out read no lock, so nothing is saved (30° each way here, and 27 was saved before).
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
        Assert.That(step.CalibrationCompleted, Is.False);
        Assert.That(step.PhaseResult, Does.Contain("They were still turning when the 6 s for each side ran out."));
    }

    // ── A push cut off by its time is no lock ─────────────────────────────

    [Test]
    public async Task A_side_cut_off_by_the_time_is_not_taken_as_a_lock()
    {
        // 10°/s: the right lock (36°) is reached, the left push starts from it and its 6 s end
        // at -25°, the wheels still turning. 25 read as the left lock, and 22 was saved.
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 36, left: 36, perReading: 1);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.Multiple(() =>
        {
            Assert.That(step.CalibrationCompleted, Is.False);
            Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
            Assert.That(step.PhaseResult, Is.EqualTo(
                "Right max: 36.0\nLeft max: 25.0\n" +
                "The wheels did not reach the lock in the test's time. " +
                "They were still turning left when the 6 s for that side ran out. Max steer angle not changed.\n" +
                "The angle can be typed in AutoSteer, Steering Sensor, Max Steer Angle."));
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33), "a reading cut off on the way must not become the max steer angle");
    }

    [Test]
    public async Task Slow_wheels_cut_off_on_both_sides_are_told_so()
    {
        // A tester's hydraulic block with its relief valve set low (18 Nov 2026): start 2.3°,
        // right 33.1°, left -0.1°. At 5°/s the right push ran out of time 30° out and the left
        // one on the way back, near the start.
        _store.Vehicle.MaxSteerAngle = 31;
        var step = StepWithLocks(right: 36, left: 36, perReading: 0.5);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.CalibrationCompleted, Is.False);
        Assert.That(step.PhaseResult, Does.EndWith(
            "They were still turning when the 6 s for each side ran out. Max steer angle not changed."));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(31));
    }

    [Test]
    public async Task A_lock_reached_just_before_the_time_runs_out_is_taken()
    {
        // 6.5°/s: the right lock (36°) is reached 0.4 s before the 6 s run out, too few steady
        // readings for a plateau, while the approach is still in the last second. The wheels had
        // stopped: 36 is the lock, not a push cut off on the way.
        var step = StepWithLocks(right: 36, left: 30, perReading: 0.65, leftPerReading: 6);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Does.Not.Contain("still turning"));
        Assert.That(step.CalibrationCompleted, Is.True);
        Assert.That(step.DetectedMaxAngleRight, Is.EqualTo(36).Within(0.01));
        Assert.That(step.MaxSteerAngle, Is.EqualTo(27));
    }

    [Test]
    public async Task Locks_reached_in_time_are_still_taken()
    {
        // 2° a reading: both locks well inside the 6 s, nothing cut off.
        var step = StepWithLocks(right: 35, left: 30);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.CalibrationCompleted, Is.True);
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
        Assert.That(step.MaxSteerAngle, Is.EqualTo(27));
    }

    [TestCase(false, false, "")]
    [TestCase(true, false, " They were still turning right when the 6 s for that side ran out.")]
    [TestCase(false, true, " They were still turning left when the 6 s for that side ran out.")]
    [TestCase(true, true, " They were still turning when the 6 s for each side ran out.")]
    public void StillTurningNote_cases(bool right, bool left, string expected) =>
        Assert.That(MaxSteeringAngleStepViewModel.StillTurningNote(right, left), Is.EqualTo(expected));

    // ── #170: the wheels must actually move ───────────────────────────────

    [Test]
    public async Task Wheels_that_do_not_move_fail_and_nothing_is_saved()
    {
        // Reporter's case: free drive didn't move the wheels (module not steering), the WAS
        // sat at 7.2° and the old code saved it as both the right and left lock.
        _store.Vehicle.MaxSteerAngle = 34;
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        step.ReadWasAngle = () => 7.2;
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.Multiple(() =>
        {
            Assert.That(step.CalibrationCompleted, Is.False);
            Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
            Assert.That(step.PhaseResult, Does.Contain("didn't move").And.Contain("not changed"));
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(34), "a failed test must not overwrite the max steer angle");
    }

    [TestCase(0, 35, -30, null)]
    [TestCase(7.2, 7.2, 7.2, "didn't move")]
    [TestCase(0, 3, -3, "didn't move")]
    [TestCase(0, -30, 30, "opposite way")]
    public void LockMovementError_cases(double start, double right, double left, string? expected)
    {
        var err = MaxSteeringAngleStepViewModel.LockMovementError(start, right, left);
        if (expected == null) Assert.That(err, Is.Null);
        else Assert.That(err, Does.Contain(expected));
    }

    [Test]
    public void Hint_is_quiet_before_Start_even_though_the_module_reports_not_steering()
    {
        // AIO v4 reports "not steering" whenever the app sends status 0, so before Free Drive
        // the bit says nothing about the steer switch (#154).
        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty with { SteerSwitchActive = true });
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        SetActive(step, true);
        Assert.That(step.RecordHint, Is.Empty);
        Assert.That(step.CanStartTest, Is.True);
    }

    /// <summary>
    /// Wheels that run to <paramref name="right"/>° when steered right and to −<paramref name="left"/>°
    /// when steered left, <paramref name="perReading"/>° a reading (ten readings a second; steered left
    /// <paramref name="leftPerReading"/> when given), then stay there.
    /// </summary>
    private MaxSteeringAngleStepViewModel StepWithLocks(double right, double left, double perReading = 2, double? leftPerReading = null)
    {
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        double angle = 0, target = 0;
        _autoSteer.When(a => a.SetFreeDriveAngle(Arg.Any<double>())).Do(c =>
        {
            double cmd = c.Arg<double>();
            target = cmd > 0 ? right : cmd < 0 ? -left : 0;
        });
        step.ReadWasAngle = () =>
        {
            // So far a reading towards where the wheels stop.
            double speed = target < 0 ? leftPerReading ?? perReading : perReading;
            angle += System.Math.Clamp(target - angle, -speed, speed);
            return angle;
        };
        return step;
    }

    private static void SetActive(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel step, bool active) =>
        typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel)
            .GetProperty(nameof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel.IsActive))!.SetValue(step, active);
}
