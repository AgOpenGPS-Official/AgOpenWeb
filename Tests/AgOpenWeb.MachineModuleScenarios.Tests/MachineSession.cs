// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>
/// One AgOpenWeb (its own data folder), the emulated machine module on the loopback, and a
/// tractor that keeps sending RTK fixes at 10 Hz in real time (the module's timeouts are real
/// time). The tractor drives north on fresh ground, so automatic sections stay on.
/// </summary>
public sealed class MachineSession : IAsyncDisposable
{
    public MachineModuleRig Rig { get; private set; } = null!;
    /// <summary>The machine module: it belongs to the scenario and outlives an AgOpenWeb restart.</summary>
    public EmulatedSprayerModule Module { get; private set; } = null!;
    public ISectionControlService Sections { get; private set; } = null!;
    public ICoverageMapService Coverage { get; private set; } = null!;
    public ConfigurationStore Store => Rig.Store;

    private static readonly (double Lat, double Lon) Origin = (41.4987, -5.5012); // made-up place
    private const double MetresPerDegLat = 111_320.0;
    private readonly CancellationTokenSource _cts = new();
    private Task? _gps;

    // the tractor
    public double E, N;
    public volatile bool GpsOn = true;
    public double SpeedKmh = 8;
    public double HeadingDeg;

