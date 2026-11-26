// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace AgOpenWeb.Services;

/// <summary>
/// Cross-platform "is the dev overlay turned on?" check. Reads once at startup;
/// users toggle by creating/removing the marker file and relaunching.
///
/// <para>
/// Hotkeys can't be used on iPad / Android, and we don't want a Settings entry
/// that a non-dev user could trip over — a file in the same Documents folder
/// AgOpenWeb already uses keeps the toggle out of the UI entirely.
/// </para>
///
/// <para>Marker file: <c>&lt;Documents&gt;/AgOpenWeb/.show_dev_overlay</c></para>
/// </summary>
public static class DevOverlayMarker
{
    public const string MarkerFileName = ".show_dev_overlay";

    /// <summary>
    /// Returns true when the marker file exists in the AgOpenWeb Documents
    /// folder. Safe to call before any service is wired; falls back to
    /// <c>false</c> on any I/O exception.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            var path = Path.Combine(AppDataRoot.Documents, MarkerFileName);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
