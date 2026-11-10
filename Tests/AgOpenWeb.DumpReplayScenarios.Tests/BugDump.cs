// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>One row of a dump's <c>gps_data_log.csv</c> (GpsDataRecorder): what one GPS cycle
/// gave, after the parser and after the pipeline. Columns a dump of an older build does not
/// have read as their default.</summary>
public sealed class GpsRow
{
    /// <summary>Seconds since the first row of the log (the recorder's UTC clock, across midnight).</summary>
    public double T;
    public TimeSpan Utc;
    public double E, N, Heading, Speed, Roll;
    public int Fix;
    public double ToolE, ToolN, ToolHeadingRad, Steer, Xte;
    public bool HasGuidance, AutoSteer, YtTriggered, YtExecuting, IsTurnLeft;
    public int PathsAway;
    public double? HeadlandDist;
    public string Sentence = "";
    public double SentenceHeading;
    public bool ImuValid, Reverse, DualMissing;
    public double DiffAge;
    public bool HasSentenceColumns;
}

/// <summary>A line of the dump's <c>logs.txt</c>: local time of the reporter's computer.</summary>
public sealed record LogLine(TimeSpan Local, string Level, string Category, string Text);

/// <summary>
/// A bug report dump (<c>DebugDumpService</c>), unpacked: the reporter's settings
/// (<c>configuration.json</c>, <c>appsettings.json</c>), what the app was doing
/// (<c>runtime_state.json</c>), the field folder with the active job's coverage, the GPS log of
/// the last five minutes and the app's log. <c>Tools/unpack-dump.sh</c> prints the same files.
/// </summary>
public sealed class BugDump
{
    public string Id { get; }
    public string Folder { get; }
    public string FieldDir => Path.Combine(Folder, "field");
    public string SystemInfo { get; private set; } = "";
    public string BuildHash { get; private set; } = "";
    public DateTime? DumpLocalTime { get; private set; }
    public string Notes { get; private set; } = "";
    public JsonElement? Config { get; private set; }
    public JsonElement? AppSettings { get; private set; }
    public JsonElement? Runtime { get; private set; }
    public string? FieldName { get; private set; }
    public string? ActiveTrack { get; private set; }
    public string? JobTask { get; private set; }
    public bool HasCoverageTiles { get; private set; }
    public List<GpsRow> Rows { get; } = new();
    public List<LogLine> Log { get; } = new();
    /// <summary>The reporter's local time minus UTC (the log is local, the GPS log UTC).</summary>
    public TimeSpan UtcOffset { get; private set; }
    public List<string> Problems { get; } = new();
    public bool IsSimulator => Rows.Count > 0 && Rows.Count(r => r.Sentence == "SIM") * 2 > Rows.Count;

    private BugDump(string id, string folder) { Id = id; Folder = folder; }

    /// <summary>Unpack <paramref name="zip"/> into <paramref name="folder"/> (emptied first) and read it.</summary>
    public static BugDump Open(string id, string zip, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var d = new BugDump(id, folder);
        try { ZipFile.ExtractToDirectory(zip, folder); }
        catch (Exception ex) { d.Problems.Add("the zip did not open: " + ex.Message); return d; }
        d.Read();
        return d;
    }

    private string? Text(string name)
    {
        var p = Path.Combine(Folder, name);
        return File.Exists(p) ? File.ReadAllText(p).TrimStart('﻿') : null;
    }

