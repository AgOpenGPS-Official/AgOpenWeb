// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

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
            ("R3", "appsettings.json, appstate.json and a profile cut short by a power cut",
                "After the season, the power goes while the files are being written: appsettings.json, appstate.json and the vehicle profile in use are each left truncated (half their bytes). The next start must recover from the .bak copies without losing a value, and say so.", TruncatedFilesAsync),
            ("R4", "The data folder moved to another computer",
                "The farmer copies Documents/AgOpenWeb to a new cab computer (another path) and deletes the old one. appsettings.json holds the Fields folder as an absolute path of the old computer; this build must find the fields, profiles and casters in the new place.", MovedFolderAsync),
            ("R5", "An AgOpenGPS field folder copied into Fields",
                "A field from AgOpenGPS 6.x (Field.txt, Boundary.txt, Headland.Txt, TrackLines.txt with an AB line and a curve, Flags.txt, Sections.txt with worked area) is copied into the Fields folder by hand. Opening it must import the boundary, the headland, the tracks, the flags and the worked area as a job; a restart must keep them; the AgOpenGPS files are deleted.", AgOpenGpsImportAsync),
            ("R6", "A comma-decimal computer (pt-PT) saves and reads the season",
                "The same farm, on a computer whose Windows is set to Portuguese (decimal comma, as in Finland, Germany, France): every file is written and read under that culture. Values must come back as set (the globalization fence of CONTRIBUTING.md).", CommaDecimalAsync),
            ("R8", "An AgOpenGPS 6.8.6 installation moved to AgOpenWeb",
                "A farmer on AgOpenGPS 6.8.6 moves to AgOpenWeb and keeps AgOpenGPS: the field folders and the profile folders of Documents/AgOpenGPS (Vehicles, VehicleProfiles, ToolProfiles, Environment, AgIO) are copied into Documents/AgOpenWeb, the AgOpenGPS folder is left as it is. Every tractor, implement, steer, roll, GPS, U-turn, tram and NTRIP setting and every field file (boundary, headland, tracks, flags, coverage, contour, recorded path, elevation, background image) is looked for in this build; the AgOpenGPS folder must not change.", AgOpenGpsInstallationAsync),
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
    private static async Task AgOpenGpsInstallationAsync(Scenario sc)
    {
        string root = Kit.NewDataRoot("R8");
        string aogRoot = Path.Combine(root, "AgOpenGPS");
        var aog = AgOpenGpsInstall.Standard();
        aog.Write(aogRoot);
        var (legacy, vprof, tprof, nfields) = AgOpenGpsInstall.Count(aogRoot);
        string hashBefore = AgOpenGpsInstall.TreeHash(aogRoot);
        sc.Step(Kit.F($"an AgOpenGPS {AgOpenGpsInstall.Version} folder: {vprof} tractors in VehicleProfiles/ ({string.Join(", ", aog.Vehicles.Select(v => v.Name))}), {tprof} implements in ToolProfiles/ ({string.Join(", ", aog.Tools.Select(t => t.Name))}), Environment/environment.xml (U-turn, tram), AgIO/{aog.AgIo.Name}.XML (NTRIP caster), {legacy} combined Vehicles/*.XML left from before 6.8.2 ('{aog.Vehicles.First(v => v.InLegacyFile).Name}', with that day's values), {nfields} fields"));
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
                sc.Problem(Severity.DataLoss, "AOG-VEHICLE-NOT-LISTED", Kit.F($"the tractor '{v.Name}' (VehicleProfiles/{v.Name}.xml, {v.Settings.Count} settings) is not in the vehicle list: this build lists Vehicles/*.XML (AgOpenGPS's combined file of before 6.8.2) and looks for 6.8.2 split files beside it ({v.Name}.tool.xml, {v.Name}.env.xml), while {AgOpenGpsInstall.Version} keeps a tractor in VehicleProfiles/, an implement in ToolProfiles/ and the rest in Environment/environment.xml"));
            foreach (var t in aog.Tools.Where(t => !first.Tools.Contains(t.Name)))
                sc.Problem(Severity.DataLoss, "AOG-TOOL-NOT-LISTED", Kit.F($"the implement '{t.Name}' (ToolProfiles/{t.Name}.xml: {t.Settings.First(x => x.Dump == "Tool.Width").Value} m, {t.Settings.First(x => x.Dump == "NumSections").Value} sections) is not in the implement list: this build lists Tools/*.json only and reads no ToolProfiles/ folder"));

            // Each listed tractor, as loaded: its settings and the 6.8.6 environment's against
            // the store, and for the one with a combined file the implement that file holds.
            foreach (var v in aog.Vehicles.Where(v => first.Vehicles.Contains(v.Name)))
            {
                var dump = first.Dump;
                if (first.ActiveVehicle != v.Name)
                {
                    await rig.SendAsync("profile.load", v.Name + "\t" + first.ActiveTool);
                    dump = new Dictionary<string, string>(StringComparer.Ordinal);
                    var d = dump;
                    await rig.RunAsync(() => Snapshot.DumpStore(d, rig));
                    sc.Step(Kit.F($"'{v.Name}' loaded with the implement in use '{first.ActiveTool}'"));
                }
                if (v.InLegacyFile && first.ActiveVehicle == v.Name && first.ActiveTool != v.ToolName)
                    sc.Note(Kit.F($"'{v.Name}': the implement its combined file holds (that day's '{v.ToolName}') is in use under the name '{first.ActiveTool}'"));
                var specs = new List<AogSetting>(v.Settings);
                specs.AddRange(aog.Environment);
                if (v.InLegacyFile) specs.AddRange(aog.Tools.First(t => t.Name == v.ToolName).Settings);
                JudgeAog(sc, v.Name, specs, dump, v.InLegacyFile);
            }

            // The NTRIP caster of AgIO.
            if (first.Ntrips.Any(n => string.Equals(n.Host, aog.AgIo.CasterHost, StringComparison.OrdinalIgnoreCase) && n.Mount == aog.AgIo.Mount))
                sc.Ok(Kit.F($"AgIO's caster {aog.AgIo.CasterHost}:{aog.AgIo.CasterPort}/{aog.AgIo.Mount} is an NTRIP profile"));
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
