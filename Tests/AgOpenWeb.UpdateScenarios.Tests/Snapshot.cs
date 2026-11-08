// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// What this build reads from a data folder, taken through its own services after a start: every
/// setting as a flat "Section.Property" → value dump (reflection over AppSettings, the persistent
/// state and the configuration store's sections), the profile names, the NTRIP casters, and each
/// field opened the way the operator opens it (field.openOnly, then field.resumeJob for its jobs)
/// with its boundary, headland, tracks, flags, jobs and worked area.
/// </summary>
public sealed class Snapshot
{
    public Dictionary<string, string> Dump { get; } = new(StringComparer.Ordinal);
    public List<string> Vehicles { get; } = new();
    public List<string> Tools { get; } = new();
    public string ActiveVehicle = "", ActiveTool = "";
    /// <summary>Dumps of the Vehicle/Tool/AutoSteer/Guidance sections per profile pair loaded.</summary>
    public Dictionary<string, Dictionary<string, string>> PairDumps { get; } = new();
    public List<NtripRead> Ntrips { get; } = new();
    public List<FieldRead> Fields { get; } = new();
    public string LastOpenedField = "";
    public string SettingsLoad = "", StateLoad = "";
    public List<string> Said { get; } = new();

    public sealed record NtripRead(string Name, string Host, int Port, string Mount, string User, string Password, bool AutoConnect, bool IsDefault);

    public sealed class FieldRead
    {
        public string Name = "";
        public bool Opened;
        public double OriginLat, OriginLon;
        public int OuterPoints, InnerCount;
        public double AreaSqM;
        public bool HasHeadland;
        public int HeadlandPoints;
        public List<TrackRead> Tracks = new();
        public List<FlagRead> Flags = new();
        public List<JobRead> Jobs = new();
        public List<string> Files = new();
        public List<string> Said = new();
    }

