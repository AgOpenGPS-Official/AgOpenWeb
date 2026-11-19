// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Services.Fields;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <inheritdoc />
public class JobService : IJobService
{
    private readonly Func<string> _fieldsRootProvider;

    public JobService(Func<string> fieldsRootProvider)
    {
        _fieldsRootProvider = fieldsRootProvider
            ?? throw new ArgumentNullException(nameof(fieldsRootProvider));
    }

    public Job? ActiveJob { get; private set; }

    public event EventHandler<Job?>? ActiveJobChanged;

    public IReadOnlyList<JobSummary> ListJobs(string fieldName)
    {
        var fieldDir = ResolveFieldDir(fieldName);
        if (fieldDir == null) return Array.Empty<JobSummary>();

        return JobJsonService.LoadAll(fieldDir)
            .Select(ToSummary)
            .OrderByDescending(s => s.LastOpenedAt)
            .ToArray();
    }

    public IReadOnlyList<JobSummary> ListAllJobs()
    {
        var root = _fieldsRootProvider();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return Array.Empty<JobSummary>();

        var summaries = new List<JobSummary>();
        foreach (var fieldDir in Directory.GetDirectories(root))
        {
            foreach (var job in JobJsonService.LoadAll(fieldDir))
                summaries.Add(ToSummary(job));
        }
        summaries.Sort((a, b) => b.LastOpenedAt.CompareTo(a.LastOpenedAt));
        return summaries;
    }

    public Job? GetJob(string fieldName, string taskName)
    {
        var fieldDir = ResolveFieldDir(fieldName);
        if (fieldDir == null) return null;
        return JobJsonService.Load(fieldDir, taskName);
    }

    public Job CreateJob(string fieldName, string workType, string notes, string? taskName = null)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("fieldName must be set", nameof(fieldName));

        var fieldDir = ResolveFieldDir(fieldName)
                       ?? throw new InvalidOperationException(
                           $"Field directory for '{fieldName}' not found under {_fieldsRootProvider()}");

        var safeName = string.IsNullOrWhiteSpace(taskName) ? string.Empty : SafeTaskName(taskName!);
        var resolvedName = safeName.Length == 0
            ? BuildUniqueDefaultTaskName(fieldDir, workType, DateTime.Now)
            : EnsureUnique(fieldDir, safeName);

        var now = DateTime.Now;
        var job = new Job
        {
            FieldName = fieldName,
            TaskName = resolvedName,
            WorkType = workType ?? string.Empty,
            Notes = notes ?? string.Empty,
            StartedAt = now,
            EndedAt = null,
            LastOpenedAt = now,
            Status = JobStatus.InProgress
        };

