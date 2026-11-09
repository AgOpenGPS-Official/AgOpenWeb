// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>What the operator meant by a step, so the harness knows what to complain about.</summary>
public enum Expect
{
    /// <summary>A correct action: a refusal or an error message is itself a finding.</summary>
    Works,
    /// <summary>A wrong action: it must change nothing harmful, and should say why it was refused.</summary>
    Refused,
    /// <summary>No opinion (random mischief): only the invariants count.</summary>
    Any,
}

public sealed class StepRecord
{
    public int Index;
    public string What = "";
    public Expect Expect;
    public long Ms;
    public List<string> Messages = new();
    public List<Finding> NewFindings = new();
    public string After = "";
}

public sealed class ScenarioRecord
{
    public string Name = "";
    public string Operator = "";
    public List<StepRecord> Steps = new();
}

/// <summary>Collects every scenario's steps and findings and writes the reports.</summary>
public sealed class ScenarioJournal
{
    public List<ScenarioRecord> Scenarios { get; } = new();

    /// <summary>Every finding with where it was first seen and how often.</summary>
    public Dictionary<string, (Finding First, string Where, int Count, List<string> Places)> Findings { get; } = new();

    public void Add(Finding f, string where)
    {
        string key = f.Code + "|" + Normalize(f.Message);
        if (Findings.TryGetValue(key, out var e))
        {
            if (e.Places.Count < 6 && !e.Places.Contains(where)) e.Places.Add(where);
            Findings[key] = (e.First, e.Where, e.Count + 1, e.Places);
        }
        else Findings[key] = (f, where, 1, new List<string> { where });
    }

