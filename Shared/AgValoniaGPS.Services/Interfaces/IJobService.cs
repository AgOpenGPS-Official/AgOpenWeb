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
using AgValoniaGPS.Models.Job;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Per-session work tracking. A <see cref="Job"/> owns coverage paint,
/// section-log events, and notes for one work session against an
/// <see cref="AgValoniaGPS.Models.Field"/>; field geometry stays on the
/// field. See <c>Plans/FIELDS_AND_JOBS_PLAN.md</c> for the design.
/// </summary>
public interface IJobService
{
    /// <summary>
    /// Jobs for one field, ordered by <see cref="Job.LastOpenedAt"/>
    /// descending (most recently touched first).
    /// </summary>
    IReadOnlyList<JobSummary> ListJobs(string fieldName);

    /// <summary>
    /// Every job across every field, ordered by
    /// <see cref="Job.LastOpenedAt"/> descending. Powers the
    /// <c>ResumeTaskDialog</c> cross-field history.
    /// </summary>
    IReadOnlyList<JobSummary> ListAllJobs();

    /// <summary>
    /// Load one job's full metadata. Returns null if not found.
    /// </summary>
    Job? GetJob(string fieldName, string taskName);

    /// <summary>
    /// Create a new job, persist <c>job.json</c>, mark it active.
    /// </summary>
    /// <param name="fieldName">Parent field folder name.</param>
    /// <param name="workType">Free-text work type (Decision #1).</param>
    /// <param name="notes">Operator notes (multi-line allowed).</param>
    /// <param name="taskName">
    /// Optional explicit name. If null/empty, a default
    /// <c>YYYY-MM-DD_&lt;work_type&gt;</c> is generated and
    /// auto-bumped (<c>_2</c>, <c>_3</c>, …) on collision.
    /// </param>
    Job CreateJob(string fieldName, string workType, string notes, string? taskName = null);

    /// <summary>
    /// If an in-progress job exists for this field, resume the most
    /// recent one. Otherwise create a fresh job with default values
    /// (used by the M2 silent-path flow before the dialog UI lands).
    /// </summary>
    Job GetOrCreateDefaultJob(string fieldName);

    /// <summary>
    /// Suggestions for the New-Job WorkType autocomplete.
    /// <see cref="JobWorkTypeSuggestions.Seed"/> first, then distinct
    /// prior labels across all known jobs (case-insensitive, recency-ordered).
    /// </summary>
    IReadOnlyList<string> SuggestWorkTypes();

    /// <summary>
    /// Resume an existing job. Per Decision #3, this flips
    /// <see cref="Job.Status"/> back to <see cref="JobStatus.InProgress"/>,
    /// clears <see cref="Job.EndedAt"/>, bumps <see cref="Job.LastOpenedAt"/>,
    /// and persists. Coverage and section-log files are appended to,
    /// not replaced.
    /// </summary>
    void ResumeJob(string fieldName, string taskName);

    /// <summary>
    /// Close the active job: stamp <see cref="Job.EndedAt"/>, set status,
    /// persist, clear <see cref="ActiveJob"/>. No-op if no active job.
    /// </summary>
    void CloseCurrentJob(JobStatus closingStatus = JobStatus.Done);

    /// <summary>
    /// Persist the active job and clear <see cref="ActiveJob"/> without
    /// changing <see cref="Job.Status"/> or stamping
    /// <see cref="Job.EndedAt"/>. Used when a field is closed without an
    /// explicit operator "Close Job" action — the next open of the same
    /// field will resume the same in-progress job rather than creating a
    /// new default. No-op if no active job.
    /// </summary>
    void SuspendCurrentJob();

    /// <summary>
    /// Delete a job and all of its files (<c>job.json</c>, coverage,
    /// section log) from disk. Refuses to delete the currently
    /// <see cref="ActiveJob"/> — close or switch first.
    /// </summary>
    /// <returns>True if a job was deleted, false if it didn't exist.</returns>
    /// <exception cref="InvalidOperationException">Thrown when attempting
    /// to delete the active job.</exception>
    bool DeleteJob(string fieldName, string taskName);

    /// <summary>
    /// The job currently receiving coverage / section-log writes, or null
    /// if the user has only opened a field for view (Decision #2).
    /// </summary>
    Job? ActiveJob { get; }

    /// <summary>
    /// Fired whenever <see cref="ActiveJob"/> changes (including to null).
    /// </summary>
    event EventHandler<Job?>? ActiveJobChanged;
}
