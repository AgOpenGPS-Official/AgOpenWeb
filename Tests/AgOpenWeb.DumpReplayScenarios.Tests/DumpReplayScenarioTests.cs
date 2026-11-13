// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>
/// Every dump of <c>TestData/dumps.json</c> (and of <c>AGOPENWEB_DUMP_DIR</c>): replayed, then
/// taken over. The test fails only when the harness itself breaks; the findings are the
/// product, in the report.
/// </summary>
[TestFixture, Category("Scenario"), NonParallelizable]
public class DumpReplayScenarioTests
{
    [Test]
    public async Task Replay_every_dump()
    {
        // One run at a time on a machine, and not beside the field or path job harness: they all
        // bind AgOpenWeb's UDP port 9999 and the module port 8888 (with address reuse), so a
        // second run's backend would hear the first run's steer module and GPS. The same lock file.
        using var exclusive = OneRunAtATime();
        var journal = new Journal();
        var root = Path.Combine(Path.GetTempPath(), "aow-dump-replay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        bool takeOver = Environment.GetEnvironmentVariable("AGOPENWEB_REPLAY_TAKEOVER") != "0";
        try
        {
            foreach (var entry in DumpCatalog.Manifest())
            {
                var (zip, note) = await DumpCatalog.FetchAsync(entry);
                if (zip == null)
                {
                    journal.Skipped.Add((entry, note));
                    Journal.Trace($"[dr] {entry.Id}: not replayed: {note}");
                    continue;
                }
                string story = (entry.Issue > 0 ? $"#{entry.Issue.ToString(CultureInfo.InvariantCulture)}, " : "") + (entry.Reporter.Length > 0 ? "reported by " + entry.Reporter + ". " : "")
                    + entry.Reported + (entry.FixedBy.Length > 0 ? " Closed by " + entry.FixedBy + "." : "");
                BugDump dump;
                try { dump = BugDump.Open(entry.Id, zip, Path.Combine(root, "dump-" + entry.Id)); }
                catch (Exception ex)
                {
                    // One bad dump is a note for that dump, not a failed night.
                    var bad = new Scenario(entry.Id, entry.Title, story);
                    bad.Row.Private = entry.IsPrivate;
                    bad.Note("HARNESS: the dump could not be read (" + ex.GetType().Name + (entry.IsPrivate ? "" : ": " + ex.Message) + "): not replayed");
                    Journal.Trace(ex.ToString());
                    journal.Add(bad.Row);
                    continue;
                }
                // The reporter's own note is theirs to make public: shown only for a dump attached to a public issue.
                var s = new Scenario(entry.Id, entry.Title, story
                    + (dump.Notes.Length > 0 && !entry.IsPrivate ? " The reporter's note: \"" + dump.Notes.TrimStart('#', ' ') + "\"." : ""));
                s.Row.Private = entry.IsPrivate;
                s.Row.Metrics.Add(("Issue", entry.Issue > 0 ? "#" + entry.Issue.ToString(CultureInfo.InvariantCulture) : ""));
                s.Row.Metrics.Add(("Reporter", entry.Reporter));
                s.Row.Metrics.Add(("Dump", note + ", SHA-256 " + DumpCatalog.Sha256(zip)[..12]));
                s.Row.Metrics.Add(("Reporter's build", dump.BuildHash.Length >= 7 ? dump.BuildHash[..7] : "unknown"));
                if (dump.Rows.Count > 0)
                    s.Row.Metrics.Add(("GPS log", string.Create(CultureInfo.InvariantCulture,
                        $"{dump.Rows.Count} rows, {dump.Rows[^1].T:0} s, {string.Join(" ", dump.Rows.GroupBy(r => r.Sentence.Length == 0 ? "?" : r.Sentence).Select(g => g.Key + " " + g.Count()))}{(dump.IsSimulator ? " (simulator)" : "")}")));
                foreach (var p in dump.Problems) s.Note("dump: " + p);
                if (dump.Rows.Count < 10 || !Directory.Exists(dump.FieldDir))
                {
                    s.Note("not replayed: the dump has no GPS log or no field");
                    journal.Add(s.Row);
                    continue;
                }
                await RunOne(entry, dump, s, Path.Combine(root, "data-" + entry.Id), takeOver);
                s.MarkLate();
                if (!entry.IsPrivate) ReporterLog(dump, s);
                journal.Add(s.Row);
            }
        }
        finally
        {
            var report = ReportPath.For("dump-replay-scenarios-report.md");
            File.WriteAllText(report, journal.ToMarkdown());
            TestContext.Out.WriteLine("Report: " + report);
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
        var broken = journal.Rows.SelectMany(r => r.Findings.Where(f => f.Code is "HARNESS" or "REPLAY-NO-CYCLES").Select(f => r.Id + ": " + (r.Private ? f.Code : f.Message))).ToList();
        Assert.That(broken, Is.Empty, "the harness itself failed: " + string.Join("; ", broken));
    }

    private static async Task RunOne(DumpEntry entry, BugDump dump, Scenario s, string dataRoot, bool takeOver)
    {
        await using (var r = new DumpReplay(entry, dump, s))
        {
            try
            {
                if (await r.SetUpAsync(dataRoot))
                {
                    await r.ReplayAsync();
                    ReplayJudge.Judge(r);
                }
            }
            catch (Exception ex)
            {
                s.Harness("HARNESS", "the replay threw: " + ex.GetType().Name + ": " + ex.Message);
                Journal.Trace(ex.ToString());
            }
        }
        if (!takeOver) return;
        await using (var t = new TakeOver(entry, dump, s))
        {
            try
            {
                if (await t.SetUpAsync(dataRoot + "-takeover")) await t.DriveAsync();
            }
            catch (Exception ex)
            {
                s.Harness("HARNESS", "the take-over threw: " + ex.GetType().Name + ": " + ex.Message);
                Journal.Trace(ex.ToString());
            }
        }
    }

    private static FileStream OneRunAtATime()
    {
        string path = Path.Combine(Path.GetTempPath(), "agopenweb-field-job-scenarios.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            Assert.Fail("Another dump replay, field job or path job run is going on this machine (" + path + " is held): "
                + "they would share AgOpenWeb's UDP ports 9999 and 8888. Run them one after the other.");
            throw;
        }
    }

    /// <summary>The lines of the reporter's own log that say what the operator and the U-turn
    /// did during the GPS log, on the GPS log's clock.</summary>
    private static void ReporterLog(BugDump d, Scenario s)
    {
        string[] keys = { "[AutoSteer]", "Creating turn:", "Triggered", "Turn complete", "cancel", "Cancel", "[Field] OpenFieldAsync", "[SelectedTrack]", "No U-turn", "fallback", "missed" };
        foreach (var l in d.Log)
        {
            if (d.LogT(l) is not { } t || t < -5 || t > d.Rows[^1].T + 5) continue;
            // NTRIP lines name the reporter's caster and mount point: left out.
            if (l.Category.Contains("Ntrip", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("NTRIP", StringComparison.OrdinalIgnoreCase)) continue;
            if (l.Level is "Error" or "Warning" || keys.Any(k => l.Text.Contains(k, StringComparison.Ordinal)))
            {
                var text = Scrub(l.Text);
                text = text.Length > 160 ? text[..160] + "…" : text;
                s.Row.ReporterLog.Add(string.Create(CultureInfo.InvariantCulture, $"t {t,6:0.0} s  [{l.Level}] {text}"));
            }
        }
        if (s.Row.ReporterLog.Count > 60) s.Row.ReporterLog.RemoveRange(30, s.Row.ReporterLog.Count - 60);
    }

    /// <summary>The report is public: a path in a log line names the reporter's account on their
    /// computer, so the folder above the user's own is cut (C:\Users\name\..., /home/name/...).</summary>
    public static string Scrub(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"([A-Za-z]:[\\/](?:Users|Documents and Settings)[\\/]|/(?:home|Users)/)[^\\/\s]+", "$1…");
}
