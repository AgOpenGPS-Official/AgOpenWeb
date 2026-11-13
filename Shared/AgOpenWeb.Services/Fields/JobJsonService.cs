// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgOpenWeb.Models.Job;

namespace AgOpenWeb.Services.Fields;

/// <summary>
/// Reads and writes <c>job.json</c> per-session metadata under
/// <c>&lt;FieldDirectory&gt;/jobs/&lt;TaskName&gt;/job.json</c>.
/// Coverage and section-log binaries live next to the json but are
/// owned by other services.
/// </summary>
public static class JobJsonService
{
    private const string JobsFolder = "jobs";
    private const string JobFileName = "job.json";

    /// <summary>
    /// The job.json version this build writes. A new member doesn't change it (a reader ignores
    /// it, a writer keeps it: <see cref="Job.UnknownProperties"/>); a change of meaning raises it.
    /// A job in a newer version, or whose coverage is (<see cref="Coverage.CoverageTileStore"/>),
    /// is listed but never written: <see cref="IsNewerFormat"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    public static string JobsRoot(string fieldDirectory) =>
        Path.Combine(fieldDirectory, JobsFolder);

    public static string JobDirectory(string fieldDirectory, string taskName) =>
        Path.Combine(JobsRoot(fieldDirectory), taskName);

    public static string JobFilePath(string fieldDirectory, string taskName) =>
        Path.Combine(JobDirectory(fieldDirectory, taskName), JobFileName);

    public static bool Exists(string fieldDirectory, string taskName) =>
        File.Exists(JobFilePath(fieldDirectory, taskName));

