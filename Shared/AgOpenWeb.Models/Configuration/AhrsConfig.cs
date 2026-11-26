// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// AHRS/IMU configuration settings.
/// Part of ConfigurationStore, persisted with profile.
/// Runtime sensor values are in SensorState (not persisted).
/// </summary>
public class AhrsConfig : ObservableObject
{
    private double _rollZero;
    public double RollZero
    {
        get => _rollZero;
        set => SetProperty(ref _rollZero, value);
    }

    private double _rollFilter;
    public double RollFilter
    {
        get => _rollFilter;
        set => SetProperty(ref _rollFilter, value);
    }

    private double _fusionWeight;
    public double FusionWeight
    {
        get => _fusionWeight;
        set => SetProperty(ref _fusionWeight, value);
    }

    private bool _isRollInvert;
    public bool IsRollInvert
    {
        get => _isRollInvert;
        set => SetProperty(ref _isRollInvert, value);
    }

    private double _forwardCompensation;
    public double ForwardCompensation
    {
        get => _forwardCompensation;
        set => SetProperty(ref _forwardCompensation, value);
    }

    private double _reverseCompensation;
    public double ReverseCompensation
    {
        get => _reverseCompensation;
        set => SetProperty(ref _reverseCompensation, value);
    }

    private bool _isAutoSteerAuto = true;
    public bool IsAutoSteerAuto
    {
        get => _isAutoSteerAuto;
        set => SetProperty(ref _isAutoSteerAuto, value);
    }

    // IsReverseOn duplicated Connections.ReverseDetection, never read (#125).

    private bool _isDualAsIMU;
    public bool IsDualAsIMU
    {
        get => _isDualAsIMU;
        set => SetProperty(ref _isDualAsIMU, value);
    }

    // AutoSwitchDualFixOn / AutoSwitchDualFixSpeed / AlarmStopsAutoSteer were duplicates
    // of Connections.AutoDualFix / DualSwitchSpeed / RtkLostAction, never read (#112).
}
