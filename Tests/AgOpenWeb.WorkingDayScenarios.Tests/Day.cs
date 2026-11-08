// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>One field of the day: which field, which AB line, a new field from KML or one
/// worked earlier in the day opened again for a new job.</summary>
public sealed record DayJob(string Code, string Field, string Track, string Title)
{
    public bool Obstacles { get; init; }
    public bool Reopen { get; init; }
    public double HeadlandM { get; init; } = 16;
}

/// <summary>Something that happened in the day, on the day's clock.</summary>
public sealed record DayEvent(string Code, DateTime Clock, double WallSeconds, string Text);

/// <summary>One look at the process, every five minutes of the day (and at each baseline).</summary>
public sealed record Sample(
    DateTime Clock, double WallSeconds, string Phase, ProcessState P,
    double CycleP50, double CycleP95, double CycleP99, double CycleMax, long Cycles,
    double TickP99, double TickMax,
    double HostLoopMaxMs,
    double TabletTicksPerSec, double TabletMaxGapMs, double TabletTickBytes,
    double DataMB, int DataFiles, bool NtripOpen, ulong NtripBytes, int NtripEvents,
    int Errors, int Warnings);

/// <summary>The process in one state at different hours: no field open, the tractor parked,
/// the browsers connected. A leak shows as a baseline that keeps rising.</summary>
public sealed record Baseline(string Code, DateTime Clock, string What, ProcessState P, double DataMB);

/// <summary>What one field's job measured.</summary>
public sealed class JobResult
{
    public DayJob Job = null!;
    public DateTime Start, End;
    public double DrivenMinutes, ParkedMinutes;
    public int Turns, ManualTurns, Reengaged;
    public bool Done, CutShort;
    public double OpenSeconds;
    public Timings Cycle = new(), Tick = new();
    public double AreaHa;
    public string Note = "";
}

/// <summary>
/// A whole working day of AgOpenWeb in one process, on the day's clock (10 fixes a second,
/// paced against the wall clock by <c>AGOPENWEB_DAY_PACE</c>): the tractor sprays one field
/// after another with AutoSteer, automatic U-turns and automatic sections, refills, has lunch,
/// drives on the road between fields; NTRIP corrections come all day; the operator's tablet and
/// a helper's phone watch through the web client and go away the ways tablets and phones do.
/// Every five minutes the harness looks at the process (memory, threads, timers, open files,
/// how long a GPS cycle and a control tick take, how fast the tablet gets its frames).
/// </summary>
public sealed partial class Day : IAsyncDisposable
{
    public const double FixSeconds = 0.1;
    /// <summary>Real ms per simulated fix (0.1 s): 8 = 12.5x, 100 = real time.</summary>
    public static readonly int PaceMs = int.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_DAY_PACE"), out var p) && p > 0 ? p : 8;
    /// <summary>How long the day is, hours (07:00 to 15:00 by default).</summary>
    public static readonly double Hours = double.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_DAY_HOURS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 8;
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_DAY_VERBOSE") != "0";
    /// <summary>The day's clock at the first fix: a summer day.</summary>
    public static readonly DateTime DayStart = new(2026, 6, 16, 7, 0, 0, DateTimeKind.Utc);
    public DateTime DayEnd => DayStart.AddHours(Hours);

    public static readonly VehicleSpec Tractor = Machines.Puma;
    public static readonly ToolSpec Sprayer = Machines.Sprayer8;
    public static readonly SystemSpec Sys = Machines.PumaSys;
    public const double WorkKmh = 9, RoadKmh = 25;

    public readonly List<DayEvent> Events = new();
    public readonly List<Sample> Samples = new();
    public readonly List<Baseline> Baselines = new();
    public readonly List<JobResult> Jobs = new();
    public readonly List<HeapCensus> Censuses = new();
    public readonly Dictionary<string, (int Count, DateTime First, string Text)> Said = new(StringComparer.Ordinal);
    public readonly List<string> HarnessProblems = new();
    public readonly Timings CycleDay = new(), TickDay = new();
    public readonly Dictionary<string, Dictionary<string, (long Bytes, int Files)>> Disk = new();
    public readonly Dictionary<string, Dictionary<string, long>> FileSizes = new();
    public readonly List<string> Measures = new();   // one-off measurements for the report

