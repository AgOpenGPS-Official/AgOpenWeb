// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.UpdateScenarios.Tests;

public enum Severity
{
    /// <summary>The farmer's data is gone or wrong after an update or a restart: a field, a job's
    /// coverage, a profile, a steering setting that steers differently.</summary>
    DataLoss,
    /// <summary>Wrong value or wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two parts of the system disagree (the file says one thing, the screen another).</summary>
    Inconsistency,
    /// <summary>A bad file or situation is accepted silently, or recovery leaves the operator uninformed.</summary>
    Robustness,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One scenario: what was done on which build, what was read back.</summary>
public sealed class ScenarioRow
{
    public string Operator = "", Code = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    public List<string> OldBuildLog = new();
    public string Files = "";
}

/// <summary>The clock, the steps and the checks of one scenario.</summary>
public sealed class Scenario
{
    public ScenarioRow Row { get; }
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Scenario(string op, string code, string title, string story)
    {
        Row = new ScenarioRow { Operator = op, Code = code, Title = title, Story = story };
        if (Journal.Verbose) Console.WriteLine($"[upd] ===== {op} {code}: {title}");
    }

    public void Step(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,6:0.0} s  {text}");
        Row.Steps.Add(line);
        if (Journal.Verbose) Console.WriteLine("[upd]   " + line);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        if (Journal.Verbose) Console.WriteLine("[upd]   OK " + text);
    }

    public void Problem(Severity severity, string code, string text)
    {
        Row.Checks.Add($"✗ **{severity}** `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, text));
        if (Journal.Verbose) Console.WriteLine($"[upd]   FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        if (Journal.Verbose) Console.WriteLine("[upd]   NOTE " + text);
    }

    public void Harness(string text) => Problem(Severity.Bug, "HARNESS", text);

    public double Seconds => _clock.Elapsed.TotalSeconds;
}

/// <summary>Collects every scenario and finding, and writes the report. Finding codes and
/// scenario codes are fixed identifiers, so one night's report can be compared with the next;
/// KnownFindings.txt lists the codes already reported, so the report says which are new.</summary>
public sealed class Journal
{
    public static bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") != "0";

    public List<ScenarioRow> Rows { get; } = new();
    public Dictionary<string, (Finding First, int Count, List<string> Places)> Findings { get; } = new();
    public List<string> Environment_ { get; } = new();

    /// <summary>Code → where it was reported (an issue or PR, or "harness" for a known limit of the
    /// harness itself), from KnownFindings.txt next to the test assembly.</summary>
    public Dictionary<string, string> Known { get; } = LoadKnown();

    private static Dictionary<string, string> LoadKnown()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "KnownFindings.txt");
            if (!File.Exists(path)) return d;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp < 0) d[line] = "";
                else d[line[..sp]] = line[sp..].Trim();
            }
        }
        catch { /* the list is a help, not a requirement */ }
        return d;
    }

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
        sb.Append("# Update and settings-survival scenarios\n\n");
        sb.Append("A season's worth of settings (units, language, hotkeys, display, connections, NTRIP casters), two vehicle + implement ")
          .Append("profile pairs with their steering settings, fields with a boundary, a headland, AB and curve tracks, flags and a sprayed job, is made ")
          .Append("on an **older AgOpenWeb release** (its Linux daemon bundle from the GitHub release, run headless as a child process and driven over its ")
          .Append("WebSocket with the web client's own commands), shut down as systemd does (SIGTERM). Then **this build** starts in-process on the same ")
          .Append("data folder, as an update in place does, and everything is read back through the app's own services and compared with what was set. ")
          .Append("The same farm is made on this build and read back after a plain restart, a crash, a damaged settings file, a moved data folder, a ")
          .Append("comma-decimal locale, and from an AgOpenGPS field folder dropped in by hand.\n\n");
        string? sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrEmpty(sha)) sb.Append("Commit: `").Append(sha).Append("`\n\n");
        foreach (var e in Environment_) sb.Append("- ").Append(e).Append('\n');
        if (Environment_.Count > 0) sb.Append('\n');

        var ordered = Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code, StringComparer.Ordinal).ToList();
        var fresh = ordered.Where(v => !Known.ContainsKey(v.First.Code)).ToList();
        var known = ordered.Where(v => Known.ContainsKey(v.First.Code)).ToList();

        sb.Append("## Findings (each kind once, most serious first)\n\n");
        sb.Append(string.Create(inv, $"**New: {fresh.Count}. Already reported: {known.Count}.** A code is \"already reported\" when it is listed in `KnownFindings.txt`.\n\n"));
        int n = 1;
        if (fresh.Count > 0) sb.Append("### New\n\n");
        foreach (var g in fresh)
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** `{g.First.Code}` ×{g.Count}: {g.First.Message}\n"));
            sb.Append("   Seen in: ").Append(string.Join(", ", g.Places)).Append('\n');
        }
        if (known.Count > 0) sb.Append("\n### Already reported\n\n");
        foreach (var g in known)
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** `{g.First.Code}` ×{g.Count}: {g.First.Message}\n"));
            sb.Append("   Seen in: ").Append(string.Join(", ", g.Places));
            if (Known[g.First.Code].Length > 0) sb.Append(". Reported: ").Append(Known[g.First.Code]);
            sb.Append('\n');
        }
        if (Findings.Count == 0) sb.Append("None.\n");

        foreach (var op in Rows.Select(r => r.Operator).Distinct())
        {
            sb.Append("\n## ").Append(op).Append('\n');
            foreach (var r in Rows.Where(x => x.Operator == op))
            {
                sb.Append("\n### ").Append(r.Code).Append(". ").Append(r.Title).Append("\n\n").Append(r.Story).Append("\n\n");
                foreach (var c in r.Checks) sb.Append("- ").Append(c).Append('\n');
                if (r.Files.Length > 0) sb.Append("\n").Append(r.Files).Append('\n');
                sb.Append("\n<details><summary>Timeline</summary>\n\n```\n");
                foreach (var s in r.Steps) sb.Append(s).Append('\n');
                if (r.OldBuildLog.Count > 0)
                {
                    sb.Append("--- the older build's console\n");
                    foreach (var l in r.OldBuildLog) sb.Append(l).Append('\n');
                }
                sb.Append("```\n\n</details>\n");
            }
        }
        return sb.ToString();
    }
}
