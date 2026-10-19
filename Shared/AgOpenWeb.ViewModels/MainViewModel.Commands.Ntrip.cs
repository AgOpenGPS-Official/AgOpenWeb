// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Ntrip;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

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
                NtripTestStatus = await AgOpenWeb.Services.NtripConnectionTester.TestAsync(
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