    public string DataRoot { get; }
    public DayRig Rig { get; private set; } = null!;
    public EmulatedSprayerModule Module { get; private set; } = null!;
    public SteerModuleStub Steer { get; private set; } = null!;
    public FakeCaster Caster { get; private set; } = null!;
    public WebClient Tablet { get; private set; } = null!;
    public WebClient Phone { get; private set; } = null!;
    public TruthMachine Truth { get; private set; } = null!;
    public INtripClientService Ntrip => Rig.Services.GetRequiredService<INtripClientService>();
    public ICoverageMapService Coverage => Rig.Services.GetRequiredService<ICoverageMapService>();

    // The day's clock.
    public long Fixes;
    public DateTime Now => DayStart.AddSeconds(Fixes * FixSeconds);
    public long SimMs => Fixes * 100;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    public double WallSeconds => _wall.Elapsed.TotalSeconds;
    private long _paceFixes; private double _paceWallMs;
    /// <summary>The longest the harness fell behind its pace (the process could not keep up), ms.</summary>
    public double MaxBehindMs;

    // The tractor.
    private double _steerCmd;
    public bool Parked = true;
    public string Phase = "start";
    private double _drivenSinceRefillS;
    private int _refills;

    // Windows between samples.
    private Timings _cycleWin = new(), _tickWin = new();
    private double _hostLoopMax;
    private readonly object _hostLock = new();
    private int _errors, _warnings;
    private long _nextSampleFix;
    public const int SampleEveryFixes = 3000;   // five minutes of the day

    public Day(string dataRoot) { DataRoot = dataRoot; }

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public void Event(string code, string text)
    {
        Events.Add(new DayEvent(code, Now, WallSeconds, text));
        if (Verbose) TestContext.Progress.WriteLine(F($"[day] {Now:HH:mm:ss} (wall {WallSeconds,6:0} s) {code}: {text}"));
    }

    public void Harness(string text)
    {
        HarnessProblems.Add(F($"{Now:HH:mm}: {text}"));
        Event("HARNESS", text);
    }

