// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenWeb.Services.Profile;

/// <summary>
/// What a vehicle or tool profile can be called. A profile is a file named after it in the
/// Vehicles or Tools folder, so a name is a file name on every head: no folder separators
/// (<c>Puma 180/CVX</c> went into a sub-folder the picker never lists, <c>../x</c> was
/// written outside the folder), none of the characters Windows refuses, and no two names
/// that differ only in case (one file on Windows and macOS, two on Linux and Android).
/// </summary>
public static class ProfileNames
{
    public const int MaxLength = 64;

    private static readonly char[] Forbidden = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Null when <paramref name="name"/> can name a new profile; otherwise why not.</summary>
    public static string? WhyNot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Type a name for the profile";
        if (name.Length > MaxLength)
            return $"A profile name can have at most {MaxLength} characters";
        if (name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl))
            return "A profile name can't contain / \\ : * ? \" < > |";
        if (name != name.Trim() || name.EndsWith('.') || name.StartsWith('.'))
            return "A profile name can't start or end with a space or a dot";
        // "<profile>.AutoSteer.json" is a vehicle's AutoSteer settings: a profile named
        // "Puma.AutoSteer" would overwrite Puma's, and the picker never lists it.
        if (name.EndsWith(".AutoSteer", StringComparison.OrdinalIgnoreCase))
            return "A profile name can't end with .AutoSteer";
        // Windows keeps these for devices, with any extension after them (CON.json is no file).
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem))
            return $"{stem} can't be a profile name: Windows keeps it for a device";
        return null;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The existing profile <paramref name="name"/> would be the same file as on Windows and
    /// macOS (equal ignoring case), or null.
    /// </summary>
    public static string? Twin(IEnumerable<string> existing, string name) =>
        existing.FirstOrDefault(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
}
