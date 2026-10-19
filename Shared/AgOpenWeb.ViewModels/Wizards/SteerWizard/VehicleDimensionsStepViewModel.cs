// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Combined step for vehicle wheelbase and track width.
/// Shows a vehicle-type-dependent diagram with both input fields.
/// </summary>
public class VehicleDimensionsStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;

    public override string Title => "Vehicle Dimensions";

    public override string Description =>
        "Enter the wheelbase (front to rear axle) and track width " +
        "(left to right wheel center) of your vehicle.";

    private double _wheelbase;
    public double Wheelbase
    {
        get => _wheelbase;
        set => SetProperty(ref _wheelbase, value);
    }

    private double _trackWidth;
    public double TrackWidth
    {
        get => _trackWidth;
        set => SetProperty(ref _trackWidth, value);
    }

    public VehicleDimensionsStepViewModel(IConfigurationService configService)
    {
        _configService = configService;
    }

    protected override void OnEntering()
    {
        var vehicle = _configService.Store.Vehicle;
        Wheelbase = vehicle.Wheelbase;
        TrackWidth = vehicle.TrackWidth;
    }

    protected override void OnLeaving()
    {
        var vehicle = _configService.Store.Vehicle;
        if (Touched(nameof(Wheelbase))) vehicle.Wheelbase = Wheelbase;
        if (Touched(nameof(TrackWidth))) vehicle.TrackWidth = TrackWidth;
    }

    public override Task<bool> ValidateAsync()
    {
        if (Wheelbase < 0.5)
        {
            SetValidationError("Wheelbase must be at least 0.5 meters");
            return Task.FromResult(false);
        }
        if (Wheelbase > 15)
        {
            SetValidationError("Wheelbase seems too large. Please check the value.");
            return Task.FromResult(false);
        }

        if (TrackWidth < 0.5)
        {
            SetValidationError("Track width must be at least 0.5 meters");
            return Task.FromResult(false);
        }
        if (TrackWidth > 10)
        {
            SetValidationError("Track width seems too large. Please check the value.");
            return Task.FromResult(false);
        }

        ClearValidation();
        return Task.FromResult(true);
    }
}
