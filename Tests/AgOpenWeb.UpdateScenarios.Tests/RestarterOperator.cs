// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// The farmer on this build: a season's work, then the things that happen to a cab computer: a
/// plain restart, a power cut in a job, a settings file cut short by that power cut, the data
/// folder moved to another computer, a comma-decimal Windows, an AgOpenGPS field folder
/// copied in by hand, and a whole AgOpenGPS 6.8.6 installation moved over.
/// </summary>
public static class RestarterOperator
{
    public const string Name = "Restarter (this build only)";

    public static async Task RunAsync(Journal journal, string? only)
    {
        var all = new (string Code, string Title, string Story, Func<Scenario, Task> Run)[]
        {
            ("R1", "A season on this build, then a plain restart",
                "The farm is made on this build (profiles, device settings, casters, three fields with tracks, flags and a sprayed job), the app is closed as the daemon closes (exit save) and started again: everything must read back as set, and a second restart must change no value.", PlainRestartAsync),
            ("R2", "A power cut in the middle of a job",
                "The same farm, but the app dies with the sprayer job running and the field open: no exit save. The next start must find the fields, the profiles and the settings; the job's coverage is whatever the 30 s autosave had written.", PowerCutAsync),
            ("R3", "Fault injection: appsettings.json, appstate.json and the profiles in use cut to half their bytes",
                "Fault injection, not what a power cut does in use: AgOpenWeb writes these files to a scratch file, flushes it to the media and swaps it in (AtomicJsonFile), so a primary cut in half is left by a failing disk, a copy cut short or a hand edit, not by a save. After the season appsettings.json, appstate.json and the vehicle and implement profiles in use are each cut to half their bytes. The next start must recover from the .bak copies without losing a value, and say so. R9 has what a power cut can really leave.", TruncatedFilesAsync),
            ("R4", "The data folder moved to another computer",
                "The farmer copies Documents/AgOpenWeb to a new cab computer (another path) and deletes the old one. appsettings.json holds the Fields folder as an absolute path of the old computer; this build must find the fields, profiles and casters in the new place.", MovedFolderAsync),
            ("R5", "An AgOpenGPS field folder copied into Fields",
                "A field from AgOpenGPS 6.x (Field.txt, Boundary.txt, Headland.Txt, TrackLines.txt with an AB line and a curve, Flags.txt, Sections.txt with worked area) is copied into the Fields folder by hand. Opening it must import the boundary, the headland, the tracks, the flags and the worked area as a job; a restart must keep them; the AgOpenGPS files are deleted.", AgOpenGpsImportAsync),
            ("R6", "A comma-decimal computer (pt-PT) saves and reads the season",
                "The same farm, on a computer whose Windows is set to Portuguese (decimal comma, as in Finland, Germany, France): every file is written and read under that culture. Values must come back as set (the globalization fence of CONTRIBUTING.md).", CommaDecimalAsync),
            ("R8", "An AgOpenGPS 6.8.6 installation moved to AgOpenWeb",
                "A farmer on AgOpenGPS 6.8.6 moves to AgOpenWeb and keeps AgOpenGPS: the field folders and the profile folders of Documents/AgOpenGPS (Vehicles, VehicleProfiles, ToolProfiles, Environment, AgIO) are copied into Documents/AgOpenWeb, the AgOpenGPS folder is left as it is. Every tractor, implement, steer, roll, GPS, U-turn, tram and NTRIP setting and every field file (boundary, headland, tracks, flags, coverage, contour, recorded path, elevation, background image) is looked for in this build; the AgOpenGPS folder must not change.", sc => AgOpenGpsInstallationAsync(sc, freshInstall: false)),
            ("R8F", "A fresh AgOpenGPS 6.8.6 install (never older than 6.8.2) moved to AgOpenWeb",
                "As R8, but the computer never ran AgOpenGPS before 6.8.2: there is no combined Vehicles/*.XML, and the environment (U-turn, tram, GPS switches) is in Environment/DefaultEnvironment.xml, the file 6.8.6 creates on a first start when no environment.xml was made by the migration from a combined profile.", sc => AgOpenGpsInstallationAsync(sc, freshInstall: true)),
            ("R9", "What a power cut can really leave: an empty file, a stale .tmp, a .bak older than the file, both damaged",
                "After the season, one more session: a display setting (written to appsettings.json at once) and a tractor and an implement setting followed by a profile save; then the power goes. On disk each file now has a .bak one save older. Four starts, each on its own copy: as left (the newer file must win over its older .bak); each file zero bytes long (an SD card on FAT/exFAT can keep the new name but not the bytes when the power goes; the .bak is read, so the last save is lost); a half-written .tmp left beside each file (the power went while the scratch file was being written; it must be ignored and the next save must go through); the file and its .bak both damaged (defaults, and the operator must be told). What the app does is reported, case by case.", PowerCutLeftoversAsync),
        };
        foreach (var (code, title, story, run) in all)
        {
            if (!Kit.Want(only, code) && !Kit.Want(only, "R")) continue;
            var sc = new Scenario(Name, code, title, story);
            try { await run(sc); }
            catch (Exception ex) { sc.Harness("unhandled: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
            journal.Add(sc.Row);
        }
        string? aog = Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_AOG_FIELDS");
        if (!string.IsNullOrWhiteSpace(aog) && (Kit.Want(only, "R7") || Kit.Want(only, "R")))
        {
            var sc = new Scenario(Name, "R7", "The farmer's own AgOpenGPS fields copied into Fields",
                Kit.F($"Every AgOpenGPS field folder under AGOPENWEB_UPDATE_AOG_FIELDS (or, when it is a whole AgOpenGPS folder with Fields/ inside, that folder as R8 copies it) is copied in and opened. Only counts are reported: the fields stay on this computer."));
            try { await OwnAgOpenGpsFieldsAsync(sc, aog); }
            catch (Exception ex) { sc.Harness("unhandled: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
            journal.Add(sc.Row);
        }
    }

    private static List<(string, string)> Pairs(Farm farm) => farm.Vehicles.Zip(farm.Tools, (v, t) => (v.Name, t.Name)).ToList();

    /// <summary>The season on this build: a fresh data folder, the farm built, the app closed as
    /// asked (exit save or crash).</summary>
    private static async Task<(string Root, Farm Farm)> SeasonAsync(Scenario sc, string code, bool crash, Action<Farm>? tune = null)
    {
        string root = Kit.NewDataRoot(code);
        var farm = Farm.Standard();
        tune?.Invoke(farm);
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step(Kit.F($"this build starts on an empty data folder {root}"));
            Kit.CheckRig(sc, rig, "first start, empty folder");
            await FarmBuilder.BuildAsync(new RigSender(rig), farm, sc, slow: false);
            if (crash)
            {
                var last = farm.Fields[^1];
                var job = new JobSpec("Night spraying", "Spraying", DriveSeconds: 45, SpeedSteps: 8);
                await rig.SendAsync("field.startJob", string.Join("\t", last.Name, job.WorkType, "made by the update scenario harness", job.Task));
                await rig.SettleAsync();
                await rig.SendAsync("section.master");
                for (int i = 0; i < job.SpeedSteps; i++) await rig.SendAsync("sim.speedUp");
                await Task.Delay(TimeSpan.FromSeconds(job.DriveSeconds));
                last.Job = job;
                double painted = await rig.ReadAsync(() => rig.Services.GetService(typeof(AgOpenWeb.Services.Interfaces.ICoverageMapService)) is AgOpenWeb.Services.Interfaces.ICoverageMapService c ? c.TotalWorkedArea : 0);
                sc.Step(Kit.F($"'{last.Name}': job '{job.Task}' running {job.DriveSeconds} s at {job.SpeedSteps} km/h, {painted:0} m² painted; the power goes now (no exit save)"));
                rig.CrashOnDispose = true;
            }
            else
            {
                Kit.CheckRig(sc, rig, "after the season's work");
                sc.Step("the app is closed (exit save)");
            }
        }
        Kit.CheckSaveErrors(sc, root, "THIS-BUILD", "this build");
        return (root, farm);
    }

    private static async Task PlainRestartAsync(Scenario sc)
    {
        var (root, farm) = await SeasonAsync(sc, "R1", crash: false);
        sc.Row.Files = Kit.FilesSummary("Files after the season", root);
        Snapshot first;
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("restart 1");
            Kit.CheckRig(sc, rig, "restart 1");
            first = await Snapshot.TakeAsync(rig, openFields: true, Pairs(farm));
            Judge.Farm(sc, farm, first, "RESTART");
            Kit.CheckRig(sc, rig, "after opening every field and job");
        }
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("restart 2");
            var second = await Snapshot.TakeAsync(rig, openFields: false);
            Judge.Dumps(sc, first.Dump, second.Dump, "RESTART-DRIFT", "restart 2 against restart 1", UpgraderOperator.IsVolatile);
            Kit.CheckRig(sc, rig, "restart 2");
        }
        Kit.TryDelete(root);
    }

    private static async Task PowerCutAsync(Scenario sc)
    {
        var (root, farm) = await SeasonAsync(sc, "R2", crash: true);
        sc.Row.Files = Kit.FilesSummary("Files after the power cut", root);
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("the next start");
            Kit.CheckRig(sc, rig, "start after the power cut");
            var s = await Snapshot.TakeAsync(rig, openFields: true, Pairs(farm));
            Judge.Farm(sc, farm, s, "POWERCUT", what => what is "lastfield" or "view" or "theme" or "job");
            if (sc.Row.Findings.Any(f => f.Code == "POWERCUT-UNSAVED-PROFILE-EDIT-LOST"))
                sc.Note("what the power cut took is every profile setting changed since the last profile save (here GPS, roll and an implement setting): the vehicle and implement profiles are written only at a profile save, a profile switch or the exit save, in AgOpenWeb as in AgOpenGPS; Units, Display and Connections are written to appsettings.json at once");
            var fr = s.Fields.FirstOrDefault(f => f.Name == farm.Fields[^1].Name);
            var job = fr?.Jobs.FirstOrDefault(j => j.Task == farm.Fields[^1].Job!.Task);
            if (job == null) sc.Problem(Severity.DataLoss, "POWERCUT-JOB-LOST", Kit.F($"the job '{farm.Fields[^1].Job!.Task}' that was running when the power went is not listed (found: {string.Join(", ", fr?.Jobs.Select(j => j.Task) ?? Array.Empty<string>())})"));
            else if (job.WorkedSqM < 1) sc.Problem(Severity.DataLoss, "POWERCUT-COVERAGE-LOST", Kit.F($"the job '{job.Task}' ran {farm.Fields[^1].Job!.DriveSeconds} s before the power cut (autosave every 30 s) and resumes with no worked area"));
            else sc.Ok(Kit.F($"the job '{job.Task}' interrupted by the power cut resumes with {job.WorkedSqM:0} m² worked (what the autosave had written)"));
            if (s.LastOpenedField != farm.Fields[^1].Name)
                sc.Problem(Severity.Robustness, "POWERCUT-LAST-FIELD-LOST", Kit.F($"after the power cut the last opened field reads '{s.LastOpenedField}' (the job was running on '{farm.Fields[^1].Name}'): appstate.json is written at exit, so 'Resume last' points at the wrong field"));
            if (s.Dump.TryGetValue("State.IsDayMode", out var day) && day != "False")
                sc.Note("the day/night theme chosen before the power cut was not kept (appstate.json is written at exit)");
            Kit.CheckRig(sc, rig, "after opening every field and job");
        }
        Kit.TryDelete(root);
    }

