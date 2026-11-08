// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>The short drive a setting is tried on. Each one excites the part of the app the
/// setting belongs to, the same way every time.</summary>
public enum Probe
{
    /// <summary>No drive: what the modules are sent (PGN 251/252/238/236/235/247).</summary>
    Module,
    /// <summary>A fixed weave across the AB line (1.5 m either side, 16 s, 8 km/h), AutoSteer on:
    /// the steer angle AgOpenWeb asks for on every fix, off the line and on it.</summary>
    Approach,
    /// <summary>The weave with Stanley instead of Pure Pursuit.</summary>
    Stanley,
    /// <summary>The weave on a 6° cross slope, with the roll bumping ±2° as on rough ground.</summary>
    Slope,
    /// <summary>The weave from a single antenna with an IMU ($PANDA with the IMU's heading),
    /// the IMU's heading drifting 0.5°/s as a gyro does: how fast GPS pulls it back.</summary>
    SingleImu,
    /// <summary>The weave from a single antenna without an IMU (heading from the track).</summary>
    SingleNoImu,
    /// <summary>The weave with Nudge right pressed once on the way.</summary>
    Nudge,
    /// <summary>The weave with the reference line nudged right once on the way.</summary>
    RefNudge,
    /// <summary>The weave with a manual U-turn (right) pressed on the way.</summary>
    ManualTurn,
    /// <summary>The weave with RTK fixed dropping to float half way.</summary>
    RtkDrop,
    /// <summary>The weave with a steer module answering (its wheel angle = the angle it was sent).</summary>
    Deadzone,
    /// <summary>3 s forward, then 6 s reversing at 4 km/h, AutoSteer on.</summary>
    Reverse,
    /// <summary>The same from a single antenna with an IMU (reverse told from the travel
    /// against the IMU's heading).</summary>
    ReverseImu,
    /// <summary>Creeping at 2 km/h, AutoSteer on, for 10 s of real time (the below-minimum grace is 8 s).</summary>
    Slow,
    /// <summary>20 km/h, AutoSteer on.</summary>
    Fast,
    /// <summary>AutoSteer off, driving diagonally across the passes (1 m/s sideways): which
    /// pass AgOpenWeb shows.</summary>
    FreeDrive,
    /// <summary>A strip worked northbound, then driven back over southbound: where each section
    /// turns off on the worked ground and on again past it.</summary>
    Sections,
    /// <summary>The way back 0.3 m to the side, so one section is only partly over worked ground.</summary>
    SectionsShifted,
    /// <summary>15 m on fresh ground at 2 km/h, sections on automatic.</summary>
    SectionsSlow,
    /// <summary>The way back over the strip with a steer module whose work and steer switches are
    /// off for the first half and on for the second.</summary>
    Switches,
    /// <summary>Not tried yet: the reason is in the row.</summary>
    None,
}

/// <summary>What a probe saw, fix by fix.</summary>
public sealed class Trace
{
    public readonly List<double> Steer = new();       // AgOpenWeb's steer angle (NaN without guidance)
    public readonly List<double> SentAngle = new();   // PGN 254 bytes 8-9 / 100
    public readonly List<int> Status = new();         // PGN 254 byte 7
    public readonly List<bool> Engaged = new();
    public readonly List<int> Pass = new();
    public readonly List<ulong> Sections = new();     // PGN 239 bytes 11-12
    public readonly List<int> Machine = new();        // PGN 239 bytes 5, 7, 8, 9 (U-turn, lift, tram, geo stop)
    public readonly List<double> ToolN = new();       // the real implement's centre, along the strip
    /// <summary>Section probes: the worked area the first strip added, and the implement's width
    /// times the strip's length (what it should have added).</summary>
    public double StripArea = double.NaN, StripExpected = double.NaN;
    public readonly List<string> Said = new();
    public string? Disengaged;
    public int Held;                                  // fixes where PGN 254 did not carry AgOpenWeb's angle
    public Dictionary<byte, byte[]> ConfigPgns = new();
    public string Problem = "";

