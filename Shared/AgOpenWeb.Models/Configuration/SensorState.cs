// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Runtime sensor state - updated every GPS frame.
/// NOT persisted, NOT part of ConfigurationStore.
/// This separates runtime state from configuration.
/// </summary>
public class SensorState : ObservableObject
{
    private static SensorState? _instance;

    /// <summary>
    /// Singleton instance for runtime sensor state
    /// </summary>
    public static SensorState Instance => _instance ??= new SensorState();

    /// <summary>
    /// For testing - allows replacing the singleton instance
    /// </summary>
    public static void SetInstance(SensorState state) => _instance = state;

    // IMU/AHRS runtime values
    private double _imuHeading = 99999;
    public double ImuHeading
    {
        get => _imuHeading;
        set => SetProperty(ref _imuHeading, value);
    }

    private double _imuRoll;
    public double ImuRoll
    {
        get => _imuRoll;
        set => SetProperty(ref _imuRoll, value);
    }

    private double _imuPitch;
    public double ImuPitch
    {
        get => _imuPitch;
        set => SetProperty(ref _imuPitch, value);
    }

    private double _imuYawRate;
    public double ImuYawRate
    {
        get => _imuYawRate;
        set => SetProperty(ref _imuYawRate, value);
    }

    private short _angularVelocity;
    public short AngularVelocity
    {
        get => _angularVelocity;
        set => SetProperty(ref _angularVelocity, value);
    }

    // Validity checks
    public bool HasValidImu => ImuHeading != 99999;

    /// <summary>
    /// Resets all sensor values to their defaults
    /// </summary>
    public void Reset()
    {
        ImuHeading = 99999;
        ImuRoll = 0;
        ImuPitch = 0;
        ImuYawRate = 0;
        AngularVelocity = 0;
    }
}
