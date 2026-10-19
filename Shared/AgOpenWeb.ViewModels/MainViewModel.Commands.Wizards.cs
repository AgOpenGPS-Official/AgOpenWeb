// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;

using AgOpenWeb.ViewModels.Wizards;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Wizard commands - AutoSteer wizard, etc.
/// </summary>
public partial class MainViewModel
{
    // SteerWizard ViewModel
    private SteerWizardViewModel? _steerWizardViewModel;
    public SteerWizardViewModel? SteerWizardViewModel
    {
        get => _steerWizardViewModel;
        set => SetProperty(ref _steerWizardViewModel, value);
    }

    // Wizard commands
    public ICommand? ShowSteerWizardCommand { get; private set; }

    private void InitializeWizardCommands()
    {
        ShowSteerWizardCommand = new RelayCommand(ShowSteerWizard);
    }

    /// <summary>
    /// True while the Steer Wizard is open. Its motor tests steer through Free Drive, so
    /// AgOpenWeb's AutoSteer is kept off until it closes (#154): when a test ends, PGN 254
    /// goes back to normal mode, and an engaged AutoSteer would send its guidance angle —
    /// full lock in the field report.
    /// </summary>
    public bool IsSteerWizardOpen => SteerWizardViewModel != null;

    private void ShowSteerWizard()
    {
        // Create a new instance of the wizard
        SteerWizardViewModel = new SteerWizardViewModel(_configurationService, _dispatcher, _autoSteerService);
        DisengageForSteerWizard();

        // Handle wizard close
        SteerWizardViewModel.CloseRequested += (s, e) => EndRemoteSteerWizard();

        // Show the wizard
        SteerWizardViewModel.IsDialogVisible = true;
    }

    /// <summary>
    /// Start a fresh Steer Wizard for the remote (web) client WITHOUT showing the
    /// native overlay — the host drives the same VM and the browser renders it. The
    /// projector streams its state while it's non-null.
    /// </summary>
    public void StartRemoteSteerWizard()
    {
        EndRemoteSteerWizard(); // a reopen replaces the wizard; stop its test first
        SteerWizardViewModel = new SteerWizardViewModel(_configurationService, _dispatcher, _autoSteerService);
        DisengageForSteerWizard();
    }

    /// <summary>Tear down the remote wizard (Finish / Cancel from the browser).</summary>
    public void EndRemoteSteerWizard()
    {
        // Cancel cannot leave a motor test running with the AutoSteer lockout lifted.
        (SteerWizardViewModel?.CurrentStep as SwitchGatedWizardStep)?.StopFreeDriveTest();
        SteerWizardViewModel = null;
    }

    private void DisengageForSteerWizard()
    {
        if (!IsAutoSteerEngaged)
            return;
        ToggleAutoSteerCommand?.Execute(null);
        StatusMessage = "AutoSteer disengaged for the steer wizard";
    }
}
