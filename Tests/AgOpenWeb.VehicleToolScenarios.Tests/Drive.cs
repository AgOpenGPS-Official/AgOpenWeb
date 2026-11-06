// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>One drive: the real machine, what the operator typed into AgOpenWeb, and what to
/// make of it.</summary>
public sealed record DriveSpec(
    string Code,
    string Mistake,
    VehicleSpec V,
    ToolSpec T,
    SystemSpec Sys,
    double Slope,
    IReadOnlyList<string> AppSettings,
    int Passes = 3,
    Func<FieldRun, int, Task>? MidPass = null,
    // Run after the settings, before the first pass (e.g. saving another profile to load later).
    Func<FieldRun, Task>? Prepare = null,
    // The app could have known the value is impossible (a negative wheelbase, an overlap as
    // wide as the implement): accepting it with no word is a finding.
    bool Impossible = false,
    string Note = "");

public static class Drive
{
    public static string DataRootBase = Path.Combine(Path.GetTempPath(), "agopenweb-vehicle-tool");

    /// <summary>The settings a careful operator types for this machine (Vehicle, then Tool).</summary>
    public static List<string> Careful(VehicleSpec v, ToolSpec t, SystemSpec sys) => v.Settings(sys).Concat(t.Settings()).ToList();

    /// <summary>The careful settings with some keys replaced (or added) by the operator's mistakes.</summary>
    public static List<string> With(VehicleSpec v, ToolSpec t, SystemSpec sys, params string[] mistakes)
    {
        var list = Careful(v, t, sys);
        foreach (var m in mistakes)
        {
            string key = m[..m.IndexOf(':')];
            int at = list.FindIndex(x => x.StartsWith(key + ":", StringComparison.Ordinal));
            if (at >= 0 && key != "tool.sectionWidth") list[at] = m;
            else if (key == "tool.sectionWidth")
            {
                string idx = m[(key.Length + 1)..m.IndexOf(',')];
                int s = list.FindIndex(x => x.StartsWith(key + ":" + idx + ",", StringComparison.Ordinal));
                if (s >= 0) list[s] = m; else list.Add(m);
            }
            else list.Add(m);
        }
        return list;
    }

    public static async Task<DriveRow> RunAsync(Journal journal, string op, DriveSpec d)
    {
        var row = new DriveRow
        {
            Operator = op, Code = d.Code, Mistake = d.Mistake, Vehicle = d.V.Name, Implement = d.T.Name,
            System = d.Sys.Label, Slope = d.Slope, Note = d.Note,
        };
        journal.Drives.Add(row);
        string dir = Path.Combine(DataRootBase, op[..1] + "-" + d.Code + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var truth = new TruthMachine(d.V, d.T, d.Slope, d.Sys);
        FieldRun? run = null;
        try
        {
            run = await FieldRun.StartAsync(dir, truth);
            if (!await run.MakeFieldAsync())
            {
                row.Problems.AddRange(run.Problems);
                Add(row, journal, new Finding("HARNESS", Severity.Bug, "the harness could not make its field: " + string.Join("; ", run.Problems)));
                return row;
            }
            run.CollectMessages();
            run.Messages.Clear();
            await run.ApplyAsync(d.AppSettings);
            await run.Rig.WaitForVmAsync();
            if (d.Prepare != null) await d.Prepare(run);
            // The harness sends no fixes while it types the settings: the GPS timeout that
            // gives is its own doing.
            row.Said.AddRange(run.CollectMessages().Select(Clean).Where(m => !IsNoise(m) && !m.Contains("GPS data lost", StringComparison.Ordinal)));
            run.Messages.Clear();
            await run.DrivePassesAsync(d.Passes, d.MidPass == null ? null : k => d.MidPass(run, k));
            row.Passes.AddRange(run.Passes);
            row.Joins = run.GapsAndOverlaps();
            foreach (var m in run.Messages.Select(Clean))
                if (!row.Said.Contains(m) && !IsNoise(m)) row.Said.Add(m);
            row.Problems.AddRange(run.Problems);
        }
        catch (Exception ex)
        {
            Add(row, journal, new Finding("HARNESS", Severity.Bug, "the drive threw " + ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            if (run != null) await run.DisposeAsync();
            try { Directory.Delete(dir, true); } catch { }
        }

        // What any drive must not do, careful settings or not.
        foreach (var p in row.Problems)
        {
            if (p.Contains("steer angle", StringComparison.Ordinal))
                Add(row, journal, new Finding("STEER-NAN", Severity.Safety, "AgOpenWeb computed a steer angle that is not a number"));
            else if (p.Contains("left the pass", StringComparison.Ordinal))
                Add(row, journal, new Finding(d.Code + "-RAN-OFF", Severity.Safety,
                    d.Mistake + ": with AutoSteer on, the machine left its pass (" + p + ")"));
        }
        foreach (var m in row.Said)
            if (m.StartsWith("Exception", StringComparison.Ordinal) || m.StartsWith("Error", StringComparison.Ordinal))
                Add(row, journal, new Finding("LOG-" + d.Code, Severity.Bug, d.Mistake + ": " + m));
        return row;
    }

    public static void Add(DriveRow row, Journal journal, Finding f)
    {
        row.Findings.Add(f);
        journal.Add(f, row.Operator + ": " + row.Mistake);
    }

    /// <summary>What the app told the operator: not its own chatter, nor the warning that the
    /// receiver went quiet (the settings checks run with no receiver).</summary>
    public static bool Spoken(RigEvent e) =>
        e.Kind is RigEventKind.Status or RigEventKind.Failure or RigEventKind.Warning or RigEventKind.Error or RigEventKind.Exception
        && !IsNoise(Clean(e.Text)) && !e.Text.Contains("GPS data lost", StringComparison.Ordinal)
        && !e.Text.Contains("No GPS input", StringComparison.Ordinal);

    private static string Clean(string m) => m.StartsWith("Status: ", StringComparison.Ordinal) ? m[8..] : m;

    private static bool IsNoise(string m) =>
        m.StartsWith("Created field", StringComparison.Ordinal) || m.StartsWith("Opened field", StringComparison.Ordinal)
        || m.StartsWith("Line '", StringComparison.Ordinal) || m.StartsWith("Simulator reset", StringComparison.Ordinal)
        || m.StartsWith("Waiting for modules", StringComparison.Ordinal) || m.StartsWith("GPS Timeout", StringComparison.Ordinal)
        || m.Contains("origin re-anchored", StringComparison.Ordinal);

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
