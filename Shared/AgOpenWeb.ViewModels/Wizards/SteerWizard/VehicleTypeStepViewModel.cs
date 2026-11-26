// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;

using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Step for selecting the vehicle type.
/// Options: Tractor, Harvester, or Articulated 4WD.
/// This determines steering geometry, antenna positioning, and wheelbase configuration.
/// </summary>
public class VehicleTypeStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;

    public override string Title => "Vehicle Type";

    public override string Description =>
        "Select your vehicle type. This determines steering geometry, " +
        "antenna positioning, and wheelbase configuration.";

    public override bool CanSkip => false;

    private VehicleType _vehicleType;
    public VehicleType VehicleType
    {
        get => _vehicleType;
        set
        {
            SetProperty(ref _vehicleType, value);
            OnPropertyChanged(nameof(IsTractorSelected));
            OnPropertyChanged(nameof(IsHarvesterSelected));
            OnPropertyChanged(nameof(IsFourWDSelected));
        }
    }

    /// <summary>
    /// True when Tractor is selected.
    /// </summary>
    public bool IsTractorSelected => VehicleType == VehicleType.Tractor;

    /// <summary>
    /// True when Harvester is selected.
    /// </summary>
    public bool IsHarvesterSelected => VehicleType == VehicleType.Harvester;

    /// <summary>
    /// True when Articulated 4WD is selected.
    /// </summary>
    public bool IsFourWDSelected => VehicleType == VehicleType.FourWD;

    public VehicleTypeStepViewModel(IConfigurationService configService)
    {
        _configService = configService;
    }

    protected override void OnEntering()
    {
        VehicleType = _configService.Store.Vehicle.Type;
    }

    protected override void OnLeaving()
    {
        if (Touched(nameof(VehicleType))) _configService.Store.Vehicle.Type = VehicleType;
    }

    public void SelectTractor() => VehicleType = VehicleType.Tractor;
    public void SelectHarvester() => VehicleType = VehicleType.Harvester;
    public void SelectFourWD() => VehicleType = VehicleType.FourWD;

    public override Task<bool> ValidateAsync()
    {
        ClearValidation();
        return Task.FromResult(true);
    }
}
