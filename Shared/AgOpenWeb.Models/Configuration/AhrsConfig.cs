// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

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
