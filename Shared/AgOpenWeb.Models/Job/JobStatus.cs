// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Job;

/// <summary>
/// Lifecycle state of a <see cref="Job"/>.
/// </summary>
/// <remarks>
/// Per the Fields &amp; Jobs plan (Decision #3), closing a job sets
/// <see cref="Done"/> but does not lock it: resuming flips back to
/// <see cref="InProgress"/> and appends to the same coverage / section log.
/// <see cref="Abandoned"/> is reserved for jobs explicitly marked as
/// non-resumable by the operator.
/// </remarks>
public enum JobStatus
{
    InProgress,
    Done,
    Abandoned
}
