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

namespace AgValoniaGPS.Models.Job;

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