    public Task SendAsync(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    // ------------------------------------------------------------------ the whole day

    public async Task RunAsync()
    {
        var before = BeforeStart = ProcessState.Take();
        Measures.Add(F($"Before AgOpenWeb started: managed heap {before.HeapMB:0.0} MB, resident {before.RssMB:0} MB, {before.Threads} threads"));
        if (!await MorningAsync()) return;
        await BaselineAsync("B0", "after start-up and set-up, no field open yet");

        var jobs = new List<DayJob>
        {
            new("J1", "Olival", "AB 159 deg", "Olival, 3.75 ha, outer boundary only"),
            new("J2", "Quinta Torta", "AB 0°", "Quinta Torta, 15.2 ha"),
            new("J3", "Olival", "AB 159 deg", "Olival again: the field opened from the list for a second job") { Reopen = true },
            new("J4", "Tapada da Casa", "1 Principal AB 162 deg", "Tapada da Casa, 14.6 ha with its 82 obstacles") { Obstacles = true },
            new("J5", "Baetas", "1 Principal AB 8 deg", "Baetas, 6.3 ha"),
            new("J6", "Couto Ribeira", "1 Principal AB 119 deg", "Couto Ribeira, 12.9 ha"),
        };
        bool first = true;
        foreach (var job in jobs)
        {
            if (Now >= DayEnd) break;
            if (!first) await RoadAsync(job);
            first = false;
            var r = await JobAsync(job);
            if (r == null) break;
            await BaselineAsync("B-" + job.Code, "after " + job.Code + " (" + job.Field + ") was closed");
            if (_dumps.Count == 0 && HeapCensus.Enabled) await CensusAsync("after-" + job.Code);
            if (HarnessProblems.Count > 0) break;
        }
        await EveningAsync();
    }

    // ------------------------------------------------------------------ morning: start and set-up

    private async Task<bool> MorningAsync()
    {
        Directory.CreateDirectory(DataRoot);
        Module = new EmulatedSprayerModule(relays: 8, directSections: false) { Clock = () => SimMs };
        Module.Start();
        Module.PowerOn();
        Steer = new SteerModuleStub { On = true };
        Caster = new FakeCaster("caster");
        Truth = new TruthMachine(Tractor, Sprayer, 0, Sys) { SpeedKmh = 0 };

        var sw = Stopwatch.StartNew();
        Rig = await DayRig.StartAsync(Path.Combine(DataRoot, "app"));
        double bootS = sw.Elapsed.TotalSeconds;
        Event("BOOT", F($"AgOpenWeb started on a fresh data folder in {bootS:0.00} s"));
        Measures.Add(F($"First start (fresh data folder): {bootS:0.00} s"));
        BootSeconds = bootS;

        // The receiver sends from the yard before anything is set.
        var yard = ScenarioFields.Get("Olival").InsidePoint();
        _yard = Geo.Offset(yard, -400, -300);
        for (int i = 0; i < 30; i++) { SendFixAt(_yard.Lat, _yard.Lon, 0); await Task.Delay(10); }

        Tablet = new WebClient("tablet", Rig.Server.Port, isOperator: true);
        await Tablet.ConnectAsync();
        await WaitAsync(() => Tablet.Ticked > 0, 10);
        Phone = new WebClient("phone", Rig.Server.Port, isOperator: false);
        await Phone.ConnectAsync();
        await WaitAsync(() => Phone.Ticked > 0, 10);
        Event("BROWSERS", F($"the operator's tablet connects (operator), then the helper's phone (observer); first map update after {Tablet.Seeds[0].FirstTickMs:0} and {Phone.Seeds[0].FirstTickMs:0} ms"));
        if (Tablet.Ticked == 0) { Harness("the tablet got no Tick frame within 10 s of connecting"); return false; }

        var settings = Tractor.Settings(Sys).Concat(Sprayer.Settings()).ToList();
        for (int s = 0; s < Sprayer.Sections; s++) settings.Add(F($"machine.pin:{s},{s + 1}"));
        settings.Add("tool.isSectionOffWhenOut:1");
        settings.Add("tool.isHeadlandSectionControl:1");
        settings.Add("uturn.radius:8");
        settings.Add("uturn.distanceFromBoundary:2");
        foreach (var kv in settings) await SendAsync("config.set", kv);
        await SendAsync("profile.save");
        Event("SETUP", F($"Vehicle & Tool: {Tractor.Name}, {Sprayer.Name}, {Sys.Label}; relay functions one per section; U-turn radius 8 m"));

        string arg = string.Join("\t", "", "Farm RTK", "127.0.0.1", Caster.Port.ToString(CultureInfo.InvariantCulture), FakeCaster.Mount,
            FakeCaster.User, FakeCaster.Password, "1", "1", "");
        await SendAsync("ntrip.save", arg);
        bool ntrip = await WaitAsync(() => Ntrip.TotalBytesReceived > 0, 15, keepFixing: true);
        Event("NTRIP", ntrip ? "the NTRIP profile saved; corrections flowing from the caster" : "the NTRIP profile saved but no corrections arrived within 15 s");
        if (!ntrip) Harness("NTRIP corrections did not start");
        ResetPace();
        return true;
    }

    private (double Lat, double Lon) _yard;
    public double BootSeconds, NextBootSeconds = double.NaN;

    // ------------------------------------------------------------------ evening

    private async Task EveningAsync()
    {
        Event("END", F($"end of the day at {Now:HH:mm}"));
        await PhoneOffAsync("the helper goes home: the phone's page is closed");
        await BaselineAsync("B-END", "end of the day: no field open, the tablet connected");
        OpenFilesAtEnd = ProcessState.OpenFiles();
        if (HeapCensus.Enabled && _dumps.Count > 0) await CensusAsync("end");
        Disk["evening"] = DiskUse.Take(Rig.DataRoot);
        FileSizes["evening"] = DiskUse.Files(Rig.DataRoot);

        // The tractor is switched off: AgOpenWeb's exit save, then it is gone.
        await Tablet.CloseAsync();
        var sw = Stopwatch.StartNew();
        _firstVm = new WeakReference(Rig.Vm);
        await Rig.DisposeAsync();
        // Off the shut-down's own call stack (its last frames still refer to the rig when it
        // completes), so only what AgOpenWeb itself keeps is measured below.
        await Task.Yield();
        Event("EXIT", F($"AgOpenWeb shut down (exit save) in {sw.Elapsed.TotalSeconds:0.00} s"));
        Measures.Add(F($"Shut-down with the exit save: {sw.Elapsed.TotalSeconds:0.00} s"));
        Rig = null!;
        AfterExit = ProcessState.Take();
        // What still lives once the backend is gone: dumped now, counted after the day.
        if (HeapCensus.Enabled && _dumps.Count > 0)
        {
            var (path, error, seconds) = HeapCensus.Dump("after-exit");
            _dumps.Add(("after-exit", path, error));
            Event("CENSUS", error == null ? F($"heap dump 'after-exit' in {seconds:0.0} s (counted after the day)") : "heap dump failed: " + error);
        }
        Measures.Add(F($"After AgOpenWeb shut down in this process (as on Android and iOS, where the process outlives the backend): managed heap {AfterExit.HeapMB:0.0} MB, {AfterExit.Threads} threads, {AfterExit.Timers} timers, {AfterExit.Sockets} sockets"));

        // The next morning: AgOpenWeb starts on what the day left, and the last job is resumed.
        sw.Restart();
        Rig = await DayRig.StartAsync(Path.Combine(DataRoot, "app"), freshStore: false);
        NextBootSeconds = sw.Elapsed.TotalSeconds;
        Event("NEXT-MORNING", F($"AgOpenWeb started again on the day's data in {NextBootSeconds:0.00} s"));
        Measures.Add(F($"Start the next morning on the day's data: {NextBootSeconds:0.00} s"));
        var last = Jobs.LastOrDefault();
        if (last != null)
        {
            for (int i = 0; i < 20; i++) { SendFixAt(_yard.Lat, _yard.Lon, 0); await Task.Delay(10); }
            sw.Restart();
            await SendAsync("field.resumeJob", last.Job.Field + "\t" + TaskName(last.Job));
            bool open = await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy, 30000);
            double s = sw.Elapsed.TotalSeconds;
            Event("RESUME", open ? F($"yesterday's job on {last.Job.Field} resumed in {s:0.00} s") : "yesterday's job did not open within 30 s");
            Measures.Add(open ? F($"Resume the last job ({last.Job.Field}, {last.AreaHa:0.0} ha worked): {s:0.00} s") : "Resume the last job: did not open within 30 s");
            NextMorning = ProcessState.Take();
            Measures.Add(F($"The next morning with that job open: managed heap {NextMorning.HeapMB:0.0} MB, resident {NextMorning.RssMB:0} MB"));
            FirstBackendAliveNextMorning = _firstVm?.IsAlive == true;
            Measures.Add(FirstBackendAliveNextMorning
                ? "Yesterday's MainViewModel is still alive after the second start in the same process"
                : "Yesterday's MainViewModel is gone after the second start in the same process");
        }
    }

