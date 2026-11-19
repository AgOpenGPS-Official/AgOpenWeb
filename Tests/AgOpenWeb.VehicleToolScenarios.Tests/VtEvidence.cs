// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The evidence of one backend's work: a drive, a group of the settings sweep, the settings typed
/// wrong, the profile checks (<c>Tests/ScenarioShared/Evidence.cs</c>). A kind of finding gets the
/// app's bug report dump taken where it was first seen in the run, while that backend still runs
/// and before the harness puts anything back: at the end of a drive, right after the sweep's row
/// was driven (the setting still flipped), right after the wrong setting or profile action. Beside
/// it, <c>attachments/commands.txt</c>: every command this backend was sent, as the web client sends
/// it (<c>id|arg</c>), and the harness's own steps (<c>#</c> lines).
/// </summary>
public sealed class VtEvidence
{
    /// <summary>The <c>evidence/</c> folder beside the report; set by the test before anything runs.</summary>
    public static string Folder = Path.Combine(Path.GetTempPath(), "agopenweb-vehicle-tool-evidence");

    public readonly Evidence Ev;
    private readonly Journal _journal;
    private readonly string _scenario, _title;
    private readonly List<string> _actions = new();
    private bool _kept;

    /// <summary>Made before the backend starts: the app's log and the GPS recorder are the
    /// process's, and are cleared here for this scenario.</summary>
    public VtEvidence(Journal journal, string scenario, string title)
    {
        _journal = journal;
        _scenario = scenario;
        _title = title;
        Ev = new Evidence(Folder, scenario);
    }

    /// <summary>Every command the rig is sent from now on goes into <c>commands.txt</c>.</summary>
    public void Attach(VehicleToolRig rig) => rig.OnAction = Log;

    public void Log(string line)
    {
        lock (_actions) _actions.Add(DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line);
    }

    private string ActionsText()
    {
        lock (_actions) return string.Join("\n", _actions) + "\n";
    }

    /// <summary>
    /// Keeps a dump now when a kind of finding in the journal has none yet: the ones this step
    /// just found. With <c>AGOPENWEB_SCENARIO_EVIDENCE=all</c> and <paramref name="end"/>, a
    /// scenario that kept nothing keeps one at its end too. <paramref name="rig"/> null (the
    /// backend never started or is gone): the findings are marked as having no dump.
    /// <paramref name="more"/> adds the step's own words to the notes (what was typed, the passes).
    /// </summary>
    public async Task KeepIfNewAsync(VehicleToolRig? rig, string? moment, string taken, Func<string>? more = null, bool end = false)
    {
        if (Evidence.Off) return;
        var wanted = _journal.Findings.Where(kv => !_journal.EvidenceFor.ContainsKey(kv.Key)).ToList();
        if (wanted.Count == 0 && !(end && Evidence.All && !_kept)) return;
        if (rig == null)
        {
            foreach (var (key, _) in wanted) _journal.EvidenceFor[key] = "not kept (the backend was not running)";
            return;
        }

        var inv = CultureInfo.InvariantCulture;
        var notes = new StringBuilder(Evidence.NotesHeader("Vehicle & Tool scenarios", _scenario, _title, taken, turns: false));
        if (wanted.Count > 0)
        {
            notes.Append("## Findings first seen here\n\n");
            foreach (var (key, g) in wanted)
                notes.Append(string.Create(inv, $"- **{g.First.Severity}** `{key}`: {g.First.Message}\n"));
            notes.Append('\n');
        }
        else notes.Append("No finding: kept because AGOPENWEB_SCENARIO_EVIDENCE=all.\n\n");
        if (more != null)
        {
            try { notes.Append(more()).Append('\n'); }
            catch (Exception ex) { notes.Append("(the step's notes failed: ").Append(ex.Message).Append(")\n\n"); }
        }
        notes.Append("## The harness's own data (attachments/)\n\n")
             .Append("- `commands.txt`: every command this backend was sent, in order, as the web client sends it (`id|arg`), and the harness's steps (`#` lines). The backend started on an empty data folder.\n");
        if (Ev.HasTable("truth_track"))
            notes.Append("- `truth_track.csv`: fix by fix, the real machine (pivot, heading, wheel angle, the implement's centre) and what AgOpenWeb's last finished cycle said (pivot, heading, implement, AutoSteer, steer angle, distance from the line, pass, sections). `cmd` is the steer angle the machine was driven by.\n");
        if (Ev.HasTable("sweep_trace"))
            notes.Append("- `sweep_trace.csv` and `sweep_runs.json`: every probe run of this sweep group, fix by fix (the steer angle AgOpenWeb computed, the PGN 254 angle and status, the pass, the PGN 239 section and machine bytes, where the real implement was), and each run's summary (what the app said, the config PGNs it had sent).\n");

        string tmp = Path.Combine(Path.GetTempPath(), "aow-vt-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        var files = new List<string>();
        try
        {
            Directory.CreateDirectory(tmp);
            string cmds = Path.Combine(tmp, "commands.txt");
            await File.WriteAllTextAsync(cmds, ActionsText());
            files.Add(cmds);
        }
        catch (Exception ex) { notes.Append("- (commands.txt not written: ").Append(ex.Message).Append(")\n"); }
        string? zip = await Ev.SaveAsync(moment, notes.ToString(), rig.Services, rig.State, rig.Store, rig.RunAsync, files);
        try { Directory.Delete(tmp, true); } catch { }
        _kept = true;
        string shown = zip != null ? "`evidence/" + Path.GetFileName(zip) + "`"
            : "not kept (" + (Ev.Errors.Count > 0 ? Ev.Errors[^1] : "no reason given") + ")";
        foreach (var (key, _) in wanted) _journal.EvidenceFor[key] = shown;
        foreach (var e in Ev.Errors) TestContext.Out.WriteLine("evidence not kept: " + e);
        Ev.Errors.Clear();
    }

    /// <summary>Findings made after the backend is gone, or by no backend, get no dump.</summary>
    public void Close()
    {
        if (!Evidence.Off)
            foreach (var key in _journal.Findings.Keys.Where(k => !_journal.EvidenceFor.ContainsKey(k)).ToList())
                _journal.EvidenceFor[key] = "not kept (found after the backend stopped)";
        Ev.Close();
    }
}