    /// <summary>A saved track: its points as lat/lon, in order (AB lines are extended past the
    /// boundary when drawn, so a line is compared to the drawn points, not end to end).</summary>
    public sealed record TrackRead(string Name, string Type, List<(double Lat, double Lon)> Points);
    public sealed record FlagRead(string Name, string Color, double Lat, double Lon);
    public sealed record JobRead(string Task, string WorkType, string Status, double WorkedSqM, bool Resumed);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Everything, with each field opened and each job resumed, the pair in use restored.</summary>
    public static async Task<Snapshot> TakeAsync(UpdateRig rig, bool openFields = true, IEnumerable<(string Vehicle, string Tool)>? pairs = null)
    {
        var s = new Snapshot();
        var settings = rig.Settings;
        s.SettingsLoad = settings.LastLoadStatus.ToString();
        s.StateLoad = rig.Services.GetRequiredService<IPersistentStateService>().LastLoadStatus.ToString();
        await rig.RunAsync(() =>
        {
            DumpObject(s.Dump, "AppSettings", settings.Settings);
            DumpObject(s.Dump, "State", PersistentAppState.Instance);
            DumpStore(s.Dump, rig);
            s.Vehicles.AddRange(rig.Config.GetAvailableProfiles());
            s.Tools.AddRange(rig.Config.GetAvailableToolProfiles());
            s.ActiveVehicle = rig.Store.ActiveVehicleProfileName;
            s.ActiveTool = rig.Store.ActiveToolProfileName;
            s.LastOpenedField = PersistentAppState.Instance.LastOpenedField ?? "";
        });
        var ntrip = rig.Services.GetRequiredService<INtripProfileService>();
        if (ntrip.Profiles.Count == 0) await ntrip.LoadProfilesAsync();
        foreach (var p in ntrip.Profiles)
            s.Ntrips.Add(new NtripRead(p.Name, p.CasterHost, p.CasterPort, p.MountPoint, p.Username, p.Password, p.AutoConnectOnFieldLoad, p.IsDefault));

        // The other profile pairs: load each, dump the profile sections, then put the pair in
        // use back (and save nothing new: profile.load saves the outgoing pair as the picker does).
        if (pairs != null)
        {
            string v0 = s.ActiveVehicle, t0 = s.ActiveTool;
            foreach (var (v, t) in pairs)
            {
                await rig.SendAsync("profile.load", v + "\t" + t);
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                await rig.RunAsync(() => DumpStore(d, rig));
                s.PairDumps[v + " / " + t] = d;
            }
            if (!string.IsNullOrEmpty(v0)) await rig.SendAsync("profile.load", v0 + "\t" + t0);
        }

        if (openFields)
        {
            var fieldService = rig.Services.GetRequiredService<AgOpenWeb.Services.IFieldService>();
            var jobs = rig.Services.GetRequiredService<IJobService>();
            var coverage = rig.Services.GetRequiredService<ICoverageMapService>();
            rig.TakeEvents();
            foreach (var name in fieldService.GetAvailableFields(rig.FieldsDir))
            {
                var fr = new FieldRead { Name = name };
                string dir = Path.Combine(rig.FieldsDir, name);
                await rig.SendAsync("field.openOnly", name);
                await rig.WaitUntilAsync(() => rig.Vm.IsFieldOpen && rig.Vm.CurrentFieldName == name, 15000);
                await rig.SettleAsync();
                await rig.RunAsync(() =>
                {
                    fr.Opened = rig.Vm.IsFieldOpen && rig.Vm.CurrentFieldName == name;
                    if (!fr.Opened) return;
                    fr.OriginLat = rig.State.Field.OriginLatitude;
                    fr.OriginLon = rig.State.Field.OriginLongitude;
                    var plane = rig.State.Field.LocalPlane ?? new LocalPlane(new Wgs84(fr.OriginLat, fr.OriginLon), new SharedFieldProperties());
                    (double Lat, double Lon) LL(double e, double n) { var w = plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e)); return (w.Latitude, w.Longitude); }
                    var b = rig.State.Field.CurrentBoundary;
                    if (b?.OuterBoundary is { } outer)
                    {
                        fr.OuterPoints = outer.Points.Count;
                        fr.AreaSqM = outer.AreaSquareMeters;
                    }
                    fr.InnerCount = b?.InnerBoundaries.Count ?? 0;
                    // AgOpenWeb's headland is a line (HeadlandLine); an AgOpenGPS import brings the
                    // headland polygon of Headland.Txt instead.
                    var hp = b?.HeadlandPolygon;
                    fr.HasHeadland = rig.State.Field.HasHeadland || hp is { IsValid: true };
                    fr.HeadlandPoints = rig.State.Field.HasHeadland ? rig.State.Field.HeadlandLine!.Count : hp?.Points.Count ?? 0;
                    foreach (var t in rig.Vm.SavedTracks)
                        fr.Tracks.Add(new TrackRead(t.Name, t.Type.ToString(), t.Points.Select(p => LL(p.Easting, p.Northing)).ToList()));
                    // A flag placed on the map has its field-local position only (its Wgs84 reads 0,0).
                    foreach (var f in rig.Vm.Flags)
                    {
                        var (lat, lon) = LL(f.Easting, f.Northing);
                        fr.Flags.Add(new FlagRead(f.Name, f.FlagColor.ToString(), lat, lon));
                    }
                });
                foreach (var j in jobs.ListJobs(name))
                {
                    double worked = 0; bool resumed = false;
                    await rig.SendAsync("field.resumeJob", name + "\t" + j.TaskName);
                    await rig.WaitUntilAsync(() => jobs.ActiveJob?.TaskName == j.TaskName, 15000);
                    await rig.SettleAsync();
                    await rig.RunAsync(() =>
                    {
                        resumed = jobs.ActiveJob?.TaskName == j.TaskName;
                        worked = coverage.TotalWorkedArea;
                    });
                    fr.Jobs.Add(new JobRead(j.TaskName, j.WorkType, j.Status.ToString(), worked, resumed));
                }
                fr.Said = Kit.Said(rig);
                await rig.SendAsync("field.close");
                await rig.WaitUntilAsync(() => !rig.Vm.IsFieldOpen, 15000);
                await rig.SettleAsync();
                // The files after the open and the close: an import writes field.geojson then.
                fr.Files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(f => !f.Contains("/coverage/", StringComparison.Ordinal) || f.EndsWith("manifest.json", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                s.Fields.Add(fr);
            }
        }
        return s;
    }

    /// <summary>The configuration store's sections and the few derived values the operator sees.</summary>
    public static void DumpStore(Dictionary<string, string> d, UpdateRig rig)
    {
        var st = rig.Store;
        DumpObject(d, "Vehicle", st.Vehicle);
        DumpObject(d, "Tool", st.Tool);
        DumpObject(d, "Guidance", st.Guidance);
        DumpObject(d, "Display", st.Display);
        DumpObject(d, "Connections", st.Connections);
        DumpObject(d, "Ahrs", st.Ahrs);
        DumpObject(d, "Machine", st.Machine);
        DumpObject(d, "Tram", st.Tram);
        DumpObject(d, "AutoSteer", st.AutoSteer);
        DumpObject(d, "Hotkeys", st.Hotkeys);
        d["NumSections"] = st.NumSections.ToString(Inv);
        d["ActualToolWidth"] = Num(st.ActualToolWidth);
        d["ActiveVehicleProfileName"] = st.ActiveVehicleProfileName;
        d["ActiveToolProfileName"] = st.ActiveToolProfileName;
        d["IsMetric"] = st.IsMetric.ToString();
    }