    private static JsonElement? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private void Read()
    {
        SystemInfo = Text("system_info.txt") ?? "";
        var m = Regex.Match(SystemInfo, @"Build Info: [^+\r\n]*\+([0-9a-f]{7,40})");
        if (m.Success) BuildHash = m.Groups[1].Value;
        var ts = Regex.Match(SystemInfo, @"Timestamp: (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
        if (ts.Success && DateTime.TryParseExact(ts.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            DumpLocalTime = t;
        Notes = (Text("user_notes.txt") ?? "").Trim();
        Config = Json(Text("configuration.json"));
        AppSettings = Json(Text("appsettings.json"));
        Runtime = Json(Text("runtime_state.json"));
        FieldName = Str(Runtime, "Field", "ActiveField");
        ActiveTrack = Str(Runtime, "Field", "ActiveTrack");
        if (string.IsNullOrEmpty(ActiveTrack)) ActiveTrack = (Text("field/ActiveTrack.txt") ?? "").Trim();
        if (string.IsNullOrEmpty(ActiveTrack)) ActiveTrack = null;
        // The dump holds the active job's folder only (DebugDumpService step 6b).
        var jobs = Path.Combine(FieldDir, "jobs");
        if (Directory.Exists(jobs))
        {
            var dirs = Directory.GetDirectories(jobs);
            if (dirs.Length == 1) JobTask = Path.GetFileName(dirs[0]);
            else if (dirs.Length > 1) Problems.Add("more than one job folder in the dump");
            HasCoverageTiles = Directory.EnumerateFiles(jobs, "*.tile", SearchOption.AllDirectories).Any();
        }
        if (!Directory.Exists(FieldDir)) Problems.Add("no field in the dump (no field was open)");
        ReadGpsLog();
        ReadLog();
        // The log is in the reporter's local time; the GPS log in UTC. The dump was made just
        // after the last GPS row: the offset is that gap, rounded to a quarter hour.
        if (DumpLocalTime is { } local && Rows.Count > 0)
        {
            var gap = local.TimeOfDay - Rows[^1].Utc;
            double q = Math.Round(gap.TotalMinutes / 15.0) * 15.0;
            if (q > 14 * 60) q -= 24 * 60;
            if (q < -14 * 60) q += 24 * 60;
            UtcOffset = TimeSpan.FromMinutes(q);
        }
    }

    public static string? Str(JsonElement? root, params string[] path)
    {
        var e = Get(root, path);
        return e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    public static JsonElement? Get(JsonElement? root, params string[] path)
    {
        if (root is not { } r) return null;
        foreach (var p in path)
        {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(p, out var next)) return null;
            r = next;
        }
        return r;
    }

    private void ReadGpsLog()
    {
        var text = Text("gps_data_log.csv");
        if (text == null) { Problems.Add("no gps_data_log.csv in the dump"); return; }
        var lines = text.Split('\n');
        if (lines.Length < 2) { Problems.Add("gps_data_log.csv is empty"); return; }
        var head = lines[0].Trim().Split(',');
        var col = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < head.Length; i++) col[head[i].Trim()] = i;
        var inv = CultureInfo.InvariantCulture;
        double prevUtc = -1, dayAdd = 0, first = double.NaN;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var f = line.Split(',');
            string S(string name) => col.TryGetValue(name, out var i) && i < f.Length ? f[i] : "";
            double D(string name) => double.TryParse(S(name), NumberStyles.Float, inv, out var v) ? v : 0;
            double? Dn(string name) => double.TryParse(S(name), NumberStyles.Float, inv, out var v) ? v : null;
            bool B(string name) => S(name) == "1";
            if (!TimeSpan.TryParseExact(S("timestamp"), @"hh\:mm\:ss\.fff", inv, out var utc)) continue;
            double secs = utc.TotalSeconds + dayAdd;
            if (prevUtc >= 0 && secs < prevUtc - 3600) { dayAdd += 86400; secs += 86400; }
            prevUtc = secs;
            if (double.IsNaN(first)) first = secs;
            Rows.Add(new GpsRow
            {
                T = secs - first,
                Utc = utc,
                E = D("easting"), N = D("northing"), Heading = D("heading"), Speed = D("speed"),
                Fix = (int)D("fix"), Roll = D("roll"),
                ToolE = D("tool_e"), ToolN = D("tool_n"), ToolHeadingRad = D("tool_h_rad"),
                Steer = D("steer_angle"), Xte = D("xte"),
                HasGuidance = B("has_guidance"), PathsAway = (int)D("paths_away"), AutoSteer = B("autosteer"),
                YtTriggered = B("yt_triggered"), YtExecuting = B("yt_executing"), IsTurnLeft = B("is_turn_left"),
                HeadlandDist = Dn("headland_dist"),
                Sentence = S("sentence"), SentenceHeading = D("sentence_heading"),
                ImuValid = B("imu_valid"), Reverse = B("reverse"), DualMissing = B("dual_missing"),
                DiffAge = D("diff_age"),
                HasSentenceColumns = col.ContainsKey("sentence"),
            });
        }
        if (Rows.Count == 0) Problems.Add("gps_data_log.csv has no rows");
    }

    private static readonly Regex LogRx = new(@"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\] \[(\w+)\] ([^:]+): (.*)$", RegexOptions.Compiled);

    private void ReadLog()
    {
        var text = Text("logs.txt");
        if (text == null) return;
        foreach (var raw in text.Split('\n'))
        {
            var m = LogRx.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!TimeSpan.TryParseExact(m.Groups[1].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var t)) continue;
            Log.Add(new LogLine(t, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
        }
    }

    /// <summary>The GPS log's clock (seconds since its first row) of a log line, or null if it
    /// is outside the GPS log's day.</summary>
    public double? LogT(LogLine l)
    {
        if (Rows.Count == 0) return null;
        var utc = l.Local - UtcOffset;
        double s = utc.TotalSeconds - Rows[0].Utc.TotalSeconds;
        if (s < -43200) s += 86400;
        if (s > 43200) s -= 86400;
        return s;
    }

    /// <summary>A number from configuration.json, by its path.</summary>
    public double? Num(params string[] path)
    {
        var e = Get(Config, path);
        return e is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;
    }

    public bool? Bool(params string[] path)
    {
        var e = Get(Config, path);
        return e?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
}
