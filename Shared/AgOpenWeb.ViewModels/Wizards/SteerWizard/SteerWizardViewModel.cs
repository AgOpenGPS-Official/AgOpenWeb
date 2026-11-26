// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// ViewModel for the Steer Configuration Wizard.
/// Guides users through AutoSteer setup in 10 combined steps.
/// </summary>
public class SteerWizardViewModel : WizardViewModel
{
    private readonly IConfigurationService _configService;

    public override string WizardTitle => "AutoSteer Configuration Wizard";

    /// <summary>
    /// Persistent status bar showing live hardware data across all wizard steps.
    /// </summary>
    public override WizardStatusBarViewModel? StatusBar { get; }

    public SteerWizardViewModel(IConfigurationService configService,
        IUiDispatcher dispatcher, IAutoSteerService? autoSteerService = null)
    {
        _configService = configService;
        StatusBar = new WizardStatusBarViewModel(autoSteerService);

        // Step 1: Welcome
        AddStep(new WelcomeStepViewModel());

        // Step 2: Vehicle Type
        AddStep(new VehicleTypeStepViewModel(configService));

        // Step 3: Hardware Installed (GPS only / AutoSteer / Full)
        var hardwareStep = new HardwareInstalledStepViewModel();
        AddStep(hardwareStep);

        // Step 4: Vehicle Dimensions (wheelbase + track width)
        AddStep(new VehicleDimensionsStepViewModel(configService));

        // Step 5: Antenna Position (pivot + height + offset)
        AddStep(new AntennaSetupStepViewModel(configService));

        // Steps 6-10: AutoSteer-only steps (skipped when GPS Only)
        var hwConfig = new HardwareConfigStepViewModel(configService);
        hwConfig.SetHardwareStep(hardwareStep);
        AddStep(hwConfig);

        var rollCal = new RollCalibrationStepViewModel(configService, autoSteerService);
        rollCal.SetHardwareStep(hardwareStep);
        AddStep(rollCal);

        var wasCal = new WasCalibrationStepViewModel(configService, autoSteerService);
        wasCal.SetHardwareStep(hardwareStep);
        AddStep(wasCal);

        var autoCal = new AutoMotorCalibrationStepViewModel(configService, dispatcher, autoSteerService);
        autoCal.SetHardwareStep(hardwareStep);
        AddStep(autoCal);

        // Step 10: Maximum Steering Angle (split out of the old combined
        // motor cal step so the stress-style full-lock test gets its own
        // operator-facing page with distinct warnings).
        var maxSteer = new MaxSteeringAngleStepViewModel(configService, dispatcher, autoSteerService);
        maxSteer.SetHardwareStep(hardwareStep);
        AddStep(maxSteer);

        var cpdCircle = new CpdCircleTestStepViewModel(configService, autoSteerService);
        cpdCircle.SetHardwareStep(hardwareStep);
        AddStep(cpdCircle);

        var ackermannTest = new AckermannTestStepViewModel(configService, autoSteerService);
        ackermannTest.SetHardwareStep(hardwareStep);
        AddStep(ackermannTest);

        var steerGains = new SteeringGainsStepViewModel(configService, autoSteerService);
        steerGains.SetHardwareStep(hardwareStep);
        AddStep(steerGains);

        // Step 11: Speed Limits + Sensors
        AddStep(new SpeedAndSensorsStepViewModel(configService));

        // Step 12: Finish
        AddStep(new FinishStepViewModel(() => NotTested(hardwareStep, rollCal, wasCal, autoCal, maxSteer, cpdCircle, ackermannTest)));

        // Initialize navigation
        Initialize();
    }

    protected override Task OnCompletingAsync()
    {
        // Save all configuration changes
        var store = _configService.Store;
        _configService.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
        return Task.CompletedTask;
    }

    /// <summary>The tests not run in this wizard, for the Finish page (none with GPS only: it has no tests).</summary>
    private static IReadOnlyList<string> NotTested(HardwareInstalledStepViewModel hardware,
        RollCalibrationStepViewModel roll, WasCalibrationStepViewModel was, AutoMotorCalibrationStepViewModel motor,
        MaxSteeringAngleStepViewModel maxAngle, CpdCircleTestStepViewModel cpd, AckermannTestStepViewModel ackermann)
    {
        var missing = new List<string>();
        if (hardware.HardwareLevel == 0) return missing;
        if (!was.ZeroDone) missing.Add("the WAS zero");
        if (!roll.ZeroDone) missing.Add("the roll zero");
        if (!motor.TestDone) missing.Add("the motor direction test");
        if (!maxAngle.TestDone) missing.Add("the max steer angle test");
        if (!cpd.TestCompleted) missing.Add("the CPD circle");
        if (!ackermann.TestCompleted) missing.Add("the Ackermann circle");
        return missing;
    }
}
