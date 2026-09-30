// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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
    public async Task NoPlateau_FallsBackToLastSample_WithoutHanging()
    {
        // If the WAS never settles (e.g. simulator with no clamp, runaway
        // hydraulics), the detector must time out and return the last
        // sample rather than hang forever. The wizard caps the poll loop
        // at PlateauTimeoutMs internally.
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        double angle = 0;
        bool right = true;
        step.ReadWasAngle = () =>
        {
            angle += right ? 0.5 : -0.5;  // never plateaus, just drifts
            if (System.Math.Abs(angle) > 50)
            {
                right = !right;
                angle = right ? 0 : 0;
            }
            return angle;
        };

        await step.RunMaxAngleMeasurementAsync();

        // We don't assert exact values — the timeout-fallback path is
        // operational, not metric. The point: it returned without
        // hanging, and CalibrationCompleted is true so OnLeaving will
        // still persist *something*.
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
        Assert.That(step.CalibrationCompleted, Is.True);
    }

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
    public void Hint_warns_when_the_module_is_not_steering()
    {
        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty with { SteerSwitchActive = true });
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        SetActive(step, true);
        Assert.That(step.RecordHint, Does.Contain("isn't steering"));

        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty with { SteerSwitchActive = false });
        Assert.That(step.RecordHint, Is.Empty);
    }

    private static void SetActive(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel step, bool active) =>
        typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel)
            .GetProperty(nameof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel.IsActive))!.SetValue(step, active);
}