    public ProcessState? AfterExit, NextMorning, BeforeStart;
    private WeakReference? _firstVm;
    /// <summary>The first backend's MainViewModel outlived the second start in the process.</summary>
    public bool FirstBackendAliveNextMorning;
    public List<string> OpenFilesAtEnd = new();
    /// <summary>AgOpenWeb's last status line (not the module count) and when it came.</summary>
    public (DateTime Clock, string Text) LastStatus;

    // ------------------------------------------------------------------ baselines and censuses

    private async Task BaselineAsync(string code, string what)
    {
        await Rig.DrainAsync();
        double pausedFrom = Tablet.WallMs;
        var ps = ProcessState.Take();
        Tablet.Paused(pausedFrom);
        Phone?.Paused(pausedFrom);
        var disk = DiskUse.Take(Rig.DataRoot);
        Baselines.Add(new Baseline(code, Now, what, ps, disk.Values.Sum(v => v.Bytes) / 1048576.0));
        Disk[code] = disk;
        Event(code, F($"baseline: heap {ps.HeapMB:0.0} MB, resident {ps.RssMB:0} MB, {ps.Threads} threads, {ps.Timers} timers, {ps.Sockets} sockets, {ps.Files} files open"));
        ResetPace();
    }

    /// <summary>Dumps taken in the day (label, file), counted after it.</summary>
    private readonly List<(string Label, string? Path, string? Error)> _dumps = new();

