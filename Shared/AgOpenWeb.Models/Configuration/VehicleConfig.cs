// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Vehicle physical configuration.
/// Replaces: Vehicle.cs, VehicleConfiguration.cs (physical parts)
/// </summary>
public class VehicleConfig : ObservableObject
{
    // Identity
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // Vehicle type
    private VehicleType _type = VehicleType.Tractor;
    public VehicleType Type
    {
        get => _type;
        set
        {
            var oldValue = _type;
            SetProperty(ref _type, value);
            if (oldValue != value)
            {
                // Notify computed properties that depend on Type
                OnPropertyChanged(nameof(VehicleTypeDisplayName));
            }
        }
    }

    // Physical dimensions
    private double _wheelbase = 2.5;
    public double Wheelbase
    {
        get => _wheelbase;
        set
        {
            var oldValue = _wheelbase;
            SetProperty(ref _wheelbase, value);
            if (oldValue != value)
            {
                OnPropertyChanged(nameof(MinTurningRadius));
            }
        }
    }

    private double _trackWidth = 1.8;
    public double TrackWidth
    {
        get => _trackWidth;
        set => SetProperty(ref _trackWidth, value);
    }

    // Antenna position
    private double _antennaHeight = 3.0;
    public double AntennaHeight
    {
        get => _antennaHeight;
        set => SetProperty(ref _antennaHeight, value);
    }

    private double _antennaPivot = 0.0;
    public double AntennaPivot
    {
        get => _antennaPivot;
        set => SetProperty(ref _antennaPivot, value);
    }

    private double _antennaOffset = 0.0;
    public double AntennaOffset
    {
        get => _antennaOffset;
        set => SetProperty(ref _antennaOffset, value);
    }

    // IMU position, same axes as the antenna: pivot = ahead of the rear axle (+), height =
    // above the ground, offset = right of the centreline (+). Sent to the modules in PGN 247
    // so a receiver's INS (UM981) or the steer firmware gets the IMU-to-antenna lever arm; a
    // remote IMU is often mounted low, away from the board (#387). All 0 = not entered.
    private double _imuPivot = 0.0;
    public double ImuPivot
    {
        get => _imuPivot;
        set => SetProperty(ref _imuPivot, value);
    }

    private double _imuHeight = 0.0;
    public double ImuHeight
    {
        get => _imuHeight;
        set => SetProperty(ref _imuHeight, value);
    }

    private double _imuOffset = 0.0;
    public double ImuOffset
    {
        get => _imuOffset;
        set => SetProperty(ref _imuOffset, value);
    }

    // The tractor's hitch points, distances from the centre of the rear axle (ISO 11783-10: the
    // tractor's device reference point; its connectors). Kept positive, each in its own direction.
    //
    // Drawbar: the hitch pin, behind the axle. Trailing and TBT tools hang on it.
    private double _hitchLength = 1.8;
    public double HitchLength
    {
        get => _hitchLength;
        set => SetProperty(ref _hitchLength, value);
    }

    // Rear 3-point hitch: the centre of the lower link points, behind the axle (ISO 11783-10's CRP
    // of a three-point hitch). Rear-fixed tools hang on it.
    private double _threePointHitchLength = 1.8;
    public double ThreePointHitchLength
    {
        get => _threePointHitchLength;
        set => SetProperty(ref _threePointHitchLength, Math.Max(0, value));
    }

    // Front 3-point hitch: the centre of its lower link points, ahead of the axle. Front-fixed tools
    // hang on it; asked for only when a front tool is used.
    private double _frontHitchLength;
    public double FrontHitchLength
    {
        get => _frontHitchLength;
        set => SetProperty(ref _frontHitchLength, Math.Max(0, value));
    }

    // ISO 11783 hitch/coupling type code on the tractor side (-1 = not available,
    // 0 = unknown/default, 1..10 = specific ISO coupling standards). Stored as the
    // ISO integer; the UI shows the text description. Informational metadata for now.
    private int _hitchType;
    public int HitchType
    {
        get => _hitchType;
        set => SetProperty(ref _hitchType, value);
    }

    // Steering limits
    private double _maxSteerAngle = 35.0;
    public double MaxSteerAngle
    {
        get => _maxSteerAngle;
        set
        {
            var oldValue = _maxSteerAngle;
            SetProperty(ref _maxSteerAngle, value);
            if (oldValue != value)
            {
                OnPropertyChanged(nameof(MinTurningRadius));
            }
        }
    }

    private double _maxAngularVelocity = 35.0;
    public double MaxAngularVelocity
    {
        get => _maxAngularVelocity;
        set => SetProperty(ref _maxAngularVelocity, value);
    }

    // Computed properties
    public double MinTurningRadius => Wheelbase / Math.Tan(MaxSteerAngle * Math.PI / 180.0);


    /// <summary>
    /// Gets a user-friendly display name for the current vehicle type
    /// </summary>
    public string VehicleTypeDisplayName => Type switch
    {
        VehicleType.Harvester => "Harvester",
        VehicleType.FourWD => "Articulated",
        _ => "Tractor"
    };
}