    /// <summary>Public readable properties of value type, string, enum, arrays and dictionaries
    /// of those; nested objects one level down. Properties that are live state, not settings,
    /// are left out by name.</summary>
    public static void DumpObject(Dictionary<string, string> d, string prefix, object? o, int depth = 0)
    {
        if (o == null) { d[prefix] = "null"; return; }
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (Skip.Any(s => p.Name.Contains(s, StringComparison.Ordinal))) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            string key = prefix + "." + p.Name;
            if (v == null) { d[key] = "null"; continue; }
            var t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal || t.IsEnum || v is DateTime) { d[key] = Fmt(v); continue; }
            if (v is IDictionary dict)
            {
                var parts = new List<string>();
                foreach (DictionaryEntry e in dict) parts.Add(Fmt(e.Key) + "=" + Fmt(e.Value));
                parts.Sort(StringComparer.Ordinal);
                d[key] = string.Join(";", parts);
                continue;
            }
            if (v is IEnumerable en && v is not string)
            {
                var parts = new List<string>();
                int count = 0;
                foreach (var e in en)
                {
                    count++;
                    if (e == null) continue;
                    var et = e.GetType();
                    if (et.IsPrimitive || e is string || et.IsEnum) parts.Add(Fmt(e));
                }
                d[key] = parts.Count == count ? string.Join(";", parts) : Kit.F($"[{count} items]");
                continue;
            }
            if (depth < 1 && t.Namespace?.StartsWith("AgOpenWeb", StringComparison.Ordinal) == true)
                DumpObject(d, key, v, depth + 1);
        }
    }

    private static readonly string[] Skip =
    {
        "HasUnsavedChanges", "LastRunDate", "IsFirstRun", "LastOpenedDate", "LastModifiedDate", "CreatedDate",
        "Fingerprint", "IsDirty", "Window", "SimulatorPanel", "ActiveVehicleProfilePath", "ActiveToolProfilePath",
        "SectionPositions", "Instance", "LastRun",
    };

    public static string Fmt(object? v) => v switch
    {
        null => "null",
        double x => Num(x),
        float x => Num(x),
        bool b => b.ToString(),
        DateTime dt => dt.ToString("s", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    public static string Num(double x) => double.IsNaN(x) ? "NaN" : Math.Round(x, 6).ToString("0.######", Inv);
}

/// <summary>Compares what was set with what this build read back, and one read-back with the
/// next. Finding codes are fixed; the message says which field, profile or key.</summary>
public static class Judge
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Does the read-back hold the farm? <paramref name="prefix"/> names the event
    /// (UPDATE after an older release, RESTART on this build).</summary>
    public static void Farm(Scenario sc, Farm farm, Snapshot s, string prefix, Func<string, bool>? skip = null, OldProfileFiles? oldFiles = null)
    {
        bool Skip(string what) => skip != null && skip(what);
        bool powerCut = prefix.StartsWith("POWERCUT", StringComparison.Ordinal);

        // Settings files
        sc.Note(Kit.F($"appsettings.json read: {s.SettingsLoad}; appstate.json read: {s.StateLoad}"));
        if (s.SettingsLoad is "Missing" or "CorruptNoBackup" && !Skip("settings"))
            sc.Problem(Severity.DataLoss, prefix + "-SETTINGS-NOT-READ", Kit.F($"appsettings.json was {s.SettingsLoad}: this build started with default settings"));

        // Profiles
        foreach (var v in farm.Vehicles)
            if (!s.Vehicles.Contains(v.Name)) sc.Problem(Severity.DataLoss, prefix + "-PROFILE-MISSING", Kit.F($"the vehicle profile '{v.Name}' is not in the list (found: {string.Join(", ", s.Vehicles)})"));
        foreach (var t in farm.Tools)
            if (!s.Tools.Contains(t.Name)) sc.Problem(Severity.DataLoss, prefix + "-PROFILE-MISSING", Kit.F($"the implement profile '{t.Name}' is not in the list (found: {string.Join(", ", s.Tools)})"));
        if (Skip("pair")) { }
        else if (s.ActiveVehicle != farm.ActivePair.Vehicle || s.ActiveTool != farm.ActivePair.Tool)
            sc.Problem(Severity.Bug, prefix + "-ACTIVE-PAIR-CHANGED", Kit.F($"the pair in use is '{s.ActiveVehicle}' / '{s.ActiveTool}', the operator left '{farm.ActivePair.Vehicle}' / '{farm.ActivePair.Tool}'"));
        else sc.Ok(Kit.F($"the pair in use is still '{s.ActiveVehicle}' / '{s.ActiveTool}'"));
        if (farm.Vehicles.Count + farm.Tools.Count > 0 && s.Vehicles.Count >= farm.Vehicles.Count && s.Tools.Count >= farm.Tools.Count)
            sc.Ok(Kit.F($"all {farm.Vehicles.Count} vehicle and {farm.Tools.Count} implement profiles are listed"));

        // The settings of each pair, from the dump taken with that pair loaded.
        var vehicleByName = farm.Vehicles.ToDictionary(v => v.Name);
        var toolByName = farm.Tools.ToDictionary(t => t.Name);
        int okSettings = 0, badSettings = 0;
        foreach (var (pair, dump) in s.PairDumps)
        {
            var names = pair.Split(" / ");
            var specs = new List<Setting>();
            if (vehicleByName.TryGetValue(names[0], out var vs)) specs.AddRange(vs.Settings);
            if (toolByName.TryGetValue(names[1], out var ts)) specs.AddRange(ts.Settings);
            var wrong = new List<(Setting St, string Got, string? InFile)>();
            foreach (var st in specs)
            {
                if (Skip(st.Key)) continue;
                if (!dump.TryGetValue(st.Dump, out var got)) { sc.Harness(Kit.F($"no dump key '{st.Dump}' for {st.Key}")); continue; }
                if (Same(st.Reads, got)) { okSettings++; continue; }
                badSettings++;
                wrong.Add((st, got, oldFiles?.ValueOf(names[0], names[1], st)));
            }
            if (oldFiles == null)
            {
                foreach (var (st, got, _) in wrong)
                    sc.Problem(Severity.DataLoss, prefix + "-PROFILE-SETTING-CHANGED", Kit.F($"'{pair}': {st.Key} was set to {st.Value}, reads back as {got} ({st.Dump})"));
                continue;
            }
            // An update: was it this build that lost it, or did the older release never write it?
            // A file value that is neither what was set nor what should read back proves the release
            // did not write the setting; with such proof on the pair, a key the file does not have
            // (a count kept as a list, a computed width) is laid at the same door. After a power cut
            // on the older release, a value its files do not hold is what the power cut took, not a
            // bug of the release.
            bool proven = wrong.Any(w => w.InFile != null && !Same(w.St.Value, w.InFile) && !Same(w.St.Reads, w.InFile));
            int notSaved = 0;
            foreach (var (st, got, inFile) in wrong)
            {
                bool computed = !st.Dump.Contains('.');   // NumSections, ActualToolWidth: made from the file's sections
                bool holds = inFile != null && (Same(st.Value, inFile) || Same(st.Reads, inFile));
                if (powerCut && !holds)
                    sc.Problem(Severity.DataLoss, prefix + "-PROFILE-SETTING-CHANGED", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}; the power cut came before it was written ({(inFile == null ? "no such key in" : "the profile file holds " + inFile + " in")} the files {oldFiles.BuildName} left), reads back as {got} ({st.Dump})"));
                else if (inFile != null && !holds)
                {
                    if (++notSaved <= 6) sc.Problem(Severity.Bug, "OLD-BUILD-SETTING-NOT-SAVED", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}, which wrote {inFile} into the profile file: the release's own doing, nothing for this build to lose (reads back as {got})"));
                }
                else if (proven && (inFile == null || computed))
                {
                    if (++notSaved <= 6) sc.Problem(Severity.Bug, "OLD-BUILD-SETTING-NOT-SAVED", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}, reads back as {got} ({st.Dump}; {(inFile == null ? "no such key in its profile file" : "its file holds " + inFile + " but the sections it wrote give this")}, and the pair's other settings were not written by {oldFiles.BuildName} either)"));
                }
                else if (holds && computed)
                    sc.Problem(Severity.Bug, "OLD-BUILD-FILE-INCONSISTENT", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}, whose profile file holds {inFile}, but the sections in the same file give {got} ({st.Dump}): the release wrote a file that disagrees with itself"));
                else if (holds)
                    sc.Problem(Severity.DataLoss, prefix + "-PROFILE-SETTING-CHANGED", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}, whose profile file holds {inFile}; this build reads back {got} ({st.Dump})"));
                else
                    sc.Problem(Severity.DataLoss, prefix + "-PROFILE-SETTING-CHANGED", Kit.F($"'{pair}': {st.Key} was set to {st.Value} on {oldFiles.BuildName}, reads back as {got} ({st.Dump}; no such key in the profile files {oldFiles.BuildName} wrote)"));
            }
            if (notSaved > 6)
                sc.Note(Kit.F($"'{pair}': {notSaved - 6} more settings {oldFiles.BuildName} did not write into its profile files ({notSaved} in all)"));
        }
        if (badSettings == 0 && okSettings > 0) sc.Ok(Kit.F($"all {okSettings} profile settings read back as set"));

        // Device settings (appsettings.json, written at once), then the profile settings tuned
        // after the last profile save (written at the next profile save, switch or exit save).
        JudgeLoose(farm.Device, "DEVICE-SETTING-CHANGED", "device settings", "written to appsettings.json at once", inProfile: false);
        JudgeLoose(farm.ProfileEdits, "UNSAVED-PROFILE-EDIT-LOST", "profile settings tuned after the last profile save",
            "a vehicle or implement profile setting, written only at a profile save, a profile switch or the exit save", inProfile: true);

        void JudgeLoose(List<Setting> list, string code, string what, string where, bool inProfile)
        {
            int ok = 0;
            foreach (var st in list)
            {
                if (Skip(st.Key)) continue;
                if (!s.Dump.TryGetValue(st.Dump, out var got)) { sc.Harness(Kit.F($"no dump key '{st.Dump}' for {st.Key}")); continue; }
                if (Same(st.Reads, got)) { ok++; continue; }
                // A profile setting is looked for in the files of the pair in use, not in any profile.
                string? inFile = inProfile ? oldFiles?.ValueOf(farm.ActivePair.Vehicle, farm.ActivePair.Tool, st) : oldFiles?.DeviceValueOf(st);
                bool fileHolds = inFile != null && (Same(st.Value, inFile) || Same(st.Reads, inFile));
                if (oldFiles != null && powerCut && !fileHolds)
                    sc.Problem(Severity.DataLoss, prefix + "-" + code, Kit.F($"{st.Key} ({where}) was set to {st.Value} on {oldFiles.BuildName}; the power cut came before it was written ({(inFile == null ? "no such key in" : "the files hold " + inFile + " in")} what {oldFiles.BuildName} left), reads back as {got} ({st.Dump})"));
                else if (oldFiles != null && inFile != null && !fileHolds)
                    sc.Problem(Severity.Bug, "OLD-BUILD-SETTING-NOT-SAVED", Kit.F($"{st.Key} was set to {st.Value} on {oldFiles.BuildName}, which wrote {inFile} into its files: the release's own doing, nothing for this build to lose (reads back as {got}, {st.Dump})"));
                else if (oldFiles != null && inFile == null)
                    sc.Problem(Severity.DataLoss, prefix + "-" + code, Kit.F($"{st.Key} ({where}) was set to {st.Value} on {oldFiles.BuildName}, reads back as {got} ({st.Dump}; no such key in the files {oldFiles.BuildName} wrote)"));
                else if (oldFiles != null)
                    sc.Problem(Severity.DataLoss, prefix + "-" + code, Kit.F($"{st.Key} ({where}) was set to {st.Value} on {oldFiles.BuildName}, whose files hold {inFile}; this build reads back {got} ({st.Dump})"));
                else if (powerCut)
                    sc.Problem(Severity.DataLoss, prefix + "-" + code, Kit.F($"{st.Key} ({where}) was set to {st.Value} and the power went before it was written: reads back as {got} ({st.Dump})"));
                else sc.Problem(Severity.DataLoss, prefix + "-" + code, Kit.F($"{st.Key} ({where}) was set to {st.Value}, reads back as {got} ({st.Dump})"));
            }
            if (ok == list.Count && ok > 0) sc.Ok(Kit.F($"all {ok} {what} read back as set"));
        }
        if (!Skip("language"))
        {
            if (s.Dump.TryGetValue("AppSettings.Language", out var lang) && lang != farm.Language)
                sc.Problem(Severity.Bug, prefix + "-LANGUAGE-CHANGED", Kit.F($"the language was '{farm.Language}', reads back as '{lang}'"));
            else sc.Ok(Kit.F($"language '{farm.Language}' kept"));
        }
        foreach (var (action, key) in farm.Hotkeys)
        {
            if (Skip("hotkey")) break;
            string got = s.Dump.TryGetValue("Hotkeys." + action, out var k) ? k : "";
            if (!s.Dump.TryGetValue("Hotkeys.Bindings", out var all) || !all.Contains(action + "=" + key, StringComparison.Ordinal))
                sc.Problem(Severity.Bug, prefix + "-HOTKEY-CHANGED", Kit.F($"the hotkey for {action} was {key}; bindings now: {all}"));
        }
        if (farm.Hotkeys.Count > 0 && !Skip("hotkey") && s.Dump.TryGetValue("Hotkeys.Bindings", out var binds) && farm.Hotkeys.All(h => binds.Contains(h.Key + "=" + h.Value, StringComparison.Ordinal)))
            sc.Ok(Kit.F($"{farm.Hotkeys.Count} hotkeys kept"));
        if (!Skip("theme"))
        {
            if (s.Dump.TryGetValue("State.IsDayMode", out var day) && (day == "False") != farm.NightMode)
                sc.Problem(Severity.Bug, prefix + "-THEME-CHANGED", Kit.F($"night mode was {farm.NightMode}; IsDayMode reads {day}"));
            else sc.Ok("day/night theme kept");
        }
        if (!Skip("view"))
        {
            if (s.Dump.TryGetValue("State.WebCameraPitch", out var pitch) && s.Dump.TryGetValue("State.WebCameraZoom", out var zoom)
                && (!Same(Snapshot.Num(farm.View.Pitch), pitch) || !Same(Snapshot.Num(farm.View.Zoom), zoom)))
                sc.Problem(Severity.Improvement, prefix + "-CAMERA-CHANGED", Kit.F($"the saved camera was pitch {farm.View.Pitch} zoom {farm.View.Zoom}; reads pitch {pitch} zoom {zoom}"));
            else sc.Ok("camera pitch and zoom kept");
        }

        // NTRIP
        foreach (var n in farm.Ntrips)
        {
            if (Skip("ntrip")) break;
            var got = s.Ntrips.FirstOrDefault(x => x.Name == n.Name);
            if (got == null) { sc.Problem(Severity.DataLoss, prefix + "-NTRIP-MISSING", Kit.F($"the NTRIP profile '{n.Name}' is gone (found: {string.Join(", ", s.Ntrips.Select(x => x.Name))})")); continue; }
            var diffs = new List<string>();
            if (got.Host != n.Host) diffs.Add("host " + got.Host);
            if (got.Port != n.Port) diffs.Add(Kit.F($"port {got.Port}"));
            if (got.Mount != n.Mount) diffs.Add("mount " + got.Mount);
            if (got.User != n.User) diffs.Add("user " + got.User);
            if (got.Password != n.Password) diffs.Add(got.Password.Length == 0 ? "password EMPTY" : "password changed");
            if (got.AutoConnect != n.AutoConnect) diffs.Add(Kit.F($"auto-connect {got.AutoConnect}"));
            if (got.IsDefault != n.IsDefault) diffs.Add(Kit.F($"default {got.IsDefault}"));
            if (diffs.Count > 0) sc.Problem(Severity.DataLoss, prefix + "-NTRIP-CHANGED", Kit.F($"the NTRIP profile '{n.Name}' reads back with {string.Join(", ", diffs)}"));
        }
        if (!Skip("ntrip") && farm.Ntrips.All(n => s.Ntrips.Any(x => x.Name == n.Name && x.Host == n.Host && x.Password == n.Password)))
            sc.Ok(Kit.F($"{farm.Ntrips.Count} NTRIP profiles kept with their passwords"));

        // Fields
        foreach (var fs in farm.Fields)
        {
            var fr = s.Fields.FirstOrDefault(x => x.Name == fs.Name);
            if (fr == null) { sc.Problem(Severity.DataLoss, prefix + "-FIELD-MISSING", Kit.F($"the field '{fs.Name}' is not listed (found: {string.Join(", ", s.Fields.Select(x => x.Name))})")); continue; }
            if (!fr.Opened) { sc.Problem(Severity.DataLoss, prefix + "-FIELD-NOT-OPENING", Kit.F($"the field '{fs.Name}' does not open{(fr.Said.Count > 0 ? ": " + string.Join(" | ", fr.Said) : "")}")); continue; }
            var problems = new List<string>();
            double dOrigin = Dist(fs.Lat, fs.Lon, fr.OriginLat, fr.OriginLon);
            if (dOrigin > 2) problems.Add(Kit.F($"origin moved {dOrigin:0.0} m"));
            if (fs.Boundary.Count > 0)
            {
                if (fr.OuterPoints == 0) sc.Problem(Severity.DataLoss, prefix + "-BOUNDARY-LOST", Kit.F($"'{fs.Name}': the boundary is gone"));
                else
                {
                    double want = fs.AreaSqM();
                    if (Math.Abs(fr.AreaSqM - want) > want * 0.02 + 20)
                        sc.Problem(Severity.DataLoss, prefix + "-BOUNDARY-CHANGED", Kit.F($"'{fs.Name}': the boundary area is {fr.AreaSqM:0} m², it was drawn with {want:0} m² ({fr.OuterPoints} points)"));
                    else sc.Ok(Kit.F($"'{fs.Name}': boundary of {fr.OuterPoints} points, {fr.AreaSqM / 10000:0.00} ha as drawn"));
                }
            }
            if (fs.HeadlandOffset != null && !Skip("headland"))
            {
                if (!fr.HasHeadland) sc.Problem(Severity.DataLoss, prefix + "-HEADLAND-LOST", Kit.F($"'{fs.Name}': the headland is gone"));
                else sc.Ok(Kit.F($"'{fs.Name}': headland kept ({fr.HeadlandPoints} points)"));
            }
            foreach (var t in fs.Tracks)
            {
                var got = fr.Tracks.FirstOrDefault(x => x.Name == t.Name);
                if (got == null) { sc.Problem(Severity.DataLoss, prefix + "-TRACK-MISSING", Kit.F($"'{fs.Name}': the track '{t.Name}' is gone (found: {string.Join(", ", fr.Tracks.Select(x => x.Name + " (" + x.Type + ")"))})")); continue; }
                string wantType = t.Curve ? "Curve" : "ABLine";
                if (got.Type != wantType) sc.Problem(Severity.Bug, prefix + "-TRACK-TYPE-CHANGED", Kit.F($"'{fs.Name}': the track '{t.Name}' was a {wantType}, reads as {got.Type}"));
                // The saved line is extended past the boundary at both ends and a drawn curve is
                // smoothed, so the test is: every point drawn lies on the saved line.
                double off = t.Points.Max(p => DistToLine(p.Lat, p.Lon, got.Points));
                double allow = t.Curve ? 2.5 : 1.0;
                if (off > allow) sc.Problem(Severity.DataLoss, prefix + "-TRACK-MOVED", Kit.F($"'{fs.Name}': the track '{t.Name}' is up to {off:0.0} m off the line drawn ({got.Points.Count} points saved)"));
            }
            if (fs.Tracks.Count > 0 && fs.Tracks.All(t => fr.Tracks.Any(x => x.Name == t.Name)))
                sc.Ok(Kit.F($"'{fs.Name}': {fs.Tracks.Count} tracks kept with their names"));
            foreach (var f in fs.Flags)
            {
                var got = fr.Flags.FirstOrDefault(x => x.Name == f.Name);
                if (got == null) { sc.Problem(Severity.DataLoss, prefix + "-FLAG-MISSING", Kit.F($"'{fs.Name}': the flag '{f.Name}' is gone (found: {string.Join(", ", fr.Flags.Select(x => x.Name + " " + x.Color))})")); continue; }
                double d = Dist(f.Lat, f.Lon, got.Lat, got.Lon);
                if (d > 1.0) sc.Problem(Severity.DataLoss, prefix + "-FLAG-MOVED", Kit.F($"'{fs.Name}': the flag '{f.Name}' moved {d:0.0} m"));
                if (got.Color != f.Color) sc.Problem(Severity.Bug, prefix + "-FLAG-COLOR-CHANGED", Kit.F($"'{fs.Name}': the flag '{f.Name}' was {f.Color}, reads {got.Color}"));
            }
            if (fs.Flags.Count > 0 && fs.Flags.All(f => fr.Flags.Any(x => x.Name == f.Name && x.Color == f.Color)))
                sc.Ok(Kit.F($"'{fs.Name}': {fs.Flags.Count} flags kept with names and colours"));
            if (fs.Job is { } job && !Skip("job"))
            {
                var got = fr.Jobs.FirstOrDefault(j => j.Task == job.Task);
                if (got == null) sc.Problem(Severity.DataLoss, prefix + "-JOB-MISSING", Kit.F($"'{fs.Name}': the job '{job.Task}' is gone (found: {string.Join(", ", fr.Jobs.Select(j => j.Task))})"));
                else
                {
                    if (!got.Resumed) sc.Problem(Severity.DataLoss, prefix + "-JOB-NOT-RESUMING", Kit.F($"'{fs.Name}': the job '{job.Task}' does not resume"));
                    if (got.WorkType != job.WorkType) sc.Problem(Severity.Bug, prefix + "-JOB-CHANGED", Kit.F($"'{fs.Name}': the job '{job.Task}' was '{job.WorkType}', reads '{got.WorkType}'"));
                    if (got.WorkedSqM < 1) sc.Problem(Severity.DataLoss, prefix + "-COVERAGE-LOST", Kit.F($"'{fs.Name}': the job '{job.Task}' resumes with no worked area (it was sprayed for {job.DriveSeconds} s)"));
                    else sc.Ok(Kit.F($"'{fs.Name}': job '{job.Task}' resumes with {got.WorkedSqM:0} m² worked"));
                }
            }
            if (problems.Count > 0) sc.Problem(Severity.Bug, prefix + "-FIELD-CHANGED", Kit.F($"'{fs.Name}': {string.Join(", ", problems)}"));
        }
        if (!Skip("lastfield") && farm.LastField.Length > 0)
        {
            if (s.LastOpenedField != farm.LastField)
                sc.Problem(Severity.Improvement, prefix + "-LAST-FIELD-CHANGED", Kit.F($"the last opened field reads '{s.LastOpenedField}', the operator closed '{farm.LastField}' last"));
            else sc.Ok(Kit.F($"last opened field '{s.LastOpenedField}' kept"));
        }
    }

    /// <summary>Two dumps of the same data folder, read by this build before and after an event
    /// (a restart, a save). Anything that changed is a finding; <paramref name="volatile"/> keys
    /// are expected to move.</summary>
    public static int Dumps(Scenario sc, Dictionary<string, string> before, Dictionary<string, string> after, string code, string when, Func<string, bool>? volatileKey = null)
    {
        int changed = 0;
        var keys = before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var k in keys)
        {
            if (volatileKey != null && volatileKey(k)) continue;
            before.TryGetValue(k, out var b); after.TryGetValue(k, out var a);
            if (b == a || (b != null && a != null && Same(b, a))) continue;
            changed++;
            if (lines.Count < 12) lines.Add(Kit.F($"{k}: {b ?? "(absent)"} → {a ?? "(absent)"}"));
        }
        if (changed == 0) sc.Ok(Kit.F($"{when}: all {before.Count} setting values read back the same"));
        else sc.Problem(Severity.Bug, code, Kit.F($"{when}: {changed} setting value(s) changed by themselves: {string.Join("; ", lines)}{(changed > lines.Count ? " …" : "")}"));
        return changed;
    }

    public static bool Same(string want, string got)
    {
        if (string.Equals(want, got, StringComparison.Ordinal)) return true;
        if (double.TryParse(want, NumberStyles.Float, Inv, out var w) && double.TryParse(got, NumberStyles.Float, Inv, out var g))
            return Math.Abs(w - g) <= 1e-6 * Math.Max(1, Math.Abs(w));
        if (bool.TryParse(want, out var wb) && bool.TryParse(got, out var gb)) return wb == gb;
        return false;
    }

    public static double Dist(double lat1, double lon1, double lat2, double lon2)
    {
        double dN = (lat2 - lat1) * 111_320.0;
        double dE = (lon2 - lon1) * 111_320.0 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(dN * dN + dE * dE);
    }

    /// <summary>Distance in metres from a point to a polyline (its nearest segment).</summary>
    public static double DistToLine(double lat, double lon, List<(double Lat, double Lon)> line)
    {
        if (line.Count == 0) return double.PositiveInfinity;
        if (line.Count == 1) return Dist(lat, lon, line[0].Lat, line[0].Lon);
        double k = Math.Cos(lat * Math.PI / 180);
        double px = lon * k, py = lat;
        double best = double.PositiveInfinity;
        for (int i = 0; i < line.Count - 1; i++)
        {
            double ax = line[i].Lon * k, ay = line[i].Lat, bx = line[i + 1].Lon * k, by = line[i + 1].Lat;
            double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
            double t = len2 < 1e-18 ? 0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0, 1);
            double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy) * 111_320.0);
        }
        return best;
    }
}

