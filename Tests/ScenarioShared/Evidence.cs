// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
// Linked into the scenario harnesses that keep evidence (<Compile Include="..\ScenarioShared\Evidence.cs" />).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// What a finding is fixed from, kept beside the report: the app's own bug report dump
/// (<see cref="DebugDumpService"/>: settings, configuration, runtime state, log, the GPS log, the
/// field's files and the job's coverage), as a tester's report gives us, plus what only the
/// harness knows (the GPS log of the whole scenario, every U-turn's path, its own notes). A fix is
/// then made and proven from the dump with a unit test, and the harness is not run again to look
/// (the nightly on develop is the check).
///
/// <c>AGOPENWEB_SCENARIO_EVIDENCE</c>: unset, a scenario with a finding keeps its evidence;
/// <c>all</c>, every scenario does; <c>0</c>, none. The zips go to <c>evidence/</c> beside the
/// report. One per scenario: the instance is the scenario's.
/// </summary>
public sealed class Evidence
{
    private static readonly string Mode = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_EVIDENCE") ?? "";
    public static bool Off => Mode == "0";
    public static bool All => Mode.Equals("all", StringComparison.OrdinalIgnoreCase);

    /// <summary>The evidence folder for a report at <paramref name="reportPath"/>.</summary>
    public static string FolderFor(string reportPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".", "evidence");

    private readonly string _folder, _scenario;
    private readonly List<string> _gpsRows = new();
    private string? _gpsHeader, _lastGpsRow;
    private readonly Dictionary<string, List<object>> _lists = new();
    private readonly Dictionary<string, StreamWriter> _tables = new();
    private readonly string _tableDir;

    /// <summary>The zips written, by file name, for the report.</summary>
    public List<string> Files { get; } = new();
    /// <summary>What went wrong keeping the evidence (the run goes on without it).</summary>
    public List<string> Errors { get; } = new();

    public Evidence(string folder, string scenario)
    {
        _folder = folder;
        _scenario = Safe(scenario);
        // The recorder and the app's log are the process's: without this they start with the
        // scenario before (and a short scenario's logs.txt would be mostly the last one's).
        if (!Off) { GpsDataRecorder.Instance.Clear(); LogStore.Instance.Clear(); WholeLog.Clear(); HarnessTrace.Clear(); }
        _tableDir = Path.Combine(Path.GetTempPath(), "agopenweb-evidence", _scenario + "-" + Guid.NewGuid().ToString("N")[..6]);
    }

    /// <summary>
    /// A line of the harness's own trace (what it saw and decided: the pass it is on, a turn, the
    /// operator's actions), kept for <c>attachments/harness_trace.txt</c> (the last
    /// <see cref="WholeLogLines.Max"/> lines of the scenario). A harness's trace calls this, so a
    /// finding that proves to be the harness's doing can be followed in the zip too.
    /// </summary>
    public static void Trace(string line)
    {
        if (!Off) HarnessTrace.Add(line);
    }
    private static readonly WholeLogLines HarnessTrace = new();

    /// <summary>
    /// One row of a table the harness keeps for the dump, written as it goes to a file of its own
    /// (a whole job's rows are too many to hold), saved as <c>attachments/&lt;table&gt;.csv</c>:
    /// <c>truth_track</c> is what the harness's machine really did, fix by fix. The header is
    /// written with the first row; the row's values are the caller's (invariant culture).
    /// </summary>
    public void Row(string table, string header, string row)
    {
        if (Off) return;
        try
        {
            if (!_tables.TryGetValue(table, out var w))
            {
                Directory.CreateDirectory(_tableDir);
                _tables[table] = w = new StreamWriter(Path.Combine(_tableDir, Safe(table) + ".csv"), false, new UTF8Encoding(false));
                w.Write(header); w.Write('\n');
            }
            w.Write(row); w.Write('\n');
        }
        catch (Exception ex) { if (Errors.Count < 20) Errors.Add(table + ": " + ex.Message); }
    }

