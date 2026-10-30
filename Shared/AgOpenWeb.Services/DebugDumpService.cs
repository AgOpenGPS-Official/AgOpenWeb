// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Logging;

namespace AgOpenWeb.Services;

/// <summary>
/// Creates a debug dump zip containing logs, settings, field data, and runtime state.
/// For bug reporting and diagnostics.
/// </summary>
public class DebugDumpService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// The live parts of a dump, read at one moment: settings, configuration, runtime state,
    /// logs and the GPS recorder, as text. <see cref="Capture"/> reads them on the host loop,
    /// where that state lives; <see cref="WriteDump"/> does the slow part (field files, coverage
    /// tiles, compression) on any thread, so making a report doesn't stall the loop, the
    /// simulator or guidance (a dump held a slow tablet's loop for 1-4 s and set off GPS lost).
    /// </summary>
    public sealed class DumpSnapshot
    {
        internal List<(string Name, string Text)> Entries { get; } = new();
        internal string? FieldDirectory { get; init; }
        internal string? ProfileName { get; init; }
    }

    /// <summary>Read the dump's live parts (see <see cref="DumpSnapshot"/>). Quick; call it on the host loop.</summary>
    public static DumpSnapshot Capture(
        ISettingsService settingsService,
        RuntimeStateCapture runtime,
        ConfigurationStore configStore,
        string? ntripRtcmReport = null)
    {
        string? profileName = null;
        try { profileName = configStore.ActiveVehicleProfileName; } catch { }
        var snap = new DumpSnapshot { FieldDirectory = runtime.FieldDirectory, ProfileName = profileName };
        var entries = snap.Entries;

        // 1. System info
        entries.Add(("system_info.txt", BuildSystemInfo()));

        // 2. App settings
        try
        {
            var settingsJson = RedactSecrets(JsonSerializer.Serialize(settingsService.Settings, JsonOptions));
            entries.Add(("appsettings.json", settingsJson));
        }
        catch (Exception ex)
        {
            entries.Add(("appsettings_error.txt", ex.ToString()));
        }

        // 3. Configuration store snapshot
        try
        {
            entries.Add(("configuration.json", BuildConfigSnapshot(configStore)));
        }
        catch (Exception ex)
        {
            entries.Add(("configuration_error.txt", ex.ToString()));
        }

        // 4. Runtime state snapshot
        try
        {
            entries.Add(("runtime_state.json", runtime.Json));
        }
        catch (Exception ex)
        {
            entries.Add(("state_error.txt", ex.ToString()));
        }

        // 5. Log entries
        try
        {
            entries.Add(("logs.txt", BuildLogDump()));
        }
        catch (Exception ex)
        {
            entries.Add(("logs_error.txt", ex.ToString()));
        }

        // 5a. What the NTRIP caster sent, by RTCM message, and the receiver's differential age.
        if (!string.IsNullOrEmpty(ntripRtcmReport))
            entries.Add(("ntrip_rtcm.txt", ntripRtcmReport));

        // 5b. GPS data recorder (last 5 minutes of position/guidance/YouTurn/heading inputs)
        try
        {
            var gpsCsv = Logging.GpsDataRecorder.Instance.ExportCsv();
            if (gpsCsv.Length > 50) // more than just the header
                entries.Add(("gps_data_log.csv", gpsCsv));
        }
        catch (Exception ex)
        {
            entries.Add(("gps_data_log_error.txt", ex.ToString()));
        }

        // 5c. Last observed YouTurn path — forensic sidecar so we can
        // replay the exact production omega geometry in a test fixture
        // when diagnosing drive-over. The recorder retains the most recent
        // non-null TurnPath; if the operator captured the dump during or
        // immediately after a U-turn, that path is the offender.
        try
        {
            var lastPath = Logging.GpsDataRecorder.Instance.GetLastTurnPath();
            if (lastPath != null && lastPath.Count > 0)
            {
                var pathDtos = new object[lastPath.Count];
                for (int i = 0; i < lastPath.Count; i++)
                {
                    var p = lastPath[i];
                    pathDtos[i] = new { e = p.Easting, n = p.Northing, h = p.Heading };
                }
                entries.Add(("turn_path.json", JsonSerializer.Serialize(pathDtos, JsonOptions)));
            }
        }
        catch (Exception ex)
        {
            entries.Add(("turn_path_error.txt", ex.ToString()));
        }

        return snap;
    }

    /// <summary>
    /// Create a debug dump zip file containing all diagnostic information.
    /// Returns the path to the created zip file.
    /// </summary>
    public static string CreateDump(
        ISettingsService settingsService,
        RuntimeStateCapture runtime,
        ConfigurationStore configStore,
        string? additionalNotes = null,
        byte[]? screenshotPng = null,
        string? outputDirectory = null,
        string filePrefix = "debug_dump",
        IReadOnlyList<string>? userAttachments = null,
        string? activeJobTaskName = null,
        string? ntripRtcmReport = null) =>
        WriteDump(Capture(settingsService, runtime, configStore, ntripRtcmReport),
            additionalNotes, screenshotPng, outputDirectory, filePrefix, userAttachments, activeJobTaskName);

    /// <summary>
    /// Write a captured dump to its zip with the field's files, the active job's coverage tiles,
    /// the screenshot, notes and attachments. Reads only the disk and the snapshot: any thread.
    /// Returns the path to the created zip file.
    /// </summary>
    public static string WriteDump(
        DumpSnapshot snapshot,
        string? additionalNotes = null,
        byte[]? screenshotPng = null,
        string? outputDirectory = null,
        string filePrefix = "debug_dump",
        IReadOnlyList<string>? userAttachments = null,
        string? activeJobTaskName = null)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var dumpDir = outputDirectory
            ?? Path.Combine(Path.GetTempPath(), "AgOpenWeb", "dumps");
        Directory.CreateDirectory(dumpDir);
        var zipPath = Path.Combine(dumpDir, $"{filePrefix}_{timestamp}.zip");

        using var zipStream = File.Create(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // 1-5c. The live parts, as captured.
        foreach (var (name, text) in snapshot.Entries)
            AddTextEntry(archive, name, text);

        // 6. Current field files (if a field is open)
        try
        {
            var fieldDir = snapshot.FieldDirectory;
            if (!string.IsNullOrEmpty(fieldDir) && Directory.Exists(fieldDir))
            {
                foreach (var file in Directory.GetFiles(fieldDir))
                {
                    var fileName = Path.GetFileName(file);
                    // Skip large binary files
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".bin" && new FileInfo(file).Length > 5_000_000)
                        continue;

                    AddFileEntry(archive, $"field/{fileName}", file);
                }
            }
        }
        catch (Exception ex)
        {
            AddTextEntry(archive, "field_error.txt", ex.ToString());
        }

        // 6b. The active job's folder: its coverage tiles (worked area) and job file,
        // so coverage display bugs can be reproduced from the dump (#175). The caller
        // saves coverage first; the tiles on disk are otherwise up to an autosave old.
        try
        {
            var fieldDir = snapshot.FieldDirectory;
            if (!string.IsNullOrEmpty(fieldDir) && !string.IsNullOrWhiteSpace(activeJobTaskName))
            {
                var jobDir = Path.Combine(fieldDir, "jobs", activeJobTaskName);
                if (Directory.Exists(jobDir))
                {
                    foreach (var file in Directory.GetFiles(jobDir, "*", SearchOption.AllDirectories))
                    {
                        if (new FileInfo(file).Length > 20_000_000) continue;
                        var relative = Path.GetRelativePath(fieldDir, file).Replace('\\', '/');
                        AddFileEntry(archive, $"field/{relative}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddTextEntry(archive, "job_error.txt", ex.ToString());
        }

        // 7. Current vehicle profile
        if (!string.IsNullOrEmpty(snapshot.ProfileName))
            AddTextEntry(archive, "active_profile_name.txt", snapshot.ProfileName);

        // 8. Screenshot
        if (screenshotPng != null && screenshotPng.Length > 0)
        {
            try
            {
                var entry = archive.CreateEntry("screenshot.png", CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                entryStream.Write(screenshotPng, 0, screenshotPng.Length);
            }
            catch (Exception ex)
            {
                AddTextEntry(archive, "screenshot_error.txt", ex.ToString());
            }
        }

        // 9. User notes
        if (!string.IsNullOrEmpty(additionalNotes))
        {
            AddTextEntry(archive, "user_notes.txt", additionalNotes);
        }

        // 10. User-attached files (screenshots, videos, logs, etc.)
        if (userAttachments != null)
        {
            foreach (var filePath in userAttachments)
            {
                try
                {
                    if (File.Exists(filePath))
                        AddFileEntry(archive, $"attachments/{Path.GetFileName(filePath)}", filePath);
                }
                catch (Exception ex)
                {
                    AddTextEntry(archive, $"attachments/{Path.GetFileName(filePath)}_error.txt", ex.ToString());
                }
            }
        }

        return zipPath;
    }

    /// <summary>
    /// Append user-supplied notes and attachments to an existing dump zip,
    /// then move it to its final location with a title-based filename.
    /// Used by the Bug Report flow so the state-snapshot zip can be created
    /// the moment the operator presses the Bug Report button (capturing app
    /// state at the time the bug occurred), and only the user-visible parts
    /// are appended after they finish typing in the dialog.
    /// </summary>
    /// <param name="sourceZipPath">Path to the existing dump zip (typically
    /// from <see cref="CreateDump"/> with no notes/attachments). Deleted on
    /// success; left in place if any step throws.</param>
    /// <param name="outputDirectory">Final destination folder. Created if
    /// missing.</param>
    /// <param name="filePrefix">Final file's prefix; the timestamp suffix
    /// is added here, not taken from the source zip's name.</param>
    /// <param name="notes">Optional user-typed notes (becomes
    /// <c>user_notes.txt</c> inside the zip).</param>
    /// <param name="userAttachments">Optional list of file paths to attach
    /// under <c>attachments/</c> inside the zip.</param>
    /// <returns>The final zip path.</returns>
    public static string FinalizeBugReport(
        string sourceZipPath,
        string outputDirectory,
        string filePrefix,
        string? notes,
        IReadOnlyList<string>? userAttachments)
    {
        Directory.CreateDirectory(outputDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var finalPath = Path.Combine(outputDirectory, $"{filePrefix}_{timestamp}.zip");

        File.Copy(sourceZipPath, finalPath, overwrite: false);

        using (var zipStream = File.Open(finalPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Update))
        {
            if (!string.IsNullOrEmpty(notes))
                AddTextEntry(archive, "user_notes.txt", notes);

            if (userAttachments != null)
            {
                foreach (var filePath in userAttachments)
                {
                    try
                    {
                        if (File.Exists(filePath))
                            AddFileEntry(archive, $"attachments/{Path.GetFileName(filePath)}", filePath);
                    }
                    catch (Exception ex)
                    {
                        AddTextEntry(archive, $"attachments/{Path.GetFileName(filePath)}_error.txt", ex.ToString());
                    }
                }
            }
        }

        try { File.Delete(sourceZipPath); } catch { /* best-effort temp cleanup */ }
        return finalPath;
    }

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();

        // App version + git hash from AssemblyInformationalVersion
        var assembly = typeof(DebugDumpService).Assembly;
        var infoVersion = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var fileVersion = assembly
            .GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>()
            ?.Version ?? "unknown";

        // Diagnostic dump is consumed by us / shared in bug reports —
        // pin every interpolation to InvariantCulture so timestamps and
        // numbers are unambiguous across reporters' locales.
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"App Version: {fileVersion}");
        sb.AppendLine(inv, $"Build Info: {infoVersion}");
        sb.AppendLine(inv, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(inv, $"OS: {Environment.OSVersion}");
        sb.AppendLine(inv, $"Runtime: {Environment.Version}");
        sb.AppendLine(inv, $"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine(inv, $"64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine(inv, $"Machine: {Environment.MachineName}");
        sb.AppendLine(inv, $"Processors: {Environment.ProcessorCount}");
        sb.AppendLine(inv, $"Working Set: {Environment.WorkingSet / 1024 / 1024}MB");
        return sb.ToString();
    }

    private static string BuildConfigSnapshot(ConfigurationStore store)
    {
        var snapshot = new
        {
            Vehicle = new
            {
                store.Vehicle.Name,
                store.Vehicle.Wheelbase,
                store.Vehicle.TrackWidth,
                store.Vehicle.AntennaHeight,
                store.Vehicle.AntennaPivot,
                store.Vehicle.AntennaOffset,
                store.Vehicle.MaxSteerAngle
            },
            Tool = new
            {
                store.Tool.Width,
                store.Tool.Overlap,
                store.Tool.Offset,
                store.Tool.HitchLength,
                store.Tool.TrailingHitchLength,
                store.Tool.IsToolTrailing,
                store.Tool.IsToolRearFixed,
                store.Tool.IsToolTBT,
                store.Tool.IsMultiColoredSections
            },
            Guidance = new
            {
                store.Guidance.GoalPointLookAheadHold,
                store.Guidance.StanleyHeadingErrorGain,
                store.Guidance.StanleyDistanceErrorGain,
                // Where and how a U-turn is laid out (#418: only the radius was here).
                store.Guidance.UTurnRadius,
                store.Guidance.UTurnExtension,
                store.Guidance.UTurnDistanceFromBoundary,
                store.Guidance.UTurnStyle,
                store.Guidance.UTurnSmoothing,
                store.Guidance.UTurnCompensation
            },
            // GPS / dual-antenna / heading settings (#157: a heading bug report had none of
            // these). ConnectionConfig also holds NTRIP and AgShare credentials — listed
            // field by field so those can never ride along.
            Gps = new
            {
                store.Connections.IsDualGps,
                store.Connections.DualHeadingOffset,
                store.Connections.DualReverseDistance,
                store.Connections.AutoDualFix,
                store.Connections.DualSwitchSpeed,
                store.Connections.GpsUpdateRate,
                store.Connections.UseRtk,
                store.Connections.MinGpsStep,
                store.Connections.FixToFixDistance,
                store.Connections.HeadingFusionWeight,
                store.Connections.ReverseDetection,
                store.Connections.HeadingSource,
                store.Connections.MinFixQuality,
                store.Connections.RtkLostAlarm,
                store.Connections.RtkLostAction,
                store.Connections.MaxDifferentialAge,
                store.Connections.MaxHdop
            },
            Ahrs = store.Ahrs,
            AutoSteer = store.AutoSteer,
            NumSections = store.NumSections,
            // Pass spacing and coverage width come from these, not Tool.Width.
            SectionWidthsCm = store.Tool.SectionWidths.Take(Math.Max(0, store.NumSections)).ToArray(),
            ActualToolWidth = store.ActualToolWidth,
            IsMetric = store.IsMetric,
            ActiveProfile = store.ActiveVehicleProfileName,
            ActiveToolProfile = store.ActiveToolProfileName
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    /// <summary>
    /// Bug reports get attached to public issues: blank out anything credential-shaped
    /// (NTRIP user/password, AgShare API key, …) that's set. Empty values stay empty so
    /// the report still shows whether one was configured.
    /// </summary>
    internal static string RedactSecrets(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is not System.Text.Json.Nodes.JsonObject obj) return json;
        foreach (var key in new List<string>(obj.Select(kv => kv.Key)))
        {
            bool secret = key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NtripUsername", StringComparison.OrdinalIgnoreCase);
            if (secret && obj[key] is System.Text.Json.Nodes.JsonValue v
                && v.TryGetValue<string>(out var str) && !string.IsNullOrEmpty(str))
                obj[key] = "REDACTED";
        }
        return obj.ToJsonString(JsonOptions);
    }

    private static string BuildLogDump()
    {
        var sb = new StringBuilder();
        var entries = LogStore.Instance.GetSnapshot();
        foreach (var entry in entries)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] {entry.Category}: {entry.Message}");
        }
        return sb.ToString();
    }

    // Already compressed: deflating these again takes the most time in a dump (a field's
    // background image is several MB) and saves nothing.
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".zip", ".gz", ".mp4", ".mov", ".webm",
    };

    private static void AddFileEntry(ZipArchive archive, string name, string path)
    {
        var level = StoredExtensions.Contains(Path.GetExtension(path)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        var entry = archive.CreateEntry(name, level);
        using var entryStream = entry.Open();
        using var fileStream = File.OpenRead(path);
        fileStream.CopyTo(entryStream);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
