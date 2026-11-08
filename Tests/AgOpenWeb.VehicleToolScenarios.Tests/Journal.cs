// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

public enum Severity
{
    /// <summary>AutoSteer steers the machine somewhere it should not go, or the module is sent nonsense.</summary>
    Safety,
    /// <summary>Wrong data or wrong behaviour the operator can hit.</summary>
    Bug,
    /// <summary>Two parts of the app disagree.</summary>
    Inconsistency,
    /// <summary>An impossible value accepted and kept, with no word to the operator.</summary>
    Robustness,
    /// <summary>It works, but the operator is not told, or could be helped.</summary>
    Improvement,
}

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One drive of the passes: what was set, what the machine really did.</summary>
public sealed class DriveRow
{
    public string Operator = "", Code = "", Mistake = "", Vehicle = "", Implement = "", System = "";
    public double Slope;
    public List<PassResult> Passes = new();
    public (double Gap, double Overlap, double Mean) Joins;
    public List<string> Said = new();
    public List<string> Problems = new();
    public List<Finding> Findings = new();
    public string Note = "";

    public double LineError => Passes.Where(p => p.Samples > 0).Select(p => p.LineError).DefaultIfEmpty(double.NaN).Average();
    public double WorstLineError => Passes.Where(p => p.Samples > 0).Select(p => Math.Abs(p.LineError)).DefaultIfEmpty(double.NaN).Max();
    public double MaxDev => Passes.Where(p => p.Samples > 0).Select(p => p.MaxLineDev).DefaultIfEmpty(double.NaN).Max();
    public double SteerMax => Passes.Select(p => p.MaxSteerCmd).DefaultIfEmpty(0).Max();
    public double ToolSide => Passes.Select(p => p.AppToolSide).DefaultIfEmpty(0).Max();
    public double ToolAlong => Passes.Select(p => p.AppToolAlong).DefaultIfEmpty(0).Max();
    public double HeadingErr => Passes.Select(p => p.AppHeadingErr).DefaultIfEmpty(0).Max();
    public double SectionsOn => Passes.Where(p => !double.IsNaN(p.SectionsOnShare)).Select(p => p.SectionsOnShare).DefaultIfEmpty(double.NaN).Min();
}

/// <summary>One setting the operator typed, and what the app made of it.</summary>
public sealed class SettingRow
{
    public string Group = "", What = "", Sent = "", Stored = "", Effect = "";
    public List<string> Said = new();
    public List<Finding> Findings = new();
}

/// <summary>Collects every drive, setting and finding, and writes the report.</summary>
public sealed class Journal
{
    public List<DriveRow> Drives { get; } = new();
    public List<SettingRow> Settings { get; } = new();
    /// <summary>The settings-effect sweep: one row per setting tried.</summary>
    public List<SweepRow> Sweep { get; } = new();
    public List<string> SweepNotes { get; } = new();
    public Dictionary<string, (Finding First, int Count, List<string> Places)> Findings { get; } = new();

    public void Add(Finding f, string where)
    {
        string key = f.Code;
        if (Findings.TryGetValue(key, out var e))
        {
            if (e.Places.Count < 8 && !e.Places.Contains(where)) e.Places.Add(where);
            Findings[key] = (e.First, e.Count + 1, e.Places);
        }
        else Findings[key] = (f, 1, new List<string> { where });
        if (FieldRun.Verbose) Console.WriteLine($"[vt] FINDING {f.Severity} {f.Code}: {f.Message} ({where})");
    }

    private static string N(double v, string fmt = "0.00") =>
        double.IsNaN(v) ? "–" : v.ToString(fmt, CultureInfo.InvariantCulture);

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# Vehicle & Tool scenarios: the careful and the careless operator\n\n");
        sb.Append("A simulated machine (bicycle model at the pivot axle, a steering motor with its speed and lag, the implement on its real linkage, ")
          .Append("the antenna moving sideways when a slope tilts the cab) drives AgOpenWeb's whole backend: its receiver sentences ($PAOGI from a dual ")
          .Append("receiver, $PANDA with the IMU's roll and heading from a single one) go through the real parser, heading fusion, roll correction, guidance ")
          .Append("and section control, and AgOpenWeb's steer angle drives the machine back. Distances are where the real implement went.\n\n");
        sb.Append("Columns: **line** = the implement's mean distance from the pass line AgOpenWeb steers to (+ = east); **dev** = its worst distance; ")
          .Append("**gap/overlap** = worst gap (+) and overlap (−) between neighbouring passes, from the real implement's ends; **tool side/along** = how far ")
          .Append("AgOpenWeb's implement is from the real one; **hdg** = AgOpenWeb's heading error (°); **steer** = largest steer angle sent (°); ")
          .Append("**sect** = the share of section time on.\n\n");