    /// <summary>
    /// For the rig's logging, beside the app's own <see cref="InMemoryLoggerProvider"/> (which
    /// fills the dump's <c>logs.txt</c>, the last 2000 lines, as on a tester's device): keeps the
    /// log of the whole scenario (the last <see cref="WholeLogLines.Max"/> lines of it) for
    /// <c>attachments/log_whole_scenario.txt</c>.
    /// </summary>
    public static ILoggerProvider LogProvider { get; } = new WholeLogProvider();
    private static readonly WholeLogLines WholeLog = new();

    private sealed class WholeLogLines
    {
        public const int Max = 400_000;
        private readonly Queue<string> _lines = new();
        private long _dropped;
        public void Add(string line) { lock (_lines) { if (_lines.Count >= Max) { _lines.Dequeue(); _dropped++; } _lines.Enqueue(line); } }
        public void Clear() { lock (_lines) { _lines.Clear(); _dropped = 0; } }
        public string Text()
        {
            lock (_lines)
                return (_dropped > 0 ? "# " + _dropped.ToString(CultureInfo.InvariantCulture) + " earlier lines not kept\n" : "") + string.Join("\n", _lines) + "\n";
        }
        public int Count { get { lock (_lines) return _lines.Count; } }
    }

    private sealed class WholeLogProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new L(categoryName.Contains('.') ? categoryName[(categoryName.LastIndexOf('.') + 1)..] : categoryName);
        public void Dispose() { }
        private sealed class L(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => !Off && logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                // UTC, as the GPS log's timestamps, so a line is found beside its fix.
                WholeLog.Add(string.Create(CultureInfo.InvariantCulture,
                    $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{logLevel}] {category}: {formatter(state, exception)}{(exception != null ? " " + exception : "")}"));
            }
        }
    }

    /// <summary>
    /// Takes the fixes recorded since the last call. The app keeps the last
    /// <see cref="GpsDataRecorder.BufferSize"/> fixes (5 minutes at 10 Hz, what a tester's dump
    /// has); a scenario is longer, so call this more often than that many fixes and the whole
    /// scenario is kept.
    /// </summary>
    public void PullGpsLog()
    {
        if (Off) return;
        try
        {
            var lines = GpsDataRecorder.Instance.ExportCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r')).ToArray();
            if (lines.Length < 2) return;
            _gpsHeader ??= lines[0];
            int from = 1;
            if (_lastGpsRow != null)
            {
                int at = Array.LastIndexOf(lines, _lastGpsRow);
                if (at >= 1) from = at + 1;
                else _gpsRows.Add("# fixes lost here: the log was not taken for more than the recorder keeps");
            }
            for (int i = from; i < lines.Length; i++) _gpsRows.Add(lines[i]);
            _lastGpsRow = lines[^1];
        }
        catch (Exception ex) { Errors.Add("GPS log: " + ex.Message); }
    }

    /// <summary>One more entry of a list the harness keeps for the dump (<c>turns</c>: every
    /// U-turn's path as the app planned it), written as <c>attachments/&lt;list&gt;.json</c>.</summary>
    public void Add(string list, object entry)
    {
        if (Off) return;
        if (!_lists.TryGetValue(list, out var l)) _lists[list] = l = new List<object>();
        l.Add(entry);
    }

    /// <summary>
    /// Writes the dump <c>&lt;scenario&gt;[-moment].zip</c>: the app's state as it is now, read on
    /// the host loop (<paramref name="onHostLoop"/>), then the field, the open job's coverage (saved
    /// first, as the app's bug report does) and the harness's own files. <paramref name="notes"/>
    /// is the dump's <c>user_notes.txt</c>: the findings, in the harness's words. Returns the
    /// zip's path, or null when it could not be written (never throws: a scenario is not lost for
    /// its evidence).
    /// </summary>
    public async Task<string?> SaveAsync(string? moment, string notes, IServiceProvider services, ApplicationState state,
        ConfigurationStore store, Func<Action, Task> onHostLoop, IEnumerable<string>? attachments = null)
    {
        if (Off) return null;
        string name = moment == null ? _scenario : _scenario + "-" + Safe(moment);
        string work = Path.Combine(_folder, ".work-" + name);
        try
        {
            Directory.CreateDirectory(work);
            PullGpsLog();
            var files = new List<string>(attachments ?? Array.Empty<string>());
            if (_gpsHeader != null)
            {
                string gps = Path.Combine(work, "gps_data_log_whole_scenario.csv");
                File.WriteAllText(gps, _gpsHeader + "\n" + string.Join("\n", _gpsRows) + "\n");
                files.Add(gps);
            }
            if (WholeLog.Count > 0)
            {
                string log = Path.Combine(work, "log_whole_scenario.txt");
                File.WriteAllText(log, WholeLog.Text());
                files.Add(log);
            }
            if (HarnessTrace.Count > 0)
            {
                string trace = Path.Combine(work, "harness_trace.txt");
                File.WriteAllText(trace, HarnessTrace.Text());
                files.Add(trace);
            }
            foreach (var (table, w) in _tables)
            {
                w.Flush();
                string f = Path.Combine(work, Safe(table) + ".csv");
                File.Copy(Path.Combine(_tableDir, Safe(table) + ".csv"), f, true);
                files.Add(f);
            }
            foreach (var (list, entries) in _lists)
            {
                string f = Path.Combine(work, Safe(list) + ".json");
                File.WriteAllText(f, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
                files.Add(f);
            }

            DebugDumpService.DumpSnapshot? snap = null;
            (string FieldDir, string Task)? job = null;
            await onHostLoop(() =>
            {
                snap = DebugDumpService.Capture(services.GetRequiredService<ISettingsService>(), RuntimeStateCapture.From(state), store);
                string? task = services.GetService<IJobService>()?.ActiveJob?.TaskName;
                string? dir = state.Field.ActiveField?.DirectoryPath;
                if (!string.IsNullOrWhiteSpace(task) && !string.IsNullOrEmpty(dir)) job = (dir, task);
            });
            string final = Path.Combine(_folder, name + ".zip");
            await Task.Run(() =>
            {
                if (job is { } j)
                {
                    try { services.GetRequiredService<ICoverageMapService>().SaveToFile(j.FieldDir, j.Task); }
                    catch (Exception ex) { lock (Errors) Errors.Add(name + ": the coverage was not saved first, the dump has the last autosave (" + ex.Message + ")"); }
                }
                string zip = DebugDumpService.WriteDump(snap!, additionalNotes: notes, outputDirectory: work,
                    filePrefix: name, userAttachments: files, activeJobTaskName: job?.Task);
                File.Move(zip, final, overwrite: true);
            });
            Files.Add(Path.GetFileName(final));
            return final;
        }
        catch (Exception ex)
        {
            Errors.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>The tables' files go when the scenario is done with its evidence.</summary>
    public void Close()
    {
        foreach (var w in _tables.Values) { try { w.Dispose(); } catch { } }
        _tables.Clear();
        try { if (Directory.Exists(_tableDir)) Directory.Delete(_tableDir, true); } catch { }
    }

    /// <summary>The head of a dump's notes: which harness and scenario, the commit, the settings
    /// of the run, and how the dump is used.</summary>
    public static string NotesHeader(string harness, string scenario, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(harness).Append(": ").Append(scenario).Append(" — ").Append(title).Append("\n\n");
        string commit = Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: >= 7 } sha ? sha[..7] : "local";
        sb.Append("Commit ").Append(commit).Append(", ").Append(DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)).Append(".\n");
        var env = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("AGOPENWEB_SCENARIO_", StringComparison.Ordinal) && !k.EndsWith("_REPORT", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => k + "=" + Environment.GetEnvironmentVariable(k)).ToList();
        if (env.Count > 0) sb.Append("Run with ").Append(string.Join(" ", env)).Append(".\n");
        sb.Append("\nA bug report dump as the app writes it, taken by the harness at the end of the scenario. ")
          .Append("`gps_data_log.csv` is the last 5 minutes; `attachments/gps_data_log_whole_scenario.csv` the whole scenario, as `attachments/log_whole_scenario.txt` is for `logs.txt`; ")
          .Append("`attachments/turns.json` every U-turn's path as planned, with where the tractor was. ")
          .Append("What the harness itself measured is in `attachments/` too (its trace, and what its own machine really did; listed at the end of these notes), so a finding that is the harness's doing can be checked from the zip as well. ")
          .Append("Build the unit test for the fix from this dump; the harness is not run again to look.\n\n");
        return sb.ToString();
    }

    private static string Safe(string s) =>
        new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray());
}