    private async Task CensusAsync(string label)
    {
        await Rig.DrainAsync();
        // Writing the dump suspends the process for seconds: a pause of the harness's own,
        // longer than the deadman (the tablet then claims the free seat back).
        double pausedFrom = Tablet.WallMs;
        ProcessState.PauseStarted();
        var (path, error, seconds) = HeapCensus.Dump(label);
        ProcessState.PauseEnded();
        Tablet.Paused(pausedFrom);
        Phone?.Paused(pausedFrom);
        _dumps.Add((label, path, error));
        long size = path != null && File.Exists(path) ? new FileInfo(path).Length : 0;
        Event("CENSUS", error == null
            ? F($"heap dump '{label}' for the census: {size / 1048576.0:0} MB in {seconds:0.0} s (counted after the day)")
            : "heap dump failed: " + error);
        ResetPace();
    }

    /// <summary>Count the day's dumps (after the day, so the counting never shows in it).</summary>
    public void CountCensuses()
    {
        HeapCensus? first = null;
        foreach (var (label, path, error) in _dumps)
        {
            var c = path == null ? HeapCensus.Failed(label, error ?? "no dump")
                : label == "after-exit" ? HeapCensus.Analyze(label, path, pathsFor: StillAliveAfterExit)
                : HeapCensus.Analyze(label, path, label == "end" ? first : null);
            Censuses.Add(c);
            if (label.StartsWith("after-J", StringComparison.Ordinal) && c.Error == null) first = c;
        }
    }

    /// <summary>The objects that must not outlive the backend: the path from a root says what
    /// keeps them (on Android and iOS the process outlives the backend).</summary>
    private static readonly string[] StillAliveAfterExit =
    {
        "AgOpenWeb.ViewModels.MainViewModel",
        "AgOpenWeb.Services.Coverage.CoverageMapService",
        "AgOpenWeb.RemoteServer.RemoteServerHost",
    };

    // ------------------------------------------------------------------ helpers

    private static string Kb(long? bytes) => bytes == null ? "none" : F($"{bytes.Value / 1024.0:0} KB");

