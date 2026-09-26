// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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
using System.Linq;
using AgOpenWeb.Services;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// Single entry point for pointing AGOPENWEB_DATA at a location Android will let us use
/// without root or any user-granted permission, on every supported version (API 23+).
///
/// MUST be called from both <c>MainActivity.OnCreate</c> and <c>BackendService.OnCreate</c>,
/// before anything touches <see cref="AppDataRoot.Documents"/> — a sticky service restart can
/// bring BackendService back up without MainActivity ever running, and it still needs to
/// resolve the same folder. The env var is set once per process and never changed afterward,
/// so a mid-life move that would strand already-loaded data can't happen.
///
/// On first run after upgrading from the old MyDocuments-based layout, an existing
/// MyDocuments/AgOpenWeb folder is copied (never moved — a failure part-way through can't
/// lose the source) into the new root, then a marker file stops it running again.
/// </summary>
internal static class AndroidDataRoot
{
    private const string MarkerFileName = ".migrated_external_files_v1";
    private static bool _initialized;

    public static void Initialize(global::Android.Content.Context context)
    {
        if (_initialized) return; // idempotent per process
        _initialized = true;

        try
        {
            // Capture where AppDataRoot resolves to BEFORE the env var is set, i.e. exactly
            // the pre-migration location every existing install has been using. This defers
            // to AppDataRoot's own resolution instead of guessing at Android internals here.
            var legacyAgOpenWeb = AppDataRoot.Documents;

            var external = context.GetExternalFilesDir(null)?.AbsolutePath;
            if (string.IsNullOrEmpty(external))
                return; // leave AppDataRoot's own fallback chain in charge

            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, external);

            var newAgOpenWeb = Path.Combine(external, "AgOpenWeb");
            TryMigrateFromLegacyLocation(legacyAgOpenWeb, newAgOpenWeb, Path.Combine(external, MarkerFileName));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidDataRoot] Initialize failed: {ex.Message}");
        }
    }

    private static void TryMigrateFromLegacyLocation(string legacyAgOpenWeb, string newAgOpenWeb, string marker)
    {
        try
        {
            if (File.Exists(marker)) return; // already handled (migrated, or nothing was there)

            var newHasData = Directory.Exists(newAgOpenWeb) && Directory.EnumerateFileSystemEntries(newAgOpenWeb).Any();
            if (!newHasData && Directory.Exists(legacyAgOpenWeb))
            {
                CopyDirectory(legacyAgOpenWeb, newAgOpenWeb);
            }

            // Written whether or not there was anything to copy, so we don't keep re-checking
            // the (now-empty-of-relevance) legacy folder on every cold start.
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            // Deliberately do NOT write the marker here: if the copy failed partway through,
            // the next launch should retry rather than silently accepting a partial migration.
            System.Diagnostics.Debug.WriteLine($"[AndroidDataRoot] migration failed, will retry next launch: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
