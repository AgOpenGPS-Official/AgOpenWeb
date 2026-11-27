// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// ViewModel for the AutoSteer Configuration Panel.
/// Handles compact/full mode switching, test mode, and PGN communication.
/// </summary>
public partial class AutoSteerConfigViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;
    private readonly IUdpCommunicationService? _udpService;
    private readonly IAutoSteerService? _autoSteerService;

    // Debounce timer for auto-sending slider changes
    private readonly System.Timers.Timer _sliderDebounceTimer;
    private const int SliderDebounceDelayMs = 1000;

    // Properties that trigger immediate slider debounce (left-side compact mode)
    private static readonly HashSet<string> LeftSideSliderProperties = new()
    {
        nameof(AutoSteerConfig.CountsPerDegree),
        nameof(AutoSteerConfig.Ackermann),
        nameof(AutoSteerConfig.AcquireFactor),
        nameof(AutoSteerConfig.ProportionalGain),
        nameof(AutoSteerConfig.MaxPwm),
        nameof(AutoSteerConfig.MinPwm)
    };

    public AutoSteerConfigViewModel(
        IConfigurationService configService,
        IUdpCommunicationService? udpService = null,
        IAutoSteerService? autoSteerService = null,
        Action? launchWizard = null,
        Action? openSmartWas = null)
    {
        _configService = configService;
        _udpService = udpService;
        _autoSteerService = autoSteerService;

        // Set up debounce timer for slider changes
        _sliderDebounceTimer = new System.Timers.Timer(SliderDebounceDelayMs);
        _sliderDebounceTimer.AutoReset = false;
        _sliderDebounceTimer.Elapsed += OnSliderDebounceElapsed;

        // Subscribe to AutoSteer property changes
        AutoSteer.PropertyChanged += OnAutoSteerPropertyChanged;

        InitializeNumericInputCommands();
        InitializeTab2Commands();
        InitializeTab3Commands();
        InitializeTab4Commands();
        InitializeTab5Commands();
        InitializeTab6Commands();
        InitializeTab7Commands();
        InitializeTab8Commands();
        InitializeTab9Commands();
        InitializeTestModeCommands();
        InitializeActionCommands();
    }

    #region Configuration Access

    public ConfigurationStore Config => _configService.Store;
    public AutoSteerConfig AutoSteer => Config.AutoSteer;

    #endregion

    #region Change Tracking & Auto-Send

    private bool _hasUnsavedRightSideChanges;
    /// <summary>
    /// True when right-side (expanded mode) settings have changed but not been saved.
    /// Shows warning indicator to remind user to click Send+Save.
    /// </summary>
    public bool HasUnsavedRightSideChanges
    {
        get => _hasUnsavedRightSideChanges;
        set => SetProperty(ref _hasUnsavedRightSideChanges, value);
    }

    private void OnAutoSteerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null) return;

        if (LeftSideSliderProperties.Contains(e.PropertyName))
        {
            // Left-side slider changed - debounce and auto-send
            _sliderDebounceTimer.Stop();
            _sliderDebounceTimer.Start();
        }
        else
        {
            // Right-side setting changed - show warning
            HasUnsavedRightSideChanges = true;
        }
    }

    private void OnSliderDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Timer fires on background thread - send PGN updates
        SendSettingsToModule();
    }

    /// <summary>
    /// Send current settings to the steering module (PGN 251 + 252).
    /// Thread-safe, can be called from timer thread.
    /// </summary>
    private void SendSettingsToModule()
    {
        if (_udpService == null) return;

        try
        {
            var pgn251 = PgnBuilder.BuildSteerConfigPgn(AutoSteer);
            var pgn252 = PgnBuilder.BuildSteerSettingsPgn(AutoSteer);
            _udpService.SendToModules(pgn251);
            _udpService.SendToModules(pgn252);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to send settings: {ex.Message}");
        }
    }

    #endregion

    #region Panel Visibility & Mode

    private bool _isPanelVisible;
    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set
        {
            if (SetProperty(ref _isPanelVisible, value))
            {
                if (value)
                {
                    SubscribeToUdpEvents();
                }
                else
                {
                    UnsubscribeFromUdpEvents();
                    // Disable free drive when panel closes (safety)
                    StopFreeDrive();
                }
            }
        }
    }

    private bool _isFullMode;
    public bool IsFullMode
    {
        get => _isFullMode;
        set => SetProperty(ref _isFullMode, value);
    }

    #endregion

    #region Numeric Input Dialog

    private bool _isNumericInputVisible;
    public bool IsNumericInputVisible
    {
        get => _isNumericInputVisible;
        set => SetProperty(ref _isNumericInputVisible, value);
    }

    private string _numericInputTitle = string.Empty;
    public string NumericInputTitle
    {
        get => _numericInputTitle;
        set => SetProperty(ref _numericInputTitle, value);
    }

    private string _numericInputUnit = string.Empty;
    public string NumericInputUnit
    {
        get => _numericInputUnit;
        set => SetProperty(ref _numericInputUnit, value);
    }

    private string _numericInputDisplayText = string.Empty;
    public string NumericInputDisplayText
    {
        get => _numericInputDisplayText;
        set => SetProperty(ref _numericInputDisplayText, value);
    }

    private bool _numericInputIntegerOnly;
    private bool _numericInputAllowNegative = true;
    private double _numericInputMin = double.MinValue;
    private double _numericInputMax = double.MaxValue;
    private bool _isFirstDigitEntry = true;
    private Action<double>? _numericInputCallback;

    public ICommand ConfirmNumericInputCommand { get; private set; } = null!;
    public ICommand CancelNumericInputCommand { get; private set; } = null!;
    public ICommand NumericInputDigitCommand { get; private set; } = null!;
    public ICommand NumericInputBackspaceCommand { get; private set; } = null!;
    public ICommand NumericInputClearCommand { get; private set; } = null!;
    public ICommand NumericInputNegateCommand { get; private set; } = null!;

    private void InitializeNumericInputCommands()
    {
        ConfirmNumericInputCommand = new RelayCommand(() =>
        {
            if (_numericInputCallback != null &&
                decimal.TryParse(NumericInputDisplayText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                var value = Math.Clamp((double)parsed, _numericInputMin, _numericInputMax);
                _numericInputCallback(value);
                Config.MarkChanged();
            }
            IsNumericInputVisible = false;
            _numericInputCallback = null;
        });

        CancelNumericInputCommand = new RelayCommand(() =>
        {
            IsNumericInputVisible = false;
            _numericInputCallback = null;
        });

        NumericInputDigitCommand = new RelayCommand<string>(digit =>
        {
            if (string.IsNullOrEmpty(digit)) return;

            if (digit == ".")
            {
                if (_numericInputIntegerOnly) return;
                if (_isFirstDigitEntry)
                {
                    NumericInputDisplayText = "0.";
                    _isFirstDigitEntry = false;
                }
                else if (!NumericInputDisplayText.Contains("."))
                {
                    NumericInputDisplayText += ".";
                }
                return;
            }

            if (_isFirstDigitEntry)
            {
                NumericInputDisplayText = digit;
                _isFirstDigitEntry = false;
            }
            else
            {
                NumericInputDisplayText = NumericInputDisplayText == "0" ? digit : NumericInputDisplayText + digit;
            }
        });

        NumericInputBackspaceCommand = new RelayCommand(() =>
        {
            _isFirstDigitEntry = false;
            var current = NumericInputDisplayText;
            if (current.Length > 1)
            {
                NumericInputDisplayText = current.Length == 2 && current.StartsWith("-")
                    ? "0"
                    : current.Substring(0, current.Length - 1);
            }
            else
            {
                NumericInputDisplayText = "0";
            }
        });

        NumericInputClearCommand = new RelayCommand(() =>
        {
            NumericInputDisplayText = "0";
            _isFirstDigitEntry = false;
        });

        NumericInputNegateCommand = new RelayCommand(() =>
        {
            if (!_numericInputAllowNegative) return;
            _isFirstDigitEntry = false;

            if (NumericInputDisplayText.StartsWith("-"))
                NumericInputDisplayText = NumericInputDisplayText.Substring(1);
            else if (NumericInputDisplayText != "0")
                NumericInputDisplayText = "-" + NumericInputDisplayText;
        });
    }

    #endregion


    #region Tab 2: Steering Sensor

    public ICommand ZeroWasCommand { get; private set; } = null!;

    private void InitializeTab2Commands()
    {
        ZeroWasCommand = new RelayCommand(() =>
        {
            // New offset that makes the live WAS angle read zero (firmware-derived, independent
            // of Invert WAS — #103, see WasCalibration). Read the angle straight from the
            // service: _smoothedActualAngle only updates while the (native) panel is visible,
            // which the web never sets, so from the web it was always 0.
            double angle = _autoSteerService?.LastSteerData.ActualSteerAngle ?? _smoothedActualAngle;
            if (!WasCalibration.TryZero(AutoSteer.WasOffset, angle, AutoSteer.CountsPerDegree, out int zeroed))
                return; // "Excessive steer angle — cannot zero" (AgOpenGPS)
            AutoSteer.WasOffset = zeroed;
            Config.MarkChanged();

            // Send updated settings to module immediately
            SendSteerSettingsPgn();
        });
    }

    #endregion

    #region Tab 3: Deadzone / Timing

    private void InitializeTab3Commands()
    {
    }

    #endregion

    #region Tab 4: Gain / PWM

    private void InitializeTab4Commands()
    {
    }

    #endregion

    #region Tab 5: Turn Sensors

    private void InitializeTab5Commands()
    {
    }

    #endregion

    #region Tab 6: Hardware Config

    private void InitializeTab6Commands()
    {
    }

    #endregion

    #region Tab 7: Algorithm Settings

    private void InitializeTab7Commands()
    {
    }

    #endregion

    #region Tab 8: Speed Limits

    private void InitializeTab8Commands()
    {
    }

    #endregion

    #region Tab 9: Display Settings

    /// <summary>Whether any bar mode (lightbar or steerbar) is enabled.</summary>
    public bool IsBarEnabled => AutoSteer.LightbarEnabled || AutoSteer.SteerBarEnabled;

    private void InitializeTab9Commands()
    {
    }

    #endregion

    #region Test Mode (Free Drive)

    private bool _isFreeDriveMode;
    public bool IsFreeDriveMode
    {
        get => _isFreeDriveMode;
        set => SetProperty(ref _isFreeDriveMode, value);
    }

    // Free-drive steering is limited to the vehicle's max steer angle (was a fixed ±40°, #112).
    private double FreeDriveLimit => Math.Max(1, Config.Vehicle.MaxSteerAngle);

    private double _freeDriveSteerAngle;
    public double FreeDriveSteerAngle
    {
        get => _freeDriveSteerAngle;
        set => SetProperty(ref _freeDriveSteerAngle, Math.Clamp(value, -FreeDriveLimit, FreeDriveLimit));
    }

    private int _pwmDisplay;
    public int PwmDisplay
    {
        get => _pwmDisplay;
        set => SetProperty(ref _pwmDisplay, value);
    }

    private double _actualSteerAngle;
    public double ActualSteerAngle
    {
        get => _actualSteerAngle;
        set => SetProperty(ref _actualSteerAngle, value);
    }

    private double _steerError;
    public double SteerError
    {
        get => _steerError;
        set => SetProperty(ref _steerError, value);
    }

    private double _setSteerAngle;
    public double SetSteerAngle
    {
        get => _setSteerAngle;
        set => SetProperty(ref _setSteerAngle, value);
    }

    // Switch status from module (PGN 253)
    private bool _steerSwitchActive;
    public bool SteerSwitchActive
    {
        get => _steerSwitchActive;
        set => SetProperty(ref _steerSwitchActive, value);
    }

    private bool _workSwitchActive;
    public bool WorkSwitchActive
    {
        get => _workSwitchActive;
        set => SetProperty(ref _workSwitchActive, value);
    }

    private bool _remoteButtonPressed;
    public bool RemoteButtonPressed
    {
        get => _remoteButtonPressed;
        set => SetProperty(ref _remoteButtonPressed, value);
    }

    private bool _vwasFusionActive;
    public bool VwasFusionActive
    {
        get => _vwasFusionActive;
        set => SetProperty(ref _vwasFusionActive, value);
    }

    private int _testSteerOffset;
    public int TestSteerOffset
    {
        get => _testSteerOffset;
        set => SetProperty(ref _testSteerOffset, value);
    }

    /// <summary>Free Drive off and the wheels back to centre in the panel (the panel closed,
    /// or the browser that held control is gone).</summary>
    public void StopFreeDrive()
    {
        if (!IsFreeDriveMode) return;
        _autoSteerService?.DisableFreeDrive();
        IsFreeDriveMode = false;
        FreeDriveSteerAngle = 0;
    }

    public ICommand ToggleFreeDriveCommand { get; private set; } = null!;
    public ICommand SteerLeftCommand { get; private set; } = null!;
    public ICommand SteerRightCommand { get; private set; } = null!;
    public ICommand SteerCenterCommand { get; private set; } = null!;

    private void InitializeTestModeCommands()
    {
        ToggleFreeDriveCommand = new RelayCommand(() =>
        {
            IsFreeDriveMode = !IsFreeDriveMode;
            if (IsFreeDriveMode)
            {
                _autoSteerService?.EnableFreeDrive();
                FreeDriveSteerAngle = 0;
                SetSteerAngle = 0; // Show target angle in status bar
            }
            else
            {
                _autoSteerService?.DisableFreeDrive();
                FreeDriveSteerAngle = 0;
            }
        });

        SteerLeftCommand = new RelayCommand(() =>
        {
            if (IsFreeDriveMode)
            {
                FreeDriveSteerAngle = Math.Max(FreeDriveSteerAngle - 2, -FreeDriveLimit);
                _autoSteerService?.SetFreeDriveAngle(FreeDriveSteerAngle);
                SetSteerAngle = FreeDriveSteerAngle; // Update status bar
            }
        });

        SteerRightCommand = new RelayCommand(() =>
        {
            if (IsFreeDriveMode)
            {
                FreeDriveSteerAngle = Math.Min(FreeDriveSteerAngle + 2, FreeDriveLimit);
                _autoSteerService?.SetFreeDriveAngle(FreeDriveSteerAngle);
                SetSteerAngle = FreeDriveSteerAngle; // Update status bar
            }
        });

        SteerCenterCommand = new RelayCommand(() =>
        {
            FreeDriveSteerAngle = 0;
            _autoSteerService?.SetFreeDriveAngle(0);
            if (IsFreeDriveMode) SetSteerAngle = 0;
        });
    }

    #endregion

    #region Action Commands

    public ICommand SendAndSaveCommand { get; private set; } = null!;

    private bool _isResetConfirmVisible;
    public bool IsResetConfirmVisible
    {
        get => _isResetConfirmVisible;
        set => SetProperty(ref _isResetConfirmVisible, value);
    }

    public ICommand ConfirmResetCommand { get; private set; } = null!;

    private void InitializeActionCommands()
    {
        SendAndSaveCommand = new RelayCommand(() =>
        {
            // Build and send PGN 252 (Steer Settings)
            SendSteerSettingsPgn();

            // Build and send PGN 251 (Steer Config)
            SendSteerConfigPgn();

            // Save configuration
            _configService.SaveProfiles(Config.ActiveVehicleProfileName, Config.ActiveToolProfileName);

            // Clear the unsaved changes warning
            HasUnsavedRightSideChanges = false;
        });

        ConfirmResetCommand = new RelayCommand(() =>
        {
            // Reset all settings to defaults
            AutoSteer.ResetToDefaults();
            Config.Guidance.ResetSteeringTuning(); // the panel's algorithm/look-ahead tab (#99)
            Config.MarkChanged();

            // Send updated settings to module
            SendSteerSettingsPgn();
            SendSteerConfigPgn();

            IsResetConfirmVisible = false;
        });
    }

    /// <summary>
    /// Build and send PGN 252 - Steer Settings
    /// </summary>
    private void SendSteerSettingsPgn()
    {
        var pgn = PgnBuilder.BuildSteerSettingsPgn(AutoSteer);
        _udpService?.SendToModules(pgn);
    }

    /// <summary>
    /// Build and send PGN 251 - Steer Config
    /// </summary>
    private void SendSteerConfigPgn()
    {
        var pgn = PgnBuilder.BuildSteerConfigPgn(AutoSteer);
        _udpService?.SendToModules(pgn);
    }

    #endregion

    #region Status Display (Updated from hardware feedback)

    #endregion

    #region UDP Event Subscription

    private bool _isSubscribed;

    // Display throttling: update at ~10Hz instead of 50Hz
    private const int DisplayUpdateIntervalMs = 100;
    private DateTime _lastDisplayUpdate = DateTime.MinValue;

    // Smoothing: exponential moving average for angle display
    private double _smoothedActualAngle;
    private const double SmoothingFactor = 0.3; // Lower = smoother, higher = more responsive

    private void SubscribeToUdpEvents()
    {
        if (_isSubscribed || _udpService == null) return;

        _udpService.DataReceived += OnUdpDataReceived;
        _isSubscribed = true;
        _smoothedActualAngle = 0;
        _lastDisplayUpdate = DateTime.MinValue;
    }

    private void UnsubscribeFromUdpEvents()
    {
        if (!_isSubscribed || _udpService == null) return;

        _udpService.DataReceived -= OnUdpDataReceived;
        _isSubscribed = false;
    }

    private void OnUdpDataReceived(object? sender, UdpDataReceivedEventArgs e)
    {
        // Handle PGN 253 (Steer Data from module)
        if (e.PGN != PgnNumbers.AUTOSTEER_DATA) return;

        // Parse the incoming steer data
        if (!PgnBuilder.TryParseSteerData(e.Data, out var steerData))
            return;

        // Apply exponential moving average smoothing to angle
        _smoothedActualAngle = (_smoothedActualAngle * (1 - SmoothingFactor)) +
                              (steerData.ActualSteerAngle * SmoothingFactor);

        // Throttle display updates to ~10Hz
        var now = DateTime.UtcNow;
        if ((now - _lastDisplayUpdate).TotalMilliseconds < DisplayUpdateIntervalMs)
            return;

        _lastDisplayUpdate = now;

        // Update display properties with smoothed values
        ActualSteerAngle = Math.Round(_smoothedActualAngle, 1);

        // Calculate steer error
        double error = SetSteerAngle - _smoothedActualAngle;
        SteerError = Math.Round(Math.Abs(error), 1);

        // Calculate PWM based on error and proportional gain
        // This is the PWM AgOpenWeb would send to the motor
        double pwmRaw = error * AutoSteer.ProportionalGain;
        int pwmClamped = (int)Math.Clamp(Math.Abs(pwmRaw), 0, AutoSteer.MaxPwm);

        // Apply minimum PWM threshold (deadband)
        if (pwmClamped < AutoSteer.MinPwm)
            pwmClamped = 0;

        PwmDisplay = pwmClamped;

        // Update switch status from module
        WorkSwitchActive = steerData.WorkSwitchActive;
        SteerSwitchActive = steerData.SteerSwitchActive;
        RemoteButtonPressed = steerData.RemoteButtonPressed;
        VwasFusionActive = steerData.VwasFusionActive;
    }

    #endregion
}
