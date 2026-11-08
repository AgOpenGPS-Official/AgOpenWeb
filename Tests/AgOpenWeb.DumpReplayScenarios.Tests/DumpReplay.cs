// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>What this build did with one row of the dump's GPS log.</summary>
public sealed class ReplayRow
{
    public double E, N, Heading, Speed, ToolE, ToolN, ToolH, Steer, Xte;
    public bool HasGuidance, Engaged, YtTriggered, YtExecuting, Reverse, GuidanceFresh;
    public int PathsAway;
    public string? Disengage;
}

/// <summary>
/// One dump, replayed: AgOpenWeb boots fresh, the operator types the reporter's settings, the
/// reporter's field folder is put in the fields folder and opened with its job, the reporter's
/// track is picked, and the headland, U-turn and skip-row buttons are set as the dump says. Then
/// each row of the GPS log goes into the GPS pipeline as the parser handed it over, at the log's
/// own pace, and the operator engages and disengages AutoSteer when the log shows the reporter
/// did. What the pipeline makes of each row is kept beside what it made for the reporter.
/// </summary>
public sealed class DumpReplay : IAsyncDisposable
{
    /// <summary>How much faster than the log's own pace (1 = real time). Gaps of more than a
    /// second (GPS lost) are replayed at real time whatever the pace: AgOpenWeb's GPS-lost and
    /// guidance watchdogs run on the wall clock.</summary>
    public static readonly double Pace = double.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_REPLAY_PACE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1.0;

    public DumpEntry Entry { get; }
    public BugDump Dump { get; }
    public Scenario S { get; }
    public ReplayRig Rig { get; private set; } = null!;
    public SteerModuleStub Steer { get; private set; } = null!;
    public LocalPlane Plane { get; private set; } = null!;
    public List<(double E, double N)> Outer { get; } = new();
    public List<(double E, double N)>? Work { get; private set; }
    public List<List<(double E, double N)>> Inner { get; } = new();
    public ReplayRow?[] Now { get; private set; } = Array.Empty<ReplayRow?>();
    public int StartRow { get; private set; }
    public bool UTurnsUsed { get; private set; }
    public bool SectionsAuto { get; private set; }
    public double DriftE, DriftN;
    public readonly List<string> Said = new();
    public readonly List<string> Exceptions = new();
    public readonly List<string> Errors = new();
    public readonly List<string> OffLoop = new();
    public bool Hung;
    public bool Ready { get; private set; }
    /// <summary>Every sound effect the host sent the web client, on the replay's clock (the GPS
    /// log's seconds; the take-over's own seconds there).</summary>
    public readonly List<(double T, SoundEffect Effect)> Sounds = new();
    private double _clock = double.NegativeInfinity;
    public double Clock { get => Volatile.Read(ref _clock); set => Volatile.Write(ref _clock, value); }
    /// <summary>Set by the judge: the first row where this build guided on another line than the
    /// reporter's (<c>Dump.Rows.Count</c> if never).</summary>
    public int DivergedRow { get; set; } = int.MaxValue;

    public DumpReplay(DumpEntry entry, BugDump dump, Scenario s) { Entry = entry; Dump = dump; S = s; }

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- set-up

    public async Task<bool> SetUpAsync(string dataRoot)
    {
        Steer = new SteerModuleStub { On = true };
        Rig = await ReplayRig.StartAsync(dataRoot);
        Collect();
        // The sounds the operator hears (the web client plays what the host sends).
        if (Rig.Services.GetService(typeof(IAudioService)) is AgOpenWeb.Services.Audio.WebClientAudioService audio)
            audio.EffectTriggered += e => { lock (Sounds) Sounds.Add((Volatile.Read(ref _clock), e)); };

        // Vehicle & Tool, as the reporter had them. What the host answers to each (a value
        // VehicleToolLimits refuses, one the section timing rules change) is kept by its key.
        var (cmds, writes, missing) = ReporterSettings.From(Dump);
        var replies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in cmds)
        {
            await Rig.SendAsync("config.set", c);
            string key = c[..c.IndexOf(':', StringComparison.Ordinal)];
            foreach (var e in Collect())
            {
                if (e.StartsWith("failure: ", StringComparison.Ordinal))
                {
                    if (!replies.TryGetValue(key, out var l)) replies[key] = l = new List<string>();
                    l.Add(e["failure: ".Length..]);
                }
                else Said.Add("set-up: " + e);
            }
        }
        await Rig.RunAsync(() => { foreach (var w in writes) w(Rig.Store); });
        S.Step(F($"Vehicle & Tool: {cmds.Count} settings typed (config.set), {writes.Count} without a web key set in the store"));
        S.Step("roll filter off (" + ReporterSettings.RollFilterOff + "): the GPS log's roll is already filtered, the pipeline would filter it again");
        if (missing.Count > 0) S.Step("not in this dump's configuration.json (AgOpenWeb's default kept): " + string.Join(", ", missing));
        foreach (var e in Collect()) Said.Add("set-up: " + e);
        await CompareSettingsAsync(replies);

        DriftE = BugDump.Get(Dump.Runtime, "Field", "DriftEasting")?.GetDouble() ?? 0;
        DriftN = BugDump.Get(Dump.Runtime, "Field", "DriftNorthing")?.GetDouble() ?? 0;

        // The reporter's field folder, as it was on their disk (with the active job's tiles).
        string name = Dump.FieldName ?? "dump-field";
        var dest = Path.Combine(Rig.FieldsDir, name);
        CopyDir(Dump.FieldDir, dest);
        var (olat, olon) = Origin();
        Plane = new LocalPlane(new Wgs84(olat, olon), new SharedFieldProperties());
        StartRow = FirstRowInField(name);
        // A receiver gives fixes before the field opens.
        var first = Dump.Rows[StartRow];
        for (int i = 0; i < 15; i++) { Rig.Feed(GpsFor(first)); Rig.TickLoop(10); await Task.Delay(20); }

