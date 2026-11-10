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

    /// <summary>Tear down the remote wizard (Finish / Cancel from the browser, or the browser
    /// gone). What the finished steps set is saved, however the wizard ends, as AgOpenGPS saves
    /// its settings when the steer wizard closes (FormSteerWiz): Cancel kept the steps' values
    /// (Invert Motor, the WAS and roll zeros) in the live settings, and the module ran them,
    /// but did not save them, so the next restart lost them (Steer wizard scenarios,
    /// CANCEL_KEEPS_LIVE_NOT_SAVED).</summary>
    public void EndRemoteSteerWizard()
    {
        var wizard = SteerWizardViewModel;
        if (wizard == null) return;
        // Cancel cannot leave a motor test running with the AutoSteer lockout lifted.
        (wizard.CurrentStep as SwitchGatedWizardStep)?.StopFreeDriveTest();
        SteerWizardViewModel = null;
        // Leave the step (a CPD / Ackermann circle stops recording, a test's temporary value
        // goes back) and drop the handlers; then save what is left.
        wizard.Close();
        if (_configurationService.Store is { } store)
            _configurationService.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
    }

    /// <summary>
    /// The browser that held control is gone (released, disconnected, or the deadman ran
    /// out). Nothing may keep actuating with no screen and no Stop button: AutoSteer off,
    /// sections off, and the steer wizard closed. Closing the wizard stops a motor test
    /// that would otherwise go on turning the wheels lock to lock; a wizard left open
    /// would also keep AutoSteer locked out (#154) with nothing on the reloaded page to
    /// close it. What the finished steps set stays, saved, as with Cancel.
    /// </summary>
    public void OnBrowserControlEnded(string offReason)
    {
        if (IsAutoSteerEngaged && ToggleAutoSteerCommand?.CanExecute(offReason) == true)
            ToggleAutoSteerCommand.Execute(offReason);
        if (IsManualSectionMode && ToggleManualModeCommand?.CanExecute(null) == true)
            ToggleManualModeCommand.Execute(null);
        if (IsSectionMasterOn && ToggleSectionMasterCommand?.CanExecute(null) == true)
            ToggleSectionMasterCommand.Execute(null);
        if (IsSteerWizardOpen)
        {
            EndRemoteSteerWizard();
            StatusMessage = "Steer wizard closed: " + offReason;
        }
    }

    private void DisengageForSteerWizard()
    {
        if (!IsAutoSteerEngaged)
            return;
        ToggleAutoSteerCommand?.Execute("steer wizard opened");
        StatusMessage = "AutoSteer disengaged for the steer wizard";
    }
}
