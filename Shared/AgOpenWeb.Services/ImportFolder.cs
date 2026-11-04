// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace AgOpenWeb.Services;

/// <summary>
/// The <c>Import</c> folder under the data root, where the user copies KML/KMZ files and
/// ISO-XML datasets for the From KML, From ISO-XML and Import KML Boundary pickers.
/// </summary>
public static class ImportFolder
{
    /// <summary><c>&lt;data root&gt;/Import</c> (honors <c>AGOPENWEB_DATA</c>, like Fields).</summary>
    public static string Path => System.IO.Path.Combine(AppDataRoot.Documents, "Import");

    /// <summary>The folder, created if missing, so the user finds where to put files
    /// without making it by hand.</summary>
    public static string Ensure()
    {
        var dir = Path;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A value that changes when a file or folder is added, removed or renamed anywhere under
    /// <paramref name="dir"/>: the folder that holds an entry gets a new modified time when the
    /// entry comes or goes. Cheap enough for every broadcast tick (one stat per folder, no file
    /// listing). 0 when the folder doesn't exist.
    /// </summary>
    public static long Stamp(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long h = Directory.GetLastWriteTimeUtc(dir).Ticks;
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                h = unchecked(h * 31 + Directory.GetLastWriteTimeUtc(sub).Ticks);
            return h;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // a folder removed mid-scan: the next tick reads it again
        }
    }
}
