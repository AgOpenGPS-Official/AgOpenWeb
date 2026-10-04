// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #154: the CPD / Ackermann circle tests must tell the operator why Record can't start,
/// warn (not block) without RTK Fixed, show the result at once, and refuse nonsense
/// measurements instead of writing them.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CircleTestFeedbackTests
{
    private IConfigurationService _configService = null!;
    private ConfigurationStore _store = null!;
    private IAutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
        _store.Vehicle.Wheelbase = 2.5;
        _store.Vehicle.TrackWidth = 1.8;
        _store.AutoSteer.CountsPerDegree = 100;
        _store.AutoSteer.Ackermann = 100;

        _configService = Substitute.For<IConfigurationService>();
        _configService.Store.Returns(_store);

        _autoSteer = Substitute.For<IAutoSteerService>();
        SetSensorAngle(15.0);
    }

    // IsActive's setter is internal; flip it via reflection so OnEntering/OnLeaving run.
    private static void SetActive(WizardStepViewModel step, bool active) =>
        typeof(WizardStepViewModel).GetProperty(nameof(WizardStepViewModel.IsActive))!.SetValue(step, active);

    private void SetSensorAngle(double deg) =>
        _autoSteer.LastSteerData.Returns(new SteerModuleData(
            ActualSteerAngle: deg, ImuHeading: 0, ImuRoll: 0,
            WorkSwitchActive: false, SteerSwitchActive: false,
            RemoteButtonPressed: false, VwasFusionActive: false, PwmDisplay: 0));

    private void Publish(int fixQuality, double speedMs) =>
        _autoSteer.StateUpdated += Raise.Event<EventHandler<VehicleStateSnapshot>>(
            _autoSteer, new VehicleStateSnapshot { FixQuality = fixQuality, Speed = speedMs });

    /// <summary>Drive a 20 m circle's worth of points, then hold until the diameter settles.</summary>
    private static void DriveCircle(Action<double, double> feed, double diameter = 20)
    {
        for (double d = 2; d <= diameter; d += 2) feed(100 + d, 200);
        for (int i = 0; i < 12; i++) feed(100 + diameter / 2, 200);
    }

    // ── CPD ──────────────────────────────────────────────────────────────

    [Test]
    public void Cpd_records_without_rtk_but_warns()
    {
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        Publish(fixQuality: 1, speedMs: 1.5);

        Assert.That(step.CanRecord, Is.True, "Record must not require RTK Fixed (AgOpenGPS parity)");
        Assert.That(step.RecordHint, Does.Contain("GPS Fix").And.Contain("not RTK Fixed"));
    }

    [Test]
    public void Cpd_hint_explains_standing_still_and_clears_when_ready()
    {
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        Publish(fixQuality: 4, speedMs: 0);
        Assert.That(step.CanRecord, Is.False);
        Assert.That(step.RecordHint, Does.Contain("Start driving"));

        Publish(fixQuality: 4, speedMs: 1.5);
        Assert.That(step.CanRecord, Is.True);
        Assert.That(step.RecordHint, Is.Empty);
    }

    [Test]
    public void Cpd_result_goes_to_the_store_and_survives_a_later_edit()
    {
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);
        Publish(fixQuality: 4, speedMs: 1.5);

        step.StartRecordingAt(100, 200);
        Assert.That(step.PhaseDescription, Does.Contain("Drive steady"));
        DriveCircle(step.ProcessGpsUpdate);

        Assert.That(step.IsRecording, Is.False);
        Assert.That(step.TestResult, Does.StartWith("CPD set to"));
        Assert.That(step.TestResult, Does.Not.Contain("without RTK"));
        double measured = _store.AutoSteer.CountsPerDegree;
        Assert.That(measured, Is.Not.EqualTo(100), "The measured CPD is applied to the store immediately");

        // The operator then edits the value on the page (config.set → store) and leaves.
        _store.AutoSteer.CountsPerDegree = 77;
        SetActive(step, false);
        Assert.That(_store.AutoSteer.CountsPerDegree, Is.EqualTo(77),
            "Leaving the step must not write the measured value back over the edit");
    }

    [Test]
    public void Cpd_result_flags_a_measurement_taken_without_rtk()
    {
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);
        Publish(fixQuality: 5, speedMs: 1.5);

        step.StartRecordingAt(100, 200);
        DriveCircle(step.ProcessGpsUpdate);

        Assert.That(step.TestResult, Does.StartWith("CPD set to").And.Contain("without RTK Fixed"));
    }

    [Test]
    public void Cpd_refuses_a_zero_sensor_reading()
    {
        SetSensorAngle(0.3);
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        step.StartRecordingAt(100, 200);
        DriveCircle(step.ProcessGpsUpdate);

        Assert.That(step.TestResult, Does.StartWith("Couldn't calculate").And.Contain("CPD not changed"));
        Assert.That(_store.AutoSteer.CountsPerDegree, Is.EqualTo(100));
    }

    [Test]
    public void Cpd_refuses_a_circle_too_small_to_measure()
    {
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        step.StartRecordingAt(100, 200);
        DriveCircle(step.ProcessGpsUpdate, diameter: 1.2);

        Assert.That(step.TestResult, Does.Contain("too small"));
        Assert.That(_store.AutoSteer.CountsPerDegree, Is.EqualTo(100));
    }

    // ── Ackermann ────────────────────────────────────────────────────────

    [Test]
    public void Ackermann_record_sets_neutral_then_measures_with_the_settled_angle()
    {
        _store.AutoSteer.Ackermann = 120;
        _autoSteer.LatestSnapshot.Returns(new VehicleStateSnapshot { FixQuality = 4, Speed = 1.5 });
        SetSensorAngle(-21.6); // the module's reading with Ackermann 120 applied
        var step = new AckermannTestStepViewModel(_configService, _autoSteer) { NeutralSettleMs = 60_000 };
        SetActive(step, true);
        Publish(fixQuality: 4, speedMs: 1.5);

        Assert.That(step.CanRecord, Is.True, "Record no longer waits for a separate Set to 100 (#154)");
        Assert.That(step.RecordHint, Is.Empty);

        step.StartRecordingCommand.Execute(null);

        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(100), "Record puts Ackermann at neutral");
        Assert.That(step.IsRecording, Is.True);
        Assert.That(step.PhaseDescription, Does.Contain("100"));

        // Still settling: the module hasn't applied 100 yet, so nothing is captured or measured.
        Publish(fixQuality: 4, speedMs: 1.5);
        step.ProcessGpsUpdate(130, 200);
        Assert.That(step.CapturedStartAngle, Is.EqualTo(0));
        Assert.That(step.Diameter, Is.EqualTo(0));

        // Settled: the start angle is the one the module reports with neutral Ackermann.
        SetSensorAngle(-18.0);
        step.NeutralSettleMs = 0;
        Publish(fixQuality: 4, speedMs: 1.5);
        Assert.That(step.CapturedStartAngle, Is.EqualTo(-18.0));

        // The snapshot's position was (0, 0): drive a 20 m circle from there.
        for (double d = 2; d <= 20; d += 2) step.ProcessGpsUpdate(d, 0);
        for (int i = 0; i < 12; i++) step.ProcessGpsUpdate(10, 0);

        Assert.That(step.TestResult, Does.StartWith("Ackermann set to"));
        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(step.Ackermann));
        Assert.That(_store.AutoSteer.Ackermann, Is.Not.EqualTo(100).And.Not.EqualTo(120));
    }

    [Test]
    public void Ackermann_stopping_puts_the_old_value_back()
    {
        _store.AutoSteer.Ackermann = 120;
        _autoSteer.LatestSnapshot.Returns(new VehicleStateSnapshot { FixQuality = 4, Speed = 1.5 });
        var step = new AckermannTestStepViewModel(_configService, _autoSteer) { NeutralSettleMs = 0 };
        SetActive(step, true);
        Publish(fixQuality: 4, speedMs: 1.5);

        step.StartRecordingCommand.Execute(null);
        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(100));

        step.StopRecordingCommand.Execute(null);

        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(120), "Nothing was measured, so nothing changes");
        Assert.That(step.Ackermann, Is.EqualTo(120));
        Assert.That(step.TestResult, Does.Contain("Ackermann not changed"));
    }

    [Test]
    public void Ackermann_leaving_the_step_mid_measurement_puts_the_old_value_back()
    {
        _store.AutoSteer.Ackermann = 120;
        _autoSteer.LatestSnapshot.Returns(new VehicleStateSnapshot { FixQuality = 4, Speed = 1.5 });
        var step = new AckermannTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);
        Publish(fixQuality: 4, speedMs: 1.5);

        step.StartRecordingCommand.Execute(null);
        SetActive(step, false);

        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(120));
    }

    [Test]
    public void Ackermann_rtk_check_is_fixed_only_and_seeded_on_entry()
    {
        _autoSteer.LatestSnapshot.Returns(new VehicleStateSnapshot { FixQuality = 5, Speed = 1.5 });
        var step = new AckermannTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        Assert.That(step.FixQuality, Is.EqualTo(5), "Seeded from the cached snapshot on entry");
        Assert.That(step.IsRtkFixed, Is.False, "RTK Float is not RTK Fixed (was a >= 4 check)");
        Assert.That(step.CanRecord, Is.True);
        Assert.That(step.RecordHint, Does.Contain("RTK Float"));
    }

    [Test]
    public void Ackermann_result_goes_to_the_store()
    {
        var step = new AckermannTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);
        Publish(fixQuality: 4, speedMs: 1.5);

        step.StartRecordingAt(100, 200, startAngle: -18.0);
        DriveCircle(step.ProcessGpsUpdate);

        Assert.That(step.TestResult, Does.StartWith("Ackermann set to"));
        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(step.Ackermann));
        Assert.That(_store.AutoSteer.Ackermann, Is.Not.EqualTo(100));
        Assert.That(step.CanRecord, Is.True, "Record can measure again without a separate step");
        Assert.That(step.RecordHint, Is.Empty);
    }

    [Test]
    public void Cpd_result_at_the_clamp_limit_is_flagged()
    {
        SetSensorAngle(15.0);
        var step = new CpdCircleTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        // A 60 m circle with a 15° sensor reading → far above 255 before clamping.
        step.StartRecordingAt(100, 200);
        DriveCircle(step.ProcessGpsUpdate, diameter: 60);

        Assert.That(_store.AutoSteer.CountsPerDegree, Is.EqualTo(255));
        Assert.That(step.TestResult, Does.StartWith("CPD set to 255").And.Contain("limit"));
    }

    [Test]
    public void Ackermann_refuses_a_zero_start_angle()
    {
        var step = new AckermannTestStepViewModel(_configService, _autoSteer);
        SetActive(step, true);

        step.StartRecordingAt(100, 200, startAngle: 0.0);
        DriveCircle(step.ProcessGpsUpdate);

        Assert.That(step.TestResult, Does.StartWith("Couldn't calculate").And.Contain("Ackermann not changed"));
        Assert.That(_store.AutoSteer.Ackermann, Is.EqualTo(100));
    }
}
