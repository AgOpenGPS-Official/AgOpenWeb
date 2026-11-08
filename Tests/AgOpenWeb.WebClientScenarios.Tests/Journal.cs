// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.WebClientScenarios.Tests;

public enum Severity
{
    /// <summary>The steering or the implement keeps working with nobody at the screen, or the
    /// screen tells the operator something false about it.</summary>
    Safety,
    /// <summary>Wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two screens, or the screen and the machine, disagree.</summary>
    Inconsistency,
    /// <summary>A control the operator needs can't be seen or tapped on this screen.</summary>
    Layout,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One scenario: what the operator did, what the screens showed, what the machine got.</summary>
public sealed class ScenarioRow
{
    public string Group = "", Code = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    public List<string> Shots = new();
}

/// <summary>The clock, the steps and the checks of one scenario.</summary>
public sealed class Scenario
{
    public ScenarioRow Row { get; }
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Scenario(string group, string code, string title, string story)
    {
        Row = new ScenarioRow { Group = group, Code = code, Title = title, Story = story };
        Journal.Trace($"===== {code}: {title}");
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;

    public void Step(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{Seconds,5:0.0} s  {text}");
        Row.Steps.Add(line);
        Journal.Trace("  " + line);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        Journal.Trace("  OK " + text);
    }

    public void Problem(Severity severity, string code, string text)
    {
        Row.Checks.Add($"✗ **{severity}** `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, text));
        Journal.Trace($"  FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        Journal.Trace("  NOTE " + text);
    }

    /// <summary>The harness itself could not do what the scenario needs (not a finding about AgOpenWeb).</summary>
    public void Harness(string text) => Problem(Severity.Bug, "HARNESS", text);

    public void Shot(string file) => Row.Shots.Add(file);
}

/// <summary>
/// The findings report. Every finding has a stable code, so two nightly reports can be compared
/// line by line; <c>KnownFindings.txt</c> lists the codes already reported (an issue or a PR), and
/// the report marks each code New or Already reported.
/// </summary>
public static class Journal
{
    public static bool Verbose => Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") == "1";
    private static readonly System.Diagnostics.Stopwatch RunClock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly object TraceLock = new();

    public static void Trace(string line)
    {
        if (!Verbose) return;
        lock (TraceLock)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[web {RunClock.Elapsed.TotalSeconds,7:0.0}] {line}"));
    }

    /// <summary>Codes already reported, with where (KnownFindings.txt: "CODE  where", # comments).</summary>
    public static Dictionary<string, string> LoadKnown()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "KnownFindings.txt");
        if (!File.Exists(path)) return known;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) known[line] = "";
            else known[line[..sp]] = line[sp..].Trim();
        }
        return known;
    }

    public static string Write(IReadOnlyList<ScenarioRow> rows, string header, TimeSpan took)
    {
        var known = LoadKnown();
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# Web client scenarios\n\n").Append(header).Append('\n');
        sb.Append(string.Create(inv, $"\n{rows.Count} scenarios in {took.TotalMinutes:0.0} min. "));

        // One line per finding code: the worst severity it was seen with and every scenario it came up in.
        var byCode = rows.SelectMany(r => r.Findings.Select(f => (Row: r, F: f)))
            .GroupBy(x => x.F.Code)
            .Select(g => (Code: g.Key, Sev: g.Min(x => x.F.Severity), Where: g.Select(x => x.Row.Code).Distinct().ToList(),
                          First: g.First().F.Message))
            .OrderBy(x => x.Sev).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        int fresh = byCode.Count(c => !known.ContainsKey(c.Code));
        sb.Append(string.Create(inv, $"{byCode.Count} finding codes ({fresh} new, {byCode.Count - fresh} already reported).\n\n"));

        sb.Append("## Findings\n\n");
        if (byCode.Count == 0) sb.Append("None.\n\n");
        else
        {
            sb.Append("| Code | Severity | Status | Scenarios | First seen as |\n|---|---|---|---|---|\n");
            foreach (var c in byCode)
            {
                string status = known.TryGetValue(c.Code, out var where) ? "Already reported" + (where.Length > 0 ? " (" + where + ")" : "") : "**New**";
                sb.Append("| `").Append(c.Code).Append("` | ").Append(c.Sev).Append(" | ").Append(status).Append(" | ")
                  .Append(string.Join(", ", c.Where)).Append(" | ").Append(Cell(c.First)).Append(" |\n");
            }
            sb.Append('\n');
        }

        foreach (var group in rows.GroupBy(r => r.Group))
        {
            sb.Append("## ").Append(group.Key).Append("\n\n");
            foreach (var r in group)
            {
                sb.Append("### ").Append(r.Code).Append(" ").Append(r.Title).Append("\n\n");
                if (r.Story.Length > 0) sb.Append(r.Story).Append("\n\n");
                if (r.Checks.Count > 0)
                {
                    foreach (var c in r.Checks) sb.Append("- ").Append(c).Append('\n');
                    sb.Append('\n');
                }
                if (r.Shots.Count > 0) sb.Append("Screenshots: ").Append(string.Join(", ", r.Shots.Select(s => "`" + s + "`"))).Append("\n\n");
                if (r.Steps.Count > 0)
                {
                    sb.Append("<details><summary>Steps</summary>\n\n```\n");
                    foreach (var s in r.Steps) sb.Append(s).Append('\n');
                    sb.Append("```\n</details>\n\n");
                }
            }
        }
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
