// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Job;

/// <summary>
/// One work session against a <see cref="Models.Field"/>.
/// Owns coverage paint, section-log events, and per-session notes.
/// Persistent geometry (boundary, headland, tracks, flags, elevation)
/// stays on the parent <see cref="Models.Field"/>.
/// </summary>
public class Job
{
    /// <summary>
    /// Stable identifier. Coverage and section-log files key off this on
    /// disk so renaming <see cref="TaskName"/> doesn't break references.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the parent field (folder name under FieldsRoot).
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// Operator-visible task label, default
    /// <c>YYYY-MM-DD_&lt;work_type&gt;[_&lt;vehicle_or_sim&gt;]</c>.
    /// Also used as the on-disk folder name under <c>jobs/</c>.
    /// User-editable.
    /// </summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text work type (Decision #1). Suggested values come from
    /// <see cref="JobWorkTypeSuggestions"/> plus the distinct set of
    /// prior <c>WorkType</c> values across jobs.
    /// </summary>
    public string WorkType { get; set; } = string.Empty;

    /// <summary>
    /// Operator notes. Multi-line. The <c>[Use Last]</c> button on the
    /// New-Job form copies notes from the most recent job for this field.
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Set when the job is closed; cleared when resumed (Decision #3).
    /// </summary>
    public DateTime? EndedAt { get; set; }

    public DateTime LastOpenedAt { get; set; } = DateTime.Now;

    public JobStatus Status { get; set; } = JobStatus.InProgress;

    /// <summary>
    /// Distance the vehicle traveled while this job was active, in meters.
    /// Computed from the per-job section log on close.
    /// </summary>
    public double DistanceTraveledMeters { get; set; }

    /// <summary>
    /// Area painted by coverage during this job, in hectares.
    /// Computed from the per-job coverage on close.
    /// </summary>
    public double AreaWorkedHectares { get; set; }

    /// <summary>
    /// Number of U-turns executed during this job.
    /// </summary>
    public int UTurnCount { get; set; }
}
