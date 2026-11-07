// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

public enum Severity
{
    /// <summary>The machine does something dangerous (steers or sprays on a position it should not trust).</summary>
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

/// <summary>One scenario: what the operator did, what the internet and the caster did, what the screen and the receiver showed.</summary>
public sealed class ScenarioRow
{
    public string Operator = "", Code = "", Title = "", Story = "";
    public List<string> Steps = new();
    public List<string> Checks = new();
    public List<Finding> Findings = new();
    public List<string> Logs = new();
    public string Rtcm = "";
}

/// <summary>The clock, the steps and the checks of one scenario.</summary>
public sealed class Scenario
{
    public ScenarioRow Row { get; }
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Scenario(string op, string code, string title, string story)
    {
        Row = new ScenarioRow { Operator = op, Code = code, Title = title, Story = story };
        if (Journal.Verbose) Console.WriteLine($"[gn] ===== {op} {code}: {title}");
    }

    public void Step(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,5:0.0} s  {text}");
        Row.Steps.Add(line);
        if (Journal.Verbose) Console.WriteLine("[gn]   " + line);
    }

    public void Ok(string text)
    {
        Row.Checks.Add("✓ " + text);
        if (Journal.Verbose) Console.WriteLine("[gn]   OK " + text);
    }

    public void Problem(Severity severity, string code, string text)
    {
        Row.Checks.Add($"✗ **{severity}** `{code}`: {text}");
        Row.Findings.Add(new Finding(code, severity, text));
        if (Journal.Verbose) Console.WriteLine($"[gn]   FINDING {severity} {code}: {text}");
    }

    public void Note(string text)
    {
        Row.Checks.Add("• " + text);
        if (Journal.Verbose) Console.WriteLine("[gn]   NOTE " + text);
    }

    public void Harness(string text) => Problem(Severity.Bug, "HARNESS", text);

    /// <summary>The last lines of the caster's, the internet's and the receiver's own logs, and the RTCM counts.</summary>
    public void Snapshot(GpsSession s)
    {
        var lines = s.Caster.Log.Concat(s.Net.Log).Concat(s.Rx.Log).ToList();
        Row.Logs = lines.TakeLast(24).ToList();
        var rx = s.Rx;
        Row.Rtcm = string.Create(CultureInfo.InvariantCulture,
            $"caster connections {s.Caster.Accepted} (at most {s.Caster.MaxStreaming} streaming at once), GGA received {s.Caster.GgasReceived}; receiver got {rx.Datagrams} datagrams, {rx.Bytes} bytes, {rx.Messages} whole messages ({rx.StationMessages} base position), checksum failures {rx.ChecksumFailures}, over 512 bytes {rx.Oversize}, back-to-back datagrams (under 5 ms) {rx.CloseGaps} (smallest {(double.IsInfinity(rx.MinGapMs) ? 0 : rx.MinGapMs):0.0} ms), older epochs after newer {rx.StaleEpochs}");
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;
}

/// <summary>Collects every scenario and finding, and writes the report.</summary>
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
        sb.Append("# GPS and NTRIP scenarios: the careful and the careless operator\n\n");
        sb.Append("AgOpenWeb's whole backend runs in-process with its real UDP service on port 9999 and its real NTRIP client. A fake caster ")
          .Append("(NTRIP 2.0 chunked and 1.0, 401 for a wrong login, the source table for an unknown mount point, a VRS mount point that waits for a GGA) ")
          .Append("sends an RTCM 3 epoch every second (MSM7 for four systems, base position every 10 s) through a fake internet that can drop silently, ")
          .Append("refuse, flap or switch networks. An emulated receiver takes what AgOpenWeb forwards to UDP 2233 (whole messages with a good checksum; ")
          .Append("it counts datagrams over 512 bytes, which an AiO v26 drops, and datagrams back to back), keeps RTK fixed while its corrections are at most 10 s old, ")
          .Append("float up to 30 s, then single, and sends $PAOGI or $PANDA at 10 Hz. A steer module stub sends PGN 253. The tractor drives north (east in C9) at 8 km/h ")
          .Append("on an AB line with sections on Auto and AutoSteer on. Everything runs in real time; the screen is sampled every 100 ms.\n\n");

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
                if (r.Rtcm.Length > 0) sb.Append("\nCorrections: ").Append(r.Rtcm).Append('\n');
                sb.Append("\n<details><summary>Timeline</summary>\n\n```\n");
                foreach (var s in r.Steps) sb.Append(s).Append('\n');
                if (r.Logs.Count > 0)
                {
                    sb.Append("--- caster, internet and receiver (each on its own clock)\n");
                    foreach (var l in r.Logs) sb.Append(l).Append('\n');
                }
                sb.Append("```\n\n</details>\n");
            }
        }
        return sb.ToString();
    }
}