    // Where sections 1 and 8 switched (the real implement's N), for the verbose log.
    private string EdgeText()
    {
        if (Sections.Count == 0 || Sections.All(x => x == 0)) return "";
        var e = TraceDiff.EdgesOf(this);
        string One(int k) => e.TryGetValue(k, out var l) ? string.Join("/", l.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture))) : "-";
        return $", s1 at {One(0)}, s8 at {One(7)}";
    }

    /// <summary>One line for the verbose log.</summary>
    public string Summary()
    {
        var inv = CultureInfo.InvariantCulture;
        var st = Steer.Where(double.IsFinite).ToList();
        return string.Create(inv, $"{Steer.Count} fixes, engaged {Engaged.Count(e => e)}, guidance {st.Count}, steer mean {(st.Count > 0 ? st.Average() : 0):0.00} max {(st.Count > 0 ? st.Max(Math.Abs) : 0):0.0}, status1 {Status.Count(x => x == 1)}, held {Held}, passes {string.Join(",", Pass.Distinct())}, sections on {Sections.Count(x => x != 0)}{EdgeText()}{(double.IsFinite(StripArea) ? $", strip {StripArea:0}/{StripExpected:0} m²" : "")}{(Disengaged != null ? ", off: " + Disengaged : "")}{(Problem.Length > 0 ? ", " + Problem : "")}");
    }
}

