// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: guards against silently discarding worked coverage
/// that was painted with no active job. Coverage only persists as part of a
/// job (<see cref="CloseFieldAsync"/> skips the save when ActiveJob is null),
/// so a field-only session that painted coverage would lose it on close.
///
/// Rather than nag at "Open Field Only" (a click on every use, even when no
/// work happens), we warn at the moment of loss: when a close is requested and
/// there is painted coverage with no job, offer Save-to-a-job / Discard /
/// Cancel. The coverage is already in memory, so "Save to a job" creates a job
/// and the normal close save captures it — nothing is lost.
/// </summary>
public partial class MainViewModel
{
    // The close work to run once the user resolves the prompt (Save or Discard).
    // For the in-app Close Field command this finishes the close; for app-exit
    // the platform passes an action that also tears the window down.
    private Action? _pendingUnsavedCoverageProceed;

    private string _unsavedCoverageMessage = string.Empty;
    public string UnsavedCoverageMessage
    {
        get => _unsavedCoverageMessage;
        private set
        {
            if (_unsavedCoverageMessage != value)
            {
                _unsavedCoverageMessage = value;
                OnPropertyChanged(nameof(UnsavedCoverageMessage));
            }
        }
    }

    public ICommand? SaveCoverageToJobCommand { get; private set; }
    public ICommand? DiscardCoverageAndCloseCommand { get; private set; }
    public ICommand? CancelUnsavedCoverageCommand { get; private set; }

    private void InitializeCoverageGuardCommands()
    {
        // Save: create a job for the orphan coverage, then let the close run —
        // CloseFieldAsync now sees an active job and saves the coverage under it.
        SaveCoverageToJobCommand = new RelayCommand(() =>
        {
            var proceed = _pendingUnsavedCoverageProceed;
            _pendingUnsavedCoverageProceed = null;
            State.UI.CloseDialog();

            try
            {
                if (ActiveField != null)
                {
                    // taskName: null → JobService builds a unique date-based
                    // default name and persists the job immediately.
                    var job = _jobService.CreateJob(
                        ActiveField.Name,
                        workType: string.Empty,
                        notes: string.Empty);
                    _logger.LogDebug("[CoverageGuard] Created job '{TaskName}' to save orphan coverage", job.TaskName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CoverageGuard] Failed to create job for unsaved coverage");
            }

            proceed?.Invoke();
        });

        // Discard: run the close with no job, so the coverage is dropped.
        DiscardCoverageAndCloseCommand = new RelayCommand(() =>
        {
            var proceed = _pendingUnsavedCoverageProceed;
            _pendingUnsavedCoverageProceed = null;
            State.UI.CloseDialog();
            proceed?.Invoke();
        });

        // Cancel: abandon the close entirely, stay in the field.
        CancelUnsavedCoverageCommand = new RelayCommand(() =>
        {
            _pendingUnsavedCoverageProceed = null;
            State.UI.CloseDialog();
        });
    }

    /// <summary>
    /// If the current session has painted coverage with no active job, shows the
    /// unsaved-coverage prompt and returns <c>true</c> (the caller must NOT close
    /// now — one of the dialog buttons will invoke <paramref name="onProceed"/>).
    /// Returns <c>false</c> when there is nothing at risk, in which case the
    /// caller should proceed with the close itself.
    /// </summary>
    public bool TryShowUnsavedCoverageGuard(Action onProceed)
    {
        if (ActiveField == null
            || _jobService.ActiveJob != null
            || _coverageMapService.PatchCount <= 0)
        {
            return false;
        }

        _pendingUnsavedCoverageProceed = onProceed;
        UnsavedCoverageMessage =
            "You've worked this field without an open job, so the coverage " +
            "won't be saved. Save it to a job, or it will be lost.";
        State.UI.ShowDialog(DialogType.UnsavedCoverage);
        return true;
    }
}
