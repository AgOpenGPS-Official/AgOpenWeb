// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.FieldJobScenarios.Tests;

public enum Severity
{
    /// <summary>The machine does something dangerous, or the module is sent nonsense.</summary>
    Safety,
    /// <summary>Wrong data or wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two parts of the system disagree.</summary>
    Inconsistency,
    /// <summary>A setting that cannot work accepted and kept, with no word to the operator.</summary>
    Robustness,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One scenario: what the operator did, what the module got, what the valves did.</summary>
public sealed class ScenarioRow
{
    public string Operator = "", Code = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    public List<string> ModuleLog = new();
    public string Pgns = "";
    /// <summary>Name, value pairs of what was measured (areas, turns, times).</summary>
    public List<(string Name, string Value)> Metrics = new();
}

/// <summary>The clock, the steps and the checks of one scenario.</summary>
public sealed class Scenario
{
    public ScenarioRow Row { get; }
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Scenario(string op, string code, string title, string story)
    {
        Row = new ScenarioRow { Operator = op, Code = code, Title = title, Story = story };
        Journal.Trace($"[fj] ===== {op} {code}: {title}");
    }

    public void Step(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,5:0.0} s  {text}");
        Row.Steps.Add(line);
        Journal.Trace("[fj]   " + line);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        Journal.Trace("[fj]   OK " + text);
    }

    public void Problem(Severity severity, string code, string text)
    {
        Row.Checks.Add($"✗ **{severity}** `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, text));
        Journal.Trace($"[fj]   FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        Journal.Trace("[fj]   NOTE " + text);
    }

    public void Harness(string text) => Problem(Severity.Bug, "HARNESS", text);

    public void ModuleSnapshot(EmulatedSprayerModule m, double seconds)
    {
        Row.ModuleLog = m.Log.TakeLast(14).ToList();
        // The counts are the module's since it was made (a module can serve several scenarios).
        Row.Pgns = string.Create(CultureInfo.InvariantCulture,
            $"239 ×{m.Count(239)}, 229 ×{m.Count(229)}, 235 ×{m.Count(235)}, 236 ×{m.Count(236)}, 238 ×{m.Count(238)}, hellos answered {m.HellosAnswered}, checksum errors {m.ChecksumErrors}, short packets {m.ShortPackets}, flash writes {m.FlashCopy().Writes}");
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;
}

/// <summary>Collects every scenario and finding, and writes the report.</summary>
public sealed class Journal
{
    public static bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") != "0";
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_TRACE");
    private static readonly object TraceLock = new();

    /// <summary>A line to the console (NUnit shows it at the end) and, with AGOPENWEB_SCENARIO_TRACE
    /// set, straight to that file, to follow a long run while it goes.</summary>
    public static void Trace(string line)
    {
        if (Verbose) Console.WriteLine(line);
        if (TracePath == null) return;
        lock (TraceLock) { try { File.AppendAllText(TracePath, line + "\n"); } catch { } }
    }

    public List<ScenarioRow> Rows { get; } = new();
    public Dictionary<string, (Finding First, int Count, List<string> Places)> Findings { get; } = new();

    public void Add(ScenarioRow row)
    {
        Rows.Add(row);
        foreach (var f in row.Findings)
        {
            string where = row.Code;
            if (Findings.TryGetValue(f.Code, out var e))
            {
                if (!e.Places.Contains(where)) e.Places.Add(where);
                Findings[f.Code] = (e.First, e.Count + 1, e.Places);
            }
            else Findings[f.Code] = (f, 1, new List<string> { where });
        }
    }

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# Field job scenarios: the careful and the careless operator\n\n");
        sb.Append("AgOpenWeb's whole backend runs in-process with its real UDP service on port 9999, as the other scenario harnesses. ")
          .Append("A real machine (the Vehicle & Tool harness's bicycle model, steering motor and trailed implement) drives whole fields with ")
          .Append("AutoSteer, automatic U-turns and automatic sections, on RTK fixes at 10 Hz; an emulated machine module (the Sprayer_ESP32 ")
          .Append("firmware's PGN handling) on 127.0.0.1:8888 opens the valves AgOpenWeb asks for. The truth coverage is what the real implement ")
          .Append("swept with a valve open, on a 0.2 m grid; it is compared with the field, the headland, the obstacles and AgOpenWeb's own map.\n\n");

        sb.Append("## Findings (each kind once, most serious first)\n\n");
        int n = 1;
        foreach (var g in Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code))
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** `{g.First.Code}` ×{g.Count}: {g.First.Message}\n"));
            sb.Append("   Seen in: ").Append(string.Join(", ", g.Places)).Append('\n');
        }
        if (Findings.Count == 0) sb.Append("None.\n");

        foreach (var op in Rows.Select(r => r.Operator).Distinct())
        {
            sb.Append("\n## ").Append(op).Append(" operator\n");
            foreach (var r in Rows.Where(x => x.Operator == op))
            {
                sb.Append("\n### ").Append(r.Code).Append(". ").Append(r.Title).Append("\n\n").Append(r.Story).Append("\n\n");
                foreach (var c in r.Checks) sb.Append("- ").Append(c).Append('\n');
                if (r.Metrics.Count > 0)
                {
                    sb.Append("\n| Measured | Value |\n|---|---|\n");
                    foreach (var (k, v) in r.Metrics) sb.Append("| ").Append(k).Append(" | ").Append(v).Append(" |\n");
                }
                if (r.Pgns.Length > 0) sb.Append("\nPGNs the module received: ").Append(r.Pgns).Append('\n');
                sb.Append("\n<details><summary>Timeline</summary>\n\n```\n");
                foreach (var s in r.Steps) sb.Append(s).Append('\n');
                if (r.ModuleLog.Count > 0)
                {
                    sb.Append("--- module log (its own clock)\n");
                    foreach (var l in r.ModuleLog) sb.Append(l).Append('\n');
                }
                sb.Append("```\n\n</details>\n");
            }
        }
        return sb.ToString();
    }
}
