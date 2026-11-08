// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// The farmer on this build: a season's work, then the things that happen to a cab computer: a
/// plain restart, a power cut in a job, a settings file cut short by that power cut, the data
/// folder moved to another computer, a comma-decimal Windows, and an AgOpenGPS field folder
/// copied in by hand.
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
            ("R3", "appsettings.json, appstate.json and a profile cut short by a power cut",
                "After the season, the power goes while the files are being written: appsettings.json, appstate.json and the vehicle profile in use are each left truncated (half their bytes). The next start must recover from the .bak copies without losing a value, and say so.", TruncatedFilesAsync),
            ("R4", "The data folder moved to another computer",
                "The farmer copies Documents/AgOpenWeb to a new cab computer (another path) and deletes the old one. appsettings.json holds the Fields folder as an absolute path of the old computer; this build must find the fields, profiles and casters in the new place.", MovedFolderAsync),
            ("R5", "An AgOpenGPS field folder copied into Fields",
                "A field from AgOpenGPS 6.x (Field.txt, Boundary.txt, Headland.Txt, TrackLines.txt with an AB line and a curve, Flags.txt, Sections.txt with worked area) is copied into the Fields folder by hand. Opening it must import the boundary, the headland, the tracks, the flags and the worked area as a job; a restart must keep them; the AgOpenGPS files are deleted.", AgOpenGpsImportAsync),
            ("R6", "A comma-decimal computer (pt-PT) saves and reads the season",
                "The same farm, on a computer whose Windows is set to Portuguese (decimal comma, as in Finland, Germany, France): every file is written and read under that culture. Values must come back as set (the globalization fence of CONTRIBUTING.md).", CommaDecimalAsync),
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
                Kit.F($"Every AgOpenGPS field folder under AGOPENWEB_UPDATE_AOG_FIELDS is copied into the Fields folder and opened. Only counts are reported: the fields stay on this computer."));
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
            if (sc.Row.Findings.Any(f => f.Code == "POWERCUT-DEVICE-SETTING-CHANGED"))
                sc.Note("the device settings lost are the GPS and Roll ones: they live in the vehicle profile file, written at a profile save, a profile switch or the exit save; Units, Display and Connections are written to appsettings.json at once and survived");
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

    private static async Task OwnAgOpenGpsFieldsAsync(Scenario sc, string sourceDir)
    {
        string root = Kit.NewDataRoot("R7");
        string fieldsDir = Path.Combine(root, "AgOpenWeb", "Fields");
        int copied = 0;
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            if (!File.Exists(Path.Combine(dir, "Field.txt"))) continue;
            Kit.CopyDirectory(dir, Path.Combine(fieldsDir, Path.GetFileName(dir)));
            copied++;
        }
        sc.Step(Kit.F($"{copied} AgOpenGPS field folder(s) copied in from {sourceDir}"));
        if (copied == 0) { sc.Note("no folder with a Field.txt found"); return; }
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            var s = await Snapshot.TakeAsync(rig, openFields: true);
            int opened = s.Fields.Count(f => f.Opened), withBoundary = s.Fields.Count(f => f.OuterPoints > 0), tracks = s.Fields.Sum(f => f.Tracks.Count), flags = s.Fields.Sum(f => f.Flags.Count), jobs = s.Fields.Sum(f => f.Jobs.Count);
            sc.Note(Kit.F($"{s.Fields.Count} listed, {opened} open, {withBoundary} with a boundary, {tracks} tracks, {flags} flags, {jobs} jobs imported"));
            foreach (var f in s.Fields.Where(f => !f.Opened))
                sc.Problem(Severity.DataLoss, "AOG-OWN-FIELD-NOT-OPENING", Kit.F($"an AgOpenGPS field of the farmer's does not open{(Journal.Verbose ? " (" + f.Name + ")" : "")}: {string.Join(" | ", f.Said)}"));
            Kit.CheckRig(sc, rig, "after opening the farmer's fields");
        }
        Kit.TryDelete(root);
    }
}
