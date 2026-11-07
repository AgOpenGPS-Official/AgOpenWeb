// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Step for WAS (Wheel Angle Sensor) calibration with live sensor reading.
/// </summary>
public class WasCalibrationStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;
    private readonly IAutoSteerService? _autoSteerService;
    private HardwareInstalledStepViewModel? _hardwareStep;

    public override string Title => "Wheel Angle Sensor";

    public override bool ShouldSkip => _hardwareStep?.HardwareLevel == 0;

    public void SetHardwareStep(HardwareInstalledStepViewModel step) => _hardwareStep = step;

    public override string Description =>
        "1. Point wheels straight ahead\n" +
        "2. Press 'Zero WAS' to set the zero position\n" +
        "3. Turn wheels RIGHT - the angle should read POSITIVE\n" +
        "4. If it reads negative, enable 'Invert WAS'";

    private bool _invertWas;
    /// <summary>Invert WAS direction if steering reads backwards.</summary>
    public bool InvertWas
    {
        get => _invertWas;
        set
        {
            // Push to the store immediately so PGN 251/252 emit the new
            // setting on the next AutoSteer config cycle. Without the
            // live write, the operator's toggle takes effect only when
            // they advance past the wizard step (OnLeaving), so live
            // WAS bar feedback in the gauge above doesn't match the
            // toggle state.
            if (SetProperty(ref _invertWas, value))
                _configService.Store.AutoSteer.InvertWas = value;
        }
    }

    private int _wasOffset;
    public int WasOffset
    {
        get => _wasOffset;
        set
        {
            // Same live push as InvertWas. The Zero WAS button captures
            // the current angle into this setter; the host then emits
            // PGN 251 with the new WasOffset on the next config cycle
            // so the module starts subtracting it from raw counts.
            if (SetProperty(ref _wasOffset, value))
                _configService.Store.AutoSteer.WasOffset = value;
        }
    }

    private double _liveSteerAngle;
    /// <summary>Live steer angle from PGN 253 (degrees).</summary>
    public double LiveSteerAngle
    {
        get => _liveSteerAngle;
        set => SetProperty(ref _liveSteerAngle, value);
    }

    private bool _hasLiveData;
    /// <summary>True if live sensor data is being received.</summary>
    public bool HasLiveData
    {
        get => _hasLiveData;
        set => SetProperty(ref _hasLiveData, value);
    }

    public ICommand ZeroWasCommand { get; }

    /// <summary>Zero WAS was pressed in this wizard (the Finish page lists the tests not run).</summary>
    public bool ZeroDone => TouchedInWizard(nameof(WasOffset));

    public WasCalibrationStepViewModel(IConfigurationService configService,
        IAutoSteerService? autoSteerService = null)
    {
        _configService = configService;
        _autoSteerService = autoSteerService;

        ZeroWasCommand = new RelayCommand(ZeroWas);
    }

    /// <summary>What the last press of Zero WAS did, or why it did nothing, for the operator.</summary>
    public string TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }
    private string _testResult = "";

    /// <summary>A reading older than this is the module unplugged, not the wheels.</summary>
    public static readonly TimeSpan ModuleStale = TimeSpan.FromSeconds(2);
    /// <summary>After a zero, presses are ignored until the module reports the new centre (or this long).</summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(1.5);
    /// <summary>A reading this far from the current centre needs a second press: the wheels may be turned.</summary>
    public const double ConfirmDeg = 10;
    /// <summary>The second press takes the reading only if it is still the one the operator was
    /// shown (within this): wheels that moved from 12° to 21° between the presses are asked about
    /// again, not zeroed at a value nobody saw.</summary>
    public const double ConfirmMatchDeg = 1;
    /// <summary>How long the second press has to come.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);

    private DateTime _zeroSentUtc;
    private bool _zeroPending;
    private DateTime _confirmUtc;
    private double _confirmAngle;

    /// <summary>The clock the timing above runs on; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private void ZeroWas()
    {
        if (_autoSteerService == null)
            return;

        var now = Clock();
        // The reading must be the module's, now: unplugged, LastSteerData keeps its last value
        // and a zero taken from it sets a centre the wheels never had.
        var at = _autoSteerService.LastSteerDataUtc;
        if (at == DateTime.MinValue)
        {
            TestResult = "No reading from the steer module yet: check its power and network.";
            return;
        }
        if (now - at > ModuleStale)
        {
            TestResult = string.Create(CultureInfo.InvariantCulture,
                $"No reading from the steer module for {(now - at).TotalSeconds:F0} s: check its power and network, then press Zero WAS again.");
            return;
        }
        // A second press before the module reports the new centre would zero from the old
        // reading again and double the correction.
        if (_zeroPending && now - _zeroSentUtc < SettleWindow)
        {
            TestResult = "Zeroing: wait for the module to report 0° before pressing again.";
            return;
        }
        _zeroPending = false;

        // ActualSteerAngle from PGN 253 is the module's post-calibration angle, so accumulate
        // against the prior offset. The formula follows the real firmware and does NOT depend
        // on Invert WAS (#103; see WasCalibration). Out-of-range zeros are refused like AgOpenGPS.
        double actualAngle = _autoSteerService.LastSteerData.ActualSteerAngle;
        // Far from the current centre, the wheels are more likely turned than the sensor off:
        // say what it reads and take it only on a second press.
        if (Math.Abs(actualAngle) > ConfirmDeg
            && !(now - _confirmUtc <= ConfirmWindow && Math.Abs(actualAngle - _confirmAngle) < ConfirmMatchDeg))
        {
            _confirmUtc = now;
            _confirmAngle = actualAngle;
            TestResult = string.Create(CultureInfo.InvariantCulture,
                $"The sensor reads {actualAngle:F1}°. Are the wheels straight ahead? Press Zero WAS again within {ConfirmWindow.TotalSeconds:F0} s to take this as the centre.");
            return;
        }
        _confirmUtc = default;

        var autoSteer = _configService.Store.AutoSteer;
        if (!WasCalibration.TryZero(autoSteer.WasOffset, actualAngle, autoSteer.CountsPerDegree, out int zeroed))
        {
            TestResult = string.Create(CultureInfo.InvariantCulture,
                $"Cannot zero: the offset would be beyond ±{WasCalibration.MaxOffset} counts. Check the sensor's mounting and counts per degree.");
            return;
        }
        WasOffset = zeroed;
        _zeroPending = true;
        _zeroSentUtc = now;
        TestResult = "Centre set: straight ahead now reads 0°.";
    }

    protected override void OnEntering()
    {
        var autoSteer = _configService.Store.AutoSteer;
        InvertWas = autoSteer.InvertWas;
        WasOffset = autoSteer.WasOffset;
        TestResult = "";
        _zeroPending = false;
        _confirmUtc = default;

        // Start listening for live data
        if (_autoSteerService != null)
            _autoSteerService.StateUpdated += OnAutoSteerStateUpdated;
        // The web toggles Invert WAS in the live settings (config.set), and the offset flips
        // with it: follow both, or OnLeaving writes the offset from before the toggle back.
        autoSteer.PropertyChanged += OnStoreChanged;
    }

    protected override void OnLeaving()
    {
        _configService.Store.AutoSteer.PropertyChanged -= OnStoreChanged;
        // Stop listening
        if (_autoSteerService != null)
            _autoSteerService.StateUpdated -= OnAutoSteerStateUpdated;

        var autoSteer = _configService.Store.AutoSteer;
        if (Touched(nameof(InvertWas))) autoSteer.InvertWas = InvertWas;
        if (Touched(nameof(WasOffset))) autoSteer.WasOffset = WasOffset;
    }

    private void OnStoreChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var autoSteer = _configService.Store.AutoSteer;
        switch (e.PropertyName)
        {
            case nameof(AutoSteerConfig.InvertWas): SetUntouched(() => InvertWas = autoSteer.InvertWas); break;
            case nameof(AutoSteerConfig.WasOffset): SetUntouched(() => WasOffset = autoSteer.WasOffset); break;
        }
    }

    private void OnAutoSteerStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        // Show actual WAS angle from hardware (PGN 253), not commanded angle
        LiveSteerAngle = Math.Round(_autoSteerService!.LastSteerData.ActualSteerAngle, 1);
        HasLiveData = true;
        // The module applied the new offset: the next press is a new zero, not a repeat.
        if (_zeroPending && Math.Abs(_autoSteerService.LastSteerData.ActualSteerAngle) < 0.5)
            _zeroPending = false;
    }

    public override Task<bool> ValidateAsync()
    {
        ClearValidation();
        return Task.FromResult(true);
    }
}
