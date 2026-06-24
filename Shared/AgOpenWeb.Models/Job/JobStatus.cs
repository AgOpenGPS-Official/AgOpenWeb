// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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
