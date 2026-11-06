// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;

namespace AgOpenWeb.Models;

/// <summary>
/// A field's name is its folder's name under the Fields root, so it must be one folder name on
/// every head: no path separators or "..", which put the folder somewhere else (the field never
/// appears in the list), and nothing Windows refuses, so a field copied from a tablet still
/// opens on a Windows box. AgOpenGPS strips such characters as they are typed (AgIO's
/// CGLM.fileRegex); the web client can't be relied on for that, so the host checks every name.
/// </summary>
public static class FieldNames
{
    private static readonly char[] Forbidden = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] Reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The name to use, trimmed (and without the trailing dots Windows drops), or false with the
    /// reason to show the operator.
    /// </summary>
    public static bool TryCheck(string? raw, out string name, out string reason)
    {
        name = (raw ?? string.Empty).Trim().TrimEnd('.', ' ');
        reason = string.Empty;

        if (name.Length == 0)
        {
            reason = "Please enter a field name";
            return false;
        }
        if (name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl))
        {
            reason = "A field name can't contain \\ / : * ? \" < > |";
            return false;
        }
        string stem = name.Split('.')[0].Trim();
        if (Reserved.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"\"{name}\" is a name Windows keeps for itself; please pick another";
            return false;
        }
        if (name.Length > 120)
        {
            reason = "Please use a shorter field name (120 characters at most)";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The field folder under <paramref name="fieldsDirectory"/> with this name, or with a name
    /// that differs from it only in capitals; null when there is none. Windows and macOS keep
    /// "Vinha" and "vinha" in one folder, Linux in two, and the field list keys fields by name
    /// ignoring capitals: a new field never takes a name that is already there in other capitals.
    /// </summary>
    public static string? ExistingTwin(string? fieldsDirectory, string name)
    {
        if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory)) return null;
        string? twin = null;
        foreach (var dir in Directory.EnumerateDirectories(fieldsDirectory))
        {
            var existing = Path.GetFileName(dir);
            if (string.Equals(existing, name, StringComparison.Ordinal)) return existing;
            if (twin == null && string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) twin = existing;
        }
        return twin;
    }

    /// <summary>The refusal for a taken name, naming the existing field when only its capitals differ.</summary>
    public static string ExistsMessage(string name, string existing) =>
        string.Equals(name, existing, StringComparison.Ordinal)
            ? $"Field '{name}' already exists"
            : $"Field '{existing}' already exists; '{name}' differs from it only in capitals";
}
