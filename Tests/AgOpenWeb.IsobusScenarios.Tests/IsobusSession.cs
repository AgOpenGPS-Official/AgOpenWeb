// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.IsobusScenarios.Tests;

/// <summary>
/// One AgOpenWeb (its own data folder), the emulated task controller on the loopback, and a
/// tractor that keeps sending RTK fixes at 10 Hz in real time (the task controller's and
/// AgOpenWeb's timeouts are real time). The tractor drives north on fresh ground, so automatic
/// sections stay on.
/// </summary>
public sealed class IsobusSession : IAsyncDisposable
{
    public IsobusRig Rig { get; private set; } = null!;
    /// <summary>The task controller: it belongs to the scenario and outlives an AgOpenWeb restart.</summary>
    public EmulatedTaskController Tc { get; private set; } = null!;
    public ISectionControlService Sections { get; private set; } = null!;
    public ICoverageMapService Coverage { get; private set; } = null!;
    public IIsobusTaskControllerService Isobus { get; private set; } = null!;
    public ConfigurationStore Store => Rig.Store;

    /// <summary>Hardware messages (PGN 221) AgOpenWeb parsed, with the session clock.</summary>
    public readonly List<(double At, string Text, int Seconds, bool Warning)> HardwareMessages = new();

    private static readonly (double Lat, double Lon) Origin = (41.4987, -5.5012); // made-up place
    private const double MetresPerDegLat = 111_320.0;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Task? _gps;

    // the tractor
    public double E, N;
    public volatile bool GpsOn = true;
    public double SpeedKmh = 8;
    public double HeadingDeg;