        if (Dump.JobTask != null)
        {
            await Rig.SendAsync("field.resumeJob", name + "\t" + Dump.JobTask);
            S.Step($"Field > Resume job '{Dump.JobTask}' in '{name}'");
        }
        else
        {
            await Rig.SendAsync("field.openOnly", name);
            S.Step($"Field > Open '{name}' (no job in the dump)");
        }
        if (!await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && Rig.Vm.CurrentFieldName == name && !Rig.State.UI.IsBusy, 30000))
        {
            S.Problem(Severity.Bug, Kind.AppDefect, "FIELD-DID-NOT-OPEN", "the reporter's field folder did not open: " + string.Join(" | ", Collect().Take(4)));
            return false;
        }
        Plane = await Rig.ReadAsync(() => Rig.State.Field.LocalPlane) ?? Plane;
        if (Dump.JobTask != null)
        {
            var jobs = (AgOpenWeb.Services.Interfaces.IJobService)Rig.Services.GetService(typeof(AgOpenWeb.Services.Interfaces.IJobService))!;
            bool jobOn = await Rig.ReadAsync(() => jobs.ActiveJob?.TaskName == Dump.JobTask);
            if (!jobOn) S.Problem(Severity.Bug, Kind.AppDefect, "JOB-DID-NOT-RESUME", $"job '{Dump.JobTask}' is in the dump but did not resume");
        }

        // The field as AgOpenWeb loaded it.
        await Rig.RunAsync(() =>
        {
            var b = Rig.State.Field.CurrentBoundary;
            if (b?.OuterBoundary?.Points is { Count: > 2 } op) Outer.AddRange(op.Select(q => (q.Easting, q.Northing)));
            foreach (var ib in b?.InnerBoundaries ?? new List<BoundaryPolygon>())
                if (ib.Points.Count > 2) Inner.Add(ib.Points.Select(q => (q.Easting, q.Northing)).ToList());
            if (Rig.State.Field.HeadlandLine is { Count: > 2 } hl) Work = hl.Select(q => (q.Easting, q.Northing)).ToList();
        });
        S.Step(F($"field open: {(Outer.Count > 2 ? Ring.Area(Outer) / 1e4 : 0):0.00} ha, {Inner.Count} obstacles, headland line {(Work == null ? "none" : F($"{Work.Count} points"))}"));
        FieldChecks();

        // The reporter's track.
        if (Dump.ActiveTrack != null)
        {
            int idx = await Rig.ReadAsync(() => Rig.Vm.SavedTracks.ToList().FindIndex(t => t.Name == Dump.ActiveTrack));
            if (idx < 0)
                S.Problem(Severity.Bug, Kind.AppDefect, "TRACK-NOT-FOUND", $"the reporter's active track '{Dump.ActiveTrack}' is not among the field's tracks after opening");
            else
            {
                await Rig.SendAsync("track.select", idx.ToString(CultureInfo.InvariantCulture));
                var (sel, pts) = await Rig.ReadAsync(() => (Rig.Vm.SelectedTrack?.Name, Rig.Vm.SelectedTrack?.Points.Count ?? 0));
                S.Step($"track '{sel}' selected ({pts} points)");
                if (sel != Dump.ActiveTrack) S.Problem(Severity.Bug, Kind.AppDefect, "TRACK-NOT-SELECTED", $"'track.select {idx}' left '{sel}' active, not '{Dump.ActiveTrack}'");
                else await AlignPassesAsync();
            }
        }

        // The reporter's GPS drift correction (Field Tools > Offset Fix), typed in after the
        // field opened (opening a field zeroes it). The recorded pivot is after the drift.
        if (Math.Abs(DriftE) > 1e-4 || Math.Abs(DriftN) > 1e-4)
        {
            await Rig.SendAsync("offset.set", DriftE.ToString("R", CultureInfo.InvariantCulture) + "," + DriftN.ToString("R", CultureInfo.InvariantCulture));
            S.Step(F($"Offset Fix: {DriftE:0.00} m east, {DriftN:0.00} m north (the reporter's drift correction)"));
        }

        // Headland, skip rows, U-turns: as the dump's runtime state says.
        var headOn = BugDump.Get(Dump.Runtime, "FieldTools", "IsHeadlandOn");
        if (headOn is { } ho && (ho.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
        {
            bool want = ho.GetBoolean();
            if (await Rig.ReadAsync(() => Rig.Vm.IsHeadlandOn) != want) await Rig.SendAsync("headland.toggle");
            S.Step("headland " + (want ? "on" : "off") + (await Rig.ReadAsync(() => Rig.Vm.IsHeadlandOn) == want ? "" : " (did NOT switch)"));
        }
        int mode = (int)(BugDump.Get(Dump.Runtime, "FieldTools", "UTurnSkipMode")?.GetDouble() ?? 0);
        for (int k = 0; k < 3 && await Rig.ReadAsync(() => Rig.Vm.UTurnSkipMode) != mode; k++) await Rig.SendAsync("youturn.skipToggle");
        int rows = (int)(BugDump.Get(Dump.Runtime, "FieldTools", "UTurnSkipRows")?.GetDouble() ?? 0);
        if (mode != 0 || BugDump.Get(Dump.Runtime, "FieldTools", "IsUTurnSkipRowsEnabled")?.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            for (int k = 0; k < 10 && await Rig.ReadAsync(() => Rig.Vm.UTurnSkipRows) != rows; k++) await Rig.SendAsync("youturn.skipCycle");
            S.Step(F($"skip rows: mode {mode}, {rows} rows"));
        }
        UTurnsUsed = Dump.Rows.Any(r => r.YtTriggered || r.YtExecuting)
            || Dump.Log.Any(l => l.Text.Contains("[YouTurn] Creating turn", StringComparison.Ordinal) || l.Text.Contains("Triggered on the path", StringComparison.Ordinal));
        if (UTurnsUsed && !await Rig.ReadAsync(() => Rig.Vm.IsYouTurnEnabled)) await Rig.SendAsync("youturn.toggle");
        S.Step("U-turns " + (UTurnsUsed ? "on (the reporter's log shows U-turns)" : "off (no U-turn in the reporter's log)"));
        // The dump does not say whether the section master was on: a job with coverage in it
        // was being worked, so the operator switches the sections to Auto when engaging.
        SectionsAuto = Dump.JobTask != null && Dump.HasCoverageTiles;
        foreach (var e in Collect()) Said.Add("set-up: " + e);
        Now = new ReplayRow?[Dump.Rows.Count];
        Ready = true;
        return true;
    }

    /// <summary>configuration.json paths not compared: names, units and the roll filter (off on
    /// purpose, see <see cref="ReporterSettings.RollFilterOff"/>).</summary>
    private static readonly HashSet<string> NotCompared = new(StringComparer.Ordinal)
    {
        "Vehicle.Name", "ActiveProfile", "ActiveToolProfile", "IsMetric", "Ahrs.RollFilter",
    };

    /// <summary>
    /// The settings read back after they were typed: the store as this build's own bug report
    /// would write it (DebugDumpService's snapshot, the same shape as the dump's
    /// configuration.json), value by value against the dump. A value the harness typed that the
    /// host refused or changed, and said why, is a set-up note with the host's reason; one it did
    /// not keep without a word is a finding (the replay is not on the reporter's settings). A
    /// value the harness has no way to set is listed.
    /// </summary>
    private async Task CompareSettingsAsync(Dictionary<string, List<string>> replies)
    {
        if (Dump.Config is not { } then) return;
        var build = typeof(AgOpenWeb.Services.DebugDumpService).GetMethod("BuildConfigSnapshot",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (build == null)
        {
            S.Note("the settings were not read back: DebugDumpService.BuildConfigSnapshot, which writes a dump's configuration.json, is not there any more");
            return;
        }
        string json = await Rig.ReadAsync(() => (string)build.Invoke(null, new object[] { Rig.Store })!);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var diffs = new List<(string Path, string Then, string Now)>();
        int compared = Leaves(then, doc.RootElement, "", diffs);
        string[] timing = { "tool.lookAheadOn", "tool.lookAheadOff", "tool.turnOffDelay" };
        var untyped = new List<string>();
        foreach (var (path, a, b) in diffs)
        {
            var key = ReporterSettings.KeyOf(path);
            if (key == null) { untyped.Add(F($"{path} {a} (now {b})")); continue; }
            var why = replies.TryGetValue(key, out var own) ? own
                : path is "Tool.LookAheadOnSetting" or "Tool.LookAheadOffSetting" or "Tool.TurnOffDelay"
                    ? timing.SelectMany(k => replies.TryGetValue(k, out var l) ? l : new List<string>()).ToList()
                    : new List<string>();
            if (why.Count > 0)
                S.Note(F($"set-up: {path} is {b} in this build, {a} in the dump: the host said \"{string.Join("; ", why.Distinct())}\" ({key})"));
            else
                S.Problem(Severity.Inconsistency, Kind.HarnessArtifact, "SETUP-SETTING-NOT-KEPT",
                    F($"{path} was set to the dump's {a} ({key}), and this build has {b} with no word from the host: the replay does not run on the reporter's value"));
        }
        if (untyped.Count > 0)
            S.Note("set-up: values of the dump the harness has no way to set, different here (AgOpenWeb's own kept): " + string.Join(", ", untyped));
        S.Step(F($"settings read back: {compared - diffs.Count} of {compared} values of the dump's configuration.json as there"));
    }

    /// <summary>Every leaf of <paramref name="then"/> against the same path in <paramref name="now"/>
    /// (a path this build no longer has is skipped). Returns how many were compared.</summary>
    private static int Leaves(System.Text.Json.JsonElement then, System.Text.Json.JsonElement now, string path, List<(string, string, string)> diffs)
    {
        if (NotCompared.Contains(path)) return 0;
        switch (then.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
            {
                int n = 0;
                if (now.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
                foreach (var p in then.EnumerateObject())
                    if (now.TryGetProperty(p.Name, out var q)) n += Leaves(p.Value, q, path.Length == 0 ? p.Name : path + "." + p.Name, diffs);
                return n;
            }
            case System.Text.Json.JsonValueKind.Array:
            {
                int n = 0, i = 0;
                if (now.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
                int len = now.GetArrayLength();
                foreach (var item in then.EnumerateArray())
                {
                    if (i < len) n += Leaves(item, now[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", diffs);
                    i++;
                }
                return n;
            }
            case System.Text.Json.JsonValueKind.Number:
                if (now.ValueKind == System.Text.Json.JsonValueKind.Number && Math.Abs(then.GetDouble() - now.GetDouble()) <= 1e-6 * Math.Max(1, Math.Abs(then.GetDouble())))
                    return 1;
                diffs.Add((path, then.GetRawText(), now.GetRawText()));
                return 1;
            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
            case System.Text.Json.JsonValueKind.String:
                if (now.ValueKind != then.ValueKind || then.GetRawText() != now.GetRawText()) diffs.Add((path, then.GetRawText(), now.GetRawText()));
                return 1;
            default:
                return 0;
        }
    }

    private (double Lat, double Lon) Origin()
    {
        var lat = BugDump.Get(Dump.Runtime, "Field", "OriginLat")?.GetDouble();
        var lon = BugDump.Get(Dump.Runtime, "Field", "OriginLon")?.GetDouble();
        if (lat is { } a && lon is { } b) return (a, b);
        var txt = File.Exists(Path.Combine(Dump.FieldDir, "field.origin")) ? File.ReadAllText(Path.Combine(Dump.FieldDir, "field.origin")).TrimStart('﻿').Split(',') : Array.Empty<string>();
        if (txt.Length == 2 && double.TryParse(txt[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
            && double.TryParse(txt[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) return (la, lo);
        return (0, 0);
    }

    /// <summary>The first GPS row recorded with this field open: rows from before the reporter
    /// opened it are in another local plane.</summary>
    private int FirstRowInField(string name)
    {
        double? opened = null;
        foreach (var l in Dump.Log)
            if (l.Text.Contains("OpenFieldAsync: " + name, StringComparison.Ordinal) && Dump.LogT(l) is { } t && t >= 0) opened = t;
        if (opened is not { } o) return 0;
        int i = Dump.Rows.FindIndex(r => r.T > o + 1.0);
        return i < 0 ? 0 : i;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---------------------------------------------------------------- the GPS log

    /// <summary>
    /// The fix the parser handed the pipeline for a row: the antenna's position (the row's pivot
    /// moved back out by the reporter's antenna pivot, offset and roll lean, the inverse of
    /// AntennaToPivotTransform with the heading and roll the pipeline used), the heading field of
    /// the sentence (PAOGI's dual heading, PANDA's IMU heading, the simulator's own), the roll, the
    /// fix quality and correction age as the receiver said.
    /// </summary>
    public GpsData GpsFor(GpsRow r)
    {
        var store = Rig.Store;
        double h = r.Heading * Math.PI / 180.0;
        double fe = Math.Sin(h), fn = Math.Cos(h), re = Math.Cos(h), rn = -Math.Sin(h);
        double side = Math.Abs(store.Vehicle.AntennaOffset) > 0.001 ? store.Vehicle.AntennaOffset : 0;
        if (Math.Abs(r.Roll) > 0.01 && Math.Abs(store.Vehicle.AntennaHeight) > 0.01)
            side += store.Vehicle.AntennaHeight * Math.Sin(r.Roll * Math.PI / 180.0);
        double pivot = Math.Abs(store.Vehicle.AntennaPivot) > 0.001 ? store.Vehicle.AntennaPivot : 0;
        double ae = r.E - DriftE + fe * pivot + re * side;
        double an = r.N - DriftN + fn * pivot + rn * side;
        var w = Plane.ConvertGeoCoordToWgs84(new GeoCoord(an, ae));
        var type = r.Sentence switch
        {
            "PANDA" => GpsSentenceType.Panda,
            "PAOGI" => GpsSentenceType.Paogi,
            "KSXT" => GpsSentenceType.Ksxt,
            "EPOCH" => GpsSentenceType.NmeaEpoch,
            "INSPVAX" => GpsSentenceType.Inspvax,
            "SIM" => GpsSentenceType.Simulator,
            _ => GpsSentenceType.Panda,
        };
        bool sim = type == GpsSentenceType.Simulator;
        // A dual heading: $PAOGI with its heading field, the simulator (true heading), a receiver's
        // own heading sentence in an NMEA epoch (no IMU talker), $KSXT and INSPVAX carry one too.
        bool dual = sim
            || (type == GpsSentenceType.Paogi && !r.DualMissing)
            || (type is GpsSentenceType.NmeaEpoch or GpsSentenceType.Ksxt or GpsSentenceType.Inspvax && !r.ImuValid && store.Connections.IsDualGps && !r.DualMissing);
        if (!r.HasSentenceColumns) dual = store.Connections.IsDualGps;
        return new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                Altitude = 100,
                Heading = r.HasSentenceColumns ? r.SentenceHeading : r.Heading,
                Speed = r.Speed,
            },
            FixQuality = r.Fix,
            SatellitesInUse = 20,
            Hdop = 0.7,
            DifferentialAge = r.DiffAge,
            ImuRoll = r.Roll,
            ImuHeading = r.ImuValid || sim ? (r.HasSentenceColumns ? r.SentenceHeading : r.Heading) : 0,
            ImuValid = r.ImuValid || sim,
            HasDualHeading = dual,
            SentenceType = type,
            Timestamp = DateTime.UtcNow,
        };
    }

    /// <summary>Every row from the start row to the end, at the log's pace; the operator mirrors
    /// the reporter's AutoSteer button.</summary>
    public async Task ReplayAsync()
    {
        var rows = Dump.Rows;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double wall = 0;
        bool sectionsDone = false;
        int engages = 0, disengages = 0, resyncs = 0, manualTurns = 0;
        // The manual U-turn buttons the reporter pressed (their log has each one).
        var manual = Dump.Log
            .Where(l => l.Text.Contains("[ManualYouTurn] Triggering", StringComparison.Ordinal))
            .Select(l => (T: Dump.LogT(l), Cmd: l.Text.Contains("LEFT", StringComparison.Ordinal) ? "youturn.manualLeft" : "youturn.manualRight"))
            .Where(x => x.T is { } t && t >= rows[StartRow].T)
            .Select(x => (T: x.T!.Value, x.Cmd)).OrderBy(x => x.T).ToList();
        int mi = 0, snapCheck = 0, lateRows = 0;
        double maxLate = 0;
        double passWidth = await Rig.ReadAsync(() => Rig.Store.ActualToolWidth - Rig.Store.Tool.Overlap);
        for (int i = StartRow; i < rows.Count; i++)
        {
            var r = rows[i];
            double dt = i > StartRow ? Math.Max(0, r.T - rows[i - 1].T) : 0.1;
            if (i > StartRow)
            {
                wall += dt > 1.0 ? dt : dt / Pace;
                double wait = wall * 1000 - sw.ElapsedMilliseconds;
                if (wait > 1) await Task.Delay((int)wait);
                // How late this fix goes in against the log's pace (the watchdogs run on the
                // wall clock: a fix late enough can look like GPS lost to this build).
                double late = sw.ElapsedMilliseconds - wall * 1000;
                maxLate = Math.Max(maxLate, late);
                if (late > Scenario.LateMs) { lateRows++; S.Late.Add(("replay", r.T, late)); }
            }
            // The operator's AutoSteer button, where the reporter's log shows it.
            // (At the first row: AutoSteer already on in the reporter's log is engaged at once.)
            bool prevOn = i > StartRow && rows[i - 1].AutoSteer;
            if (r.AutoSteer != prevOn)
            {
                bool on = await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged);
                if (r.AutoSteer && !on)
                {
                    await Rig.SendAsync("autosteer.toggle");
                    engages++;
                    bool now = await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged);
                    S.Step(F($"t {r.T,6:0.0} s: operator engages AutoSteer (as the reporter did){(now ? "" : ": it did NOT engage: " + string.Join(" | ", Collect().Take(2)))}"));
                    if (SectionsAuto && !sectionsDone && now)
                    {
                        sectionsDone = true;
                        if (!await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn)) await Rig.SendAsync("section.master");
                        S.Step(F($"t {r.T,6:0.0} s: sections to Auto (a job with coverage was being worked)"));
                    }
                }
                else if (!r.AutoSteer && on)
                {
                    // The reporter's AutoSteer went off here: by the operator, or by the app (its
                    // log says why). This build has not dropped it by itself, so the operator does.
                    await Rig.SendAsync("autosteer.toggle");
                    disengages++;
                    S.Step(F($"t {r.T,6:0.0} s: operator disengages AutoSteer (the reporter's went off here{ReporterOffReason(r.T)})"));
                }
            }
            // The manual U-turn button, when the reporter pressed it.
            while (mi < manual.Count && manual[mi].T <= r.T)
            {
                await Rig.SendAsync(manual[mi].Cmd);
                manualTurns++;
                S.Step(F($"t {r.T,6:0.0} s: operator presses the manual U-turn button ({(manual[mi].Cmd.EndsWith("Left", StringComparison.Ordinal) ? "left" : "right")}), as the reporter's log shows"));
                mi++;
            }
            // The reporter's build moved to another pass with AutoSteer on and no U-turn: a lateral
            // move (one pass over, the line then 1 pass away) or a re-pick of the nearest line (the
            // steer switch), which the dump does not record. AutoSteer holds its pass here (as
            // AgOpenGPS while engaged), so the operator does the same: the lateral button, or
            // AutoSteer off and on.
            if (i > StartRow && r.AutoSteer && rows[i - 1].AutoSteer && r.PathsAway != rows[i - 1].PathsAway
                && !r.YtExecuting && !rows[i - 1].YtExecuting && !r.YtTriggered
                && await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged))
            {
                bool lateral = Math.Abs(r.Xte) > passWidth / 2;
                if (lateral) { await Rig.SendAsync("track.snapRight"); snapCheck = 1; }
                else { await Rig.SendAsync("autosteer.toggle"); await Rig.SendAsync("autosteer.toggle"); }
                if (resyncs++ < 5)
                    S.Step(F($"t {r.T,6:0.0} s: the reporter's build moved from pass {rows[i - 1].PathsAway} to {r.PathsAway} with AutoSteer on and no U-turn, ") + (lateral
                        ? F($"to a line {Math.Abs(r.Xte):0.0} m away (a lateral move the dump does not record): operator presses the lateral button")
                        : "to the nearest line (a steer switch the dump does not record): operator re-picks the line, AutoSteer off and on"));
            }
            Clock = r.T;
            Rig.Gps.MarkRealGpsParsed();
            Rig.Feed(GpsFor(r));
            int ticks = (int)Math.Clamp(Math.Round(dt * 100), 1, 300);
            Rig.TickLoop(ticks);
            var c = Rig.LastCycle;
            await AlignPassStepAsync(r);
            // The lateral button moves one pass right or left of the direction of travel: the
            // side that puts this build on the reporter's line is the one (right first).
            if (snapCheck > 0 && c?.Guidance is { HasGuidance: true } sg)
            {
                bool same = Math.Abs(Math.Abs(sg.CrossTrackError) - Math.Abs(r.Xte)) < 1.5;
                if (same) snapCheck = 0;
                else if (snapCheck == 1) { await Rig.SendAsync("track.snapLeft"); await Rig.SendAsync("track.snapLeft"); snapCheck = 2; }
                else { snapCheck = 0; S.Note(F($"t {r.T:0.0} s: the lateral button did not put this build on the reporter's line")); }
            }
            if (c != null)
            {
                Now[i] = new ReplayRow
                {
                    E = c.Easting, N = c.Northing, Heading = c.Heading, Speed = c.Speed,
                    ToolE = c.ToolEasting, ToolN = c.ToolNorthing, ToolH = c.ToolHeadingRadians,
                    Steer = c.Guidance?.SteerAngle ?? 0, Xte = c.Guidance?.CrossTrackError ?? 0,
                    HasGuidance = c.Guidance?.HasGuidance ?? false, GuidanceFresh = c.Guidance != null,
                    PathsAway = c.Guidance?.HowManyPathsAway ?? 0,
                    Engaged = c.IsAutoSteerEngaged,
                    YtTriggered = c.YouTurn?.IsTriggered ?? false, YtExecuting = c.YouTurn?.IsExecuting ?? false,
                    Reverse = c.IsReverse, Disengage = c.DisengageReason,
                };
            }
            if ((i - StartRow) % 10 == 0)
            {
                await Rig.WaitForVmAsync();
                foreach (var e in Collect()) Said.Add(F($"t {r.T,6:0.0} s: {e}"));
            }
        }
        await Rig.WaitForVmAsync();
        foreach (var e in Collect()) Said.Add(F($"t {rows[^1].T,6:0.0} s: {e}"));
        WriteCsv();
        S.Row.Metrics.Add(("GPS feed lateness (replay)", F($"max {maxLate:0} ms, {lateRows} rows more than {Scenario.LateMs:0} ms late")));
        S.Step(F($"replayed rows {StartRow}..{rows.Count - 1} ({rows[^1].T - rows[StartRow].T:0} s of log) in {sw.Elapsed.TotalSeconds:0} s; AutoSteer engaged {engages}x, disengaged {disengages}x by the operator, line re-picked {resyncs}x, manual U-turns {manualTurns}"));
    }

    /// <summary>
    /// Where the reporter's build put the passes of their track, against where this build puts
    /// them. The GPS log has the reporter's pivot, pass number and cross-track error on every row
    /// with guidance, so the centre line of each of their passes is known to the centimetre. On an
    /// AB line, if this build's passes lie half a working width from those and the reporter's build
    /// predates AgOpenGPS's pass model (commit 1cf8ba17, 2026-10-20: the reference line became the
    /// edge between two passes, so a track from before it moves by half a width), the operator
    /// moves the line back with Ref Nudge's half-width button, as an operator with an old track
    /// would. Half a width on a build that already had the pass model, or one that can't be
    /// placed, is an app defect and is left as it is; so is any other difference.
    /// </summary>
    private async Task AlignPassesAsync()
    {
        var (track, w, nudge) = await Rig.ReadAsync(() => (Rig.Vm.SelectedTrack, Rig.Store.ActualToolWidth - Rig.Store.Tool.Overlap, Rig.State.Guidance.NudgeOffset));
        if (track is not { Points.Count: 2 } || w < 0.5) return;
        var (ae, an) = (track.Points[0].Easting, track.Points[0].Northing);
        double h = Math.Atan2(track.Points[1].Easting - ae, track.Points[1].Northing - an);
        double cs = Math.Cos(h), sn = Math.Sin(h);
        var samples = new List<(double D, double X)>();
        foreach (var r in Dump.Rows)
            if (r.HasGuidance && !r.YtExecuting && !r.Reverse && r.Speed > 0.3 && Math.Abs(r.Xte) < w / 2)
                samples.Add(((r.E - ae) * cs - (r.N - an) * sn, r.Xte));
        if (samples.Count < 30) return;
        // The cross-track error's sign is the build's own: the one that puts the samples' line
        // centres on one grid is it.
        (double Centre, double R) Grid(int sign)
        {
            double sx = 0, sy = 0;
            foreach (var (d, x) in samples) { double a = 2 * Math.PI * (d + sign * x) / w; sx += Math.Cos(a); sy += Math.Sin(a); }
            sx /= samples.Count; sy /= samples.Count;
            return (Mod(w * Math.Atan2(sy, sx) / (2 * Math.PI), w), Math.Sqrt(sx * sx + sy * sy));
        }
        var plus = Grid(1); var minus = Grid(-1);
        var then = plus.R >= minus.R ? plus : minus;
        if (then.R < 0.9) { S.Note(F($"the reporter's pass centres do not lie on one grid ({samples.Count} rows): pass alignment not checked")); return; }
        double now = Mod(AgOpenWeb.Models.Track.PassGeometry.Offset(track, 0, w, nudge), w);
        double shift = Mod(then.Centre - now + w / 2, w) - w / 2;
        PassShift = shift;
        if (Math.Abs(shift) < 0.15) { S.Ok(F($"this build's passes lie where the reporter's did (within {Math.Abs(shift) * 100:0} cm, {samples.Count} rows)")); return; }
        var (old, how) = Math.Abs(Math.Abs(shift) - w / 2) < 0.15 ? PredatesPassModel() : ((bool?)null, "");
        if (Math.Abs(Math.Abs(shift) - w / 2) < 0.15 && old != true)
        {
            S.Problem(Severity.Bug, Kind.AppDefect, "TRACK-PASSES-HALF-OFF",
                F($"this build puts the passes of the reporter's track '{track.Name}' {Math.Abs(shift):0.00} m, half a working width, from where the reporter's build (") + (Dump.BuildHash.Length >= 7 ? Dump.BuildHash[..7] : "unknown") +
                ") drove them, and that build " + (old == false ? "already had" : "can't be placed before or after") + " the pass model change (1cf8ba17, 2026-10-20; " + how +
                "): the same track should give the same passes. Not moved back: the replay compares guidance on lines half a width apart");
            return;
        }
        if (Math.Abs(Math.Abs(shift) - w / 2) < 0.15)
        {
            S.Problem(Severity.Inconsistency, Kind.Parity, "TRACK-PASSES-MOVED-HALF",
                F($"this build puts the passes of the reporter's track '{track.Name}' {Math.Abs(shift):0.00} m, half a working width, from where the reporter's build (") + (Dump.BuildHash.Length >= 7 ? Dump.BuildHash[..7] : "older") +
                ") drove them: that build predates the pass model change (1cf8ba17, 2026-10-20; " + how + "), where the reference line became the edge between two passes (AgOpenGPS's model, PassGeometry), so a track made before it moves by half a width. AgOpenWeb keeps no compatibility for files of older builds (CLAUDE.md), so this is expected, but an operator with such a track is guided half a width off until the line is moved; for the replay the operator moves it back with Ref Nudge > half width");
            await Rig.SendAsync("track.refNudgeBegin");
            await Rig.SendAsync("track.refHalfNudgeRight");
            await Rig.SendAsync("track.refNudgeSave");
            var moved = await Rig.ReadAsync(() => Rig.Vm.SelectedTrack is { Points.Count: 2 } t2 ? (t2.Points[0].Easting - ae) * cs - (t2.Points[0].Northing - an) * sn : double.NaN);
            double left = Mod(then.Centre - (now + moved) + w / 2, w) - w / 2;
            S.Step(F($"Ref Nudge > half width right: the line moved {moved:0.00} m; the passes are now {Math.Abs(left) * 100:0} cm from the reporter's"));
            PassShift = left;
            return;
        }
        S.Problem(Severity.Inconsistency, Kind.AppDefect, "TRACK-PASSES-MOVED",
            F($"this build puts the passes of the reporter's track '{track.Name}' {shift:0.00} m from where the reporter's build drove them (not half a width; a nudge the dump does not carry, or a change in the pass model): the replay compares guidance on lines that differ by that much"));
    }

    /// <summary>The commit that made the reference line the edge between two passes (AgOpenGPS's
    /// pass model, 2026-10-20). A track made on a build before it has its passes half a width off.</summary>
    public const string PassModelCommit = "1cf8ba17";
    private static readonly DateTime PassModelDate = new(2026, 10, 20);

    /// <summary>
    /// Whether the reporter's build predates <see cref="PassModelCommit"/>: true, false, or null
    /// when it can't be told, and how it was told. The build hash (system_info.txt) is looked up in
    /// this checkout's history (<c>git merge-base --is-ancestor</c>; the workflow checks out the
    /// whole history); a hash git doesn't know falls back to the dump's date (a dump made before
    /// that day is from an older build; one made after can be from either).
    /// </summary>
    public (bool? Old, string How) PredatesPassModel()
    {
        if (Dump.BuildHash.Length >= 7 && RepoRoot() is { } root)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root,
                };
                foreach (var a in new[] { "merge-base", "--is-ancestor", PassModelCommit, Dump.BuildHash }) psi.ArgumentList.Add(a);
                using var git = System.Diagnostics.Process.Start(psi);
                if (git != null && git.WaitForExit(10000))
                {
                    if (git.ExitCode == 0) return (false, "git: build " + Dump.BuildHash[..7] + " has it");
                    if (git.ExitCode == 1) return (true, "git: build " + Dump.BuildHash[..7] + " is older");
                }
            }
            catch (System.ComponentModel.Win32Exception) { /* no git: the date below */ }
        }
        if (Dump.DumpLocalTime is { } t && t < PassModelDate) return (true, F($"the dump is from {t:yyyy-MM-dd}"));
        return (null, Dump.BuildHash.Length >= 7 ? "build " + Dump.BuildHash[..7] + " is not in this checkout's history" : "the dump names no build");
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    private int _alignDir, _alignPresses, _alignNet;
    private double _alignLast;
    /// <summary>Whether this build has been put on the reporter's pass (or given up on it).</summary>
    public bool AlignDone { get; private set; }

    /// <summary>
    /// On the first rows where the reporter's build had a line: this build must guide on the same
    /// pass. A freshly selected track starts this build on the pass nearest the tractor, or on
    /// pass 0 for a boundary-follow track, while the reporter may have got to theirs by U-turns
    /// (#591: pass -9 of a boundary edge). The operator presses the lateral button
    /// (<c>track.snapRight/Left</c>) one pass at a time, the way the distance from the line comes
    /// down, until this build's distance from its line is the reporter's. Called once per fix.
    /// </summary>
    public async Task AlignPassStepAsync(GpsRow r)
    {
        if (AlignDone) return;
        var c = Rig.LastCycle;
        if (!r.HasGuidance || r.YtExecuting || c?.Guidance is not { } g || c.YouTurn?.IsExecuting == true) return;
        double err = Math.Abs(Math.Abs(g.CrossTrackError) - Math.Abs(r.Xte));
        if (err < 1.0)
        {
            AlignDone = true;
            // One pass over is how a fresh track selection can land (the nearest pass, by a
            // hair); more needs the dump to say how the reporter got there.
            if (Math.Abs(_alignNet) > 1 && WhyOtherPass(r.T) == null)
                S.Problem(Severity.Inconsistency, Kind.AppDefect, "PASS-ALIGN-PRESSES",
                    F($"t {r.T:0.0} s: this build started {Math.Abs(_alignNet)} passes from the reporter's pass {r.PathsAway}, and the dump shows nothing that took the reporter there (no U-turn, manual turn or pass change in their log or GPS log before it); the lateral button was pressed {_alignPresses}x to compare on the same line"),
                    "replay", r.T);
            else if (_alignPresses > 0)
                S.Step(F($"t {r.T,6:0.0} s: operator presses the lateral button {_alignPresses}x to get onto the reporter's pass {r.PathsAway} (this build had started on another") + (Math.Abs(_alignNet) > 1 ? "; " + WhyOtherPass(r.T) : "") + ")");
            return;
        }
        if (_alignPresses >= 40)
        {
            AlignDone = true;
            S.Note(F($"t {r.T:0.0} s: the lateral button did not bring this build onto the reporter's pass {r.PathsAway} ({err:0.0} m apart after {_alignPresses} presses)"));
            return;
        }
        if (_alignDir == 0) _alignDir = 1;
        else if (err > _alignLast + 0.5)
        {
            if (_alignDir == 1 && _alignPresses == 1)
            {
                // Right was the wrong way: back, and left from there.
                _alignDir = -1;
                await Rig.SendAsync("track.snapLeft");
                _alignPresses++; _alignNet--;
            }
            else
            {
                await Rig.SendAsync(_alignDir > 0 ? "track.snapLeft" : "track.snapRight");
                _alignNet -= _alignDir;
                AlignDone = true;
                S.Note(F($"t {r.T:0.0} s: the lateral button did not bring this build onto the reporter's pass {r.PathsAway}"));
                return;
            }
        }
        await Rig.SendAsync(_alignDir > 0 ? "track.snapRight" : "track.snapLeft");
        _alignPresses++; _alignNet += _alignDir;
        _alignLast = err;
    }

    /// <summary>What in the dump explains the reporter being on another pass than a fresh
    /// selection gives, up to <paramref name="t"/>: a U-turn or a manual turn in their log, a U-turn
    /// or a pass change in the GPS log. Null when nothing does.</summary>
    private string? WhyOtherPass(double t)
    {
        foreach (var l in Dump.Log)
            if (Dump.LogT(l) is { } lt && lt <= t
                && (l.Text.Contains("Turn complete", StringComparison.Ordinal) || l.Text.Contains("[ManualYouTurn]", StringComparison.Ordinal)
                    || l.Text.Contains("Triggered on the path", StringComparison.Ordinal)))
                return "the reporter's log has a U-turn before it";
        var rows = Dump.Rows;
        for (int i = 0; i < rows.Count && rows[i].T <= t; i++)
        {
            if (rows[i].YtExecuting) return "the GPS log has a U-turn before it";
            if (i > 0 && rows[i].HasGuidance && rows[i - 1].HasGuidance && rows[i].PathsAway != rows[i - 1].PathsAway) return "the GPS log has a pass change before it";
        }
        return null;
    }

    /// <summary>The reporter's build's pass centres minus this build's, after the alignment (m).</summary>
    public double? PassShift { get; private set; }

    private static double Mod(double x, double m) => ((x % m) + m) % m;

    /// <summary>With <c>AGOPENWEB_REPLAY_CSV</c> set to a folder: every replayed row, then and now,
    /// side by side (<c>&lt;id&gt;.csv</c>), to look into a finding.</summary>
    private void WriteCsv()
    {
        var dir = Environment.GetEnvironmentVariable("AGOPENWEB_REPLAY_CSV");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder("t,then_e,then_n,now_e,now_n,then_hdg,now_hdg,then_pass,now_pass,then_xte,now_xte,then_steer,now_steer,then_guid,now_guid,then_as,now_as,then_yt_trig,now_yt_trig,then_yt_exec,now_yt_exec,speed\n");
        var rows = Dump.Rows;
        for (int i = StartRow; i < rows.Count; i++)
        {
            var r = rows[i];
            if (Now[i] is not { } n) continue;
            sb.Append(F($"{r.T:0.0},{r.E:0.000},{r.N:0.000},{n.E:0.000},{n.N:0.000},{r.Heading:0.00},{n.Heading:0.00},{r.PathsAway},{n.PathsAway},{r.Xte:0.000},{n.Xte:0.000},{r.Steer:0.00},{n.Steer:0.00},{(r.HasGuidance ? 1 : 0)},{(n.HasGuidance ? 1 : 0)},{(r.AutoSteer ? 1 : 0)},{(n.Engaged ? 1 : 0)},{(r.YtTriggered ? 1 : 0)},{(n.YtTriggered ? 1 : 0)},{(r.YtExecuting ? 1 : 0)},{(n.YtExecuting ? 1 : 0)},{r.Speed:0.00}\n"));
        }
        File.WriteAllText(Path.Combine(dir, Entry.Id + ".csv"), sb.ToString());
    }

    private string ReporterOffReason(double t)
    {
        foreach (var l in Dump.Log)
        {
            if (!l.Text.Contains("[AutoSteer] Off", StringComparison.Ordinal) && !l.Text.Contains("AutoSteer off", StringComparison.OrdinalIgnoreCase)) continue;
            if (Dump.LogT(l) is { } lt && Math.Abs(lt - t) < 1.5) return ": " + l.Text;
        }
        return "";
    }

    /// <summary>What the rig saw since the last call, as text: exceptions, errors and warnings,
    /// failures, status messages.</summary>
    public List<string> Collect()
    {
        var list = new List<string>();
        foreach (var e in Rig.TakeEvents())
        {
            switch (e.Kind)
            {
                case RigEventKind.Exception: Exceptions.Add(e.Text); list.Add("EXCEPTION " + FirstLine(e.Text)); break;
                case RigEventKind.Error: Errors.Add(e.Text); list.Add("error: " + e.Text); break;
                case RigEventKind.OffLoop: OffLoop.Add(e.Text); break;
                case RigEventKind.Hang: Hung = true; list.Add("HANG " + e.Text); break;
                case RigEventKind.Warning:
                    // NTRIP and network noise of a rig with no caster: not the replay's business.
                    if (e.Text.Contains("Ntrip", StringComparison.OrdinalIgnoreCase)) break;
                    list.Add("warning: " + e.Text); break;
                default: list.Add(e.Kind.ToString().ToLowerInvariant() + ": " + e.Text); break;
            }
        }
        return list;
    }

    private static string FirstLine(string s) { int i = s.IndexOf('\n'); return i < 0 ? s : s[..i].TrimEnd('\r'); }

    // ---------------------------------------------------------------- the field as loaded

    /// <summary>The field scenarios' rules for a field, on the reporter's field as this build loaded it.</summary>
    private void FieldChecks()
    {
        if (Outer.Count < 3) { S.Note("no outer boundary in the field"); return; }
        if (SelfCrossing(Outer) is { } x)
            S.Problem(Severity.Bug, Kind.AppDefect, "FIELD-BOUNDARY-SELF-CROSSING", F($"the outer boundary crosses itself near ({x.E:0.0}, {x.N:0.0})"));
        if (Work != null)
        {
            double worst = Work.Where(q => !Ring.Inside(q, Outer)).Select(q => Ring.DistToRing(q, Outer)).DefaultIfEmpty(0).Max();
            if (worst > 0.5) S.Problem(Severity.Bug, Kind.AppDefect, "FIELD-HEADLAND-OUTSIDE", F($"the headland line goes {worst:0.0} m outside the outer boundary"));
            else S.Ok("the headland line lies inside the outer boundary");
        }
        for (int k = 0; k < Inner.Count; k++)
        {
            double worst = Inner[k].Where(q => !Ring.Inside(q, Outer)).Select(q => Ring.DistToRing(q, Outer)).DefaultIfEmpty(0).Max();
            if (worst > 0.5) S.Problem(Severity.Inconsistency, Kind.AppDefect, "FIELD-OBSTACLE-OUTSIDE", F($"obstacle {k + 1} reaches {worst:0.0} m outside the outer boundary"));
        }
    }

    private static (double E, double N)? SelfCrossing(IReadOnlyList<(double E, double N)> p)
    {
        int n = p.Count;
        for (int i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                var c = p[j]; var d = p[(j + 1) % n];
                double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
                if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return a;
            }
        }
        return null;
    }

    private static double Cross((double E, double N) o, (double E, double N) a, (double E, double N) b)
        => (a.E - o.E) * (b.N - o.N) - (a.N - o.N) * (b.E - o.E);

    public async ValueTask DisposeAsync()
    {
        if (Rig != null) await Rig.DisposeAsync();
        Steer?.Dispose();
    }
}
