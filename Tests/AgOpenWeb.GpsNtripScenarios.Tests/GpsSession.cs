// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>What the operator and the machine show at one moment (sampled every 100 ms).</summary>
public sealed record Sample(
    double T,
    int RxQuality, double RxAge, bool RxSending,
    int AowFix, double AowAge, bool GpsLost,
    NtripState Ntrip, NtripCategory? NtripError, string NtripMessage, double RetryIn,
    bool AutoSteer, ulong Sections, double LoopMs, int Cycles,
    double SteerAngle, double Xte, double HeadingDeg, bool Reverse, bool GpsValid,
    bool InternetUp, int CasterStreams, bool DualMissing, string Status,
    double E, double N, double SpeedKmh, int SentenceType);

public sealed record Event(double T, RigEventKind Kind, string Text);

/// <summary>
/// One AgOpenWeb (its own data folder), a caster behind a fake internet, an emulated receiver
/// and steer module on the loopback, and a sampler that records every 100 ms what the screen
/// shows next to what the receiver does. Scenarios act (internet down, caster stopped, a typo
/// in the login) and mark the moment; the checks read the timeline between marks.
/// </summary>
public sealed class GpsSession : IAsyncDisposable
{
    public GpsNtripRig Rig { get; private set; } = null!;
    public FakeCaster Caster { get; private set; } = null!;
    public FakeCaster? Caster2 { get; private set; }
    public Internet Net { get; private set; } = null!;
    public EmulatedReceiver Rx { get; private set; } = null!;
    public SteerModuleStub Steer { get; private set; } = null!;
    public ISectionControlService Sections { get; private set; } = null!;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private Task? _sampler;
    public List<Sample> Timeline { get; } = new();
    public ConcurrentQueue<Event> Events { get; } = new();
    public List<(double T, string What)> Marks { get; } = new();
    public double MaxLoopMs;
    public int Freezes;
    public double Now => _clock.Elapsed.TotalSeconds;

    public sealed class Options
    {
        public bool Dual = true;
        public IPAddress? NmeaFrom;
        public bool SecondCaster;
        public double HeadingDeg;
    }

    public static async Task<GpsSession> StartAsync(string dataRoot, Options? o = null)
    {
        o ??= new Options();
        var s = new GpsSession();
        s.Caster = new FakeCaster("caster");
        if (o.SecondCaster) s.Caster2 = new FakeCaster("fallback caster");
        s.Net = new Internet(s.Caster.Port);
        var caster = s.Caster;
        s.Rx = new EmulatedReceiver(() => caster.NowMs, o.NmeaFrom) { Dual = o.Dual, HeadingDeg = o.HeadingDeg };
        s.Steer = new SteerModuleStub { On = true };
        s.Rig = await GpsNtripRig.StartAsync(dataRoot);
        s.Sections = s.Rig.Services.GetRequiredService<ISectionControlService>();
        s.Rx.Start();
        s._sampler = Task.Run(() => s.SampleLoop(s._cts.Token));
        if (o.Dual) await s.SendAsync("config.set", "gps.isDualGps:1");
        return s;
    }

    public void Mark(string what)
    {
        lock (Marks) Marks.Add((Now, what));
    }

    public double MarkTime(string what)
    {
        lock (Marks) return Marks.LastOrDefault(m => m.What == what).T;
    }

