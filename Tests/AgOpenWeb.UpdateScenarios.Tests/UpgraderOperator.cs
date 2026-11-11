// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// The farmer who updates: a season's work on an older release, then this build installed over
/// it (same data folder, new program), a restart, and a look at the older build opening what this
/// build wrote (a rollback). One scenario per release, oldest first.
/// </summary>
public static class UpgraderOperator
{
    public const string Name = "Upgrader (an older release, then this build)";

    public static async Task RunAsync(Journal journal, string? only)
    {
        var wanted = OldRelease.Wanted();
        journal.Environment_.Add("Releases updated from: " + string.Join(", ", wanted.Select(w => w.Tag)) + " (AGOPENWEB_UPDATE_FROM); bundle cache: " + OldRelease.CacheDir);
        foreach (var (tag, givenPath) in wanted)
        {
            string code = "U-" + tag.TrimStart('v');
            if (Kit.Want(only, code) || Kit.Want(only, "U"))
            {
                var sc = new Scenario(Name, code, Kit.F($"A season on {tag}, then this build installed over it"),
                    Kit.F($"The farmer ran AgOpenWeb {tag} (its Linux daemon bundle) for a season: two tractors and two implements set up, casters saved, three fields with boundary, headland, tracks and flags, one job sprayed. The daemon is stopped as systemd stops it, this build is installed in its place and started on the same data folder, then restarted once more; finally {tag} is started again on what this build left (a rollback after a bad update)."));
                try { await UpdateAsync(sc, tag, givenPath); }
                catch (Exception ex) { sc.Harness("unhandled: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
                journal.Add(sc.Row);
            }

            if (tag == wanted[^1].Tag)
            {
                string kcode = "UK-" + tag.TrimStart('v');
                if (!Kit.Want(only, kcode) && !Kit.Want(only, "U")) continue;
                var sk = new Scenario(Name, kcode, Kit.F($"A power cut on {tag} in the middle of a job, then this build"),
                    Kit.F($"The same season on {tag}, but the tractor's power goes while the sprayer job is running (the daemon is killed, no exit save): the farmer installs this build the next morning. What is lost is what the autosave (every 30 s) had not written yet."));
                try { await KillAsync(sk, tag, givenPath); }
                catch (Exception ex) { sk.Harness("unhandled: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
                journal.Add(sk.Row);
            }
        }
    }

    /// <summary>The older release, a season on it, SIGTERM. Returns the data root and the farm,
    /// or null when the release could not be run (noted on the scenario).</summary>
    private static async Task<(string Root, Farm Farm, OldProcess Old)?> SeasonAsync(Scenario sc, string tag, string? givenPath, bool crashDuringJob)
    {
        var (appDir, note, harnessFault) = await OldRelease.GetAsync(tag, givenPath);
        sc.Step(Kit.F($"release {tag}: {note}"));
        if (appDir == null && harnessFault)
        {
            sc.Harness(Kit.F($"{tag} could not be run: {note}"));
            return null;
        }
        if (appDir == null)
        {
            sc.Note(Kit.F($"{tag} could not be run here: {note}. Nothing to compare."));
            return null;
        }
        string root = Kit.NewDataRoot(tag);
        var old = new OldProcess(tag, appDir, root);
        if (!await old.StartAsync())
        {
            sc.Harness(Kit.F($"{tag} did not come up within 60 s (exit code {old.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "still running"}); console: {string.Join(" | ", old.Console.TakeLast(6))}"));
            await old.DisposeAsync();
            return null;
        }
        sc.Step(Kit.F($"{tag} started headless (reports v{old.Version ?? "?"}), data in {root}"));

        var farm = Farm.Standard();
        if (crashDuringJob)
        {
            // The job must be the last thing done, and long enough for an autosave (30 s).
            foreach (var f in farm.Fields) f.Job = null;
            farm.Fields[^1].Job = new JobSpec("Night spraying", "Spraying", DriveSeconds: 45, SpeedSteps: 8);
        }
        await using (var web = new WebClient(root))
        {
            if (!await web.ConnectAsync(old.Port))
            {
                sc.Harness(Kit.F($"no control over {tag}'s WebSocket (client id {web.ClientId ?? "none"}, held {web.Held}, holder '{web.HolderName}')"));
                await old.DisposeAsync();
                return null;
            }
            sc.Step(Kit.F($"connected to ws://127.0.0.1:{old.Port}/ws as a browser with control; the build says it is version {web.AppVersion ?? "?"}"));
            if (crashDuringJob)
            {
                await BuildUntilJobAsync(web, farm, sc);
                sc.Step("the power goes: the daemon is killed with the job open");
                await old.KillAsync();
            }
            else
            {
                await FarmBuilder.BuildAsync(web, farm, sc, slow: true);
            }
            sc.Step(Kit.F($"{web.CommandsSent} commands sent, {web.FramesSeen} frames received"));
        }
        return (root, farm, old);
    }

    /// <summary>The standard build, but the last field's job is left running (the power cut).</summary>
    private static async Task BuildUntilJobAsync(WebClient web, Farm farm, Scenario sc)
    {
        // Everything up to the last field with the builder, the last field by hand.
        var last = farm.Fields[^1];
        var job = last.Job!;
        last.Job = null;
        await FarmBuilder.BuildAsync(web, farm, sc, slow: true);
        last.Job = job;
        await web.SendAsync("field.startJob", string.Join("\t", last.Name, job.WorkType, "made by the update scenario harness", job.Task));
        await web.PauseAsync(3);
        await web.SendAsync("section.master");
        for (int i = 0; i < job.SpeedSteps; i++) await web.SendAsync("sim.speedUp");
        await web.PauseAsync(job.DriveSeconds);
        sc.Step(Kit.F($"'{last.Name}': job '{job.Task}' running for {job.DriveSeconds} s at {job.SpeedSteps} km/h, sections in Auto"));
    }

    private static async Task UpdateAsync(Scenario sc, string tag, string? givenPath)
    {
        var season = await SeasonAsync(sc, tag, givenPath, crashDuringJob: false);
        if (season == null) return;
        var (root, farm, old) = season.Value;

        bool clean = await old.StopAsync();
        sc.Step(Kit.F($"{tag} stopped with SIGTERM: {(clean ? "exited" : "did not exit in 40 s, killed")}, exit code {old.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}"));
        if (!clean) sc.Problem(Severity.Bug, "OLD-BUILD-NO-CLEAN-EXIT", Kit.F($"{tag} did not exit within 40 s of SIGTERM (systemd would kill it after its timeout; the exit save may be cut)"));
        var oldErrors = old.Console.Where(l => l.Contains("Exception", StringComparison.Ordinal) || l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.StartsWith("stderr", StringComparison.Ordinal)).ToList();
        if (oldErrors.Count > 0) sc.Note(Kit.F($"{tag}'s console had {oldErrors.Count} error-looking line(s): {string.Join(" | ", oldErrors.Take(3))}"));
        sc.Row.OldBuildLog = old.Console.TakeLast(25).ToList();
        await old.DisposeAsync();

        // What the older build wrote: if it did not write a part, this build cannot lose it.
        var skip = WhatOldBuildLeftOut(sc, root, farm, tag);
        Kit.CheckSaveErrors(sc, root, "OLD-BUILD", tag);
        var oldFiles = new OldProfileFiles(root, tag);
        var filesBefore = Kit.ListFiles(root);
        sc.Row.Files = Kit.FilesSummary(Kit.F($"Files {tag} left (what this build starts from)"), root);

        // The update: this build on the same data folder.
        sc.Step("this build starts on the same data folder (the update)");
        Snapshot after;
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "first start after the update");
            var pairs = farm.Vehicles.Zip(farm.Tools, (v, t) => (v.Name, t.Name)).ToList();
            after = await Snapshot.TakeAsync(rig, openFields: true, pairs);
            sc.Step(Kit.F($"read back: {after.Vehicles.Count} vehicles, {after.Tools.Count} implements, {after.Ntrips.Count} casters, {after.Fields.Count} fields ({after.Fields.Sum(f => f.Tracks.Count)} tracks, {after.Fields.Sum(f => f.Flags.Count)} flags, {after.Fields.Sum(f => f.Jobs.Count)} jobs)"));
            Judge.Farm(sc, farm, after, "UPDATE", skip, oldFiles);

            // Resume last: the button the farmer presses first thing in the morning.
            rig.TakeEvents();
            await rig.SendAsync("field.resumeLast");
            await rig.WaitUntilAsync(() => rig.Vm.IsFieldOpen, 15000);
            await rig.SettleAsync();
            string opened = await rig.ReadAsync(() => rig.Vm.IsFieldOpen ? rig.Vm.CurrentFieldName : "");
            var lastWithJob = farm.Fields.LastOrDefault(f => f.Job != null)?.Name ?? "";
            if (opened.Length == 0) sc.Problem(Severity.Bug, "UPDATE-RESUME-LAST-FAILED", Kit.F($"'Resume last' opened nothing after the update{(Kit.Said(rig).Count > 0 ? ": " + string.Join(" | ", Kit.Said(rig)) : "")}"));
            else sc.Ok(Kit.F($"'Resume last' opened '{opened}'{(opened == lastWithJob ? " (the last job's field)" : "")}"));
            if (opened.Length > 0) { await rig.SendAsync("field.close"); await rig.SettleAsync(); }
            Kit.CheckRig(sc, rig, "after opening every field and job");
        }
        var filesAfter = Kit.ListFiles(root);
        var added = filesAfter.Except(filesBefore).Where(f => !f.Contains("/coverage/", StringComparison.Ordinal)).ToList();
        var removed = filesBefore.Except(filesAfter).Where(f => !f.Contains("/coverage/", StringComparison.Ordinal)).ToList();
        if (added.Count > 0) sc.Note(Kit.F($"this build added {added.Count} file(s): {string.Join(", ", added.Take(8))}{(added.Count > 8 ? " …" : "")}"));
        if (removed.Count > 0) sc.Note(Kit.F($"this build removed {removed.Count} file(s) of {tag}'s: {string.Join(", ", removed.Take(8))}{(removed.Count > 8 ? " …" : "")}"));

        Kit.CheckSaveErrors(sc, root, "UPDATE", "this build");

        // The restart after the update: the values this build wrote must come back unchanged.
        sc.Step("this build restarts (second start after the update)");
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "second start");
            var again = await Snapshot.TakeAsync(rig, openFields: false);
            Judge.Dumps(sc, after.Dump, again.Dump, "UPDATE-RESTART-DRIFT", "restart after the update", IsVolatile);
            if (again.ActiveVehicle != after.ActiveVehicle || again.ActiveTool != after.ActiveTool)
                sc.Problem(Severity.Bug, "UPDATE-RESTART-PAIR-CHANGED", Kit.F($"the pair in use changed on the restart: '{after.ActiveVehicle}' / '{after.ActiveTool}' → '{again.ActiveVehicle}' / '{again.ActiveTool}'"));
        }

        // Rollback: the older build on what this build wrote.
        sc.Step(Kit.F($"rollback: {tag} starts again on the data this build wrote"));
        var (appDir, againNote, _) = await OldRelease.GetAsync(tag, givenPath);
        if (appDir == null)
        {
            sc.Harness(Kit.F($"{tag} could not be run again for the rollback: {againNote}"));
            Kit.TryDelete(root);
            return;
        }
        var back = new OldProcess(tag, appDir, root);
        if (!await back.StartAsync())
        {
            sc.Problem(Severity.Robustness, "ROLLBACK-OLD-BUILD-NOT-STARTING", Kit.F($"{tag} does not start on the data folder this build wrote (exit code {back.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}); console: {string.Join(" | ", back.Console.TakeLast(5))}"));
        }
        else
        {
            await using var web = new WebClient(root);
            if (await web.ConnectAsync(back.Port))
            {
                await web.SendAsync("field.openOnly", farm.Fields[0].Name);
                await web.PauseAsync(3);
                await web.SendAsync("field.close");
                await web.PauseAsync(2);
            }
            bool exited = await back.StopAsync();
            var bad = back.Console.Where(l => l.Contains("Exception", StringComparison.Ordinal) || l.StartsWith("stderr", StringComparison.Ordinal)).ToList();
            if (bad.Count > 0) sc.Problem(Severity.Robustness, "ROLLBACK-OLD-BUILD-ERRORS", Kit.F($"{tag}, started on the data this build wrote, logged: {string.Join(" | ", bad.Take(3))}"));
            else sc.Ok(Kit.F($"{tag} started on this build's data, opened '{farm.Fields[0].Name}' and stopped {(exited ? "cleanly" : "only when killed")}"));
            sc.Row.OldBuildLog.Add("--- rollback run");
            sc.Row.OldBuildLog.AddRange(back.Console.TakeLast(12));
        }
        await back.DisposeAsync();
        Kit.CheckSaveErrors(sc, root, "ROLLBACK", tag);

        // And this build once more after the rollback: the data must still be whole.
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "start after the rollback");
            var again = await Snapshot.TakeAsync(rig, openFields: false);
            Judge.Dumps(sc, after.Dump, again.Dump, "ROLLBACK-DRIFT", Kit.F($"after {tag} ran on this build's data"), IsVolatile);
        }
        Kit.TryDelete(root);
    }

    private static async Task KillAsync(Scenario sc, string tag, string? givenPath)
    {
        var season = await SeasonAsync(sc, tag, givenPath, crashDuringJob: true);
        if (season == null) return;
        var (root, farm, old) = season.Value;
        sc.Row.OldBuildLog = old.Console.TakeLast(20).ToList();
        await old.DisposeAsync();
        var skip = WhatOldBuildLeftOut(sc, root, farm, tag);
        Kit.CheckSaveErrors(sc, root, "OLD-BUILD", tag);
        var oldFiles = new OldProfileFiles(root, tag);
        sc.Row.Files = Kit.FilesSummary(Kit.F($"Files {tag} left after the power cut"), root);

        sc.Step("this build starts on the data folder (the update, the morning after)");
        await using (var rig = await UpdateRig.StartAsync(root))
        {
            Kit.CheckRig(sc, rig, "first start after the power cut and the update");
            var pairs = farm.Vehicles.Zip(farm.Tools, (v, t) => (v.Name, t.Name)).ToList();
            var after = await Snapshot.TakeAsync(rig, openFields: true, pairs);
            // The job's coverage: whatever the 30 s autosave wrote; none at all is the finding.
            // Profile edits the older release had not saved when the power went: its settings
            // screen saved only at Save Profile or the exit save, so it lost them, not this build.
            sc.Note(Kit.F($"{tag} saved profile edits only at Save Profile or the exit save: the {farm.ProfileEdits.Count} tuned after the last save were lost by {tag} at the power cut, not judged"));
            Judge.Farm(sc, farm, after, "POWERCUT", what => skip(what) || what is "lastfield" or "view" or "theme" or "unsaved", oldFiles);
            var fr = after.Fields.FirstOrDefault(f => f.Name == farm.Fields[^1].Name);
            var job = fr?.Jobs.FirstOrDefault(j => j.Task == farm.Fields[^1].Job!.Task);
            if (job != null) sc.Note(Kit.F($"the job interrupted by the power cut resumes with {job.WorkedSqM:0} m² worked ({farm.Fields[^1].Job!.DriveSeconds} s driven; the autosave runs every 30 s)"));
            if (after.LastOpenedField != farm.Fields[^1].Name)
                sc.Problem(Severity.Robustness, "POWERCUT-LAST-FIELD-LOST", Kit.F($"after the power cut the last opened field reads '{after.LastOpenedField}' (the job was running on '{farm.Fields[^1].Name}'): appstate.json is written at exit, so 'Resume last' points at the wrong field"));
            Kit.CheckRig(sc, rig, "after opening every field and job");
        }
        Kit.TryDelete(root);
    }

    /// <summary>The parts the older build did not write to disk at all (its own doing, or a
    /// command it does not have): named, and kept out of the "lost on update" judgement.</summary>
    private static Func<string, bool> WhatOldBuildLeftOut(Scenario sc, string root, Farm farm, string tag)
    {
        var left = new HashSet<string>(StringComparer.Ordinal);
        string aow = Path.Combine(root, "AgOpenWeb");
        void Expect(string rel, string what)
        {
            if (!File.Exists(Path.Combine(aow, rel)) && !Directory.Exists(Path.Combine(aow, rel)))
            {
                left.Add(what);
                sc.Note(Kit.F($"{tag} did not write {rel}: {what} is not judged for this release"));
            }
        }
        Expect("appsettings.json", "settings");
        Expect("appstate.json", "state");
        // The theme the older build saved at exit is what this build must keep: the harness taps
        // the theme button blind on an old build, and it toggles from what the clock had set.
        try
        {
            using var st = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(aow, "appstate.json")));
            if (st.RootElement.TryGetProperty("isDayMode", out var day) && day.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                bool night = day.ValueKind == System.Text.Json.JsonValueKind.False;
                if (night != farm.NightMode) sc.Note(Kit.F($"{tag} saved {(night ? "night" : "day")} mode (the theme button toggles from what auto day/night had set): judged against that"));
                farm.NightMode = night;
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        foreach (var v in farm.Vehicles) Expect(Path.Combine("Vehicles", v.Name + ".json"), "vehicle " + v.Name);
        foreach (var t in farm.Tools) Expect(Path.Combine("Tools", t.Name + ".json"), "tool " + t.Name);
        if (!Directory.Exists(Path.Combine(aow, "NtripProfiles")) || Directory.GetFiles(Path.Combine(aow, "NtripProfiles"), "*.json").Length == 0) { left.Add("ntrip"); sc.Note(Kit.F($"{tag} wrote no NTRIP profile: casters are not judged")); }
        foreach (var f in farm.Fields) Expect(Path.Combine("Fields", f.Name), "field " + f.Name);
        return what => left.Contains(what) || (what.StartsWith("field", StringComparison.Ordinal) && left.Any(l => l.StartsWith("field", StringComparison.Ordinal) && what.Contains(l[6..], StringComparison.Ordinal)));
    }

    /// <summary>Dump keys that move between runs by design.</summary>
    public static bool IsVolatile(string key) =>
        key.Contains("LastRun", StringComparison.Ordinal) || key.Contains("Date", StringComparison.Ordinal)
        || key.Contains("Simulator", StringComparison.Ordinal) || key.Contains("LastOpenedField", StringComparison.Ordinal)
        || key.EndsWith(".Camera", StringComparison.Ordinal) || key.Contains("NtripModeNoticeShown", StringComparison.Ordinal);
}
