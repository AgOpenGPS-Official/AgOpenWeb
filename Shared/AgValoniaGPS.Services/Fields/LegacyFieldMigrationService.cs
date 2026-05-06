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
using System.IO;
using AgValoniaGPS.Models.Job;

namespace AgValoniaGPS.Services.Fields;

/// <summary>
/// Wraps existing per-field coverage into a synthetic <c>imported-*</c>
/// job so the per-job coverage layout introduced by #349 has somewhere
/// to put data that pre-dates the Field/Job split.
/// </summary>
/// <remarks>
/// Idempotent: skips if <c>&lt;field&gt;/jobs/</c> already exists. Run
/// once on first open of a legacy field; safe to call repeatedly.
/// </remarks>
public static class LegacyFieldMigrationService
{
    /// <summary>
    /// Coverage file names written by <c>CoverageMapService</c> in the
    /// pre-#349 world. Migration relocates whichever of these are present
    /// into the imported job folder; absent files are silently skipped.
    /// </summary>
    private static readonly string[] LegacyCoverageFiles =
    {
        "coverage_detect.bin",   // RLE-compressed detection bits (COVD)
        "coverage_disp.bin",     // section display bitmap
        "Sections.txt"           // legacy AgOpenGPS quad strips
    };

    /// <summary>
    /// If <paramref name="fieldDirectory"/> contains legacy coverage but
    /// no <c>jobs/</c> folder, wrap that coverage into a single
    /// <c>jobs/imported-&lt;mtime&gt;/</c> job and return true. Returns
    /// false (no-op) if migration is unnecessary.
    /// </summary>
    public static bool MigrateIfNeeded(string fieldDirectory)
    {
        if (string.IsNullOrWhiteSpace(fieldDirectory))
            throw new ArgumentException("fieldDirectory must be set", nameof(fieldDirectory));
        if (!Directory.Exists(fieldDirectory))
            return false;

        // Idempotence guard. The presence of jobs/ means this field has
        // already been through migration (or was created post-#349).
        if (Directory.Exists(JobJsonService.JobsRoot(fieldDirectory)))
            return false;

        var present = FindLegacyCoverageFiles(fieldDirectory);
        if (present.Length == 0)
            return false;

        var mtime = GetMaxMTime(present, fieldDirectory);
        var taskName = BuildImportedTaskName(mtime);
        var jobDir = JobJsonService.JobDirectory(fieldDirectory, taskName);

        Directory.CreateDirectory(jobDir);

        foreach (var src in present)
        {
            var dst = Path.Combine(jobDir, Path.GetFileName(src));
            File.Move(src, dst);
        }

        // Write job.json LAST. If the process dies mid-move, the field
        // is in a degraded but recoverable state (some coverage in
        // jobs/, no job.json yet); a manual cleanup can finish the job
        // record. Writing job.json first would mark the job as "done"
        // before its coverage was actually relocated.
        var fieldName = Path.GetFileName(fieldDirectory.TrimEnd(Path.DirectorySeparatorChar))
                        ?? string.Empty;
        var job = new Job
        {
            FieldName = fieldName,
            TaskName = taskName,
            WorkType = "imported",
            Notes = "Imported from legacy field",
            StartedAt = mtime,
            EndedAt = mtime,
            LastOpenedAt = mtime,
            Status = JobStatus.Done
        };
        JobJsonService.Save(job, fieldDirectory);

        return true;
    }

    private static string[] FindLegacyCoverageFiles(string fieldDirectory)
    {
        var found = new System.Collections.Generic.List<string>();
        foreach (var name in LegacyCoverageFiles)
        {
            var path = Path.Combine(fieldDirectory, name);
            if (File.Exists(path)) found.Add(path);
        }
        return found.ToArray();
    }

    private static DateTime GetMaxMTime(string[] files, string fieldDirectory)
    {
        var max = DateTime.MinValue;
        foreach (var f in files)
        {
            var t = File.GetLastWriteTime(f);
            if (t > max) max = t;
        }
        if (max == DateTime.MinValue)
            max = Directory.GetLastWriteTime(fieldDirectory);
        return max;
    }

    private static string BuildImportedTaskName(DateTime mtime) =>
        $"imported-{mtime:yyyy-MM-dd_HHmmss}";
}
