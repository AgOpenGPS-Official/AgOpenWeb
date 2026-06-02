// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.Models.Configuration;

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
                OnPropertyChanged(nameof(WheelbaseImageSource));
                OnPropertyChanged(nameof(WheelbaseCropImageSource));
                OnPropertyChanged(nameof(HitchCropImageSource));
                OnPropertyChanged(nameof(HasHitchImage));
                OnPropertyChanged(nameof(TrackWidthImageSource));
                OnPropertyChanged(nameof(AntennaImageSource));
                OnPropertyChanged(nameof(AntennaSideImageSource));
                OnPropertyChanged(nameof(AntennaOffsetImageSource));
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

    // Hitch (vehicle): rear axle center -> tractor hitch pin. This is the trailer
    // attach point and is used ONLY by trailing/TBT tools to place the hitch.
    // Rigid front/rear-fixed tools instead use Tool.HitchLength (axle -> implement
    // working center, which is tool-dependent). Stored unsigned/positive; the tool
    // position geometry applies the rear sign and takes Math.Abs().
    private double _hitchLength = 1.8;
    public double HitchLength
    {
        get => _hitchLength;
        set => SetProperty(ref _hitchLength, value);
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
    /// Gets the image source for the wheelbase/track diagram based on vehicle type
    /// </summary>
    public string WheelbaseImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/RadiusWheelBaseHarvester.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/RadiusWheelBaseArticulated.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/RadiusWheelBase.png"
    };

    /// <summary>
    /// Cropped wheelbase diagram (focused on the axle span) for the dimensions UI.
    /// </summary>
    public string WheelbaseCropImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/WheelbaseHarvester.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/WheelbaseArticulated.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/WheelbaseTractor.png"
    };

    /// <summary>
    /// Track-width diagram (left-to-right wheel centers) for the dimensions UI.
    /// </summary>
    public string TrackWidthImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/TrackWidthHarvester.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/TrackWidthArticulated.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/TrackWidthTractor.png"
    };

    /// <summary>
    /// Top-down hitch diagram (rear axle &#8594; hitch pin) for the dimensions UI card.
    /// Tractor and articulated have their own top-down art; harvester has no hitch
    /// diagram (matches AgOpen, which shows none for harvesters).
    /// </summary>
    public string HitchCropImageSource => Type switch
    {
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/HitchArticulated.png",
        VehicleType.Harvester => string.Empty,
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/Hitch.png"
    };

    /// <summary>
    /// Whether a hitch diagram exists for the current vehicle type (false for harvester).
    /// </summary>
    public bool HasHitchImage => Type != VehicleType.Harvester;

    /// <summary>
    /// Gets the image source for the antenna position diagram based on vehicle type
    /// </summary>
    public string AntennaImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaHarvester.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaArticulated.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaTractor.png"
    };

    /// <summary>
    /// Side-view antenna diagram (fore/aft pivot distance + antenna height) for the
    /// dimensions UI card.
    /// </summary>
    public string AntennaSideImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaHarvesterTop.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaArticulatedTop.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaTractorTop.png"
    };

    /// <summary>
    /// Top-down antenna diagram (left/right offset from centerline) for the dimensions UI card.
    /// </summary>
    public string AntennaOffsetImageSource => Type switch
    {
        VehicleType.Harvester => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaHarvesterOffset.png",
        VehicleType.FourWD => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaArticulatedOffset.png",
        _ => "avares://AgValoniaGPS.Views/Assets/Icons/AntennaTractorOffset.png"
    };

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