/// <summary>
/// The profile files an older release left (<c>Vehicles/&lt;name&gt;.json</c>, its
/// <c>.AutoSteer.json</c>, <c>Tools/&lt;name&gt;.json</c>), read as text before this build touches
/// them, so a setting that does not read back can be laid at the right door: the release never
/// wrote it (its file holds another value), or this build does not read what the file holds.
/// </summary>
public sealed class OldProfileFiles
{
    private readonly Dictionary<string, string> _vehicle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tool = new(StringComparer.Ordinal);
    public string BuildName { get; }

    public OldProfileFiles(string dataRoot, string buildName)
    {
        BuildName = buildName;
        string aow = Path.Combine(dataRoot, "AgOpenWeb");
        string vdir = Path.Combine(aow, "Vehicles"), tdir = Path.Combine(aow, "Tools");
        if (Directory.Exists(vdir))
            foreach (var f in Directory.GetFiles(vdir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.EndsWith(".AutoSteer", StringComparison.Ordinal)) name = name[..^".AutoSteer".Length];
                _vehicle[name] = (_vehicle.GetValueOrDefault(name) ?? "") + "\n" + ReadOrEmpty(f);
            }
        if (Directory.Exists(tdir))
            foreach (var f in Directory.GetFiles(tdir, "*.json"))
                _tool[Path.GetFileNameWithoutExtension(f)] = ReadOrEmpty(f);
        _settings = ReadOrEmpty(Path.Combine(aow, "appsettings.json"));
    }