    public static async Task<MachineSession> StartAsync(string dataRoot, EmulatedSprayerModule module, double startNorth = 0)
    {
        var s = new MachineSession { Module = module, N = startNorth };
        s.Rig = await MachineModuleRig.StartAsync(dataRoot);
        s.Sections = s.Rig.Services.GetRequiredService<ISectionControlService>();
        s.Coverage = s.Rig.Services.GetRequiredService<ICoverageMapService>();
        s._gps = Task.Run(() => s.GpsLoop(s._cts.Token));
        return s;
    }

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
                try { Rig.SendSentence(MachineModuleRig.Sentence(true, false, lat, lon, HeadingDeg, 0, SpeedKmh)); }
                catch (Exception ex) { Console.WriteLine("[mm] GPS cycle threw: " + ex.Message); }
            }
            next += 100;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, ct); } catch (OperationCanceledException) { return; } }
        }
    }

    public Task SendAsync(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The settings page as the operator fills it in: the tool's sections and widths,
    /// and the relay functions (0-based relay index, PinFunction value). Saved at the end.</summary>
    public async Task SetToolAsync(int sections, double cm, IEnumerable<(int Relay, int Fn)>? relays = null, bool save = true)
    {
        await SendAsync("config.set", F($"tool.numSections:{sections}"));
        for (int i = 0; i < sections; i++) await SendAsync("config.set", F($"tool.sectionWidth:{i},{cm}"));
        foreach (var (r, fn) in relays ?? Array.Empty<(int, int)>()) await SendAsync("config.set", F($"machine.pin:{r},{fn}"));
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

    /// <summary>A tram system on the AB line (24 m, every pass). The config page's "Tram lights"
    /// toggle is left as it is: it already shows On (the web toggle shows GuidanceConfig.TramDisplay,
    /// true by default; a tap sends "0" when it shows On). Returns why tram lines are missing
    /// (null when built) and the tram display mode that drives the lights.</summary>
    public async Task<(string? Error, string ModeAfterToggle)> TramOnAsync()
    {
        await SendAsync("tram.add");
        await SendAsync("tram.set", "0,width,24");
        await SendAsync("tram.set", "0,passes,1");
        await SendAsync("tram.set", "0,enabled,1");
        string mode = Store.Tram.DisplayMode.ToString();
        return (await Rig.WaitUntilAsync(() => Rig.Tram.HasTramLines, 5000) ? null : "no tram lines were built", mode);
    }

    /// <summary>Wait in real time, in short steps so the host loop keeps up.</summary>
    public static Task WaitAsync(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));

    // ------------------------------------------------------------------ checks

    /// <summary>The module's flash against what AgOpenWeb holds for the loaded tool and machine.
    /// Empty = the module has it all.</summary>
    public List<string> ConfigDifferences()
    {
        var f = Module.FlashCopy();
        var diffs = new List<string>();
        int n = Store.NumSections;
        if (f.ToolSections != n) diffs.Add(F($"section count {f.ToolSections}, AgOpenWeb {n}"));
        var wrong = new List<string>();
        for (int i = 0; i < Math.Min(n, 16); i++)
        {
            int cm = (int)Math.Round(Store.Tool.GetSectionWidth(i));
            if (f.SectionCm[i] != cm) wrong.Add(F($"{i + 1}: {f.SectionCm[i]} cm (AgOpenWeb {cm})"));
        }
        if (wrong.Count > 0) diffs.Add("widths " + string.Join(", ", wrong.Take(4)) + (wrong.Count > 4 ? F($" … ({wrong.Count})") : ""));
        var pins = Store.Machine.PinAssignments;
        var pw = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            byte app = (byte)(i < pins.Length ? pins[i] : 0);
            if (f.Pins[i] != app) pw.Add(F($"relay {i + 1}: {f.Pins[i]} (AgOpenWeb {app} {(PinFunction)app})"));
        }
        if (pw.Count > 0) diffs.Add("output functions " + string.Join(", ", pw.Take(4)) + (pw.Count > 4 ? F($" … ({pw.Count})") : ""));
        var m = Store.Machine;
        if (f.RaiseTime != Math.Clamp(m.RaiseTime, 0, 255) || f.LowerTime != Math.Clamp(m.LowerTime, 0, 255)
            || (f.HydEnable != 0) != m.HydraulicLiftEnabled || (f.InvertOutputs != 0) != m.InvertRelay)
            diffs.Add(F($"machine config raise {f.RaiseTime}/{m.RaiseTime} s, lower {f.LowerTime}/{m.LowerTime} s, lift {f.HydEnable}/{(m.HydraulicLiftEnabled ? 1 : 0)}, invert {f.InvertOutputs}/{(m.InvertRelay ? 1 : 0)} (module/AgOpenWeb)"));
        return diffs;
    }

    /// <summary>What the operator sees and what the valves do, sampled every 100 ms.</summary>
    public sealed class Watch
    {
        public int Samples;
        public double Seconds;
        /// <summary>Per section: seconds the screen showed it on while its valve was closed, and the other way.</summary>
        public double[] OnButClosed = new double[64], OffButOpen = new double[64];
        public double ScreenOnSeconds, ModuleOkSeconds, ModuleLostSeconds, LinkDownSeconds;
        public double ScreenOnWhileModuleLost;
        public double PaintWhileValvesClosed;   // screen on, module link down
        public double ScreenOnWhileGpsOff;
        public double ValvesOpenWhileGpsOff;
        public double FirstModuleOkAt = double.NaN, FirstModuleLostAt = double.NaN, FirstAllClosedAt = double.NaN;
        public int MaxScreenSections;
        /// <summary>Coverage AgOpenWeb painted (m²): in all, while the module's link was down, while there was no GPS.</summary>
        public double AreaPainted, AreaPaintedLinkDown, AreaPaintedGpsOff;

        public string Summary(int n)
        {
            var sb = new StringBuilder();
            sb.Append(F($"{Seconds:0.0} s: screen sections on {ScreenOnSeconds:0.0} s; module seen {ModuleOkSeconds:0.0} s, lost {ModuleLostSeconds:0.0} s"));
            var a = Enumerable.Range(0, n).Where(i => OnButClosed[i] >= 0.5).Select(i => F($"{i + 1}:{OnButClosed[i]:0.0}s")).ToList();
            var b = Enumerable.Range(0, n).Where(i => OffButOpen[i] >= 0.5).Select(i => F($"{i + 1}:{OffButOpen[i]:0.0}s")).ToList();
            if (a.Count > 0) sb.Append("; on screen but valve closed ").Append(string.Join(" ", a));
            if (b.Count > 0) sb.Append("; off on screen but valve open ").Append(string.Join(" ", b));
            if (ScreenOnWhileGpsOff > 0 || ValvesOpenWhileGpsOff > 0) sb.Append(F($"; sections on with no GPS {ScreenOnWhileGpsOff:0.0} s (valves open {ValvesOpenWhileGpsOff:0.0} s)"));
            if (LinkDownSeconds > 0) sb.Append(F($"; module link down {LinkDownSeconds:0.0} s, sections on screen meanwhile {PaintWhileValvesClosed:0.0} s"));
            sb.Append(F($"; coverage painted {AreaPainted:0} m²"));
            if (AreaPaintedLinkDown > 0) sb.Append(F($" ({AreaPaintedLinkDown:0} m² with the valves shut by the link loss)"));
            if (AreaPaintedGpsOff > 0) sb.Append(F($" ({AreaPaintedGpsOff:0} m² with no GPS)"));
            return sb.ToString();
        }
    }

    public async Task<Watch> WatchAsync(double seconds)
    {
        var w = new Watch();
        int n = Store.NumSections;
        ulong mask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        double area = Coverage.TotalWorkedArea;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double a = Coverage.TotalWorkedArea, da = Math.Max(0, a - area);
            area = a;
            w.AreaPainted += da;
            if (!Module.LinkOk) w.AreaPaintedLinkDown += da;
            if (!GpsOn) w.AreaPaintedGpsOff += da;
            ulong screen = Sections.GetSectionBits64() & mask;
            ulong open = Module.SectionsOpen & mask;
            bool ok = Rig.State.Connections.IsMachineDataOk;
            double dt = 0.1;
            w.Samples++;
            w.Seconds += dt;
            if (screen != 0) w.ScreenOnSeconds += dt;
            if (ok) { w.ModuleOkSeconds += dt; if (double.IsNaN(w.FirstModuleOkAt)) w.FirstModuleOkAt = sw.Elapsed.TotalSeconds; }
            else { w.ModuleLostSeconds += dt; if (double.IsNaN(w.FirstModuleLostAt)) w.FirstModuleLostAt = sw.Elapsed.TotalSeconds; if (screen != 0) w.ScreenOnWhileModuleLost += dt; }
            if (!Module.LinkOk) { w.LinkDownSeconds += dt; if (screen != 0) w.PaintWhileValvesClosed += dt; }
            if (!GpsOn) { if (screen != 0) w.ScreenOnWhileGpsOff += dt; if (open != 0) w.ValvesOpenWhileGpsOff += dt; }
            if (open == 0 && double.IsNaN(w.FirstAllClosedAt)) w.FirstAllClosedAt = sw.Elapsed.TotalSeconds;
            w.MaxScreenSections = Math.Max(w.MaxScreenSections, System.Numerics.BitOperations.PopCount(screen));
            for (int i = 0; i < n; i++)
            {
                bool s = ((screen >> i) & 1) != 0, o = ((open >> i) & 1) != 0;
                if (s && !o) w.OnButClosed[i] += dt;
                if (!s && o) w.OffButOpen[i] += dt;
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