    public static async Task<IsobusSession> StartAsync(string dataRoot, EmulatedTaskController tc, double startNorth = 0)
    {
        var s = new IsobusSession { Tc = tc, N = startNorth };
        s.Rig = await IsobusRig.StartAsync(dataRoot);
        s.Sections = s.Rig.Services.GetRequiredService<ISectionControlService>();
        s.Coverage = s.Rig.Services.GetRequiredService<ICoverageMapService>();
        s.Isobus = s.Rig.Services.GetRequiredService<IIsobusTaskControllerService>();
        s.Rig.AutoSteer.HardwareMessageReceived += (text, secs, warn) =>
        {
            lock (s.HardwareMessages) s.HardwareMessages.Add((s._clock.Elapsed.TotalSeconds, text, secs, warn));
        };
        s._gps = Task.Run(() => s.GpsLoop(s._cts.Token));
        return s;
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;

    /// <summary>What the status frame carries to the browsers (null = no button).</summary>
    public IsobusDto? Dto() => Rig.Server.IsobusProvider?.Invoke();

    private (double Lat, double Lon) LatLon(double e, double n) =>
        (Origin.Lat + n / MetresPerDegLat, Origin.Lon + e / (MetresPerDegLat * Math.Cos(Origin.Lat * Math.PI / 180)));

    private async Task GpsLoop(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        while (!ct.IsCancellationRequested)
        {
            if (GpsOn)
            {
                double v = SpeedKmh / 3.6 * 0.1;
                double h = HeadingDeg * Math.PI / 180;
                E += v * Math.Sin(h);
                N += v * Math.Cos(h);
                var (lat, lon) = LatLon(E, N);
                try { Rig.SendSentence(IsobusRig.Sentence(true, false, lat, lon, HeadingDeg, 0, SpeedKmh)); }
                catch (Exception ex) { Console.WriteLine("[iso] GPS cycle threw: " + ex.Message); }
            }
            next += 100;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, ct); } catch (OperationCanceledException) { return; } }
        }
    }

    public Task SendAsync(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The settings page as the operator fills it in: the tool's sections and widths. Saved at the end.</summary>
    public async Task SetToolAsync(int sections, double cm, bool save = true)
    {
        await SendAsync("config.set", F($"tool.numSections:{sections}"));
        for (int i = 0; i < sections; i++) await SendAsync("config.set", F($"tool.sectionWidth:{i},{cm}"));
        if (save) await SendAsync("profile.save");
    }

    /// <summary>Field, AB line north through the tractor, job, master on (automatic sections):
    /// the start of a day's work. Empty when the sections came on.</summary>
    public async Task<string?> StartJobAsync(string name)
    {
        await Task.Delay(1500); // a few fixes first, as a receiver gives before the field opens
        await SendAsync("field.new", name);
        if (!await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy)) return "the field did not open";
        var a = LatLon(E, N - 50);
        var b = LatLon(E, N + 2000);
        await SendAsync("track.latLonAB", F($"{a.Lat:F9},{a.Lon:F9},{b.Lat:F9},{b.Lon:F9}"));
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) await SendAsync("track.activate", "0");
        await SendAsync("field.startJob", name + "\tSpraying\tscenario\tSpraying " + name);
        await SendAsync("section.master");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            if (Sections.GetSectionBits64() != 0) return null;
            await Task.Delay(100);
        }
        return "the sections did not come on (master Auto, fresh ground, 8 km/h)";
    }

    /// <summary>The ISOBUS button: the opposite of the mode the task controller reports.</summary>
    public Task TapIsobusAsync() => SendAsync("isobus.sectionControl");

    /// <summary>The operator wants this mode: a tap when the button shows the other one, nothing
    /// when it already shows it (the button toggles what the task controller reports). Seconds
    /// until AgOpenWeb showed it, 0 when it already did, NaN on timeout.</summary>
    public async Task<double> WantModeAsync(bool enabled, int timeoutMs = 2000)
    {
        var st = Isobus.Status;
        if (st.IsAlive && st.SectionControlEnabled == enabled) return 0;
        await TapIsobusAsync();
        return await WaitModeAsync(enabled, timeoutMs);
    }

    /// <summary>The master button: Auto when it was off, off when it was Auto.</summary>
    public Task TapMasterAsync() => SendAsync("section.master");

    /// <summary>Wait in real time, in short steps so the host loop keeps up.</summary>
    public static Task WaitAsync(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));

    /// <summary>Wait until the task controller is heard (or not) by AgOpenWeb. Seconds it took, NaN on timeout.</summary>
    public async Task<double> WaitAliveAsync(bool alive = true, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Isobus.Status.IsAlive == alive) return sw.Elapsed.TotalSeconds;
            await Task.Delay(20);
        }
        return double.NaN;
    }

    /// <summary>Wait until AgOpenWeb shows the given section control mode. Seconds it took, NaN on timeout.</summary>
    public async Task<double> WaitModeAsync(bool enabled, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var st = Isobus.Status;
            if (st.IsAlive && st.SectionControlEnabled == enabled) return sw.Elapsed.TotalSeconds;
            await Task.Delay(20);
        }
        return double.NaN;
    }

    // ------------------------------------------------------------------ checks

    /// <summary>What the operator sees and what the implement does, sampled every 100 ms.</summary>
    public sealed class Watch
    {
        public int Samples;
        public double Seconds;
        /// <summary>Per section: seconds the screen showed it on while the implement had it off, and the other way.</summary>
        public double[] OnButImplementOff = new double[64], OffButImplementOn = new double[64];
        public double ScreenOnSeconds, AliveSeconds, NotAliveSeconds, AutoShownSeconds;
        /// <summary>Seconds the task controller had no PGN 229 younger than 1 s while AgOpenWeb's screen had sections on.</summary>
        public double ScreenOnNoSetpointsSeconds;
        /// <summary>Seconds the setpoints the task controller holds differed from the screen (beyond 0.3 s of age).</summary>
        public double SetpointsDifferSeconds;
        /// <summary>Seconds AgOpenWeb's reported sections differed from the implement's actual ones.</summary>
        public double ReportedDifferSeconds;
        /// <summary>Seconds the status frame disagreed with the service (one has a task controller, the other not).</summary>
        public double FrameDisagreeSeconds;
        /// <summary>Seconds the mode AgOpenWeb showed differed from the task controller's.</summary>
        public double ModeDifferSeconds;
        public int AliveFlips;
        public double FirstAliveAt = double.NaN, FirstNotAliveAt = double.NaN;
        /// <summary>Distinct section counts the status reported, and how often it changed.</summary>
        public SortedSet<int> SectionCountsSeen = new();
        public int SectionCountChanges;
        /// <summary>Coverage AgOpenWeb painted (m²): in all, with the implement's sections all off, with no task controller heard.</summary>
        public double AreaPainted, AreaPaintedImplementOff, AreaPaintedNotAlive;

        public string Summary(int n)
        {
            var sb = new StringBuilder();
            sb.Append(F($"{Seconds:0.0} s: screen sections on {ScreenOnSeconds:0.0} s; task controller heard {AliveSeconds:0.0} s, not heard {NotAliveSeconds:0.0} s; auto shown {AutoShownSeconds:0.0} s"));
            var a = Enumerable.Range(0, n).Where(i => OnButImplementOff[i] >= 0.5).Select(i => F($"{i + 1}:{OnButImplementOff[i]:0.0}s")).ToList();
            var b = Enumerable.Range(0, n).Where(i => OffButImplementOn[i] >= 0.5).Select(i => F($"{i + 1}:{OffButImplementOn[i]:0.0}s")).ToList();
            if (a.Count > 0) sb.Append("; on screen but implement off ").Append(string.Join(" ", a.Take(12))).Append(a.Count > 12 ? F($" … ({a.Count} sections)") : "");
            if (b.Count > 0) sb.Append("; off on screen but implement on ").Append(string.Join(" ", b.Take(12))).Append(b.Count > 12 ? F($" … ({b.Count} sections)") : "");
            if (ScreenOnNoSetpointsSeconds > 0) sb.Append(F($"; screen on with no PGN 229 at the task controller {ScreenOnNoSetpointsSeconds:0.0} s"));
            if (ReportedDifferSeconds > 0.2) sb.Append(F($"; reported sections differ from the implement {ReportedDifferSeconds:0.0} s"));
            if (ModeDifferSeconds > 0.2) sb.Append(F($"; mode shown differs {ModeDifferSeconds:0.0} s"));
            if (AliveFlips > 1) sb.Append(F($"; heard/not heard flipped {AliveFlips} times"));
            sb.Append(F($"; coverage painted {AreaPainted:0} m²"));
            if (AreaPaintedImplementOff > 0) sb.Append(F($" ({AreaPaintedImplementOff:0} m² with the implement's sections all off)"));
            if (AreaPaintedNotAlive > 0) sb.Append(F($" ({AreaPaintedNotAlive:0} m² with no task controller heard)"));
            return sb.ToString();
        }
    }

    public async Task<Watch> WatchAsync(double seconds, Implement? implement = null)
    {
        var w = new Watch();
        int n = Store.NumSections;
        ulong mask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        double area = Coverage.TotalWorkedArea;
        bool? lastAlive = null;
        int lastCount = -1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double a = Coverage.TotalWorkedArea, da = Math.Max(0, a - area);
            area = a;
            w.AreaPainted += da;
            ulong screen = Sections.GetSectionBits64() & mask;
            var st = Isobus.Status;
            var dto = Dto();
            bool alive = st.IsAlive;
            ulong actual = 0;
            int implSections = 0;
            var impl = implement ?? Tc.Implements.FirstOrDefault();
            if (impl != null) { actual = impl.Actual; implSections = impl.Sections; }
            long setAge = Tc.LastSetpointsMs < 0 ? long.MaxValue : Tc.NowMs - Tc.LastSetpointsMs;
            double dt = 0.1;
            w.Samples++;
            w.Seconds += dt;
            if (screen != 0) w.ScreenOnSeconds += dt;
            if (alive) { w.AliveSeconds += dt; if (double.IsNaN(w.FirstAliveAt)) w.FirstAliveAt = sw.Elapsed.TotalSeconds; }
            else { w.NotAliveSeconds += dt; if (double.IsNaN(w.FirstNotAliveAt)) w.FirstNotAliveAt = sw.Elapsed.TotalSeconds; w.AreaPaintedNotAlive += da; }
            if (lastAlive is bool la && la != alive) w.AliveFlips++;
            lastAlive = alive;
            if (alive && st.SectionControlEnabled) w.AutoShownSeconds += dt;
            // The bare beacon (no implement) always says manual, whatever the task controller holds.
            if (alive && st.SectionCount > 0 && st.SectionControlEnabled != Tc.SectionControlEnabled) w.ModeDifferSeconds += dt;
            if ((dto != null) != alive) w.FrameDisagreeSeconds += dt;
            if (alive && st.SectionCount != lastCount) { if (lastCount >= 0) w.SectionCountChanges++; lastCount = st.SectionCount; w.SectionCountsSeen.Add(st.SectionCount); }
            if (screen != 0 && setAge > 1000) w.ScreenOnNoSetpointsSeconds += dt;
            if (setAge <= 1000 && setAge > 300 && (Tc.LastSetpoints & mask) != screen) w.SetpointsDifferSeconds += dt;
            if (implSections > 0)
            {
                ulong imask = implSections >= 64 ? ulong.MaxValue : (1UL << implSections) - 1;
                if (alive && st.SectionCount == implSections && (st.ReportedSections & imask) != (actual & imask)) w.ReportedDifferSeconds += dt;
                if ((actual & imask) == 0 && screen != 0) w.AreaPaintedImplementOff += da;
                for (int i = 0; i < Math.Min(n, implSections); i++)
                {
                    bool s = ((screen >> i) & 1) != 0, o = ((actual >> i) & 1) != 0;
                    if (s && !o) w.OnButImplementOff[i] += dt;
                    if (!s && o) w.OffButImplementOn[i] += dt;
                }
            }
            next += 100;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay((int)wait);
        }
        return w;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_gps != null) await _gps; } catch { }
        try { await Rig.DisposeAsync(); } catch { }
    }
}