    private static async Task TruncatedFilesAsync(Scenario sc)
    {
        var (root, farm) = await SeasonAsync(sc, "R3", crash: false);
        Snapshot good;
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            good = await Snapshot.TakeAsync(rig, openFields: false, Pairs(farm));
        }
        string aow = Path.Combine(root, "AgOpenWeb");
        foreach (var rel in new[] { "appsettings.json", "appstate.json", Path.Combine("Vehicles", farm.ActivePair.Vehicle + ".json"), Path.Combine("Tools", farm.ActivePair.Tool + ".json") })
        {
            string p = Path.Combine(aow, rel);
            if (!File.Exists(p)) { sc.Note(Kit.F($"{rel} does not exist: not truncated")); continue; }
            var bytes = File.ReadAllBytes(p);
            File.WriteAllBytes(p, bytes[..(bytes.Length / 2)]);
            bool bak = File.Exists(p + ".bak") || Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*").Any(f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
            sc.Step(Kit.F($"{rel} cut to {bytes.Length / 2} of {bytes.Length} bytes{(bak ? " (a .bak copy exists)" : " (NO .bak copy next to it)")}"));
        }
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("the next start");
            Kit.CheckRig(sc, rig, "start with truncated files");
            var s = await Snapshot.TakeAsync(rig, openFields: false, Pairs(farm));
            sc.Note(Kit.F($"appsettings.json read: {s.SettingsLoad}; appstate.json read: {s.StateLoad}"));
            if (s.SettingsLoad is "Missing" or "CorruptNoBackup") sc.Problem(Severity.DataLoss, "TRUNCATED-SETTINGS-LOST", Kit.F($"a truncated appsettings.json was not recovered ({s.SettingsLoad}): the device settings are back to defaults"));
            else sc.Ok(Kit.F($"appsettings.json recovered ({s.SettingsLoad})"));
            if (s.StateLoad is "Missing" or "CorruptNoBackup") sc.Problem(Severity.Robustness, "TRUNCATED-STATE-LOST", Kit.F($"a truncated appstate.json was not recovered ({s.StateLoad}): last field, theme and camera are back to defaults"));
            else sc.Ok(Kit.F($"appstate.json recovered ({s.StateLoad})"));
            Judge.Dumps(sc, good.Dump, s.Dump, "TRUNCATED-VALUES-CHANGED", "after the truncated files", UpgraderOperator.IsVolatile);
            foreach (var (pair, dump) in good.PairDumps)
                if (s.PairDumps.TryGetValue(pair, out var d2)) Judge.Dumps(sc, dump, d2, "TRUNCATED-PROFILE-CHANGED", Kit.F($"profile pair {pair}"), UpgraderOperator.IsVolatile);
            var said = Kit.Said(rig);
            sc.Note(said.Count > 0 ? "the operator was told: " + string.Join(" | ", said) : "the operator was told nothing about the recovery");
        }
        Kit.TryDelete(root);
    }

    // R9's last session: the values before it (as the farm sets them) and after it.
    private const string GuidelinesBefore = "6", GuidelinesAfter = "9";
    private const string AntennaBefore = "3.1", AntennaAfter = "3.25";
    private const string CutoffBefore = "1.5", CutoffAfter = "2.5";

    /// <summary>R9: the leftovers a power cut can really leave next to files written by
    /// AtomicJsonFile (scratch file, fsync, swap, the previous file kept as .bak). Each case runs on
    /// its own copy of the same data folder; the outcomes are findings and notes, not assertions,
    /// since some of them (the last save lost with an empty file) are what a single .bak gives.</summary>
    private static async Task PowerCutLeftoversAsync(Scenario sc)
    {
        var (root, farm) = await SeasonAsync(sc, "R9", crash: false);
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "start after the season", "LEFTOVER");
            await rig.SendAsync("config.set", "display.extraGuidelinesCount:" + GuidelinesAfter);
            await rig.SendAsync("config.set", "vehicle.antennaHeight:" + AntennaAfter);
            await rig.SendAsync("config.set", "tool.slowSpeedCutoff:" + CutoffAfter);
            await rig.SendAsync("profile.save");
            rig.CrashOnDispose = true;
        }
        sc.Step(Kit.F($"last session: extra guidelines {GuidelinesBefore} → {GuidelinesAfter} (saved at once), antenna height {AntennaBefore} → {AntennaAfter} m and slow-speed cutoff {CutoffBefore} → {CutoffAfter} km/h, profile save; then the power goes (no exit save)"));
        Kit.CheckSaveErrors(sc, root, "LEFTOVER", "this build");

        string[] files =
        {
            "appsettings.json", "appstate.json",
            Path.Combine("Vehicles", farm.ActivePair.Vehicle + ".json"),
            Path.Combine("Tools", farm.ActivePair.Tool + ".json"),
        };
        string aow0 = Path.Combine(root, "AgOpenWeb");
        foreach (var rel in files)
        {
            string p = Path.Combine(aow0, rel);
            if (!File.Exists(p)) { sc.Note(Kit.F($"{rel} does not exist after the last session")); continue; }
            string bak = p + ".bak";
            sc.Note(File.Exists(bak)
                ? Kit.F($"{rel}: {new FileInfo(p).Length} bytes; its .bak {new FileInfo(bak).Length} bytes, {(File.ReadAllText(bak) == File.ReadAllText(p) ? "the same content" : "older content")}")
                : Kit.F($"{rel}: {new FileInfo(p).Length} bytes, no .bak beside it"));
        }

        // a) As left: the newer file must win over its older .bak.
        await OnCopyAsync(sc, root, "as-left", _ => { }, async (rig, s, screen, aow) =>
        {
            var older = Values(s).Where(v => !Kit.SameNumber(v.Got, v.After)).ToList();
            if (s.SettingsLoad != "Ok") sc.Problem(Severity.Bug, "LEFTOVER-NEWER-FILE-NOT-READ", Kit.F($"as left, appsettings.json read as {s.SettingsLoad}, not Ok"));
            if (older.Count > 0)
                sc.Problem(Severity.DataLoss, "LEFTOVER-OLDER-BAK-WON", Kit.F($"as left (each file whole, its .bak one save older): {string.Join("; ", older.Select(v => Kit.F($"{v.Name} reads {v.Got}, the last session saved {v.After}")))}"));
            else sc.Ok("as left: the last session's values are read, not the older .bak copies");
            if (screen.Length > 0) sc.Note("as left, the start showed: " + screen);
            await Task.CompletedTask;
        });

        // b) Each file zero bytes long.
        await OnCopyAsync(sc, root, "zero-length", aow =>
        {
            foreach (var rel in files) { string p = Path.Combine(aow, rel); if (File.Exists(p)) File.WriteAllBytes(p, Array.Empty<byte>()); }
        }, async (rig, s, screen, aow) =>
        {
            sc.Note(Kit.F($"zero-length files: appsettings.json read as {s.SettingsLoad}, appstate.json as {s.StateLoad}"));
            foreach (var v in Values(s))
            {
                if (Kit.SameNumber(v.Got, v.After)) sc.Ok(Kit.F($"zero-length files: {v.Name} reads {v.Got}, the last saved value"));
                else if (Kit.SameNumber(v.Got, v.Before))
                    // The save flushes the new file to the disk before the rename (AtomicJsonFile),
                    // so a file still empty after it is the filesystem's: the .bak is the most
                    // there is, and the operator is asked (the check below). AgOpenGPS keeps none.
                    sc.Note(Kit.F($"zero-length files: {v.Name} reads {v.Got} from the .bak, the save before last; the last session's {v.After} is gone with the empty file (the save flushes the data before the rename; one .bak keeps the save before)"));
                else sc.Problem(Severity.DataLoss, "ZERO-LENGTH-SETTINGS-LOST", Kit.F($"zero-length files: {v.Name} reads {v.Got}: neither the last saved {v.After} nor the .bak's {v.Before}"));
            }
            if (screen.Length > 0) sc.Ok("zero-length files: the operator was told: " + screen);
            else sc.Problem(Severity.Robustness, "ZERO-LENGTH-NOT-TOLD", "zero-length files: the start showed no dialog about the recovery");
            Quarantined(sc, aow, "zero-length files");
            await Task.CompletedTask;
        });

        // c) A half-written .tmp beside each file: ignored on read, replaced by the next save.
        await OnCopyAsync(sc, root, "stale-tmp", aow =>
        {
            foreach (var rel in files)
            {
                string p = Path.Combine(aow, rel);
                if (!File.Exists(p)) continue;
                var bytes = File.ReadAllBytes(p);
                File.WriteAllBytes(p + ".tmp", bytes[..(bytes.Length / 2)]);
            }
        }, async (rig, s, screen, aow) =>
        {
            var wrong = Values(s).Where(v => !Kit.SameNumber(v.Got, v.After)).ToList();
            if (s.SettingsLoad != "Ok" || wrong.Count > 0)
                sc.Problem(Severity.Bug, "STALE-TMP-READ", Kit.F($"with a half-written .tmp beside each file, appsettings.json read as {s.SettingsLoad}{(wrong.Count > 0 ? "; " + string.Join("; ", wrong.Select(v => Kit.F($"{v.Name} reads {v.Got}, saved {v.After}"))) : "")}"));
            else sc.Ok("a half-written .tmp beside each file is ignored: the files themselves are read");
            if (screen.Length > 0) sc.Note("with a stale .tmp, the start showed: " + screen);
            // The next saves must go through and leave no .tmp behind.
            await rig.SendAsync("config.set", "display.extraGuidelinesCount:7");
            await rig.SendAsync("profile.save");
            var left = files.Where(rel => File.Exists(Path.Combine(aow, rel) + ".tmp") && rel != "appstate.json").ToList();
            string now = await rig.ReadAsync(() => rig.Settings.Settings.ExtraGuidelinesCount.ToString(CultureInfo.InvariantCulture));
            string onDisk = Kit.ReadText(Path.Combine(aow, "appsettings.json"));
            bool saved = System.Text.RegularExpressions.Regex.IsMatch(onDisk, "\"extraGuidelinesCount\"\\s*:\\s*7\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!saved) sc.Problem(Severity.DataLoss, "STALE-TMP-BLOCKS-SAVE", Kit.F($"with a stale .tmp beside appsettings.json, the next save did not reach the file (in memory {now}, on disk not 7)"));
            else if (left.Count > 0) sc.Problem(Severity.Robustness, "STALE-TMP-LEFT", Kit.F($"after a save, the stale .tmp is still beside {string.Join(", ", left)}"));
            else sc.Ok("the next saves went through and took the stale .tmp files with them (appstate.json is written at exit)");
            Kit.CheckSaveErrors(sc, Path.GetDirectoryName(aow)!, "STALE-TMP", "this build");
        });

        // d) The file and its .bak both damaged: defaults, and the operator must be told.
        await OnCopyAsync(sc, root, "both-damaged", aow =>
        {
            foreach (var rel in files)
                foreach (var p in new[] { Path.Combine(aow, rel), Path.Combine(aow, rel) + ".bak" })
                {
                    if (!File.Exists(p)) continue;
                    var bytes = File.ReadAllBytes(p);
                    File.WriteAllBytes(p, bytes[..(bytes.Length / 2)]);
                }
        }, async (rig, s, screen, aow) =>
        {
            sc.Note(Kit.F($"file and .bak both damaged: appsettings.json read as {s.SettingsLoad}, appstate.json as {s.StateLoad}; the pair in use is '{s.ActiveVehicle}' / '{s.ActiveTool}' (profiles listed: {string.Join(", ", s.Vehicles)} / {string.Join(", ", s.Tools)})"));
            foreach (var v in Values(s))
                sc.Note(Kit.F($"file and .bak both damaged: {v.Name} reads {v.Got} (last saved {v.After}, the save before {v.Before})"));
            if (screen.Length > 0) sc.Ok("file and .bak both damaged: the operator was told: " + screen);
            else sc.Problem(Severity.Robustness, "BOTH-DAMAGED-NOT-TOLD", "file and .bak both damaged: the settings went back to defaults and the start showed no dialog");
            var fields = rig.Services.GetRequiredService<AgOpenWeb.Services.IFieldService>().GetAvailableFields(rig.FieldsDir).ToList();
            var gone = farm.Fields.Where(f => !fields.Contains(f.Name)).Select(f => f.Name).ToList();
            if (gone.Count > 0) sc.Problem(Severity.DataLoss, "BOTH-DAMAGED-FIELDS-GONE", Kit.F($"file and .bak both damaged: the fields {string.Join(", ", gone)} are not listed (Fields folder now {rig.FieldsDir})"));
            else sc.Ok(Kit.F($"file and .bak both damaged: all {farm.Fields.Count} fields are still listed"));
            Quarantined(sc, aow, "file and .bak both damaged");
            await Task.CompletedTask;
        });
        Kit.TryDelete(root);
    }

    /// <summary>The three values R9's last session changed, as this build reads them.</summary>
    private static IEnumerable<(string Name, string Got, string Before, string After)> Values(Snapshot s)
    {
        string V(string key) => s.Dump.TryGetValue(key, out var v) ? v : "(no " + key + ")";
        yield return ("extra guidelines count (appsettings.json)", V("AppSettings.ExtraGuidelinesCount"), GuidelinesBefore, GuidelinesAfter);
        yield return ("antenna height (vehicle profile)", V("Vehicle.AntennaHeight"), AntennaBefore, AntennaAfter);
        yield return ("slow-speed cutoff (implement profile)", V("Tool.SlowSpeedCutoff"), CutoffBefore, CutoffAfter);
    }

    /// <summary>One R9 case: the data folder copied, damaged as the case says, this build started
    /// on it (a start that throws is a finding), read back, judged, and dropped with no exit save.</summary>
    private static async Task OnCopyAsync(Scenario sc, string root, string label, Action<string> damage, Func<UpdateRig, Snapshot, string, string, Task> judge)
    {
        string copy = Kit.NewDataRoot("R9-" + label);
        Kit.CopyDirectory(root, copy);
        damage(Path.Combine(copy, "AgOpenWeb"));
        sc.Step(Kit.F($"{label}: this build starts on a copy ({copy})"));
        UpdateRig rig;
        try { rig = await UpdateRig.StartAsync(copy); }
        catch (Exception ex)
        {
            sc.Problem(Severity.DataLoss, "LEFTOVER-NO-START", Kit.F($"{label}: this build does not start: {ex.GetType().Name}: {ex.Message}"));
            Kit.TryDelete(copy);
            return;
        }
        await using (rig)
        {
            await rig.DrainAsync();
            string screen = await rig.ReadAsync(() =>
            {
                // The dialog texts are "" when not shown, never null: take the one that is set
                // (`??` stopped at the empty confirmation and never saw the error dialog).
                bool confirm = !string.IsNullOrEmpty(rig.Vm.ConfirmationDialogTitle);
                string title = (confirm ? rig.Vm.ConfirmationDialogTitle : rig.Vm.ErrorDialogTitle) ?? "";
                string msg = (confirm ? rig.Vm.ConfirmationDialogMessage : rig.Vm.ErrorDialogMessage) ?? "";
                return title.Length + msg.Length == 0 ? "" : (title + ": " + msg).Replace("\n", " ", StringComparison.Ordinal);
            });
            Kit.CheckRig(sc, rig, label + ": start", "LEFTOVER");
            var s = await Snapshot.TakeAsync(rig, openFields: false);
            await judge(rig, s, screen, Path.Combine(copy, "AgOpenWeb"));
            Kit.CheckRig(sc, rig, label + ": after the checks", "LEFTOVER");
            rig.CrashOnDispose = true;
        }
        Kit.TryDelete(copy);
    }

    /// <summary>Damaged files moved aside as *.corrupt.&lt;time&gt; (AtomicJsonFile quarantines them).</summary>
    private static void Quarantined(Scenario sc, string aow, string label)
    {
        string fieldsDir = Path.Combine(aow, "Fields");
        var moved = Directory.EnumerateFiles(aow, "*.corrupt.*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(fieldsDir, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(aow, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        sc.Note(moved.Count > 0 ? Kit.F($"{label}: moved aside: {string.Join(", ", moved)}") : Kit.F($"{label}: no damaged file was moved aside"));
    }

    private static async Task MovedFolderAsync(Scenario sc)
    {
        var (root, farm) = await SeasonAsync(sc, "R4", crash: false);
        Snapshot before;
        await using (var rig = await UpdateRig.StartAsync(root)) before = await Snapshot.TakeAsync(rig, openFields: false, Pairs(farm));
        string newRoot = Kit.NewDataRoot("R4-new-pc");
        Kit.CopyDirectory(root, newRoot);
        Kit.TryDelete(root);
        sc.Step(Kit.F($"the data folder is moved from {root} to {newRoot} (the old one is gone)"));
        await using (var rig = await UpdateRig.StartAsync(newRoot))
        {
            Kit.CheckRig(sc, rig, "start on the moved folder");
            var s = await Snapshot.TakeAsync(rig, openFields: true, Pairs(farm));
            string fieldsDir = s.Dump.TryGetValue("AppSettings.FieldsDirectory", out var fd) ? fd : "";
            if (!fieldsDir.StartsWith(newRoot, StringComparison.Ordinal))
                sc.Problem(Severity.DataLoss, "MOVED-FIELDS-DIR-STALE", Kit.F($"the Fields folder still reads '{fieldsDir}' (the old computer's path); the fields are under {newRoot}"));
            else sc.Ok(Kit.F($"the Fields folder now reads under the new place: {fieldsDir}"));
            Judge.Farm(sc, farm, s, "MOVED");
            Judge.Dumps(sc, before.Dump, s.Dump, "MOVED-VALUES-CHANGED", "after the move", k => UpgraderOperator.IsVolatile(k) || k.Contains("Directory", StringComparison.Ordinal) || k.Contains("Path", StringComparison.Ordinal));
            Kit.CheckRig(sc, rig, "after opening every field");
        }
        Kit.TryDelete(newRoot);
    }

    private static async Task AgOpenGpsImportAsync(Scenario sc)
    {
        string root = Kit.NewDataRoot("R5");
        var farm = Farm.Standard();
        var fs = farm.Fields[0];
        fs.Job = null;
        // The field folder, as copied from AgOpenGPS, before the app ever runs; a second one
        // appears while the app is running (a USB stick), as the farmer does it.
        string fieldsDir = Path.Combine(root, "AgOpenWeb", "Fields");
        AgOpenGpsFiles.Write(Path.Combine(fieldsDir, fs.Name), fs, withCoverage: true);
        sc.Step(Kit.F($"AgOpenGPS files for '{fs.Name}' written into Fields before the first start: {string.Join(", ", Directory.GetFiles(Path.Combine(fieldsDir, fs.Name)).Select(Path.GetFileName))}"));
        var second = farm.Fields[1];
        second.Job = null;
        var oneField = new Farm { ActivePair = farm.ActivePair };
        oneField.Fields.Add(fs);
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "first start");
            AgOpenGpsFiles.Write(Path.Combine(fieldsDir, second.Name), second, withCoverage: false);
            sc.Step(Kit.F($"AgOpenGPS files for '{second.Name}' copied in while the app runs"));
            var s = await Snapshot.TakeAsync(rig, openFields: true);
            foreach (var f in new[] { fs, second })
            {
                var fr = s.Fields.FirstOrDefault(x => x.Name == f.Name);
                if (fr == null) { sc.Problem(Severity.DataLoss, "AOG-FIELD-NOT-LISTED", Kit.F($"the AgOpenGPS field '{f.Name}' is not in the field list")); continue; }
                if (!fr.Opened) { sc.Problem(Severity.DataLoss, "AOG-FIELD-NOT-OPENING", Kit.F($"the AgOpenGPS field '{f.Name}' does not open: {string.Join(" | ", fr.Said)}")); continue; }
                var left = fr.Files.Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".Txt", StringComparison.Ordinal)).ToList();
                if (fr.Files.Any(x => x == "field.geojson")) sc.Ok(Kit.F($"'{f.Name}': field.geojson written on import")); else sc.Problem(Severity.Bug, "AOG-NO-GEOJSON", Kit.F($"'{f.Name}': no field.geojson after the import (files: {string.Join(", ", fr.Files)})"));
                if (left.Count > 0) sc.Note(Kit.F($"'{f.Name}': AgOpenGPS files still in the folder after the import: {string.Join(", ", left)}"));
                if (f == fs)
                {
                    var job = fr.Jobs.FirstOrDefault();
                    if (job == null) sc.Problem(Severity.DataLoss, "AOG-COVERAGE-NOT-IMPORTED", Kit.F($"'{f.Name}': Sections.txt ({AgOpenGpsFiles.CoverageSqM:0} m² worked) made no job"));
                    else if (job.WorkedSqM < AgOpenGpsFiles.CoverageSqM * 0.5 || job.WorkedSqM > AgOpenGpsFiles.CoverageSqM * 1.5)
                        sc.Problem(Severity.Bug, "AOG-COVERAGE-AREA", Kit.F($"'{f.Name}': the imported job '{job.Task}' ({job.WorkType}) resumes with {job.WorkedSqM:0} m² worked; Sections.txt described {AgOpenGpsFiles.CoverageSqM:0} m²"));
                    else sc.Ok(Kit.F($"'{f.Name}': Sections.txt imported as job '{job.Task}' ({job.WorkType}) with {job.WorkedSqM:0} m² worked ({AgOpenGpsFiles.CoverageSqM:0} m² written)"));
                }
            }
            // The geometry, judged as for any field (no job in the spec: the imported one is judged above).
            var two = new Farm { ActivePair = farm.ActivePair };
            two.Fields.Add(fs); two.Fields.Add(second);
            Judge.Farm(sc, two, s, "AOG", what => what is "settings" or "pair" or "lastfield" or "language" or "hotkey" or "theme" or "view" or "ntrip" || what.StartsWith("units", StringComparison.Ordinal) || what.Contains('.'));
            Kit.CheckRig(sc, rig, "after opening the imported fields");
        }
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("restart");
            var s = await Snapshot.TakeAsync(rig, openFields: true);
            var two = new Farm { ActivePair = farm.ActivePair };
            two.Fields.Add(fs); two.Fields.Add(second);
            Judge.Farm(sc, two, s, "AOG-RESTART", what => what is "settings" or "pair" or "lastfield" or "language" or "hotkey" or "theme" or "view" or "ntrip" || what.StartsWith("units", StringComparison.Ordinal) || what.Contains('.'));
            Kit.CheckRig(sc, rig, "restart");
        }
        Kit.TryDelete(root);
    }

    private static async Task CommaDecimalAsync(Scenario sc)
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        var pt = CultureInfo.GetCultureInfo("pt-PT");
        CultureInfo.CurrentCulture = pt; CultureInfo.CurrentUICulture = pt;
        CultureInfo.DefaultThreadCurrentCulture = pt; CultureInfo.DefaultThreadCurrentUICulture = pt;
        try
        {
            sc.Step(Kit.F($"culture pt-PT on every thread: 1.5 formats as '{1.5.ToString(pt)}'"));
            var (root, farm) = await SeasonAsync(sc, "R6", crash: false);
            await using (var rig = await UpdateRig.StartAsync(root))
            {
                sc.Step("restart under pt-PT");
                Kit.CheckRig(sc, rig, "restart under pt-PT");
                var s = await Snapshot.TakeAsync(rig, openFields: true, Pairs(farm));
                Judge.Farm(sc, farm, s, "COMMA");
                Kit.CheckRig(sc, rig, "after opening every field and job");
            }
            // And the same folder read by an invariant-culture start: a file written with commas
            // would break here.
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            await using (var rig = await UpdateRig.StartAsync(root))
            {
                sc.Step("restart under the invariant culture, same files");
                var s = await Snapshot.TakeAsync(rig, openFields: true, Pairs(farm));
                Judge.Farm(sc, farm, s, "COMMA-THEN-DOT");
                Kit.CheckRig(sc, rig, "invariant restart");
            }
            Kit.TryDelete(root);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved.CurrentCulture; CultureInfo.CurrentUICulture = saved.CurrentUICulture;
            CultureInfo.DefaultThreadCurrentCulture = saved.DefaultThreadCurrentCulture; CultureInfo.DefaultThreadCurrentUICulture = saved.DefaultThreadCurrentUICulture;
        }
    }

    /// <summary>The AgOpenGPS settings this build's reader looks for under another name.</summary>
    private static readonly Dictionary<string, string> AogKeyMismatch = new(StringComparer.Ordinal)
    {
        ["setHeadland_isSectionControlled"] = "AgOpenGPS writes setHeadland_isSectionControlled; the reader (VehicleProfileService.ApplyXmlSettingsToStore) looks for setTool_isHeadlandSectionControl, a name AgOpenGPS does not write, so it always reads the default",
    };

    /// <summary>The AgOpenGPS settings this build's reader takes from the combined file and
    /// then loses: <c>LoadAutoSteerConfig</c> finds no <c>&lt;name&gt;.AutoSteer.json</c> for a
    /// tractor from AgOpenGPS and resets the AutoSteer settings to their defaults.</summary>
    private static readonly HashSet<string> AogReadThenReset = new(StringComparer.Ordinal) { "setAS_minSteerSpeed", "setAS_maxSteerSpeed" };

    /// <summary>R8: a farmer's AgOpenGPS 6.8.6 folder moved to AgOpenWeb as a farmer does it: the
    /// field folders and the profile folders copied into Documents/AgOpenWeb, the AgOpenGPS folder
    /// left as it is. What comes through is judged setting by setting against the 6.8.6 profile;
    /// what does not is reported per settings tab, and a value read from the stale pre-6.8.2
    /// combined file is called out.</summary>
    private static async Task AgOpenGpsInstallationAsync(Scenario sc, bool freshInstall)
    {
        string root = Kit.NewDataRoot(freshInstall ? "R8F" : "R8");
        string aogRoot = Path.Combine(root, "AgOpenGPS");
        var aog = AgOpenGpsInstall.Standard(freshInstall);
        aog.Write(aogRoot);
        var (legacy, vprof, tprof, nfields) = AgOpenGpsInstall.Count(aogRoot);
        string hashBefore = AgOpenGpsInstall.TreeHash(aogRoot);
        sc.Step(Kit.F($"an AgOpenGPS {AgOpenGpsInstall.Version} folder: {vprof} tractors in VehicleProfiles/ ({string.Join(", ", aog.Vehicles.Select(v => v.Name))}), {tprof} implements in ToolProfiles/ ({string.Join(", ", aog.Tools.Select(t => t.Name))}), each with its .last (the save before), Environment/{aog.EnvironmentFile} (U-turn, tram), AgIO/{aog.AgIo.Name}.XML (NTRIP caster), {(legacy > 0 ? Kit.F($"{legacy} combined Vehicles/*.XML left from before 6.8.2 ('{aog.Vehicles.First(v => v.InLegacyFile).Name}', with that day's values)") : "no combined Vehicles/*.XML (a fresh 6.8.2+ install)")}, {nfields} fields"));
        var copied = AgOpenGpsInstall.CopyInto(aogRoot, root);
        sc.Step(Kit.F($"the farmer copies {string.Join(", ", copied)} into Documents/AgOpenWeb (field folders into Fields/); the AgOpenGPS folder stays where it is"));

        var fieldsFarm = new Farm { ActivePair = ("", "") };
        fieldsFarm.Fields.AddRange(aog.Fields);
        bool SkipNonField(string what) => what is "settings" or "pair" or "lastfield" or "language" or "hotkey" or "theme" or "view" or "ntrip" || what.StartsWith("units", StringComparison.Ordinal) || what.Contains('.');
        Snapshot first;
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "first start on the copied AgOpenGPS folders", "AOG");
            first = await Snapshot.TakeAsync(rig, openFields: true);
            sc.Note(Kit.F($"this build lists vehicles: {(first.Vehicles.Count == 0 ? "none" : string.Join(", ", first.Vehicles))}; implements: {(first.Tools.Count == 0 ? "none" : string.Join(", ", first.Tools))}; in use: '{first.ActiveVehicle}' / '{first.ActiveTool}'"));

            // Tractors and implements: listed at all?
            foreach (var v in aog.Vehicles.Where(v => !first.Vehicles.Contains(v.Name)))
                sc.Problem(Severity.DataLoss, "AOG-VEHICLE-NOT-LISTED", Kit.F($"the tractor '{v.Name}' (VehicleProfiles/{v.Name}.xml, {v.Settings.Count} settings) is not in the vehicle list: this build lists Vehicles/*.XML (AgOpenGPS's combined file of before 6.8.2) and looks for 6.8.2 split files beside it ({v.Name}.tool.xml, {v.Name}.env.xml), while {AgOpenGpsInstall.Version} keeps a tractor in VehicleProfiles/, an implement in ToolProfiles/ and the rest in Environment/{aog.EnvironmentFile}"));
            foreach (var t in aog.Tools.Where(t => !first.Tools.Contains(t.Name)))
                sc.Problem(Severity.DataLoss, "AOG-TOOL-NOT-LISTED", Kit.F($"the implement '{t.Name}' (ToolProfiles/{t.Name}.xml: {t.Settings.First(x => x.Dump == "Tool.Width").Value} m, {t.Settings.First(x => x.Dump == "NumSections").Value} sections) is not in the implement list: this build lists Tools/*.json only and reads no ToolProfiles/ folder"));

            // Each listed tractor, loaded with the implement in use: its own settings, the
            // environment's, and the guidance gains 6.8.6 keeps with the implement in use; for a
            // tractor with a combined file and no listed implement, the implement that file holds.
            // Then each listed implement, with its settings and the environment's U-turn and tram.
            bool ToolListed(string name) => first.Tools.Contains(name);
            bool otherPairLoaded = false;
            async Task<Dictionary<string, string>> DumpWith(string vehicle, string tool)
            {
                if (first.ActiveVehicle == vehicle && first.ActiveTool == tool) return first.Dump;
                otherPairLoaded = true;
                await rig.SendAsync("profile.load", vehicle + "\t" + tool);
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                await rig.RunAsync(() => Snapshot.DumpStore(d, rig));
                sc.Step(Kit.F($"'{vehicle}' / '{tool}' loaded"));
                return d;
            }
            string toolForVehicles = ToolListed(aog.ToolInUse.Name) ? aog.ToolInUse.Name : first.ActiveTool;
            foreach (var v in aog.Vehicles.Where(v => first.Vehicles.Contains(v.Name)))
            {
                var dump = await DumpWith(v.Name, toolForVehicles);
                var specs = new List<AogSetting>(v.Settings);
                specs.AddRange(aog.Environment);
                specs.AddRange(aog.ToolInUse.Settings.Where(x => x.Group == "guidance"));
                if (v.InLegacyFile && !ToolListed(v.ToolName))
                {
                    if (first.ActiveVehicle == v.Name)
                        sc.Note(Kit.F($"'{v.Name}': the implement its combined file holds (that day's '{v.ToolName}') is in use under the name '{first.ActiveTool}'"));
                    specs.AddRange(aog.Tools.First(t => t.Name == v.ToolName).Settings.Where(x => x.Group != "guidance"));
                }
                JudgeAog(sc, v.Name, specs, dump, v.InLegacyFile);
            }
            string vehicleForTools = first.Vehicles.Contains(aog.VehicleInUse.Name) ? aog.VehicleInUse.Name : first.ActiveVehicle;
            foreach (var t in aog.Tools.Where(t => ToolListed(t.Name)))
            {
                var dump = await DumpWith(vehicleForTools, t.Name);
                var specs = t.Settings.Where(x => x.Group != "guidance").ToList();
                specs.AddRange(aog.Environment.Where(x => x.Group is "uturn" or "tram"));
                JudgeAog(sc, t.Name, specs, dump, hasLegacyFile: false);
            }
            // Back to the pair the move left in use, so the restart is judged against it.
            if (otherPairLoaded)
            {
                await rig.SendAsync("profile.load", first.ActiveVehicle + "\t" + first.ActiveTool);
                sc.Step(Kit.F($"'{first.ActiveVehicle}' / '{first.ActiveTool}' loaded again"));
            }
            if (first.ActiveVehicle == aog.VehicleInUse.Name && first.ActiveTool == aog.ToolInUse.Name)
                sc.Ok(Kit.F($"the pair in use is AgOpenGPS's: '{first.ActiveVehicle}' / '{first.ActiveTool}'"));
            else
                sc.Note(Kit.F($"the pair in use is '{first.ActiveVehicle}' / '{first.ActiveTool}'; AgOpenGPS last saved '{aog.VehicleInUse.Name}' / '{aog.ToolInUse.Name}'"));

            // What is left of the copied AgOpenGPS profile folders in AgOpenWeb's.
            var leftOver = new[] { "VehicleProfiles", "ToolProfiles", "Environment", "AgIO" }
                .Select(d => Path.Combine(root, "AgOpenWeb", d)).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d).Select(f => Path.GetFileName(d) + "/" + Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            sc.Note(leftOver.Count == 0
                ? "the copied AgOpenGPS profile files are gone from the AgOpenWeb folder (imported)"
                : Kit.F($"copied AgOpenGPS profile files still in the AgOpenWeb folder: {string.Join(", ", leftOver)}"));

            // The NTRIP caster of AgIO.
            var caster = first.Ntrips.FirstOrDefault(n => string.Equals(n.Host, aog.AgIo.CasterHost, StringComparison.OrdinalIgnoreCase) && n.Mount == aog.AgIo.Mount);
            if (caster != null && caster.Port == aog.AgIo.CasterPort && caster.User == aog.AgIo.User && caster.Password == aog.AgIo.Password)
                sc.Ok(Kit.F($"AgIO's caster {aog.AgIo.CasterHost}:{aog.AgIo.CasterPort}/{aog.AgIo.Mount} is the NTRIP profile '{caster.Name}' with its login{(caster.IsDefault ? ", the default" : "")}"));
            else if (caster != null)
                sc.Problem(Severity.Bug, "AOG-NTRIP-LOGIN", Kit.F($"AgIO's caster is the NTRIP profile '{caster.Name}', but port {caster.Port} / login {(caster.User == aog.AgIo.User ? "user ok" : "user differs")}, {(caster.Password == aog.AgIo.Password ? "password ok" : "password differs")} (AgIO: port {aog.AgIo.CasterPort})"));
            else
                sc.Problem(Severity.Improvement, "AOG-NTRIP-NOT-IMPORTED", Kit.F($"AgIO's NTRIP caster ({aog.AgIo.CasterHost}:{aog.AgIo.CasterPort}, mount point {aog.AgIo.Mount}, a login, on) is not among the NTRIP profiles ({(first.Ntrips.Count == 0 ? "none" : string.Join(", ", first.Ntrips.Select(n => n.Name)))}): this build reads no AgIO/*.XML, so the farmer types the caster, the mount point and the login again"));

            // The fields: the geometry as for any import, then every other file AgOpenGPS writes.
            Judge.Farm(sc, fieldsFarm, first, "AOG-INSTALL", SkipNonField);
            foreach (var f in aog.Fields)
            {
                var fr = first.Fields.FirstOrDefault(x => x.Name == f.Name);
                if (fr == null || !fr.Opened) continue;
                var aogLeft = fr.Files.Where(x => !x.Contains('/') && (x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && x.StartsWith("BackPic", StringComparison.Ordinal))).ToList();
                var other = fr.Files.Where(x => x.EndsWith(".kml", StringComparison.OrdinalIgnoreCase) || x.StartsWith("zISOXML/", StringComparison.Ordinal)).ToList();
                if (aogLeft.Count > 0) sc.Note(Kit.F($"'{f.Name}': AgOpenGPS files still in the AgOpenWeb copy after the import: {string.Join(", ", aogLeft)}"));
                if (other.Count > 0) sc.Note(Kit.F($"'{f.Name}': left as they are (not AgOpenGPS field data this build reads): {string.Join(", ", other)}"));
                if (f != aog.FullField) continue;
                foreach (var (src, dst) in new[] { ("Contour.txt", "contours.geojson"), ("RecPath.txt", "recorded-paths.geojson"), ("Elevation.txt", "elevation.csv"), ("BackPic.Txt + BackPic.png", "background.png") })
                {
                    if (fr.Files.Contains(dst)) sc.Ok(Kit.F($"'{f.Name}': {src} imported as {dst}"));
                    else sc.Problem(Severity.DataLoss, "AOG-SIDECAR-NOT-IMPORTED", Kit.F($"'{f.Name}': {src} was not imported as {dst} (files: {string.Join(", ", fr.Files)})"));
                }
                var job = fr.Jobs.FirstOrDefault();
                if (job == null) sc.Problem(Severity.DataLoss, "AOG-COVERAGE-NOT-IMPORTED", Kit.F($"'{f.Name}': Sections.txt ({aog.CoverageSqM:0} m² worked) made no job"));
                else if (job.WorkedSqM < aog.CoverageSqM * 0.5 || job.WorkedSqM > aog.CoverageSqM * 1.5)
                    sc.Problem(Severity.Bug, "AOG-COVERAGE-AREA", Kit.F($"'{f.Name}': the imported job '{job.Task}' resumes with {job.WorkedSqM:0} m² worked; Sections.txt described {aog.CoverageSqM:0} m²"));
                else sc.Ok(Kit.F($"'{f.Name}': Sections.txt imported as job '{job.Task}' with {job.WorkedSqM:0} m² worked"));
            }
            Kit.CheckRig(sc, rig, "after opening the imported fields", "AOG");
        }

        await using (var rig = await UpdateRig.StartAsync(root))
        {
            sc.Step("restart");
            var again = await Snapshot.TakeAsync(rig, openFields: true);
            Judge.Dumps(sc, first.Dump, again.Dump, "AOG-RESTART-DRIFT", "restart after the move", UpgraderOperator.IsVolatile);
            Judge.Farm(sc, fieldsFarm, again, "AOG-INSTALL-RESTART", SkipNonField);
            Kit.CheckRig(sc, rig, "restart", "AOG");
        }
        string hashAfter = AgOpenGpsInstall.TreeHash(aogRoot);
        if (hashAfter == hashBefore) sc.Ok(Kit.F($"the AgOpenGPS folder is byte for byte as it was ({Directory.GetFiles(aogRoot, "*", SearchOption.AllDirectories).Length} files): AgOpenGPS still runs on it"));
        else sc.Problem(Severity.DataLoss, "AOG-ORIGINALS-CHANGED", "the AgOpenGPS folder changed while this build ran on its copy");
        Kit.CheckSaveErrors(sc, root, "AOG", "this build");
        Kit.TryDelete(root);
    }

    /// <summary>One tractor's settings against the store after it was loaded: imported, read
    /// from the stale combined file, or not imported (per settings tab).</summary>
    private static void JudgeAog(Scenario sc, string vehicle, List<AogSetting> specs, Dictionary<string, string> dump, bool hasLegacyFile)
    {
        var ok = new List<string>();
        var stale = new List<string>();
        var fromLast = new List<string>();
        var missing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var st in specs)
        {
            if (!dump.TryGetValue(st.Dump, out var got)) { sc.Harness(Kit.F($"no dump key '{st.Dump}' for {st.Key}")); continue; }
            if (Judge.Same(st.Reads, got)) { ok.Add(st.Key); continue; }
            if (hasLegacyFile && st.StaleReads != null && Judge.Same(st.StaleReads, got))
            {
                stale.Add(Kit.F($"{st.Key} {got} (6.8.6 holds {st.Value})"));
                continue;
            }
            if (st.Before != null && Judge.Same(st.Before, got))
            {
                fromLast.Add(Kit.F($"{st.Key} {got} (the last save holds {st.Value})"));
                continue;
            }
            if (hasLegacyFile && AogReadThenReset.Contains(st.Key))
            {
                sc.Problem(Severity.Bug, "AOG-SETTING-READ-THEN-RESET", Kit.F($"'{vehicle}': {st.Key} reads back as {st.Dump} {got}, neither {AgOpenGpsInstall.Version}'s {st.Value} nor the combined file's {st.Stale}: VehicleProfileService.ApplyXmlSettingsToStore sets it from Vehicles/{vehicle}.XML, then ConfigurationService.LoadAutoSteerConfig finds no {vehicle}.AutoSteer.json and resets every AutoSteer setting to its default"));
                continue;
            }
            if (AogKeyMismatch.TryGetValue(st.Key, out var why))
            {
                sc.Problem(Severity.Bug, "AOG-SETTING-KEY-MISMATCH", Kit.F($"'{vehicle}': {st.Key} = {st.Value} reads back as {st.Dump} {got}: {why}"));
                continue;
            }
            if (!missing.TryGetValue(st.Group, out var list)) missing[st.Group] = list = new List<string>();
            list.Add(Kit.F($"{st.Key} {st.Value} → {got}"));
        }
        if (ok.Count > 0) sc.Ok(Kit.F($"'{vehicle}': {ok.Count} of {specs.Count} AgOpenGPS settings read back as {AgOpenGpsInstall.Version} holds them ({string.Join(", ", ok)})"));
        if (stale.Count > 0)
            sc.Problem(Severity.DataLoss, "AOG-PROFILE-STALE-SOURCE", Kit.F($"'{vehicle}': {stale.Count} settings come from Vehicles/{vehicle}.XML, the combined file AgOpenGPS stopped writing at 6.8.2, not from the tractor's VehicleProfiles/, ToolProfiles/ and Environment/ files of {AgOpenGpsInstall.Version}; the farmer steers and sprays with the measurements of that day: {string.Join("; ", stale)}"));
        if (fromLast.Count > 0)
            sc.Problem(Severity.DataLoss, "AOG-PROFILE-FROM-LAST", Kit.F($"'{vehicle}': {fromLast.Count} settings come from the profile's .last file, the save before the farmer's last one, not from the .xml: {string.Join("; ", fromLast)}"));
        foreach (var (group, list) in missing.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sc.Problem(Severity.Improvement, "AOG-SETTING-NOT-IMPORTED", Kit.F($"'{vehicle}', {AogGroupName(group)}: {list.Count} AgOpenGPS setting(s) not imported (AgOpenGPS value → this build's): {string.Join("; ", list)}"));
    }

    private static string AogGroupName(string group) => group switch
    {
        "vehicle" => "tractor dimensions",
        "steer" => "AutoSteer (steer module settings)",
        "imu" => "roll (IMU)",
        "gps" => "GPS (dual antenna, step)",
        "tool" => "implement",
        "sections" => "sections",
        "guidance" => "guidance",
        "uturn" => "U-turn",
        "tram" => "tram lines",
        _ => group,
    };

    private static async Task OwnAgOpenGpsFieldsAsync(Scenario sc, string sourceDir)
    {
        string root = Kit.NewDataRoot("R7");
        string fieldsDir = Path.Combine(root, "AgOpenWeb", "Fields");
        int copied = 0;
        // A whole AgOpenGPS folder (Documents/AgOpenGPS, with Fields/ and the profile folders)
        // is copied as R8 copies it; a folder of field folders, field by field.
        bool wholeInstall = Directory.Exists(Path.Combine(sourceDir, "Fields"));
        string? hashBefore = null;
        if (wholeInstall)
        {
            var (legacy, vprof, tprof, nfields) = AgOpenGpsInstall.Count(sourceDir);
            hashBefore = AgOpenGpsInstall.TreeHash(sourceDir);
            AgOpenGpsInstall.CopyInto(sourceDir, root);
            copied = nfields;
            sc.Step(Kit.F($"the farmer's AgOpenGPS folder copied in: {nfields} field(s), {vprof} VehicleProfiles, {tprof} ToolProfiles, {legacy} combined Vehicles/*.XML"));
        }
        else
        {
            foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            {
                if (!File.Exists(Path.Combine(dir, "Field.txt"))) continue;
                Kit.CopyDirectory(dir, Path.Combine(fieldsDir, Path.GetFileName(dir)));
                copied++;
            }
            sc.Step(Kit.F($"{copied} AgOpenGPS field folder(s) copied in"));
        }
        if (copied == 0 && !wholeInstall) { sc.Note("no folder with a Field.txt found"); return; }
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            var s = await Snapshot.TakeAsync(rig, openFields: true);
            if (wholeInstall)
            {
                var (_, vprof, tprof, _) = AgOpenGpsInstall.Count(sourceDir);
                sc.Note(Kit.F($"this build lists {s.Vehicles.Count} vehicle(s) and {s.Tools.Count} implement(s) for the farmer's {vprof} VehicleProfiles and {tprof} ToolProfiles"));
            }
            int opened = s.Fields.Count(f => f.Opened), withBoundary = s.Fields.Count(f => f.OuterPoints > 0), tracks = s.Fields.Sum(f => f.Tracks.Count), flags = s.Fields.Sum(f => f.Flags.Count), jobs = s.Fields.Sum(f => f.Jobs.Count);
            sc.Note(Kit.F($"{s.Fields.Count} listed, {opened} open, {withBoundary} with a boundary, {tracks} tracks, {flags} flags, {jobs} jobs imported"));
            foreach (var f in s.Fields.Where(f => !f.Opened))
                sc.Problem(Severity.DataLoss, "AOG-OWN-FIELD-NOT-OPENING", Kit.F($"an AgOpenGPS field of the farmer's does not open{(Journal.Verbose ? " (" + f.Name + ")" : "")}: {string.Join(" | ", f.Said)}"));
            Kit.CheckRig(sc, rig, "after opening the farmer's fields");
        }
        if (hashBefore != null && AgOpenGpsInstall.TreeHash(sourceDir) != hashBefore)
            sc.Problem(Severity.DataLoss, "AOG-ORIGINALS-CHANGED", "the farmer's AgOpenGPS folder changed while this build ran on its copy");
        Kit.TryDelete(root);
    }
}
