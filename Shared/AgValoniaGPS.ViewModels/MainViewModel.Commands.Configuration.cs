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


using AgValoniaGPS.Models.State;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// Configuration dialog commands - profiles, settings dialogs.
/// </summary>
public partial class MainViewModel
{
    private void InitializeConfigurationCommands()
    {
        // Configuration Dialog
        ShowConfigurationDialogCommand = new RelayCommand(() =>
        {
            ConfigurationViewModel = new ConfigurationViewModel(_configurationService);
            ConfigurationViewModel.CloseRequested += (s, e) =>
            {
                ConfigurationViewModel.IsDialogVisible = false;
            };
            ConfigurationViewModel.IsDialogVisible = true;
        });

        CancelConfigurationDialogCommand = new RelayCommand(() =>
        {
            if (ConfigurationViewModel != null)
                ConfigurationViewModel.IsDialogVisible = false;
        });

        // Load Vehicle / Tool picker (#346)
        ShowLoadVehicleToolDialogCommand = new RelayCommand(() =>
        {
            LoadVehicleToolDialogVm = new LoadVehicleToolDialogViewModel(
                _configurationService,
                onClose: () => State.UI.CloseDialog(),
                confirm: (msg, action) => ShowConfirmationDialog("Confirm", msg, action),
                confirmChoice: (msg, confirmLabel, cancelLabel, action) =>
                    ShowConfirmationDialog("Profile Damaged", msg, confirmLabel, cancelLabel, action));
            State.UI.ShowDialog(DialogType.LoadVehicleTool);
        });

        CancelLoadVehicleToolDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        // AutoSteer Configuration Panel
        ShowAutoSteerConfigCommand = new RelayCommand(() =>
        {
            AutoSteerConfigViewModel ??= new AutoSteerConfigViewModel(
                _configurationService,
                _udpService,
                _autoSteerService,
                ShowSteerWizard,
                () => ShowSmartWasCommand?.Execute(null));
            AutoSteerConfigViewModel.IsPanelVisible = true;
        });

        // Smart WAS calibration dialog
        ShowSmartWasCommand = new RelayCommand(() =>
        {
            SmartWasViewModel ??= new SmartWasViewModel(
                _smartWasService,
                _configurationService,
                _udpService,
                _autoSteerService);
            State.UI.ShowDialog(DialogType.SmartWas);
        });

        CloseSmartWasDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });
    }
}