    /// <summary>Numbers (their sign included) and quoted names vary from field to field; the rest
    /// identifies the finding, so "near -0.0,-0.0" and "near 50.0,50.0" are one kind.</summary>
    private static string Normalize(string m)
    {
        var sb = new StringBuilder();
        bool inNum = false, inQuote = false;
        for (int i = 0; i < m.Length; i++)
        {
            char c = m[i];
            if (c == '\'') { inQuote = !inQuote; sb.Append("'…'"); continue; }
            if (inQuote) continue;
            bool digit = char.IsDigit(c) || (inNum && (c == '.' || c == '-'))
                || (!inNum && c == '-' && i + 1 < m.Length && char.IsDigit(m[i + 1]));
            if (digit) { if (!inNum) sb.Append('#'); inNum = true; continue; }
            inNum = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public string ToMarkdown(string title, string intro)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n").Append(intro).Append("\n\n");

        sb.Append("## Scenarios\n\n| Scenario | Operator | Steps | Bug | Inconsistency | Robustness | Improvement |\n|---|---|---|---|---|---|---|\n");
        foreach (var s in Scenarios)
        {
            var all = s.Steps.SelectMany(x => x.NewFindings).ToList();
            sb.Append(string.Create(inv, $"| {s.Name} | {s.Operator} | {s.Steps.Count} | {all.Count(x => x.Severity == Severity.Bug)} | {all.Count(x => x.Severity == Severity.Inconsistency)} | {all.Count(x => x.Severity == Severity.Robustness)} | {all.Count(x => x.Severity == Severity.Improvement)} |\n"));
        }

        sb.Append("\n## Findings (each kind once, most serious first)\n\n");
        int n = 1;
        foreach (var g in Findings.Values.OrderBy(v => v.First.Severity).ThenBy(v => v.First.Code).ThenByDescending(v => v.Count))
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
            foreach (var st in s.Steps)
            {
                sb.Append(string.Create(inv, $"{st.Index}. {st.What}"));
                if (st.Expect == Expect.Refused) sb.Append(" *(should be refused)*");
                // A step's time is noise from night to night; only a slow one is worth a word.
                if (st.Ms >= 2000) sb.Append(string.Create(inv, $" — slow: {st.Ms / 1000.0:0.0} s"));
                // The same status line can arrive two or three times in one step (events race the
                // snapshot): once is the fact, so two nights' reports read the same.
                if (st.Messages.Count > 0) sb.Append(" — says: ").Append(string.Join(" / ", st.Messages.Distinct(StringComparer.Ordinal).Select(m => "\"" + Trim(m) + "\"")));
                sb.Append('\n');
                foreach (var f in st.NewFindings)
                    sb.Append("   - **").Append(f.Severity.ToString()).Append("** `").Append(f.Code).Append("` ").Append(f.Message).Append('\n');
                if (st.NewFindings.Count > 0 || st.Index % 10 == 0)
                    sb.Append("   - state: ").Append(st.After).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length > 140 ? s[..140] + "…" : s.Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>
/// One operator working through one story. Every step: run it, collect what the app said and
/// threw, take a snapshot, run the invariants, and keep only the findings that are new since
/// the step before (a self-crossing boundary is reported by the step that drew it, not by
/// every step after it).
/// </summary>
public sealed class Scenario
{
    private readonly FieldScenarioRig _rig;
    private readonly ScenarioJournal _journal;
    private readonly ScenarioRecord _record;
    private HashSet<string> _active = new();
    private int _index;

    public FieldScenarioRig Rig => _rig;
    public FieldSnapshot Last { get; private set; } = new();
    public StepRecord? LastStep => _record.Steps.Count > 0 ? _record.Steps[^1] : null;
    public static bool Verbose => Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") == "1";

    public Scenario(FieldScenarioRig rig, ScenarioJournal journal, string name, string op)
    {
        _rig = rig;
        _journal = journal;
        _record = new ScenarioRecord { Name = name, Operator = op };
        journal.Scenarios.Add(_record);
    }

    public string Name => _record.Name;

    public async Task<FieldSnapshot> Step(string what, Func<Task> act, Expect expect = Expect.Works)
    {
        var rec = new StepRecord { Index = ++_index, What = what, Expect = expect };
        var sw = Stopwatch.StartNew();
        try { await act(); }
        catch (Exception ex)
        {
            rec.NewFindings.Add(new Finding("HARNESS", Severity.Bug, "the step itself threw: " + ex.GetType().Name + ": " + ex.Message));
        }
        rec.Ms = sw.ElapsedMilliseconds;

        var events = _rig.TakeEvents();
        var found = new List<Finding>();
        var offLoop = new List<string>();
        bool refused = false;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RigEventKind.Exception:
                    found.Add(new Finding("EXCEPTION", Severity.Bug, "host loop work item threw " + FirstLines(e.Text, 3)));
                    break;
                case RigEventKind.Error:
                    found.Add(new Finding("LOG-ERROR", Severity.Bug, "error logged: " + FirstLines(e.Text, 1)));
                    break;
                case RigEventKind.Hang:
                    found.Add(new Finding("HANG", Severity.Bug, e.Text));
                    break;
                case RigEventKind.OffLoop:
                    offLoop.Add(e.Text);
                    break;
                case RigEventKind.Failure:
                    refused = true;
                    rec.Messages.Add("⚠ " + e.Text);
                    break;
                case RigEventKind.Status:
                    if (!IsNoise(e.Text)) rec.Messages.Add(e.Text);
                    break;
                case RigEventKind.Note:
                    rec.Messages.Add("(" + e.Text + ")");
                    break;
            }
        }

        if (offLoop.Count > 0)
        {
            // One finding per step: which VM properties were written from another thread, and
            // the code path of the first one (the properties differ, the cause is the same).
            var props = offLoop.Select(t => t.Split(' ')[0]).Distinct().ToList();
            var first = offLoop[0];
            var at = first.Contains(" at ", StringComparison.Ordinal) ? first[(first.IndexOf(" at ", StringComparison.Ordinal) + 4)..] : "";
            found.Add(new Finding("OFF-LOOP", Severity.Inconsistency,
                "VM state written from a thread-pool thread, not the host loop (" + props.Count.ToString(CultureInfo.InvariantCulture) + " properties: "
                + string.Join(", ", props.Take(8)) + (props.Count > 8 ? ", …" : "") + "); first via " + at));
        }

        var snap = await _rig.ReadAsync(() => FieldSnapshot.Take(_rig));
        found.AddRange(FieldInvariants.Check(snap));
        if (snap.IsPromptPending)
            rec.Messages.Add("prompt open: " + snap.PromptText);

        if (expect == Expect.Works && refused)
            found.Add(new Finding("REFUSED-GOOD", Severity.Bug, "a correct action was refused: " + string.Join(" / ", rec.Messages.Where(m => m.StartsWith('⚠')))));

        // Only what is new since the previous step.
        var now = new HashSet<string>();
        foreach (var f in found)
        {
            var key = f.Code + "|" + f.Message;
            now.Add(key);
            bool persistent = f.Code is not ("EXCEPTION" or "LOG-ERROR" or "HANG" or "REFUSED-GOOD" or "HARNESS" or "SILENT" or "CHECK");
            if (!persistent || !_active.Contains(key))
            {
                rec.NewFindings.Add(f);
            }
        }
        _active = now;

        rec.After = snap.Summary();
        Last = snap;
        _record.Steps.Add(rec);
        foreach (var f in rec.NewFindings)
            _journal.Add(f, _record.Name + " #" + rec.Index.ToString(CultureInfo.InvariantCulture) + " (" + what + ")");
        if (Verbose)
        {
            TestContext.Out.WriteLine($"[{_record.Name}] {rec.Index}. {what} ({rec.Ms} ms) {string.Join(" / ", rec.Messages)}");
            foreach (var f in rec.NewFindings) TestContext.Out.WriteLine($"      {f.Severity} {f.Code}: {f.Message}");
        }
        return snap;
    }

    /// <summary>An extra, step-specific expectation (e.g. "the AB line was added").</summary>
    public void Check(bool ok, Severity sev, string message)
    {
        if (ok || LastStep is not { } st) return;
        var f = new Finding("CHECK", sev, message);
        st.NewFindings.Add(f);
        _journal.Add(f, _record.Name + " #" + st.Index.ToString(CultureInfo.InvariantCulture) + " (" + st.What + ")");
        if (Verbose) TestContext.Out.WriteLine($"      {f.Severity} CHECK: {f.Message}");
    }

    /// <summary>A refused action should say why: flag a refusal-worthy step that left no message.</summary>
    public void ExpectFeedback(string code = "SILENT")
    {
        if (LastStep is not { } st || st.Messages.Count > 0) return;
        var f = new Finding(code, Severity.Improvement, "wrong input ignored without any message: " + st.What);
        st.NewFindings.Add(f);
        _journal.Add(f, _record.Name + " #" + st.Index.ToString(CultureInfo.InvariantCulture) + " (" + st.What + ")");
    }

    private static bool IsNoise(string s) =>
        s.StartsWith("Waiting for modules", StringComparison.Ordinal) || s == "GPS Timeout" || s.StartsWith("Simulator reset", StringComparison.Ordinal)
        || s.StartsWith("Ready", StringComparison.Ordinal) || s.Length == 0;

    private static string FirstLines(string s, int n) =>
        string.Join(" | ", s.Split('\n').Take(n).Select(l => l.Trim()));
}
