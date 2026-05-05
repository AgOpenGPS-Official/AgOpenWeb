// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
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
using System.IO;
using System.Linq;
using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services.Fields;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services;

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

        var resolvedName = string.IsNullOrWhiteSpace(taskName)
            ? BuildUniqueDefaultTaskName(fieldDir, workType, DateTime.Now)
            : EnsureUnique(fieldDir, taskName!);

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
