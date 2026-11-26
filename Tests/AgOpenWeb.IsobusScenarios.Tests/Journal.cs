// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.IsobusScenarios.Tests;

public enum Severity
{
    /// <summary>The implement does something dangerous, or keeps working when it must not.</summary>
    Safety,
    /// <summary>Wrong data or wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two parts of the system disagree.</summary>
    Inconsistency,
    /// <summary>A setting or a situation that cannot work accepted and kept, with no word to the operator.</summary>
    Robustness,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One scenario: what the operator did, what the task controller got, what the implement did.</summary>
public sealed class ScenarioRow
{
    public string Operator = "", Code = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    public List<string> TcLog = new();
    public string Pgns = "";
}

/// <summary>The clock, the steps and the checks of one scenario.</summary>
public sealed class Scenario
{
    public ScenarioRow Row { get; }
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Scenario(string op, string code, string title, string story)
    {
        Row = new ScenarioRow { Operator = op, Code = code, Title = title, Story = story };
        if (Journal.Verbose) Console.WriteLine($"[iso] ===== {op} {code}: {title}");
    }

    public void Step(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,5:0.0} s  {text}");
        Row.Steps.Add(line);
        if (Journal.Verbose) Console.WriteLine("[iso]   " + line);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        if (Journal.Verbose) Console.WriteLine("[iso]   OK " + text);
    }

    public void Problem(Severity severity, string code, string text)
    {
        Row.Checks.Add($"✗ **{severity}** `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, text));
        if (Journal.Verbose) Console.WriteLine($"[iso]   FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        if (Journal.Verbose) Console.WriteLine("[iso]   NOTE " + text);
    }

    public void Harness(string text) => Problem(Severity.Bug, "HARNESS", text);

    /// <summary>The task controller's last log lines and the counts of what it received.</summary>
    public void TcSnapshot(EmulatedTaskController tc)
    {
        Row.TcLog = tc.Log.TakeLast(14).ToList();
        // The counts are the task controller's since it was made (one can serve several scenarios).
        Row.Pgns = string.Create(CultureInfo.InvariantCulture,
            $"229 ×{tc.Count(229)} ({tc.SetpointChanges} changes), 241 ×{tc.ModeRequests.Count}, 242 ×{tc.Count(242)}, 214 ×{tc.Count(214)}, 254 ×{tc.Count(254)}, 239 ×{tc.Count(239)}, 200 ×{tc.Count(200)}, 201 ×{tc.Count(201)}, 202 ×{tc.Count(202)}; heartbeats sent {tc.HeartbeatsSent}, messages sent {tc.MessagesSent.Count}; checksum errors {tc.ChecksumErrors}, short packets {tc.ShortPackets}, other sources dropped {tc.OtherSourceDropped}");
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;
}

/// <summary>Collects every scenario and finding, and writes the report. Finding codes and
/// scenario codes are fixed identifiers, so one night's report can be compared with the next.</summary>
public sealed class Journal
{
    public static bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") != "0";

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
        sb.Append("# ISOBUS scenarios: the careful and the careless operator\n\n");
        sb.Append("AgOpenWeb's whole backend runs in-process with its real UDP service on port 9999. An emulated AOG-TaskController (PGN 240 every 100 ms per ")
          .Append("implement with the implement's actual sections, PGN 221 messages, PGN 229 / 241 / 242 from source 0x7F, as the real one) listens on 127.0.0.1:8888 ")
          .Append("with the implements on its bus (sections that follow the setpoints after a valve delay in automatic, their own switches in manual); a steer module ")
          .Append("stub sends PGN 253. A tractor sends RTK fixes at 10 Hz in real time, driving north on fresh ground with master Auto. The screen's sections, the ")
          .Append("setpoints the task controller received, the implement's sections and what AgOpenWeb shows of them are compared every 100 ms.\n\n");
        string? sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrEmpty(sha)) sb.Append("Commit: `").Append(sha).Append("`\n\n");

        sb.Append("## Findings (each kind once, most serious first)\n\n");
        int n = 1;
        foreach (var g in Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code, StringComparer.Ordinal))
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
                if (r.Pgns.Length > 0) sb.Append("\nThe task controller received: ").Append(r.Pgns).Append('\n');
                sb.Append("\n<details><summary>Timeline</summary>\n\n```\n");
                foreach (var s in r.Steps) sb.Append(s).Append('\n');
                if (r.TcLog.Count > 0)
                {
                    sb.Append("--- task controller log (its own clock)\n");
                    foreach (var l in r.TcLog) sb.Append(l).Append('\n');
                }
                sb.Append("```\n\n</details>\n");
            }
        }
        return sb.ToString();
    }
}
