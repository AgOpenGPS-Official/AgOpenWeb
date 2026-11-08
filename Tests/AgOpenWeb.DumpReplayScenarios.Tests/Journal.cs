// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

public enum Severity
{
    /// <summary>The machine does something dangerous, or the module is sent nonsense.</summary>
    Safety,
    /// <summary>Wrong data or wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two parts of the system disagree.</summary>
    Inconsistency,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

/// <summary>What a finding is, as Plans/HARNESS_IMPROVEMENT_PLAN.md section 5 asks every
/// finding to say.</summary>
public enum Kind
{
    AppDefect,
    HarnessArtifact,
    Parity,
    Enhancement,
}

public sealed record Finding(string Code, Severity Severity, Kind Kind, string Message);

/// <summary>One dump: what was reported, what the harness did, what it found.</summary>
public sealed class DumpRow
{
    public string Id = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    /// <summary>Then (the reporter's run, from the GPS log) against now (this build, replayed).</summary>
    public List<(string What, string Then, string Now)> ThenNow = new();
    public List<(string Name, string Value)> Metrics = new();
    public List<string> ReporterLog = new();
}

/// <summary>The steps and checks of one dump.</summary>
public sealed class Scenario
{
    public DumpRow Row { get; }

    public Scenario(string id, string title, string story)
    {
        Row = new DumpRow { Id = id, Title = title, Story = story };
        Journal.Trace($"[dr] ===== {id}: {title}");
    }

    public void Step(string text)
    {
        Row.Steps.Add(text);
        Journal.Trace("[dr]   " + text);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        Journal.Trace("[dr]   OK " + text);
    }

    public void Problem(Severity severity, Kind kind, string code, string text)
    {
        // One line per code and dump: the first place it was seen (stable from night to night).
        if (Row.Findings.Any(f => f.Code == code)) return;
        Row.Checks.Add($"✗ **{severity}** ({KindText(kind)}) `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, kind, text));
        Journal.Trace($"[dr]   FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        Journal.Trace("[dr]   NOTE " + text);
    }

    public void Harness(string code, string text) => Problem(Severity.Bug, Kind.HarnessArtifact, code, text);

    public static string KindText(Kind k) => k switch
    {
        Kind.AppDefect => "app defect",
        Kind.HarnessArtifact => "harness artifact",
        Kind.Parity => "parity with AgOpenGPS",
        _ => "enhancement",
    };
}

/// <summary>Collects every dump and finding, and writes the report.</summary>
public sealed class Journal
{
    public static bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") != "0";
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_TRACE");
    private static readonly object TraceLock = new();

    /// <summary>A line to the console and, with AGOPENWEB_SCENARIO_TRACE set, straight to that file.</summary>
    public static void Trace(string line)
    {
        if (Verbose) Console.WriteLine(line);
        if (TracePath == null) return;
        lock (TraceLock) { try { File.AppendAllText(TracePath, line + "\n"); } catch { } }
    }

    public List<DumpRow> Rows { get; } = new();
    public List<(DumpEntry Entry, string Why)> Skipped { get; } = new();

    public void Add(DumpRow row) => Rows.Add(row);

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# Dump replay scenarios: the testers' own incidents, replayed\n\n");
        sb.Append("Each bug report dump is loaded as the reporter had it (settings, field, track, job and coverage) into AgOpenWeb's whole ")
          .Append("backend, run in-process. Its GPS log (the last five minutes, 10 Hz, after the parser) is fed back into the GPS pipeline at its ")
          .Append("own pace, and the operator's actions the dump shows (AutoSteer on and off, the manual U-turn button, a lateral move) are made again: **replay**. What this build does is ")
          .Append("compared with what the reporter's build did (*then* / *now*), and checked against the field and field job rules. Then a ")
          .Append("simulated machine is put where the reporter was and AgOpenWeb steers it on with AutoSteer and U-turns: **take-over**.\n\n");

        sb.Append("## Findings (each kind once, most serious first)\n\n");
        var groups = Rows.SelectMany(r => r.Findings.Select(f => (r.Id, f)))
            .GroupBy(x => x.f.Code)
            .Select(g => (First: g.First().f, Places: g.Select(x => x.Id).Distinct().ToList()))
            .OrderBy(g => g.First.Severity).ThenBy(g => g.First.Code, StringComparer.Ordinal)
            .ToList();
        int n = 1;
        foreach (var g in groups)
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** ({Scenario.KindText(g.First.Kind)}) `{g.First.Code}` ×{g.Places.Count}: {g.First.Message}\n"));
            sb.Append("   Seen in: ").Append(string.Join(", ", g.Places)).Append('\n');
        }
        if (groups.Count == 0) sb.Append("None.\n");

        sb.Append("\n## Dumps\n\n| Dump | Issue | Reporter | Then | Now | Findings |\n|---|---|---|---|---|---|\n");
        foreach (var r in Rows)
        {
            var then = r.ThenNow.FirstOrDefault(t => t.What == "Verdict");
            sb.Append("| ").Append(r.Id).Append(" | ").Append(r.Metrics.FirstOrDefault(m => m.Name == "Issue").Value ?? "")
              .Append(" | ").Append(r.Metrics.FirstOrDefault(m => m.Name == "Reporter").Value ?? "")
              .Append(" | ").Append(then.Then ?? "").Append(" | ").Append(then.Now ?? "")
              .Append(" | ").Append(r.Findings.Count == 0 ? "none" : string.Join(", ", r.Findings.Select(f => "`" + f.Code + "`")))
              .Append(" |\n");
        }
        foreach (var (e, why) in Skipped)
            sb.Append("| ").Append(e.Id).Append(" | ").Append(e.Issue > 0 ? "#" + e.Issue.ToString(CultureInfo.InvariantCulture) : "").Append(" | ").Append(e.Reporter)
              .Append(" | not replayed | | ").Append(why).Append(" |\n");

        foreach (var r in Rows)
        {
            sb.Append("\n## ").Append(r.Id).Append(". ").Append(r.Title).Append("\n\n").Append(r.Story).Append("\n\n");
            if (r.ThenNow.Count > 0)
            {
                sb.Append("| | Then (reporter's build) | Now (this build) |\n|---|---|---|\n");
                foreach (var (w, t, now) in r.ThenNow) sb.Append("| ").Append(w).Append(" | ").Append(t).Append(" | ").Append(now).Append(" |\n");
                sb.Append('\n');
            }
            foreach (var c in r.Checks) sb.Append("- ").Append(c).Append('\n');
            if (r.Metrics.Count > 0)
            {
                sb.Append("\n| Measured | Value |\n|---|---|\n");
                foreach (var (k, v) in r.Metrics) sb.Append("| ").Append(k).Append(" | ").Append(v).Append(" |\n");
            }
            sb.Append("\n<details><summary>Timeline</summary>\n\n```\n");
            foreach (var s in r.Steps) sb.Append(s).Append('\n');
            if (r.ReporterLog.Count > 0)
            {
                sb.Append("--- the reporter's log (GPS log clock)\n");
                foreach (var l in r.ReporterLog) sb.Append(l).Append('\n');
            }
            sb.Append("```\n\n</details>\n");
        }
        return sb.ToString();
    }
}
