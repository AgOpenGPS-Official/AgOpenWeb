// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Job;

/// <summary>
/// Lightweight projection of a <see cref="Job"/> for list rendering
/// (StartWorkSession Jobs grid, ResumeTask cross-field history).
/// Built from <c>job.json</c> without touching coverage or section-log
/// blobs.
/// </summary>
public sealed record JobSummary(
    Guid Id,
    string FieldName,
    string TaskName,
    string WorkType,
    string Notes,
    DateTime StartedAt,
    DateTime? EndedAt,
    DateTime LastOpenedAt,
    JobStatus Status);