    public async Task<bool> WaitAsync(Func<bool> done, double seconds, bool keepFixing = false)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (done()) return true;
            if (keepFixing) SendFixAt(_yard.Lat, _yard.Lon, 0);
            await Task.Delay(100);
        }
        return done();
    }

    /// <summary>The day's clock runs in real time (while the deadman, a wall-clock watch, is
    /// waited for: the tractor moves as far as it would on the field meanwhile).</summary>
    public bool RealTime
    {
        get => _realTime;
        set { _realTime = value; ResetPace(); }
    }
    private bool _realTime;

    public void ResetPace()
    {
        _paceFixes = Fixes;
        _paceWallMs = _wall.Elapsed.TotalMilliseconds;
    }

    /// <summary>Hold the harness to its pace: the day's clock runs PaceMs of wall time per fix.</summary>
    private async Task PaceAsync()
    {
        double target = _paceWallMs + (Fixes - _paceFixes) * (RealTime ? 100 : PaceMs);
        double now = _wall.Elapsed.TotalMilliseconds;
        if (now < target - 1) await Task.Delay(TimeSpan.FromMilliseconds(target - now));
        else if (now - target > MaxBehindMs) MaxBehindMs = now - target;
    }

    /// <summary>One fix where the tractor stands, as a receiver sends it in the yard or on a refill.</summary>
    private void SendFixAt(double lat, double lon, double headingDeg)
    {
        Fixes++;
        Module.TickNow(); Module.TickNow();
        Rig.SendSentence(Sentence(lat, lon, headingDeg, 0));
        Rig.TickLoop(10);
    }

    private string Sentence(double lat, double lon, double headingDeg, double kmh)
    {
        // The receiver's own clock: the NMEA time of day runs with the day.
        var s = DayRig.Sentence(Sys.Dual, Sys.HasImu, lat, lon, headingDeg, 0, kmh)
            .Replace("120000.00", Now.ToString("HHmmss.ff", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return Checksum(s.Split('*')[0]);
    }

    private static string Checksum(string bodyWithDollar)
    {
        byte cs = 0;
        foreach (char c in bodyWithDollar.AsSpan(1)) cs ^= (byte)c;
        return bodyWithDollar + "*" + cs.ToString("X2", CultureInfo.InvariantCulture) + "\r\n";
    }

    public void Collect()
    {
        foreach (var e in Rig.TakeEvents())
        {
            if (e.Kind == RigEventKind.Status && !e.Text.Contains("module(s) active", StringComparison.Ordinal)) LastStatus = (Now, e.Text);
            if (Environment.GetEnvironmentVariable("AGOPENWEB_DAY_VERBOSE") == "2" && e.Kind != RigEventKind.Warning)
                TestContext.Progress.WriteLine(F($"[aow] {Now:HH:mm:ss} {e}"));
            // The end of the field, as AgOpenWeb says it. Before the first U-turn the tractor is
            // still entering through the headland on the first pass and AgOpenWeb can say "End of
            // field reached - no room on the next pass at this end" about the end it came from: the
            // operator drives on (the field job harness judges that message; this one needs the day).
            if (e.Kind == RigEventKind.Status && (e.Text.Contains("End of field reached", StringComparison.Ordinal) || e.Text.Contains("Field complete", StringComparison.Ordinal))
                && _job is { Turns: > 0 })
                FieldDone = true;
            if (e.Kind is RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang) _errors++;
            if (e.Kind is RigEventKind.Warning) _warnings++;
            if (e.Kind is RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang or RigEventKind.Warning or RigEventKind.Failure or RigEventKind.OffLoop
                || (e.Kind == RigEventKind.Status && e.Text.Length > 0))
            {
                string key = e.Kind + ": " + Normalise(e.Text);
                if (Said.TryGetValue(key, out var v)) Said[key] = (v.Count + 1, v.First, v.Text);
                else Said[key] = (1, Now, e.ToString().Length > 400 ? e.ToString()[..400] + "…" : e.ToString());
            }
        }
    }

    /// <summary>A message with its numbers taken out, so the same message counts once.</summary>
    private static string Normalise(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.Length > 160 ? text[..160] : text)
            sb.Append(char.IsDigit(c) ? '#' : c);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try { if (Phone != null) await Phone.Abort(); } catch { }
        try { if (Tablet != null) await Tablet.Abort(); } catch { }
        try { if (Rig != null) await Rig.DisposeAsync(); } catch { }
        try { Steer?.Dispose(); } catch { }
        try { Module?.Dispose(); } catch { }
        try { Caster?.Dispose(); } catch { }
    }
}