    /// <summary>
    /// List task names of every job in this field's <c>jobs/</c> folder.
    /// Order is filesystem-defined; callers should sort by metadata.
    /// </summary>
    public static IReadOnlyList<string> ListTaskNames(string fieldDirectory)
    {
        var root = JobsRoot(fieldDirectory);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, JobFileName)))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToArray();
    }

    /// <summary>
    /// Write a <c>job.json</c>, creating the
    /// <c>&lt;field&gt;/jobs/&lt;TaskName&gt;/</c> folder if needed.
    /// Caller owns deciding what <see cref="Job.TaskName"/> is — if the
    /// folder name needs to change, rename it and re-save.
    /// </summary>
    public static void Save(Job job, string fieldDirectory)
    {
        if (string.IsNullOrWhiteSpace(fieldDirectory))
            throw new ArgumentException("fieldDirectory must be set", nameof(fieldDirectory));
        if (string.IsNullOrWhiteSpace(job.TaskName))
            throw new ArgumentException("Job.TaskName must be set", nameof(job));

        var dir = JobDirectory(fieldDirectory, job.TaskName);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, JobFileName);
        if (SchemaVersionOf(path) > CurrentSchemaVersion)
            return; // a newer build's job: read, never written

        var dto = JobDto.FromJob(job);
        var json = JsonSerializer.Serialize(dto, SerializerOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// The job was saved by a newer build, its job.json or its coverage, in a format this one
    /// doesn't fully understand: it is listed, but its coverage isn't loaded and nothing is
    /// written to it, so this build can't overwrite what it doesn't understand.
    /// </summary>
    public static bool IsNewerFormat(string fieldDirectory, string taskName) =>
        SchemaVersionOf(JobFilePath(fieldDirectory, taskName)) > CurrentSchemaVersion
        || new Coverage.CoverageTileStore(JobDirectory(fieldDirectory, taskName)).IsNewerFormat;

    /// <summary>A job.json's schemaVersion; 1 when it has none, or there is no readable file.</summary>
    private static int SchemaVersionOf(string path)
    {
        if (!File.Exists(path)) return CurrentSchemaVersion;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase)
                    && p.Value.TryGetInt32(out int v))
                    return Math.Max(1, v);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        return CurrentSchemaVersion;
    }

    /// <summary>
    /// Load a single job by task name. Returns null if the directory or
    /// <c>job.json</c> is missing.
    /// </summary>
    public static Job? Load(string fieldDirectory, string taskName)
    {
        var path = JobFilePath(fieldDirectory, taskName);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<JobDto>(json, SerializerOptions)
                  ?? throw new InvalidDataException($"job.json at {path} deserialized to null");

        return dto.ToJob(fieldName: ResolveFieldName(fieldDirectory), taskName: taskName);
    }

    /// <summary>
    /// Load every job in this field's <c>jobs/</c> folder. Skips entries
    /// whose <c>job.json</c> fails to parse — caller can log the skip if
    /// desired by listing task names separately.
    /// </summary>
    public static IReadOnlyList<Job> LoadAll(string fieldDirectory)
    {
        var taskNames = ListTaskNames(fieldDirectory);
        var jobs = new List<Job>(taskNames.Count);
        foreach (var name in taskNames)
        {
            try
            {
                var job = Load(fieldDirectory, name);
                if (job != null) jobs.Add(job);
            }
            catch
            {
                // intentionally swallow per-job parse errors here so one
                // bad job.json doesn't block the rest of the listing.
            }
        }
        return jobs;
    }

    private static string ResolveFieldName(string fieldDirectory) =>
        Path.GetFileName(fieldDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

    private sealed class JobDto
    {
        public int SchemaVersion { get; set; }
        public Guid Id { get; set; }
        public string? FieldName { get; set; }
        public string? TaskName { get; set; }
        public string? WorkType { get; set; }
        public string? Notes { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public DateTime? LastOpenedAt { get; set; }
        // Read leniently: a status a newer build added must not make the job unreadable (it would
        // drop out of the list). Such a job is never written, so the stand-in is never saved.
        public JsonElement? Status { get; set; }
        public double DistanceTraveledMeters { get; set; }
        public double AreaWorkedHectares { get; set; }
        public int UTurnCount { get; set; }

        /// <summary>Members this build doesn't read, kept as they were.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Other { get; set; }

        public static JobDto FromJob(Job j) => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = j.Id,
            FieldName = j.FieldName,
            TaskName = j.TaskName,
            WorkType = j.WorkType,
            Notes = j.Notes,
            StartedAt = j.StartedAt,
            EndedAt = j.EndedAt,
            LastOpenedAt = j.LastOpenedAt,
            Status = JsonSerializer.SerializeToElement(j.Status, SerializerOptions),
            DistanceTraveledMeters = j.DistanceTraveledMeters,
            AreaWorkedHectares = j.AreaWorkedHectares,
            UTurnCount = j.UTurnCount,
            Other = j.UnknownProperties?.ToDictionary(p => p.Key, p => p.Value),
        };

        public Job ToJob(string fieldName, string taskName) => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            FieldName = !string.IsNullOrEmpty(FieldName) ? FieldName : fieldName,
            TaskName = !string.IsNullOrEmpty(TaskName) ? TaskName : taskName,
            WorkType = WorkType ?? string.Empty,
            Notes = Notes ?? string.Empty,
            StartedAt = StartedAt ?? DateTime.Now,
            EndedAt = EndedAt,
            LastOpenedAt = LastOpenedAt ?? DateTime.Now,
            Status = ReadStatus(Status),
            DistanceTraveledMeters = DistanceTraveledMeters,
            AreaWorkedHectares = AreaWorkedHectares,
            UTurnCount = UTurnCount,
            UnknownProperties = Other,
        };

        private static JobStatus ReadStatus(JsonElement? e)
        {
            if (e is { ValueKind: JsonValueKind.String } s && Enum.TryParse<JobStatus>(s.GetString(), true, out var named))
                return named;
            if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int i) && Enum.IsDefined(typeof(JobStatus), i))
                return (JobStatus)i;
            return JobStatus.InProgress;
        }
    }
}
