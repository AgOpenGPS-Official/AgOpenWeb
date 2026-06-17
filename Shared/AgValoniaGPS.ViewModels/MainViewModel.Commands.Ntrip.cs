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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AgValoniaGPS.Models.State;
using AgValoniaGPS.Models.Ntrip;
using CommunityToolkit.Mvvm.Input;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// NTRIP profile management commands.
/// </summary>
public partial class MainViewModel
{
    private void InitializeNtripCommands()
    {
        // First level of the chain: opens from the Network IO fly-out, which the
        // shared chain navigation records so Back can reopen it.
        ShowNtripProfilesDialogCommand = new RelayCommand(() =>
        {
            RefreshNtripProfiles();
            OpenChainDialog(DialogType.NtripProfiles);
        });

        AddNtripProfileCommand = new RelayCommand(() =>
        {
            EditingNtripProfile = _ntripProfileService.CreateNewProfile("New Profile");
            PopulateAvailableFieldsForProfile(EditingNtripProfile);
            PushChainDialog(DialogType.NtripProfileEditor);
        });

        EditNtripProfileCommand = new RelayCommand(() =>
        {
            if (SelectedNtripProfile != null)
            {
                EditingNtripProfile = new NtripProfile
                {
                    Id = SelectedNtripProfile.Id,
                    Name = SelectedNtripProfile.Name,
                    CasterHost = SelectedNtripProfile.CasterHost,
                    CasterPort = SelectedNtripProfile.CasterPort,
                    MountPoint = SelectedNtripProfile.MountPoint,
                    Username = SelectedNtripProfile.Username,
                    Password = SelectedNtripProfile.Password,
                    AutoConnectOnFieldLoad = SelectedNtripProfile.AutoConnectOnFieldLoad,
                    IsDefault = SelectedNtripProfile.IsDefault,
                    AssociatedFields = new List<string>(SelectedNtripProfile.AssociatedFields),
                    FilePath = SelectedNtripProfile.FilePath
                };
                PopulateAvailableFieldsForProfile(EditingNtripProfile);
                PushChainDialog(DialogType.NtripProfileEditor);
            }
        });

        DeleteNtripProfileCommand = new RelayCommand(() =>
        {
            if (SelectedNtripProfile != null)
            {
                var profileToDelete = SelectedNtripProfile;
                ShowConfirmationDialog(
                    "Delete NTRIP Profile",
                    $"Are you sure you want to delete the profile '{profileToDelete.Name}'?",
                    async () =>
                    {
                        await _ntripProfileService.DeleteProfileAsync(profileToDelete.Id);
                        RefreshNtripProfiles();
                        SelectedNtripProfile = null;
                        StatusMessage = "NTRIP profile deleted";
                    });
            }
        });

        SetDefaultNtripProfileCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedNtripProfile != null)
            {
                await _ntripProfileService.SetDefaultProfileAsync(SelectedNtripProfile.Id);
                RefreshNtripProfiles();
                StatusMessage = $"Set '{SelectedNtripProfile.Name}' as default NTRIP profile";
            }
        });

        SaveNtripProfileCommand = new AsyncRelayCommand(async () =>
        {
            if (EditingNtripProfile != null)
            {
                EditingNtripProfile.AssociatedFields = AvailableFieldsForProfile
                    .Where(f => f.IsSelected)
                    .Select(f => f.FieldName)
                    .ToList();

                await _ntripProfileService.SaveProfileAsync(EditingNtripProfile);
                RefreshNtripProfiles();
                EditingNtripProfile = null;
                AvailableFieldsForProfile.Clear();
                NavigateBack(); // pop the editor back to the profiles list
                StatusMessage = "NTRIP profile saved";
            }
        });

        CancelNtripProfileEditCommand = new RelayCommand(() =>
        {
            EditingNtripProfile = null;
            AvailableFieldsForProfile.Clear();
            NtripTestStatus = string.Empty;
            NavigateBack(); // pop the editor back to the profiles list
        });

        TestNtripConnectionCommand = new AsyncRelayCommand(async () =>
        {
            if (EditingNtripProfile == null) return;

            IsTestingNtripConnection = true;
            NtripTestStatus = "Testing connection...";
            try
            {
                // Shared probe (also used by the remote Network IO editor) — no
                // duplicated TCP/NTRIP logic between native and web.
                NtripTestStatus = await AgValoniaGPS.Services.NtripConnectionTester.TestAsync(
                    EditingNtripProfile.CasterHost, EditingNtripProfile.CasterPort,
                    EditingNtripProfile.MountPoint, EditingNtripProfile.Username,
                    EditingNtripProfile.Password);
            }
            finally
            {
                IsTestingNtripConnection = false;
            }
        });
    }
}