        sb.Append("## Findings (each kind once, most serious first)\n\n");
        int n = 1;
        foreach (var g in Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code))
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** `{g.First.Code}` ×{g.Count}: {g.First.Message}\n"));
            sb.Append("   Seen in: ").Append(string.Join("; ", g.Places)).Append('\n');
        }

        foreach (var op in Drives.Select(d => d.Operator).Distinct())
        {
            sb.Append("\n## Drives: ").Append(op).Append("\n\n");
            sb.Append("| Scenario | Machine | System | Slope | line | dev | gap | overlap | tool side | tool along | hdg | steer | sect | said |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var d in Drives.Where(x => x.Operator == op))
            {
                string said = string.Join(" / ", d.Said.Concat(d.Problems.Select(p => "⚠ " + p)).Select(Trim));
                sb.Append(string.Create(inv,
                    $"| {d.Mistake} | {d.Vehicle} + {d.Implement} | {d.System} | {d.Slope:0}° | {N(d.LineError)} | {N(d.MaxDev)} | {N(d.Joins.Gap)} | {N(d.Joins.Overlap)} | {N(d.ToolSide)} | {N(d.ToolAlong)} | {N(d.HeadingErr, "0.0")} | {N(d.SteerMax, "0.0")} | {N(d.SectionsOn)} | {said} |\n"));
            }
        }

        if (Settings.Count > 0)
        {
            sb.Append("\n## Settings typed wrong: what the app kept\n\n| Group | Mistake | Sent | Kept | Effect | said |\n|---|---|---|---|---|---|\n");
            foreach (var s in Settings)
                sb.Append($"| {s.Group} | {s.What} | `{s.Sent}` | {s.Stored} | {s.Effect} | {string.Join(" / ", s.Said.Select(Trim))} |\n");
        }

        if (Sweep.Count > 0)
        {
            sb.Append("\n## Settings effect sweep\n\n");
            sb.Append("Each setting flipped on a short drive that excites it, against the same drive on the base settings (run three times: the noise; a section that switched a different number of times between them is shown as noisy and not judged). ")
              .Append("**Changed** is what moved beyond the noise: the steer angle AgOpenWeb computes or sends in PGN 254, whether it steers (PGN 254 status, AutoSteer on), ")
              .Append("the bytes the modules are sent (PGN 251/252/247/238/236/235, `[byte] old→new`), where the sections switch (PGN 239), the pass shown. ")
              .Append("**Kind** proposes app defect / harness artifact / parity / enhancement / harness gap for a reviewer to confirm.\n\n");
            foreach (var note in SweepNotes) sb.Append(note).Append("\n\n");
            sb.Append("| Setting | From → to | Probe | Expected | Changed | Verdict | Kind |\n|---|---|---|---|---|---|---|\n");
            foreach (var r in Sweep.OrderBy(r => r.Case.Probe == Probe.None).ThenBy(r => r.Case.Key, StringComparer.Ordinal).ThenBy(r => r.Case.Group, StringComparer.Ordinal))
            {
                var c = r.Case;
                string fromTo = c.Probe == Probe.None ? "–" : c.Base.Length > 0 ? $"`{c.Base}` → `{c.Flip}`" : "(button)";
                sb.Append($"| `{c.Key}` {Trim(c.What)} | {fromTo} | {c.Group} | {SettingsSweep.Expected(c.Expect)} | {Trim(r.Changed)} | {r.Verdict} | {r.Kind} |\n");
            }
        }

        sb.Append("\n## Pass logs\n");
        foreach (var d in Drives)
        {
            sb.Append("\n### ").Append(d.Operator).Append(": ").Append(d.Mistake).Append("\n\n");
            if (d.Note.Length > 0) sb.Append(d.Note).Append("\n\n");
            foreach (var p in d.Passes)
                sb.Append(string.Create(inv,
                    $"- pass {p.Index + 1} {(p.Northbound ? "N" : "S")}: implement at {N(p.MeanX, "0.000")} m [{N(p.MinX, "0.000")} … {N(p.MaxX, "0.000")}], line {N(p.LineError, "0.000")}, dev {N(p.MaxLineDev, "0.000")}, AgOpenWeb pass {p.AppPass}, steer ≤ {N(p.MaxSteerCmd, "0.0")}° ({N(p.SaturatedShare * 100, "0")} % at the stop), tool side {N(p.AppToolSide, "0.000")} along {N(p.AppToolAlong, "0.000")}, pivot {N(p.AppPivotErr, "0.000")}, heading {N(p.AppHeadingErr, "0.00")}°, sections {N(p.SectionsOnShare)}, {p.Samples} fixes{(p.Disengaged != null ? ", AutoSteer off " + p.Disengaged : "")}{(p.RanOff ? ", LEFT THE PASS" : "")}\n"));
            foreach (var f in d.Findings)
                sb.Append("- **").Append(f.Severity.ToString()).Append("** `").Append(f.Code).Append("` ").Append(f.Message).Append('\n');
        }
        return sb.ToString();
    }

    private static string Trim(string s)
    {
        s = s.Replace("\n", " ", StringComparison.Ordinal).Replace("|", "/", StringComparison.Ordinal);
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
