// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;

namespace AgValoniaGPS.Services;

/// <summary>
/// Single source of truth for the <c>AgValoniaGPS</c> data root (fields, vehicle/tool
/// profiles, NTRIP profiles, app settings, persistent state). Every service resolves its
/// directories from here so the location is consistent and overridable.
///
/// Resolution order:
///  1. <c>AGOPENWEB_DATA</c> env var — the appliance / headless daemon sets this to a path
///     OUTSIDE the program directory (e.g. /var/lib/agopenweb) so a program update that
///     replaces /opt/agopenweb can never wipe the operator's fields/tools/config.
///  2. <see cref="Environment.SpecialFolder.MyDocuments"/>, then Personal (Desktop/mobile).
///  3. <c>$HOME</c> (*nix) or LocalApplicationData — an absolute per-user fallback.
///
/// It NEVER falls back to the current working directory: on the daemon the CWD is the
/// program dir, which is exactly what gets wiped on upgrade. An empty resolution there
/// used to yield a RELATIVE "AgValoniaGPS" path under the CWD → data inside /opt → lost.
/// </summary>
public static class AppDataRoot
{
    public const string EnvVar = "AGOPENWEB_DATA";

    /// <summary>The <c>AgValoniaGPS</c> data root. Callers append their subfolder
    /// (e.g. <c>Path.Combine(AppDataRoot.Documents, "Fields")</c>).</summary>
    public static string Documents => Path.Combine(BaseDirectory(), "AgValoniaGPS");

    private static string BaseDirectory()
    {
        var explicitRoot = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot;

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs))
            docs = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (string.IsNullOrEmpty(docs))
            docs = Environment.GetEnvironmentVariable("HOME"); // *nix when SpecialFolder is empty
        if (string.IsNullOrEmpty(docs))
            docs = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Absolute, user-scoped, never the program/working directory. If even
        // LocalApplicationData is empty (pathological), fall to the system temp dir
        // rather than a relative path that would resolve under the CWD.
        if (string.IsNullOrEmpty(docs))
            docs = Path.GetTempPath();

        return docs;
    }
}
