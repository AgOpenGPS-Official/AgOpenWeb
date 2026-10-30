// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AgOpenWeb.Services.BugReport;

/// <summary>
/// Screenshots and files the reporter adds to a bug report dump after creating it. They go
/// into the zip under <c>attachments/</c>, so they travel with the dump: only the maintainers
/// can download them, and the public issue shows just how many there are. The limits keep a
/// report well under the bug report service's 50 MB cap.
/// </summary>
public static class BugReportAttachments
{
    public const string Folder = "attachments/";
    public const int MaxFiles = 10;
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxTotalBytes = 25L * 1024 * 1024;

    /// <summary>Why a file was not added. The web client words these.</summary>
    public enum Refusal { None, Empty, TooLarge, TotalTooLarge, TooMany }

    public sealed record Attachment(string Name, long Size);

    /// <summary>The attachments in a dump, in the order they were added.</summary>
    public static IReadOnlyList<Attachment> List(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return Attachments(zip);
    }

    /// <summary>Add one file. A name already in the dump gets " (2)", " (3)" … before its extension.</summary>
    public static (Refusal Refusal, IReadOnlyList<Attachment> Files) Add(string zipPath, string name, byte[] bytes)
    {
        var files = List(zipPath);
        var refusal = bytes.Length == 0 ? Refusal.Empty
            : bytes.Length > MaxFileBytes ? Refusal.TooLarge
            : files.Count >= MaxFiles ? Refusal.TooMany
            : files.Sum(f => f.Size) + bytes.Length > MaxTotalBytes ? Refusal.TotalTooLarge
            : Refusal.None;
        if (refusal != Refusal.None) return (refusal, files);

        var safe = UniqueName(SafeName(name), files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        // Photos and videos are compressed already; deflating them again only costs time.
        var level = IsCompressed(safe) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        using (var s = zip.CreateEntry(Folder + safe, level).Open())
            s.Write(bytes, 0, bytes.Length);
        return (Refusal.None, List(zipPath)); // sizes read back once the zip is written
    }

    /// <summary>Take a file back out. Unknown names change nothing.</summary>
    public static IReadOnlyList<Attachment> Remove(string zipPath, string name)
    {
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            zip.GetEntry(Folder + name)?.Delete();
        return List(zipPath);
    }

    /// <summary>A file name safe as a zip entry on every OS: no folders, no control or
    /// reserved characters, at most 100 characters (the extension kept).</summary>
    public static string SafeName(string name)
    {
        var bare = name.Replace('\\', '/');
        bare = bare[(bare.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(bare.Length);
        foreach (var c in bare)
            sb.Append(char.IsControl(c) || "<>:\"|?*".Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimStart('.');
        if (s.Length == 0) s = "attachment";
        if (s.Length > 100)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 10) ext = "";
            s = s[..(100 - ext.Length)] + ext;
        }
        return s;
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static bool IsCompressed(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".heic" or ".webp"
            or ".gif" or ".mp4" or ".mov" or ".webm" or ".zip";

    private static List<Attachment> Attachments(ZipArchive zip) =>
        zip.Entries
            .Where(e => e.FullName.StartsWith(Folder, StringComparison.Ordinal) && e.FullName.Length > Folder.Length)
            .Select(e => new Attachment(e.FullName[Folder.Length..], e.Length))
            .ToList();
}