/// <summary>How two traces differ.</summary>
public sealed record TraceDiff(double SteerMax, double SentMax, int GuidanceCount, int StatusFixes, int EngagedFixes,
    bool DisengageDiffers, int PassFixes, int SectionFixes, double SectionEdgeMax, int MachineFixes, int HeldDiff,
    List<string> PgnChanges)
{
    /// <summary>How far apart each switch is, by (section, switch number); switch -1 when the
    /// section switched a different number of times. Compared switch by switch against the
    /// noise: the switch off entering worked ground moves metres between identical runs, the
    /// switch on leaving it hardly at all, and a setting that moves only the second must show.</summary>
    public Dictionary<(int Section, int Switch), double> EdgeDiffs { get; init; } = new();

    public static TraceDiff Of(Trace a, Trace b)
    {
        int n = Math.Min(a.Steer.Count, b.Steer.Count);
        double steer = 0, sent = 0, edge = 0;
        int guid = 0, status = 0, eng = 0, pass = 0, sect = 0, mach = 0;
        for (int i = 0; i < n; i++)
        {
            bool fa = double.IsFinite(a.Steer[i]), fb = double.IsFinite(b.Steer[i]);
            if (fa && fb) steer = Math.Max(steer, Math.Abs(a.Steer[i] - b.Steer[i]));
            else if (fa != fb) guid++;
            if (a.Engaged[i] && b.Engaged[i]) sent = Math.Max(sent, Math.Abs(a.SentAngle[i] - b.SentAngle[i]));
            if (a.Status[i] != b.Status[i]) status++;
            if (a.Engaged[i] != b.Engaged[i]) eng++;
            if (a.Pass[i] != b.Pass[i]) pass++;
            if (a.Sections[i] != b.Sections[i]) sect++;
            if (a.Machine[i] != b.Machine[i]) mach++;
        }
        guid += Math.Abs(a.Steer.Count - b.Steer.Count);
        // Where each section switches, along the strip (metres), compared switch by switch.
        var ea = Edges(a);
        var eb = Edges(b);
        var edges = new Dictionary<(int, int), double>();
        foreach (var k in ea.Keys.Union(eb.Keys))
        {
            var la = ea.TryGetValue(k, out var x) ? x : new List<double>();
            var lb = eb.TryGetValue(k, out var y) ? y : new List<double>();
            if (la.Count != lb.Count) { edge = Math.Max(edge, 99); edges[(k, -1)] = 99; continue; }
            for (int i = 0; i < la.Count; i++)
            {
                double dd = Math.Abs(la[i] - lb[i]);
                edge = Math.Max(edge, dd);
                edges[(k, i)] = dd;
            }
        }
        var pgn = new List<string>();
        foreach (var p in a.ConfigPgns.Keys.Union(b.ConfigPgns.Keys).OrderByDescending(k => k))
        {
            a.ConfigPgns.TryGetValue(p, out var ba);
            b.ConfigPgns.TryGetValue(p, out var bb);
            if (ba == null || bb == null)
            {
                if (ba != bb) pgn.Add(F($"PGN {p} {(ba == null ? "first sent" : "no longer sent")}"));
                continue;
            }
            var bytes = new List<string>();
            for (int i = 5; i < Math.Min(ba.Length, bb.Length) - 1; i++)
                if (ba[i] != bb[i]) bytes.Add(F($"[{i}] {ba[i]}→{bb[i]}"));
            if (bytes.Count > 0) pgn.Add(F($"PGN {p} ") + string.Join(" ", bytes.Take(4)) + (bytes.Count > 4 ? " …" : ""));
        }
        return new TraceDiff(steer, sent, guid, status, eng, a.Disengaged != b.Disengaged, pass, sect, edge, mach,
            Math.Abs(a.Held - b.Held), pgn) { EdgeDiffs = edges };
    }

    /// <summary>The largest difference of each kind over several pairs of runs (the noise of
    /// three base runs is the largest of their three pairs).</summary>
    public static TraceDiff Max(params TraceDiff[] ds)
    {
        var edges = new Dictionary<(int, int), double>();
        foreach (var d in ds)
            foreach (var (k, v) in d.EdgeDiffs)
                edges[k] = Math.Max(edges.GetValueOrDefault(k), v);
        return new TraceDiff(ds.Max(d => d.SteerMax), ds.Max(d => d.SentMax), ds.Max(d => d.GuidanceCount),
            ds.Max(d => d.StatusFixes), ds.Max(d => d.EngagedFixes), ds.Any(d => d.DisengageDiffers), ds.Max(d => d.PassFixes),
            ds.Max(d => d.SectionFixes), ds.Max(d => d.SectionEdgeMax), ds.Max(d => d.MachineFixes), ds.Max(d => d.HeldDiff),
            ds.SelectMany(d => d.PgnChanges).Distinct().ToList()) { EdgeDiffs = edges };
    }

    /// <summary>Section number → where along the strip (tool centre N) its bit changed.</summary>
    internal static Dictionary<int, List<double>> EdgesOf(Trace t) => Edges(t);

    private static Dictionary<int, List<double>> Edges(Trace t)
    {
        var d = new Dictionary<int, List<double>>();
        for (int i = 1; i < t.Sections.Count && i < t.ToolN.Count; i++)
        {
            ulong ch = t.Sections[i] ^ t.Sections[i - 1];
            for (int s = 0; ch != 0 && s < 16; s++, ch >>= 1)
                if ((ch & 1) != 0)
                {
                    if (!d.TryGetValue(s, out var l)) d[s] = l = new List<double>();
                    l.Add(t.ToolN[i]);
                }
        }
        return d;
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Drives one probe on a field run: the machine's pose is scripted (open loop) for most
/// probes, so a difference in what AgOpenWeb sends is the setting's doing and not the machine
/// wandering somewhere else; the deadzone probe answers as a steer module. Everything goes
/// in through the receiver sentences and web commands, as on the tractor.
/// </summary>
public sealed class SweepDriver
{
    public readonly FieldRun Run;
    public int PaceMs = 12;              // > one 100 Hz control tick per fix
    /// <summary>A trailed implement (trailing, TBT): the section probes come up to each strip
    /// over 50 m in real time, so its hitch has settled behind the tractor before the strip.</summary>
    public bool Trailed;
    private const double Fix = FieldRun.FixSeconds;
    private int _lane;

    public SweepDriver(FieldRun run) => Run = run;

    private VehicleToolRig Rig => Run.Rig;
    private TruthMachine Truth => Run.Truth;

    private static readonly byte[] ConfigPgnIds = { 251, 252, 247, 238, 236, 235 };

    /// <summary>What the modules were last sent, PGN by PGN.</summary>
    public Dictionary<byte, byte[]> ConfigPgns()
    {
        var d = new Dictionary<byte, byte[]>();
        foreach (var p in ConfigPgnIds)
            if (Rig.Pgns.Last(p) is { } b) d[p] = b;
        return d;
    }

    /// <summary>One setting as the operator types it: AutoSteer settings then Send + Save, as
    /// on the AutoSteer panel; Vehicle &amp; Tool settings then Save. Waits past the 150 ms
    /// settle before the steer and machine PGNs go out. Returns what the host said back about
    /// the value (why it was refused, or what the app changed on its own), or "" when it said
    /// nothing.</summary>
    public async Task<string> SetAsync(string keyValue)
    {
        Rig.TakeEvents();
        await Rig.SendAsync("config.set", keyValue);
        string reply = string.Join("; ", Rig.TakeEvents().Where(e => e.Kind == RigEventKind.Failure).Select(e => e.Text));
        if (keyValue.StartsWith("autosteer.", StringComparison.Ordinal)) await Rig.SendAsync("autosteer.sendSave");
        // Vehicle & Tool: the operator saves the profile, which is also when the section
        // widths go out at once (otherwise 10 s after the last change).
        else if (keyValue.StartsWith("tool.", StringComparison.Ordinal) || keyValue.StartsWith("vehicle.", StringComparison.Ordinal))
            await Rig.SendAsync("profile.save");
        await Task.Delay(350);
        await Rig.DrainAsync();
        return reply;
    }

    /// <summary>What the store holds for a setting now, read from the Config frame as the
    /// panels read it (<c>section.field</c> → that DTO's property): "1"/"0" for a switch, the
    /// number otherwise. Null when the frame has no single number for the key (a button, a
    /// list entry, a name).</summary>
    public async Task<double?> StoredAsync(string key)
    {
        int dot = key.IndexOf('.');
        var projector = Rig.Server.Broadcaster?.Projector;
        if (dot <= 0 || projector == null) return null;
        var cfg = await Rig.ReadAsync(() => projector.BuildConfig());
        static object? Member(object? o, string name) =>
            o?.GetType().GetProperties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.GetValue(o);
        return Member(Member(cfg, key[..dot]), key[(dot + 1)..]) switch
        {
            bool b => b ? 1 : 0,
            int i => i,
            double d => d,
            _ => null,
        };
    }

    /// <summary>Whether the store kept a value set by <see cref="SetAsync"/>: null when it can't
    /// be told (no single number in the Config frame, or the value is not a number), else the
    /// value kept and whether it is the one sent (whole-number settings round it).</summary>
    public async Task<(bool Kept, double Stored)?> KeptAsync(string key, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sent)) return null;
        if (await StoredAsync(key) is not { } stored) return null;
        bool kept = Math.Abs(stored - sent) <= 1e-6 * Math.Max(1, Math.Abs(sent))
                    || stored == Math.Round(sent, MidpointRounding.AwayFromZero);
        return (kept, stored);
    }

    public async Task<Trace> RunAsync(Probe probe)
    {
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await Rig.SendAsync("autosteer.toggle");
        Rig.TakeEvents();
        var t = probe switch
        {
            Probe.Module => new Trace { ConfigPgns = ConfigPgns() },
            Probe.Sections or Probe.SectionsShifted or Probe.Switches => await SectionsAsync(probe),
            Probe.SectionsSlow => await SectionsSlowAsync(),
            _ => await DriveAsync(probe),
        };
        foreach (var e in Rig.TakeEvents())
            if (e.Kind is RigEventKind.Failure or RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang
                || (e.Kind == RigEventKind.Status && !e.Text.StartsWith("All sections", StringComparison.Ordinal)))
                t.Said.Add(e.ToString());
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await Rig.SendAsync("autosteer.toggle");
        foreach (var cmd in ProbeUndo(probe)) await Rig.SendAsync(cmd);
        if (FieldRun.Verbose) Console.WriteLine("[sweep] " + probe + ": " + t.Summary());
        return t;
    }

    private static IEnumerable<string> ProbeUndo(Probe p) => p switch
    {
        Probe.Nudge => new[] { "track.resetNudge" },
        Probe.RefNudge => new[] { "track.refNudgeUndo" },
        _ => Array.Empty<string>(),
    };

    // ------------------------------------------------------------------ AutoSteer probes

    private sealed record Pose(double E, double N, double Heading, double SpeedKmh);

    private async Task<Trace> DriveAsync(Probe probe)
    {
        var t = new Trace();
        double v = probe switch { Probe.Slow => 2, Probe.Fast => 20, _ => 8 } / 3.6;
        // The weave: 1.5 m either side of the first pass's line (half a working width right of
        // the AB line, which is that pass's edge), so every run steers to the same pass.
        double c0 = Truth.T.Width / 2;
        Pose Weave(double s)
        {
            double e = c0 + 1.5 * Math.Cos(2 * Math.PI * s / 16), de = -1.5 * 2 * Math.PI / 16 * Math.Sin(2 * Math.PI * s / 16);
            return new Pose(e, -60 + v * s, Math.Atan2(de, v), v * 3.6);
        }
        Pose Straight(double s) => new(c0 + 0.3, -60 + v * s, 0, v * 3.6);
        Pose Diagonal(double s) => new(c0 + 1.0 * s, -60 + v * s, Math.Atan2(1.0, v), v * 3.6);
        Pose Back(double s)
        {
            // 3 s forward at 6 km/h, 1 s stopping, then reversing at 4 km/h; the tractor faces north.
            double f = 6 / 3.6, r = 4 / 3.6;
            if (s < 3) return new Pose(c0 + 0.3, -60 + f * s, 0, 6);
            if (s < 4) return new Pose(c0 + 0.3, -60 + f * 3 + f * (s - 3) * (4 - s) / 2, 0, 6 * (4 - s));
            return new Pose(c0 + 0.3, -60 + f * 3.5 - r * (s - 4), 0, -4);
        }
        Func<double, Pose> path = probe switch
        {
            Probe.Reverse or Probe.ReverseImu => Back,
            Probe.Slow or Probe.Fast => Straight,
            Probe.FreeDrive => Diagonal,
            _ => Weave,
        };
        double seconds = probe switch { Probe.Reverse or Probe.ReverseImu => 10, Probe.Fast => 6, Probe.FreeDrive => 20, Probe.Slow => 200, _ => 16 };
        bool dual = probe is not (Probe.SingleImu or Probe.SingleNoImu or Probe.ReverseImu);
        bool imu = probe != Probe.SingleNoImu;
        double slope = probe == Probe.Slope ? 6 : 0;
        bool engage = probe != Probe.FreeDrive;

        // A gyro drifting (single antenna with an IMU): GPS pulls it back by the fusion weight.
        double drift = probe == Probe.SingleImu ? 0.5 : 0;
        // Pre-roll, AutoSteer off: heading, reverse detection and the pass settle first. From a
        // single antenna the heading comes from the travel: the tractor stands at the start and
        // the operator taps it (Reset Direction), so a run doesn't start from the heading the
        // last one left behind 35 m further on (two runs on the same settings differed).
        double preRoll = dual ? 2 : 4;
        if (!dual)
        {
            var p0 = path(-preRoll);
            for (int i = 0; i < 5; i++) { SendPose(p0 with { SpeedKmh = 0 }, dual, imu, slope, 0, 4, 0); await Task.Delay(PaceMs); }
            await Rig.SendAsync("heading.resetDirection");
        }
        // The free drive runs in real time: the pass shown is picked again at most every
        // 0.66 s of the wall clock (as AgOpenGPS), which faster than real time is a few metres
        // of jitter between two runs on the same settings, more than the look-ahead moves it.
        bool realTime = probe == Probe.FreeDrive;
        int pace = realTime ? (int)(Fix * 1000) : PaceMs;
        for (double s = -preRoll; s < 0; s += Fix)
        {
            SendPose(path(s), dual, imu, slope, 0, 4, drift * (s + preRoll));
            await Task.Delay(pace);
        }
        await Rig.WaitForVmAsync();
        if (engage)
        {
            await Rig.SendAsync("autosteer.toggle");
            if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged))
                t.Problem = "AutoSteer did not engage";
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool eventDone = false;
        double lastSent = 0;
        for (double s = 0; s < seconds; s += Fix)
        {
            if (probe == Probe.Slow && sw.ElapsedMilliseconds > 10_000) break;
            if (!eventDone && s >= 6)
            {
                eventDone = true;
                switch (probe)
                {
                    case Probe.Nudge: await Rig.SendAsync("track.nudgeRight"); break;
                    case Probe.RefNudge: await Rig.SendAsync("track.refNudgeBegin"); await Rig.SendAsync("track.refNudgeRight"); break;
                    case Probe.ManualTurn: await Rig.SendAsync("youturn.manualRight"); break;
                }
            }
            int fix = probe == Probe.RtkDrop && s >= 8 ? 5 : 4;
            double roll = slope == 0 ? 0 : 2 * Math.Sin(2 * Math.PI * s / 3);
            if (probe == Probe.Deadzone) await InjectSteerModuleAsync(t, lastSent, 0x01);
            SendPose(path(s), dual, imu, slope, roll, fix, drift * (s + preRoll));
            if (realTime)
            {
                long wait = (long)((s + Fix) * 1000) - sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
            }
            else await Task.Delay(PaceMs);
            await SentSinceAsync(254);
            lastSent = await SampleAsync(t);
        }
        return t;
    }

    /// <summary>Wait for two more of a PGN after the fix (the 100 Hz control tick sends 254 and
    /// 239): the newest one then carries what this fix's cycle set, not the one before.</summary>
    private async Task SentSinceAsync(byte pgn)
    {
        int n0 = _sentAtFix;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Rig.Pgns.Count(pgn) < n0 + 2 && sw.ElapsedMilliseconds < 200) await Task.Delay(1);
    }

    private int _sentAtFix;

    private void SendPose(Pose p, bool dual, bool imu, double slopeDeg, double extraRoll, int fix, double imuDriftDeg = 0)
    {
        Truth.Place(p.E, p.N, p.Heading);
        var v = Truth.V;
        var (de, dn) = Truth.Dir;
        var (re, rn) = Truth.Right;
        double roll = slopeDeg * Math.Sin(Math.PI / 2 - p.Heading) + extraRoll;
        double side = v.AntennaOffset + v.AntennaHeight * Math.Sin(roll * Math.PI / 180);
        double ae = p.E + de * v.AntennaPivot + re * side, an = p.N + dn * v.AntennaPivot + rn * side;
        var w = Run.Wgs(ae, an);
        double hdg = p.Heading * 180 / Math.PI + (dual ? 0 : Truth.Sys.ImuHeadingOffsetDeg + imuDriftDeg);
        Rig.SendSentence(VehicleToolRig.Sentence(dual, imu, w.Latitude, w.Longitude, hdg, roll, Math.Abs(p.SpeedKmh), fix));
        _sentAtFix = Rig.Pgns.Count(254);
    }

    /// <summary>Read what this fix produced: the cycle result and the newest PGN 254 / 239.</summary>
    private async Task<double> SampleAsync(Trace t)
    {
        var c = Rig.LastCycle;
        double steer = c?.Guidance is { HasGuidance: true } g ? g.SteerAngle : double.NaN;
        t.Steer.Add(steer);
        bool engaged = c?.IsAutoSteerEngaged == true;
        t.Engaged.Add(engaged);
        if (c?.DisengageReason is { } why && t.Disengaged == null) t.Disengaged = why;
        var p254 = Rig.Pgns.Last(254);
        double sent = p254 is { Length: >= 10 } ? (short)(p254[8] | (p254[9] << 8)) / 100.0 : 0;
        t.SentAngle.Add(sent);
        t.Status.Add(p254 is { Length: >= 8 } ? p254[7] : -1);
        if (engaged && double.IsFinite(steer) && Math.Abs(sent - steer) > 0.02) t.Held++;
        var p239 = Rig.Pgns.Last(239);
        t.Sections.Add(p239 is { Length: >= 13 } ? (ulong)(p239[11] | (p239[12] << 8)) : 0);
        t.Machine.Add(p239 is { Length: >= 13 } ? p239[5] | (p239[7] << 8) | (p239[8] << 16) | (p239[9] << 24) : 0);
        t.Pass.Add(await Rig.ReadAsync(() => Rig.State.Guidance.HowManyPathsAway));
        t.ToolN.Add(Truth.ToolCentre.N);
        return sent;
    }

    /// <summary>PGN 253 as a steer module sends it (the wheel angle, no IMU, the switches), to
    /// port 9999 on the loopback. One that did not arrive is the trace's problem.</summary>
    private async Task InjectSteerModuleAsync(Trace t, double wheelDeg, byte switches)
    {
        short a = (short)Math.Round(wheelDeg * 100);
        var m = new byte[] { 0x80, 0x81, 0x7E, 253, 8, (byte)(a & 0xFF), (byte)((a >> 8) & 0xFF), 0x0F, 0x27, 0xB8, 0x22, switches, 0, 0 };
        int sum = 0;
        for (int i = 2; i < m.Length - 1; i++) sum += m[i];
        m[^1] = (byte)sum;
        if (!await Rig.Pgns.InjectAsync(m) && !t.Problem.Contains("PGN 253", StringComparison.Ordinal))
            t.Problem += (t.Problem.Length > 0 ? "; " : "") + "the steer module's PGN 253 did not arrive on port 9999";
    }

    // ------------------------------------------------------------------ section probes

    private async Task SetMasterAsync(bool on)
    {
        if (await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn) != on) await Rig.SendAsync("section.master");
    }

    /// <summary>A strip of its own for every run (lanes 20 m apart, east of the AB line), so no
    /// run drives over another one's coverage.</summary>
    private double NextLane() => 40 + 20 * _lane++;

    /// <summary>A strip worked from <paramref name="fromN"/> to <paramref name="toN"/>. The
    /// tractor stands at each end while the master switch is pressed, so the strip starts and
    /// ends where the tractor stopped (the sections go off below the slow-speed cut-off), not
    /// wherever it was extrapolated to while the command was handled: that kept two runs on
    /// the same settings metres apart.</summary>
    private async Task StripAsync(double e, double fromN, double toN)
    {
        double v = 8 / 3.6, dir = toN > fromN ? 1 : -1, h = dir > 0 ? 0 : Math.PI;
        double window = await RealTimeWindowAsync(v);
        await SetMasterAsync(false);
        await RollToAsync(e, fromN, h);
        await SetMasterAsync(true);
        var pace = new Pacer();
        for (double n = fromN; dir * (n - toN) < 0; n += dir * v * Fix)
        {
            SendPose(new Pose(e, n, h, 8), true, true, 0, 0, 4);
            await pace.NextAsync(NearEnd(n, window, fromN, toN), PaceMs);
        }
        await StandAsync(e, toN, h);
        await SetMasterAsync(false);
    }

    /// <summary>Within <paramref name="window"/> metres of a strip's end: there the fixes go in
    /// real time. The sections wait their look-ahead and turn-off times on the wall clock
    /// (100 Hz control ticks), so faster than real time a 1 s look-ahead starts the paint
    /// metres late, and two runs on the same settings switched metres apart.</summary>
    private static bool NearEnd(double n, double window, params double[] ends) => ends.Any(x => Math.Abs(n - x) < window);

    /// <summary>How far either side of a strip's end the fixes go in real time: the distance
    /// the look-ahead on, look-ahead off and turn-off delay cover at <paramref name="v"/> m/s,
    /// plus 5 m, and at least 15 m. The control loop works from the pose estimator's speed,
    /// which still carries the faster-than-real-time stretch before the window: the 5 m let
    /// it settle to the real speed before the first switch.</summary>
    private async Task<double> RealTimeWindowAsync(double v)
    {
        var (on, off, delay) = await Rig.ReadAsync(() => (Rig.Store.Tool.LookAheadOnSetting, Rig.Store.Tool.LookAheadOffSetting, Rig.Store.Tool.TurnOffDelay));
        return Math.Max(15, v * (Math.Max(0, on) + Math.Max(0, off) + Math.Max(0, delay)) + 5);
    }

    /// <summary>One fix per 100 ms of the wall clock (real time) or <c>fastMs</c> after the
    /// last one.</summary>
    private sealed class Pacer
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private long _last;

        public async Task NextAsync(bool realTime, int fastMs)
        {
            if (realTime)
            {
                long wait = _last + (long)(Fix * 1000) - _sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
            }
            else await Task.Delay(fastMs);
            _last = _sw.ElapsedMilliseconds;
        }
    }

    /// <summary>15 m up to the point at 8 km/h, then standing there. With a trailed implement
    /// (<see cref="Trailed"/>) 50 m in real time, so its hitch angle settles on the way.</summary>
    private async Task RollToAsync(double e, double n0, double h)
    {
        double v = 8 / 3.6, dir = Math.Abs(h) < 1 ? 1 : -1, leadIn = Trailed ? 50 : 15;
        var pace = new Pacer();
        for (double n = n0 - dir * leadIn; dir * (n - n0) < 0; n += dir * v * Fix)
        {
            SendPose(new Pose(e, n, h, 8), true, true, 0, 0, 4);
            await pace.NextAsync(Trailed, PaceMs);
        }
        await StandAsync(e, n0, h);
    }

    /// <summary>Half a second standing (speed 0): the sections settle off.</summary>
    private async Task StandAsync(double e, double n, double h)
    {
        for (int i = 0; i < 5; i++) { SendPose(new Pose(e, n, h, 0), true, true, 0, 0, 4); await Task.Delay(PaceMs); }
        await Rig.WaitForVmAsync();
        await Task.Delay(30);
    }

    private async Task<Trace> SectionsAsync(Probe probe)
    {
        var t = new Trace();
        double lane = NextLane(), v = 8 / 3.6;
        // The strip: worked northbound from N = -30 to 70 with the sections on automatic.
        var cov = Rig.Services.GetRequiredService<ICoverageMapService>();
        double a0 = cov.TotalWorkedArea;
        await StripAsync(lane, -30, 70);
        t.StripArea = cov.TotalWorkedArea - a0;
        t.StripExpected = await Rig.ReadAsync(() => Rig.Store.ActualToolWidth) * 100;
        double back = lane, from = 110, to = -50;
        var ends = new List<double> { -30, 70 };
        if (probe == Probe.SectionsShifted)
        {
            // 0.2 m to the side: the edge section is 80 % over the strip. Its first 50 m are
            // worked once more on that line, so the way back finds full coverage, then 80 %:
            // whether the edge section comes back on there is the On threshold (Min coverage).
            back = lane + 0.2;
            await StripAsync(back, -30, 20);
            from = -50; to = 90;
            ends.Add(20);
        }
        double dir = to > from ? 1 : -1, h = dir > 0 ? 0 : Math.PI;
        double window = await RealTimeWindowAsync(v);
        await SetMasterAsync(false);
        await RollToAsync(back, from, h);
        await SetMasterAsync(true);
        var pace = new Pacer();
        for (double n = from; dir * (n - to) < 0; n += dir * v * Fix)
        {
            if (probe == Probe.Switches) await InjectSteerModuleAsync(t, 0, n > 30 ? (byte)0x01 : (byte)0x02);
            SendPose(new Pose(back, n, h, 8), true, true, 0, 0, 4);
            await pace.NextAsync(NearEnd(n, window, ends.ToArray()), PaceMs);
            await SentSinceAsync(254);
            await SampleAsync(t);
        }
        await SetMasterAsync(false);
        return NoPass(t);
    }

    /// <summary>The pass shown on a section probe is the lane's (each run has its own), not a
    /// setting's: left out of the comparison.</summary>
    private static Trace NoPass(Trace t)
    {
        for (int i = 0; i < t.Pass.Count; i++) t.Pass[i] = 0;
        return t;
    }

    private async Task<Trace> SectionsSlowAsync()
    {
        var t = new Trace();
        double lane = NextLane(), v = 2 / 3.6;
        await SetMasterAsync(false);
        for (double n = -35; n < -30; n += v * Fix) { SendPose(new Pose(lane, n, 0, 2), true, true, 0, 0, 4); await Task.Delay(PaceMs); }
        await Rig.WaitForVmAsync();
        await SetMasterAsync(true);
        for (double n = -30; n < -15; n += v * Fix)
        {
            SendPose(new Pose(lane, n, 0, 2), true, true, 0, 0, 4);
            await Task.Delay(PaceMs);
            await SentSinceAsync(254);
            await SampleAsync(t);
        }
        await SetMasterAsync(false);
        return NoPass(t);
    }
}