        JobJsonService.Save(job, fieldDir);
        SetActiveJob(job);
        return job;
    }

    public Job GetOrCreateDefaultJob(string fieldName)
    {
        var existing = ListJobs(fieldName)
            .FirstOrDefault(j => j.Status == JobStatus.InProgress);

        if (existing != null)
        {
            ResumeJob(fieldName, existing.TaskName);
            return ActiveJob!;
        }

        return CreateJob(fieldName, workType: string.Empty, notes: string.Empty);
    }

    public IReadOnlyList<string> SuggestWorkTypes()
    {
        // Seed first so a fresh install gets useful suggestions, then
        // any prior user-typed labels by recency, de-duped case-insensitively.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var s in JobWorkTypeSuggestions.Seed)
        {
            if (seen.Add(s)) ordered.Add(s);
        }

        var prior = ListAllJobs()
            .OrderByDescending(j => j.LastOpenedAt)
            .Select(j => j.WorkType)
            .Where(w => !string.IsNullOrWhiteSpace(w));

        foreach (var w in prior)
        {
            if (seen.Add(w)) ordered.Add(w);
        }

        return ordered;
    }

    public void ResumeJob(string fieldName, string taskName)
    {
        var fieldDir = ResolveFieldDir(fieldName)
                       ?? throw new InvalidOperationException(
                           $"Field directory for '{fieldName}' not found");

        var job = JobJsonService.Load(fieldDir, taskName)
                  ?? throw new InvalidOperationException(
                      $"Job '{taskName}' not found in field '{fieldName}'");

        // Decision #3: resume re-opens the same job rather than creating a
        // continuation. EndedAt clears, status flips back to InProgress.
        job.Status = JobStatus.InProgress;
        job.EndedAt = null;
        job.LastOpenedAt = DateTime.Now;

        JobJsonService.Save(job, fieldDir);
        SetActiveJob(job);
    }

    public void CloseCurrentJob(JobStatus closingStatus = JobStatus.Done)
    {
        if (ActiveJob == null) return;

        var fieldDir = ResolveFieldDir(ActiveJob.FieldName);
        if (fieldDir == null)
        {
            // Field directory disappeared while a job was active — clear
            // active state but don't try to write a phantom job.json.
            SetActiveJob(null);
            return;
        }

        ActiveJob.Status = closingStatus;
        ActiveJob.EndedAt = DateTime.Now;
        JobJsonService.Save(ActiveJob, fieldDir);

        SetActiveJob(null);
    }

    public bool DeleteJob(string fieldName, string taskName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("fieldName must be set", nameof(fieldName));
        if (string.IsNullOrWhiteSpace(taskName))
            throw new ArgumentException("taskName must be set", nameof(taskName));

        if (ActiveJob != null
            && string.Equals(ActiveJob.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveJob.TaskName, taskName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot delete '{taskName}' while it is the active job. Close the field first.");
        }

        var fieldDir = ResolveFieldDir(fieldName);
        if (fieldDir == null) return false;

        var jobDir = JobJsonService.JobDirectory(fieldDir, taskName);
        if (!Directory.Exists(jobDir)) return false;

        Directory.Delete(jobDir, recursive: true);
        return true;
    }

    public void RecordRig(JobRig rig)
    {
        if (ActiveJob == null || rig.SameAs(ActiveJob.Rig)) return;
        var fieldDir = ResolveFieldDir(ActiveJob.FieldName);
        if (fieldDir == null) return;
        ActiveJob.Rig = rig;
        JobJsonService.Save(ActiveJob, fieldDir); // not to a newer build's job (Save leaves it)
    }

    public void SuspendCurrentJob()
    {
        if (ActiveJob == null) return;

        var fieldDir = ResolveFieldDir(ActiveJob.FieldName);
        if (fieldDir == null)
        {
            SetActiveJob(null);
            return;
        }

        // Bump LastOpenedAt so the cross-field history reflects this most-recent
        // touch. Status and EndedAt deliberately untouched — see Decision #3.
        ActiveJob.LastOpenedAt = DateTime.Now;
        JobJsonService.Save(ActiveJob, fieldDir);

        SetActiveJob(null);
    }

    private void SetActiveJob(Job? job)
    {
        ActiveJob = job;
        ActiveJobChanged?.Invoke(this, job);
    }

    private string? ResolveFieldDir(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return null;
        var root = _fieldsRootProvider();
        if (string.IsNullOrEmpty(root)) return null;
        var dir = Path.Combine(root, fieldName);
        return Directory.Exists(dir) ? dir : null;
    }

    private static JobSummary ToSummary(Job j) => new(
        j.Id, j.FieldName, j.TaskName, j.WorkType, j.Notes,
        j.StartedAt, j.EndedAt, j.LastOpenedAt, j.Status);

    /// <summary>
    /// A job's name is its folder's name (<c>jobs/&lt;name&gt;/</c>), so the name typed for a new job is
    /// made one a folder can have on every computer: each character Windows refuses in a folder name
    /// (<c>&lt; &gt; : " / \ | ? *</c> and control characters) becomes '-', a dot or space at the end
    /// is dropped, a name longer than <see cref="MaxTaskNameLength"/> is cut there, and a name Windows
    /// keeps for a device (CON, NUL, COM1...) gets a '_' in front. Empty after
    /// that: the default name is used. A job typed "Spraying 15:51" could not be saved on Windows:
    /// the field opened with no job, said only in the log, and the coverage driven was never saved
    /// (a test run on Windows, 2026-11-15). On Linux a '/' made a folder inside the jobs folder.
    /// </summary>
    internal static string SafeTaskName(string name)
    {
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]) || "<>:\"/\\|?*".IndexOf(chars[i]) >= 0)
                chars[i] = '-';
        var safe = new string(chars);
        if (safe.Length > MaxTaskNameLength)
            safe = safe[..(char.IsHighSurrogate(safe[MaxTaskNameLength - 1]) ? MaxTaskNameLength - 1 : MaxTaskNameLength)];
        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0) return safe;
        var stem = safe.Split('.')[0].TrimEnd(' ');
        if (WindowsDeviceNames.Contains(stem)) safe = "_" + safe; // "LPT1.job" is the device too
        return safe;
    }

    /// <summary>
    /// The longest typed job name kept. A folder name may have 255 characters, but the job's folder
    /// sits under the data folder, the field and <c>jobs/</c>, with <c>coverage/</c> and its tiles
    /// inside: 100 keeps the whole path under Windows' 260 with a long field name.
    /// </summary>
    internal const int MaxTaskNameLength = 100;

    // The names Windows keeps for devices (Naming Files, Paths, and Namespaces), with or without an
    // extension: COM and LPT with 0-9 and the superscripts 1-3, and the console's CONIN$ / CONOUT$.
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private static string EnsureUnique(string fieldDir, string requestedName)
    {
        if (!JobJsonService.Exists(fieldDir, requestedName))
            return requestedName;

        for (int i = 2; i < int.MaxValue; i++)
        {
            var candidate = $"{requestedName}_{i}";
            if (!JobJsonService.Exists(fieldDir, candidate))
                return candidate;
        }
        throw new InvalidOperationException("Unable to find unique task name");
    }

    private static string BuildUniqueDefaultTaskName(string fieldDir, string workType, DateTime when)
    {
        var prefix = $"{when:yyyy-MM-dd}";
        var slug = NormalizeWorkTypeForFileName(workType);
        var baseName = string.IsNullOrEmpty(slug) ? prefix : $"{prefix}_{slug}";
        return EnsureUnique(fieldDir, baseName);
    }

    private static string NormalizeWorkTypeForFileName(string? workType)
    {
        if (string.IsNullOrWhiteSpace(workType)) return string.Empty;
        // Lowercase, spaces → underscores, strip filesystem-unsafe chars.
        var lowered = workType.Trim().ToLowerInvariant();
        var chars = new char[lowered.Length];
        for (int i = 0; i < lowered.Length; i++)
        {
            var c = lowered[i];
            chars[i] = c == ' '
                ? '_'
                : (char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        return new string(chars);
    }
}
