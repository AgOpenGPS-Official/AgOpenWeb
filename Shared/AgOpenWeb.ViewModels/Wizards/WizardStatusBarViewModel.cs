// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels.Wizards;

/// <summary>
/// Persistent status bar showing live hardware data across all wizard steps.
/// Displays WAS angle, Roll, GPS status, Speed, and PWM output.
/// </summary>
public class WizardStatusBarViewModel : ObservableObject, IDisposable
{
    private readonly IAutoSteerService? _autoSteerService;

    private double _wasAngle;
    public double WasAngle { get => _wasAngle; set => SetProperty(ref _wasAngle, value); }

    private double _rollAngle;
    public double RollAngle { get => _rollAngle; set => SetProperty(ref _rollAngle, value); }

    private string _gpsStatus = "No GPS";
    public string GpsStatus { get => _gpsStatus; set => SetProperty(ref _gpsStatus, value); }

    private bool _isModuleConnected;
    public bool IsModuleConnected { get => _isModuleConnected; set => SetProperty(ref _isModuleConnected, value); }

    private int _pwmOutput;
    public int PwmOutput { get => _pwmOutput; set => SetProperty(ref _pwmOutput, value); }

    private double _speedKmh;
    public double SpeedKmh { get => _speedKmh; set => SetProperty(ref _speedKmh, value); }

    public WizardStatusBarViewModel(IAutoSteerService? autoSteerService = null)
    {
        _autoSteerService = autoSteerService;
        if (_autoSteerService != null)
            _autoSteerService.StateUpdated += OnStateUpdated;
    }

    private void OnStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        WasAngle = Math.Round(_autoSteerService!.LastSteerData.ActualSteerAngle, 1);
        RollAngle = Math.Round(snapshot.Roll, 1);
        SpeedKmh = Math.Round(snapshot.SpeedKmh, 1);
        PwmOutput = _autoSteerService.LastSteerData.PwmDisplay;

        GpsStatus = snapshot.FixQuality switch
        {
            4 => "RTK Fixed",
            5 => "RTK Float",
            2 => "DGPS",
            1 => "GPS",
            _ => "No GPS"
        };

        IsModuleConnected = true;
    }

    public void Dispose()
    {
        if (_autoSteerService != null)
            _autoSteerService.StateUpdated -= OnStateUpdated;
    }
}
