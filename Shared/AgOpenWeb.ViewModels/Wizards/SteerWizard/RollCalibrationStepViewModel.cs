// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;

using AgOpenWeb.Services.Interfaces;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Roll/IMU calibration: inversion and zero offset.
/// </summary>
public class RollCalibrationStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;
    private readonly IAutoSteerService? _autoSteerService;
    private HardwareInstalledStepViewModel? _hardwareStep;

    public override string Title => "Roll Calibration";

    public override bool ShouldSkip => _hardwareStep?.HardwareLevel == 0;

    public void SetHardwareStep(HardwareInstalledStepViewModel step) => _hardwareStep = step;

    public override string Description =>
        "1. Tilt your vehicle slightly to the RIGHT\n" +
        "2. The gauge should move to the RIGHT (positive)\n" +
        "3. If it moves LEFT (negative), enable 'Invert Roll'\n" +
        "4. Park on LEVEL GROUND\n" +
        "5. Press 'Zero Roll' to calibrate the zero position";

    public override bool CanSkip => true;

    private bool _isRollInvert;
    public bool IsRollInvert
    {
        get => _isRollInvert;
        set
        {
            // Push the new value into the store immediately so the next
            // NMEA parse uses it. NmeaParserServiceFast post-processes
            // roll with `if (IsRollInvert) roll = -roll; roll -= RollZero;`
            // — without the live store-write, the gauge keeps showing
            // raw uncalibrated roll until the operator advances past
            // the step and OnLeaving fires.
            if (SetProperty(ref _isRollInvert, value))
                _configService.Store.Ahrs.IsRollInvert = value;
        }
    }

    private double _rollZero;
    public double RollZero
    {
        get => _rollZero;
        set
        {
            // Same live-feedback push as IsRollInvert. The Zero Roll
            // button captures LiveRoll into this setter; the next NMEA
            // parse subtracts it from raw roll so the gauge centres.
            if (SetProperty(ref _rollZero, value))
                _configService.Store.Ahrs.RollZero = value;
        }
    }

    private double _liveRoll;
    /// <summary>Live roll angle from IMU.</summary>
    public double LiveRoll
    {
        get => _liveRoll;
        set => SetProperty(ref _liveRoll, value);
    }

    private bool _hasLiveData;
    public bool HasLiveData
    {
        get => _hasLiveData;
        set => SetProperty(ref _hasLiveData, value);
    }

    public ICommand ZeroRollCommand { get; }

    /// <summary>What the last press of Zero Roll did, or why it did nothing, for the operator.</summary>
    public string TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }
    private string _testResult = "";

    /// <summary>After a zero, presses are ignored until the roll reads level again (or this long).</summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(1.5);
    /// <summary>A roll this large is more likely a slope than a crooked IMU: it needs a second press.</summary>
    public const double ConfirmDeg = 5;
    /// <summary>How long the second press has to come.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);

    private DateTime _zeroSentUtc;
    private bool _zeroPending;
    private DateTime _confirmUtc;
    private double _confirmRoll;

    /// <summary>The clock the timing above runs on; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public RollCalibrationStepViewModel(IConfigurationService configService,
        IAutoSteerService? autoSteerService = null)
    {
        _configService = configService;
        _autoSteerService = autoSteerService;

        ZeroRollCommand = new RelayCommand(ZeroRoll);
    }

    private void ZeroRoll()
    {
        var now = Clock();
        if (!HasLiveData)
        {
            TestResult = "No roll reading yet: the IMU is not reporting.";
            return;
        }
        // A second press before the parser applies the new zero would add the same roll again.
        if (_zeroPending && now - _zeroSentUtc < SettleWindow)
        {
            TestResult = "Zeroing: wait for the roll to read 0° before pressing again.";
            return;
        }
        _zeroPending = false;

        double roll = LiveRoll;
        // Level ground rarely reads this much: say what it reads and take it only on a second press.
        if (Math.Abs(roll) > ConfirmDeg
            && !(now - _confirmUtc <= ConfirmWindow && Math.Abs(roll - _confirmRoll) < ConfirmDeg))
        {
            _confirmUtc = now;
            _confirmRoll = roll;
            TestResult = string.Create(CultureInfo.InvariantCulture,
                $"The roll reads {roll:F1}°. Is the ground level? Press Zero Roll again within {ConfirmWindow.TotalSeconds:F0} s to take this as level.");
            return;
        }
        _confirmUtc = default;

        // LiveRoll is the *post-calibration* angle: the NMEA parser
        // already applies IsRollInvert and subtracts the current
        // RollZero before publishing. Replacing the offset would
        // throw away the prior calibration on a second press —
        // matches the WAS Zero accumulator bug (#385 F/G). The
        // invert sign is already baked in upstream, so the Zero
        // formula here is plain addition.
        RollZero = _configService.Store.Ahrs.RollZero + roll;
        _zeroPending = true;
        _zeroSentUtc = now;
        TestResult = "Level set: the roll now reads 0° here.";
    }

    protected override void OnEntering()
    {
        var ahrs = _configService.Store.Ahrs;
        IsRollInvert = ahrs.IsRollInvert;
        RollZero = ahrs.RollZero;
        TestResult = "";
        _zeroPending = false;
        _confirmUtc = default;

        if (_autoSteerService != null)
            _autoSteerService.StateUpdated += OnStateUpdated;
    }

    protected override void OnLeaving()
    {
        if (_autoSteerService != null)
            _autoSteerService.StateUpdated -= OnStateUpdated;

        var ahrs = _configService.Store.Ahrs;
        if (Touched(nameof(IsRollInvert))) ahrs.IsRollInvert = IsRollInvert;
        if (Touched(nameof(RollZero))) ahrs.RollZero = RollZero;
    }

    private void OnStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        LiveRoll = Math.Round(snapshot.Roll, 2);
        HasLiveData = true;
        // The new zero is applied: the next press is a new zero, not a repeat.
        if (_zeroPending && Math.Abs(snapshot.Roll) < 0.3)
            _zeroPending = false;
    }

    public override Task<bool> ValidateAsync()
    {
        ClearValidation();
        return Task.FromResult(true);
    }
}
