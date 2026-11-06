// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>How bad a finding is. Safety first: anything that moves the wheels when nobody
/// asked, keeps them moving with no screen, or leaves a setting that makes AutoSteer steer
/// the wrong way.</summary>
public enum Severity { Safety, Bug, Inconsistency, Robustness, Improvement }

public sealed record Finding(Severity Severity, string Code, string Message);

/// <summary>What the operator meant by a step, so the harness knows what to complain about.</summary>
public enum Expect
{
    /// <summary>A correct action: a refusal or an error is itself a finding.</summary>
    Works,
    /// <summary>A wrong action: it must change nothing harmful and should say why.</summary>
    Refused,
    /// <summary>No opinion: only the invariants count.</summary>
    Any,
}

public sealed class StepRecord
{
    public int Index;
    public string What = "";
    public Expect Expect;
    public long Ms;
    public List<string> Says = new();
    public List<Finding> Findings = new();
    public string After = "";
}

public sealed class ScenarioRecord
{
    public string Name = "";
    public string Operator = "";
    public string Truth = "";
    public List<StepRecord> Steps = new();
    public long Ms;
}

/// <summary>Every scenario's steps and findings; writes the report.</summary>
public sealed class ScenarioJournal
{
    public List<ScenarioRecord> Scenarios { get; } = new();
    public Dictionary<string, (Finding First, string Where, int Count, List<string> Places)> Findings { get; } = new();

    public void Add(Finding f, string where)
    {
        string key = f.Code;
        if (Findings.TryGetValue(key, out var e))
        {
            if (e.Places.Count < 8 && !e.Places.Contains(where)) e.Places.Add(where);
            Findings[key] = (e.First, e.Where, e.Count + 1, e.Places);
        }
        else Findings[key] = (f, where, 1, new List<string> { where });
    }

    public string ToMarkdown(string title, string intro)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n").Append(intro).Append("\n\n");

        sb.Append("## Scenarios\n\n| Scenario | Operator | Steps | Safety | Bug | Inconsistency | Robustness | Improvement | Time |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (var s in Scenarios)
        {
            var all = s.Steps.SelectMany(x => x.Findings).ToList();
            int C(Severity v) => all.Count(x => x.Severity == v);
            sb.Append(string.Create(inv, $"| {s.Name} | {s.Operator} | {s.Steps.Count} | {C(Severity.Safety)} | {C(Severity.Bug)} | {C(Severity.Inconsistency)} | {C(Severity.Robustness)} | {C(Severity.Improvement)} | {s.Ms / 1000.0:F0} s |\n"));
        }

        sb.Append("\n## Findings (each kind once, most serious first)\n\n");
        int n = 1;
        foreach (var g in Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code))
        {
            sb.Append(string.Create(inv, $"{n++}. **{g.First.Severity}** `{g.First.Code}` ×{g.Count}: {g.First.Message}\n"));
            sb.Append("   First seen: ").Append(g.Where).Append('\n');
            if (g.Places.Count > 1)
                sb.Append("   Also: ").Append(string.Join("; ", g.Places.Skip(1))).Append('\n');
        }

        sb.Append("\n## Step logs\n");
        foreach (var s in Scenarios)
        {
            sb.Append("\n### ").Append(s.Name).Append(" (").Append(s.Operator).Append(")\n\n");
            if (s.Truth.Length > 0) sb.Append("Tractor: ").Append(s.Truth).Append("\n\n");
            foreach (var st in s.Steps)
            {
                sb.Append(string.Create(inv, $"{st.Index}. {st.What}"));
                if (st.Expect == Expect.Refused) sb.Append(" *(should be refused)*");
                sb.Append(string.Create(inv, $" — {st.Ms} ms"));
                if (st.Says.Count > 0) sb.Append(" — says: ").Append(string.Join(" / ", st.Says.Select(m => "\"" + Trim(m) + "\"")));
                sb.Append('\n');
                foreach (var f in st.Findings)
                    sb.Append("   - **").Append(f.Severity.ToString()).Append("** `").Append(f.Code).Append("` ").Append(f.Message).Append('\n');
                if (st.After.Length > 0) sb.Append("   - state: ").Append(st.After).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Trim(string s)
    {
        s = s.Replace("\n", " ", StringComparison.Ordinal);
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