    private readonly string _settings;

    /// <summary>The raw JSON value the files hold under a device setting's key (appsettings.json
    /// first, then every profile file: the GPS and roll settings live with the vehicle), or null.</summary>
    public string? DeviceValueOf(Setting st) =>
        Find(_settings, st) ?? Find(string.Join("\n", _vehicle.Values), st) ?? Find(string.Join("\n", _tool.Values), st);

    private static string? Find(string text, Setting st)
    {
        foreach (var name in new[] { st.Key[(st.Key.IndexOf('.') + 1)..], st.Dump[(st.Dump.LastIndexOf('.') + 1)..] })
        {
            if (name.Length == 0 || name == "type") continue;
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\"\\s*:\\s*(\"[^\"]*\"|-?[0-9.eE+]+|true|false)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim('"');
        }
        return null;
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    /// <summary>The raw JSON value the files hold under the setting's key (the config.set key's last
    /// segment, else the dump key's, case-insensitive), or null when neither key is in them.</summary>
    public string? ValueOf(string vehicle, string tool, Setting st)
    {
        bool vehicleSide = st.Key.StartsWith("vehicle.", StringComparison.Ordinal) || st.Key.StartsWith("autosteer.", StringComparison.Ordinal)
            || st.Key.StartsWith("gps.", StringComparison.Ordinal) || st.Key.StartsWith("roll.", StringComparison.Ordinal);
        string own = (vehicleSide ? _vehicle.GetValueOrDefault(vehicle) : _tool.GetValueOrDefault(tool)) ?? "";
        string other = (vehicleSide ? _tool.GetValueOrDefault(tool) : _vehicle.GetValueOrDefault(vehicle)) ?? "";
        // A release may keep a setting on the other side (the U-turn settings were the vehicle's once).
        return Find(own, st) ?? Find(other, st);
    }
}