    public Task SendAsync(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the operator's steps

    /// <summary>The NTRIP profile editor's Save, field by field as the web client sends it.
    /// A new profile (empty id) is the default one and connects at once.</summary>
    public Task SaveProfileAsync(string name, string host, int port, string mount, string user, string pass,
        string id = "", bool auto = true, string fallback = "")
    {
        string arg = string.Join("\t", id, name, host, port.ToString(CultureInfo.InvariantCulture), mount, user, pass,
            auto ? "1" : "0", "1", "");
        if (fallback.Length > 0) arg += "\t" + fallback;
        return SendAsync("ntrip.save", arg);
    }

    /// <summary>The careful operator's profile: the caster through the internet, right login and mount point.</summary>
    public Task SaveGoodProfileAsync(string mount = FakeCaster.Mount) =>
        SaveProfileAsync("Farm RTK", "127.0.0.1", Net.Port, mount, FakeCaster.User, FakeCaster.Password);

    public async Task<string?> ProfileIdAsync() =>
        await Rig.ReadAsync(() => Rig.Services.GetRequiredService<INtripProfileService>().Profiles.FirstOrDefault()?.Id);

    /// <summary>Wait until <paramref name="done"/> holds (checked every 100 ms); the seconds it took, or NaN.</summary>
    public async Task<double> WaitForAsync(Func<bool> done, double seconds)
    {
        double start = Now;
        while (Now - start < seconds)
        {
            if (done()) return Now - start;
            await Task.Delay(100);
        }
        return double.NaN;
    }

    public NtripSessionStatus NtripStatus => Rig.Ntrip.Status;

    /// <summary>Field, AB line north through the tractor, job, sections on Auto, AutoSteer on:
    /// the start of a pass. Null when all of it worked, else what did not.</summary>
    public async Task<string?> StartPassAsync(string name, bool autoSteer = true)
    {
        double hd = Rx.HeadingDeg * Math.PI / 180, se = Math.Sin(hd), sn = Math.Cos(hd);
        if (double.IsNaN(await WaitForAsync(() => Rig.LastCycle is { FixQuality: > 0 }, 15))) return "no position from the receiver";
        await Task.Delay(1500);
        await SendAsync("field.new", name);
        if (!await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy)) return "the field did not open";
        var a = EmulatedReceiver.LatLon(Rx.E - 50 * se, Rx.N - 50 * sn);
        var b = EmulatedReceiver.LatLon(Rx.E + 3000 * se, Rx.N + 3000 * sn);
        await SendAsync("track.latLonAB", F($"{a.Lat:F9},{a.Lon:F9},{b.Lat:F9},{b.Lon:F9}"));
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) await SendAsync("track.activate", "0");
        // The line through the pivot, as the operator's "snap to pivot": the emulated tractor
        // drives straight and does not answer the steering, so it should start on the line.
        await SendAsync("track.snapPivot");
        await SendAsync("field.startJob", name + "\tSpraying\tscenario\tSpraying " + name);
        await SendAsync("section.master");
        if (double.IsNaN(await WaitForAsync(() => Sections.GetSectionBits64() != 0, 8))) return "the sections did not come on (Auto, fresh ground, 8 km/h)";
        if (autoSteer)
        {
            await SendAsync("autosteer.toggle");
            if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) return "AutoSteer did not engage";
        }
        return null;
    }

    public async Task<bool> AutoSteerOnAsync() => await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged);

    public static Task WaitAsync(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));

    // ------------------------------------------------------------------ the sampler

    private async Task SampleLoop(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await SampleOnce(); } catch (Exception ex) { Events.Enqueue(new Event(Now, RigEventKind.Note, "sampler: " + ex.Message)); }
            next += 100;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, ct); } catch { return; } }
            else next = sw.ElapsedMilliseconds;
        }
    }

    private async Task SampleOnce()
    {
        double t = Now;
        var loopStart = System.Diagnostics.Stopwatch.StartNew();
        var read = Rig.ReadAsync(() => (
            Rig.Vm.IsAutoSteerEngaged,
            Rig.State.Vehicle.FixQuality,
            Rig.State.Vehicle.Age,
            Rig.State.Connections.IsGpsLost,
            Rig.Vm.StatusMessage ?? ""));
        if (await Task.WhenAny(read, Task.Delay(5000)) != read)
        {
            Freezes++;
            Events.Enqueue(new Event(t, RigEventKind.Hang, "the host loop did not answer within 5 s"));
            await read;   // wait it out; the time is the freeze
        }
        double loopMs = loopStart.Elapsed.TotalMilliseconds;
        MaxLoopMs = Math.Max(MaxLoopMs, loopMs);
        var (steer, fix, age, lost, status) = await read;
        var st = Rig.Ntrip.Status;
        var c = Rig.LastCycle;
        foreach (var e in Rig.TakeEvents()) Events.Enqueue(new Event(t, e.Kind, e.Text));
        var sample = new Sample(t, Rx.Quality, Rx.CorrectionAge, Rx.Sending, fix, age, lost,
            st.State, st.LastError, st.Message, st.RetryInSeconds,
            steer, Sections.GetSectionBits64(), loopMs, Rig.CycleCount,
            c?.Guidance?.SteerAngle ?? 0, c?.Guidance?.CrossTrackError ?? 0, c?.Heading ?? 0,
            c?.IsReverse ?? false, c?.GpsValid ?? false,
            Net.Now == Internet.State.Up, Caster.Streaming, c?.IsDualHeadingMissing ?? false, status,
            c?.Easting ?? double.NaN, c?.Northing ?? double.NaN, (c?.Speed ?? 0), (int)(c?.SentenceType ?? 0));
        lock (Timeline) Timeline.Add(sample);
    }

    public List<Sample> Between(double from, double to)
    {
        lock (Timeline) return Timeline.Where(s => s.T >= from && s.T <= to).ToList();
    }

    public double FirstAt(double from, Func<Sample, bool> what, double to = double.MaxValue)
    {
        lock (Timeline)
            foreach (var s in Timeline)
                if (s.T >= from && s.T <= to && what(s)) return s.T;
        return double.NaN;
    }

    public List<Event> EventsBetween(double from, double to, params RigEventKind[] kinds) =>
        Events.Where(e => e.T >= from && e.T <= to && (kinds.Length == 0 || kinds.Contains(e.Kind))).ToList();

    /// <summary>A short text of what the operator was told between two moments: toasts, failures, sounds, NTRIP messages.</summary>
    public List<string> Told(double from, double to)
    {
        var told = new List<string>();
        foreach (var e in EventsBetween(from, to, RigEventKind.Toast, RigEventKind.Failure, RigEventKind.Sound))
            told.Add(F($"{e.T - from:0.0} s {e.Kind.ToString().ToLowerInvariant()}: {e.Text}"));
        string last = "";
        foreach (var s in Between(from, to))
        {
            if (s.NtripMessage != last && s.NtripMessage.Length > 0) told.Add(F($"{s.T - from:0.0} s NTRIP: {s.NtripMessage}"));
            last = s.NtripMessage;
        }
        return told;
    }

    /// <summary>The whole timeline as CSV, for looking at one scenario in detail.</summary>
    public void WriteCsv(string path)
    {
        var sb = new System.Text.StringBuilder("t,rxQ,rxAge,aowFix,aowAge,gpsLost,ntrip,autoSteer,sections,steer,xte,heading,reverse,gpsValid,dualMissing,e,n,speed,sentence,status\n");
        lock (Timeline)
            foreach (var x in Timeline)
                sb.Append(F($"{x.T:0.00},{x.RxQuality},{x.RxAge:0.0},{x.AowFix},{x.AowAge:0.0},{(x.GpsLost ? 1 : 0)},{x.Ntrip},{(x.AutoSteer ? 1 : 0)},{x.Sections},{x.SteerAngle:0.0},{x.Xte:0.000},{x.HeadingDeg:0.0},{(x.Reverse ? 1 : 0)},{(x.GpsValid ? 1 : 0)},{(x.DualMissing ? 1 : 0)},{x.E:0.000},{x.N:0.000},{x.SpeedKmh:0.0},{x.SentenceType},\"{x.Status.Replace("\"", "'")}\"\n"));
        foreach (var e in Events) sb.Append(F($"# {e.T:0.00} {e.Kind}: {e.Text.Split('\n')[0]}\n"));
        File.WriteAllText(path, sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_sampler != null) await _sampler; } catch { }
        try { await Rig.DisposeAsync(); } catch { }
        Rx.Dispose();
        Steer.Dispose();
        Net.Dispose();
        Caster.Dispose();
        Caster2?.Dispose();
    }
}
