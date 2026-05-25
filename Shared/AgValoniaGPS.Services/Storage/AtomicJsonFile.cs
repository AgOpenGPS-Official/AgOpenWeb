// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Text.Json;

namespace AgValoniaGPS.Services.Storage;

/// <summary>
/// Result of how a config file was loaded, for the recovery UX.
/// </summary>
public enum LoadOutcome
{
    /// <summary>Neither the primary file nor a backup existed (true first run).</summary>
    Missing,

    /// <summary>Primary file parsed and validated cleanly.</summary>
    Ok,

    /// <summary>Primary file was damaged; value came from the <c>.bak</c> last-known-good copy.</summary>
    RecoveredFromBackup,

    /// <summary>Both primary and backup were damaged or absent; caller must use defaults.</summary>
    CorruptNoBackup,
}

/// <summary>Outcome of an <see cref="AtomicJsonFile.Read{T}"/> call.</summary>
public sealed class LoadResult<T>
{
    public LoadOutcome Outcome { get; init; }

    /// <summary>The deserialized value, or <c>null</c> for <see cref="LoadOutcome.Missing"/> / <see cref="LoadOutcome.CorruptNoBackup"/>.</summary>
    public T? Value { get; init; }

    /// <summary>Last-write time of the backup that was recovered from (only set for <see cref="LoadOutcome.RecoveredFromBackup"/>).</summary>
    public DateTime? BackupTimestamp { get; init; }

    public bool Loaded => Outcome is LoadOutcome.Ok or LoadOutcome.RecoveredFromBackup;
    public bool Recovered => Outcome is LoadOutcome.RecoveredFromBackup;
}

/// <summary>
/// Crash-safe JSON persistence. Embeds the embedded-flash discipline in one
/// place: an atomic write that can never leave a half-written primary file,
/// a <c>.bak</c> last-known-good copy, and a validated read that recovers from
/// the backup (quarantining the damaged file) when the primary is unreadable.
///
/// This protects against the "settings reset on shutdown" failure where a
/// power cut mid-<see cref="File.WriteAllText(string,string)"/> leaves a
/// truncated or zero-length file that then silently loads as defaults.
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>Suffix for the last-known-good backup kept alongside the primary file.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Suffix for the scratch file written before atomic promotion.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Serialize <paramref name="value"/>, verify it round-trips (so we never
    /// promote a string that can't be read back), then write it atomically,
    /// rolling the previous good copy into <c>{path}.bak</c>.
    /// </summary>
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(value, options);

        // Round-trip guard: if the just-serialized text can't be deserialized,
        // abort before touching the primary file rather than promote a payload
        // that would fail to load next startup.
        _ = JsonSerializer.Deserialize<T>(json, options)
            ?? throw new InvalidDataException(
                $"Serialized JSON for '{Path.GetFileName(path)}' round-tripped to null; refusing to write.");

        WriteAtomic(path, json);
    }

    /// <summary>
    /// Atomically write <paramref name="contents"/> to <paramref name="path"/>:
    /// flush a scratch file to physical media, then promote it, keeping the
    /// prior contents as <c>{path}.bak</c>. A crash at any point leaves either
    /// the old file or the new file intact — never a partial one.
    /// </summary>
    public static void WriteAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + TempSuffix;
        var bak = path + BackupSuffix;

        // 1. Write the scratch file and force OS buffers down to the media.
        //    Flush(flushToDisk: true) maps to fsync / FlushFileBuffers; without
        //    it a power loss can lose the bytes still sitting in the cache.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(contents);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        // 2. Promote the scratch file over the primary, preserving the prior
        //    good copy as the backup.
        Promote(tmp, path, bak);
    }

    private static void Promote(string tmp, string path, string bak)
    {
        if (!File.Exists(path))
        {
            // First-ever write: nothing to back up, just move into place.
            File.Move(tmp, path);
            return;
        }

        try
        {
            // File.Replace is atomic on Windows and POSIX and rolls the current
            // (known-good) primary into the backup in the same operation.
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Some filesystems (certain Android/exFAT mounts) reject Replace.
            // Fall back to a manual rotate: keep the current file as backup,
            // then move the scratch file into place.
            try { File.Copy(path, bak, overwrite: true); } catch { /* best-effort backup */ }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Read and deserialize <paramref name="path"/>. If the primary file is
    /// missing-bytes, empty, or unparsable, fall back to <c>{path}.bak</c> and
    /// (when <paramref name="quarantineOnFailure"/> is set) quarantine the
    /// damaged primary. An optional <paramref name="validate"/> predicate lets
    /// the caller reject a structurally-valid-but-nonsensical payload (treated
    /// the same as a parse failure).
    ///
    /// Pass <paramref name="quarantineOnFailure"/> = <c>false</c> for a
    /// read-only probe that must not move files aside (e.g. inspecting a
    /// profile before the user has decided whether to load it).
    /// </summary>
    public static LoadResult<T> Read<T>(
        string path,
        JsonSerializerOptions options,
        Func<T, bool>? validate = null,
        bool quarantineOnFailure = true)
    {
        var bak = path + BackupSuffix;
        bool primaryExists = File.Exists(path);
        bool backupExists = File.Exists(bak);

        if (!primaryExists && !backupExists)
            return new LoadResult<T> { Outcome = LoadOutcome.Missing };

        // Primary first.
        if (primaryExists && TryDeserialize(path, options, validate, out var primary))
            return new LoadResult<T> { Outcome = LoadOutcome.Ok, Value = primary };

        // Primary missing/damaged — try the last-known-good backup.
        if (backupExists && TryDeserialize(bak, options, validate, out var backup))
        {
            // Move the damaged primary aside (forensics) so the next save can
            // promote a fresh file and re-establish a clean backup.
            if (primaryExists && quarantineOnFailure)
                Quarantine(path);

            return new LoadResult<T>
            {
                Outcome = LoadOutcome.RecoveredFromBackup,
                Value = backup,
                BackupTimestamp = SafeLastWriteTime(bak),
            };
        }

        // Nothing usable. Quarantine whatever damaged primary exists.
        if (primaryExists && quarantineOnFailure)
            Quarantine(path);

        return new LoadResult<T> { Outcome = LoadOutcome.CorruptNoBackup };
    }

    private static bool TryDeserialize<T>(
        string path,
        JsonSerializerOptions options,
        Func<T, bool>? validate,
        out T? value)
    {
        value = default;
        try
        {
            var json = File.ReadAllText(path);

            // Truncated / zero-length files are the most common power-loss
            // outcome; treat them as corrupt rather than letting the
            // deserializer throw an opaque error.
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var parsed = JsonSerializer.Deserialize<T>(json, options);
            if (parsed is null)
                return false;

            if (validate is not null && !validate(parsed))
                return false;

            value = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = $"{path}.corrupt.{stamp}";
            File.Move(path, dest, overwrite: true);
        }
        catch
        {
            // Best-effort: if we can't rename it, leave it. The next successful
            // save overwrites the primary anyway.
        }
    }

    private static DateTime? SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return null; }
    }
}
