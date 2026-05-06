// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// Backs <c>ResumeTaskDialogPanel</c> — the cross-field "Resume Task"
/// history list (#349 M4). One row per known job, ordered most-recently-
/// opened first; row tap opens both the field and the job.
/// </summary>
public partial class ResumeTaskDialogViewModel : ObservableObject
{
    private readonly IJobService _jobService;
    private readonly ISettingsService _settingsService;
    private readonly Action _close;
    private readonly Action<string, string, string> _openFieldResumingJob;

    public ResumeTaskDialogViewModel(
        IJobService jobService,
        ISettingsService settingsService,
        Action close,
        Action<string, string, string> openFieldResumingJob)
    {
        _jobService = jobService;
        _settingsService = settingsService;
        _close = close;
        _openFieldResumingJob = openFieldResumingJob;

        ResumeJobCommand = new RelayCommand<JobSummary?>(ResumeJob);
        CancelCommand = new RelayCommand(() => _close());
    }

    /// <summary>
    /// Every job across every field, most recently opened first.
    /// </summary>
    public ObservableCollection<JobSummary> Jobs { get; } = new();

    [ObservableProperty]
    private JobSummary? _selectedJob;

    [ObservableProperty]
    private string? _statusMessage;

    public RelayCommand<JobSummary?> ResumeJobCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Reload the history. Called when the dialog is shown so cross-field
    /// changes from another flow (a job created in StartWorkSession, a
    /// delete from the picker) are reflected.
    /// </summary>
    public void Refresh()
    {
        Jobs.Clear();
        foreach (var j in _jobService.ListAllJobs())
            Jobs.Add(j);
        SelectedJob = Jobs.Count > 0 ? Jobs[0] : null;
        StatusMessage = null;
    }

    private void ResumeJob(JobSummary? summary)
    {
        if (summary == null) return;
        try
        {
            var fieldsRoot = _settingsService.Settings.FieldsDirectory;
            var fieldPath = Path.Combine(fieldsRoot, summary.FieldName);
            _openFieldResumingJob(fieldPath, summary.FieldName, summary.TaskName);
            _close();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
