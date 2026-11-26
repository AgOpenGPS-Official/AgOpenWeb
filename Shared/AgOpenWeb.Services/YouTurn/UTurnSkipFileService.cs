// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text.Json;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A field's U-turn skip choice, <c>UTurnSkip.json</c> in the field folder: the skip button's
/// mode (0 Normal, 1 Alternative, 2 Ignore worked tracks) and the operator's own row count.
/// Written when either changes with the field open and when the field is saved (close, exit),
/// read when the field opens. Before, the choice lived only in memory: closing the app lost it,
/// and the next field opened got the last one used. A field without the file opens in Normal
/// with no rows skipped, as a fresh start of the app did.
/// </summary>
public static class UTurnSkipFileService
{
    public const string FileName = "UTurnSkip.json";

    /// <summary>The skip button's mode and the row count the operator picked (0-9).</summary>
    public readonly record struct Choice(int Mode, int Rows)
    {
        public static readonly Choice Normal = new(0, 0);

        /// <summary>Mode 0-2 and rows 0-9, as the button and the row cycle allow.</summary>
        public Choice Clamped() => new(Math.Clamp(Mode, 0, 2), Math.Clamp(Rows, 0, 9));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ChoiceDto
    {
        public int Mode { get; set; }
        public int Rows { get; set; }
    }

    /// <summary>Write the choice; a temporary file first, so a power cut mid-write leaves the
    /// old file whole. Normal for a field without the file writes nothing: a field whose choice
    /// was never changed stays without one. Returns false when the folder is gone or not writable.</summary>
    public static bool Save(string fieldDirectory, Choice choice)
    {
        if (string.IsNullOrEmpty(fieldDirectory) || !Directory.Exists(fieldDirectory)) return false;
        try
        {
            var c = choice.Clamped();
            var path = Path.Combine(fieldDirectory, FileName);
            if (c == Choice.Normal && !File.Exists(path)) return true;
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new ChoiceDto { Mode = c.Mode, Rows = c.Rows }, Options));
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[UTurnSkip] Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The field's saved choice; <see cref="Choice.Normal"/> when the file is absent
    /// or unreadable, so a previous field's choice doesn't carry over.</summary>
    public static Choice Load(string fieldDirectory)
    {
        if (string.IsNullOrEmpty(fieldDirectory)) return Choice.Normal;
        var path = Path.Combine(fieldDirectory, FileName);
        if (!File.Exists(path)) return Choice.Normal;
        try
        {
            var dto = JsonSerializer.Deserialize<ChoiceDto>(File.ReadAllText(path), Options);
            return dto == null ? Choice.Normal : new Choice(dto.Mode, dto.Rows).Clamped();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[UTurnSkip] Load failed: {ex.Message}");
            return Choice.Normal;
        }
    }
}
