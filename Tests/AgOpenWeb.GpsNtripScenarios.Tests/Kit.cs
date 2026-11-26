// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Services;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>What the scenarios share: data folders, the standard start of a day, common checks.</summary>
public static class Kit
{
    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static bool Want(string? only, string code) =>
        only == null || only.Split(',').Select(o => o.Trim()).Any(o =>
            o.Length == 1 ? code.StartsWith(o, StringComparison.OrdinalIgnoreCase) : code.Equals(o, StringComparison.OrdinalIgnoreCase));

    public static string NewDataRoot(string code) =>
        Path.Combine(Path.GetTempPath(), "aow-gpsntrip-" + code + "-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Profile saved, corrections flowing, receiver RTK fixed, a pass started with
    /// sections on Auto and AutoSteer on. Null when the day started, else why not.</summary>
    public static async Task<string?> StartDayAsync(Scenario sc, GpsSession s, bool autoSteer = true)
    {
        await s.SaveGoodProfileAsync();
        double streaming = await s.WaitForAsync(() => s.NtripStatus.State == AgOpenWeb.Services.NtripState.Streaming, 20);
        if (double.IsNaN(streaming)) return "NTRIP did not start streaming: " + s.NtripStatus.Message;
        double fix = await s.WaitForAsync(() => s.Rx.Quality == 4, 40);
        if (double.IsNaN(fix)) return F($"the receiver did not reach RTK fixed (age {s.Rx.CorrectionAge:0.0} s, base position {(s.Rx.HaveStation ? "yes" : "no")})");
        sc.Step(F($"profile saved; corrections arriving after {streaming:0.0} s, receiver RTK fixed {fix:0.0} s later"));
        string? pass = await s.StartPassAsync("Field " + sc.Row.Code, autoSteer);
        if (pass != null) return pass;
        sc.Step(autoSteer ? "pass started: sections on Auto, AutoSteer on" : "pass started: sections on Auto");
        return null;
    }

    /// <summary>The host loop and the GPS cycle kept going over a stretch (no freeze).</summary>
    public static void CheckNoFreeze(Scenario sc, GpsSession s, double from, double to, string what)
    {
        var samples = s.Between(from, to);
        if (samples.Count < 2) { sc.Harness("no samples for " + what); return; }
        double worstLoop = samples.Max(x => x.LoopMs);
        // GPS cycle stalls: the longest stretch with the receiver sending and no new cycle.
        double longest = 0, since = double.NaN;
        int lastCycles = samples[0].Cycles;
        foreach (var x in samples)
        {
            if (x.Cycles != lastCycles || !x.RxSending) { since = double.NaN; lastCycles = x.Cycles; continue; }
            if (double.IsNaN(since)) since = x.T;
            longest = Math.Max(longest, x.T - since);
        }
        if (worstLoop > 1000)
            sc.Problem(Severity.Bug, "SCREEN-FROZE", F($"{what}: the host loop (the screen and every command) did not answer for {worstLoop / 1000:0.0} s"));
        if (longest >= 1.0)
            sc.Problem(Severity.Safety, "GPS-CYCLE-STALLED", F($"{what}: the GPS cycle stopped for {longest:0.0} s while the receiver kept sending (the #169 freeze)"));
        if (worstLoop <= 1000 && longest < 1.0)
            sc.Ok(F($"{what}: nothing froze (host loop answered within {worstLoop:0} ms; GPS cycle never stopped for 1 s)"));
    }

    /// <summary>What reached the receiver is usable on an AiO v26: whole messages, no datagram over
    /// 512 bytes, none closer than 10 ms.</summary>
    public static void CheckWire(Scenario sc, GpsSession s)
    {
        var rx = s.Rx;
        if (rx.ChecksumFailures > 0 || rx.Oversize > 0 || rx.CloseGaps > 0)
            sc.Problem(Severity.Bug, "RTCM-DAMAGED", F($"the receiver got {rx.ChecksumFailures} message(s) with a bad checksum, {rx.Oversize} datagram(s) over 512 bytes and {rx.CloseGaps} back to back (under 5 ms)"));
        else if (rx.Datagrams > 0)
            sc.Ok(F($"every correction reached the receiver whole ({rx.Messages} messages in {rx.Datagrams} datagrams; smallest gap {rx.MinGapMs:0.0} ms)"));
    }

    public static string Describe(IEnumerable<string> told, int max = 8)
    {
        var list = told.Distinct().Take(max).ToList();
        return list.Count == 0 ? "nothing" : string.Join("; ", list);
    }

    /// <summary>
    /// What an outage did, from the internet (or the caster) going away at <paramref name="down"/>
    /// to its return at <paramref name="up"/> and <paramref name="after"/> seconds beyond: when the
    /// receiver lost RTK and got it back, when corrections reached it again, what the screen said,
    /// and whether AutoSteer and the sections stayed as AgOpenGPS would keep them.
    /// </summary>
    public static Outage Measure(GpsSession s, double down, double up, double end)
    {
        var o = new Outage { Down = down, Up = up };
        o.RxFloatAt = s.FirstAt(down, x => x.RxQuality != 4) - down;
        // Back means fresh again after the corrections went stale, and RTK fixed again after it
        // was lost; neither going away counts as back at once.
        double stale = s.FirstAt(down, x => double.IsNaN(x.RxAge) || x.RxAge > 2.5, end);
        o.RxFreshAgainAt = double.IsNaN(stale) ? 0 : s.FirstAt(Math.Max(up, stale), x => !double.IsNaN(x.RxAge) && x.RxAge < 2.5) - up;
        double unfixed = s.FirstAt(down, x => x.RxQuality != 4, end);
        o.RxFixedAgainAt = double.IsNaN(unfixed) ? 0 : s.FirstAt(Math.Max(up, unfixed), x => x.RxQuality == 4) - up;
        o.NtripLeftStreamingAt = s.FirstAt(down, x => x.Ntrip != NtripState.Streaming, end) - down;
        o.StreamingAgainAt = s.FirstAt(Math.Max(up, double.IsNaN(o.NtripLeftStreamingAt) ? up : down + o.NtripLeftStreamingAt), x => x.Ntrip == NtripState.Streaming, end) - up;
        var w = s.Between(down, end);
        o.AutoSteerOffAt = s.FirstAt(down, x => !x.AutoSteer, end) - down;
        o.SectionsOffAt = s.FirstAt(down, x => x.Sections == 0, end) - down;
        o.MaxAge = w.Count == 0 ? 0 : w.Max(x => double.IsNaN(x.AowAge) ? 0 : x.AowAge);
        o.ScreenFixLag = double.NaN;
        double rxLost = s.FirstAt(down, x => x.RxQuality != 4, end);
        if (!double.IsNaN(rxLost)) o.ScreenFixLag = s.FirstAt(rxLost, x => x.AowFix != 4, end) - rxLost;
        o.RtkLostSound = s.EventsBetween(down, end, RigEventKind.Sound).Any(e => e.Text == "RtkLost");
        o.RtkBackSound = s.EventsBetween(down, end, RigEventKind.Sound).Any(e => e.Text == "RtkRecovered");
        o.Told = s.Told(down, end);
        o.States = string.Join(" → ", Compress(w.Select(x => x.Ntrip.ToString())));
        o.RxStates = string.Join(" → ", Compress(w.Select(x => EmulatedReceiver.Name(x.RxQuality))));
        o.CasterConnections = s.Caster.Accepted;
        return o;
    }

    /// <summary>While the receiver reports no correction age (single: the field is empty), the
    /// screen should not show one: neither a fresh-looking 0.0 nor the age the corrections had
    /// when they stopped. Judged over stretches of at least 2 s.</summary>
    public static void CheckAgeShown(Scenario sc, GpsSession s, double from, double to, string what)
    {
        var none = s.Between(from, to).Where(x => x.RxQuality == 1 && x.RxSending).ToList();
        if (none.Count < 20) return;
        var shown = none.Where(x => !double.IsNaN(x.AowAge)).ToList();
        if (shown.Count < 20)
        {
            sc.Ok(F($"{what}: while the receiver reported no correction age, the screen showed none (\"Age —\")"));
            return;
        }
        var values = shown.Select(x => x.AowAge).Distinct().OrderBy(v => v).ToList();
        string example = values.Count == 1 ? F($"{values[0]:0.0}") : F($"{values[0]:0.0} to {values[^1]:0.0}");
        sc.Problem(Severity.Inconsistency, "AGE-SHOWN-WITHOUT-CORRECTIONS",
            F($"{what}: for {shown.Count * 0.1:0} s the receiver was single and sent no correction age, yet the status bar read \"Age {example}\"") +
            (values.All(v => v == 0)
                ? " (a fresh-looking 0.0: the receiver never had corrections)"
                : " (the age the corrections had when they stopped: an empty age field leaves the last one in place)"));
    }

    public static IEnumerable<string> Compress(IEnumerable<string> seq)
    {
        string? last = null;
        foreach (var x in seq) { if (x != last) yield return x; last = x; }
    }

    public sealed class Outage
    {
        public double Down, Up;
        public double RxFloatAt, RxFreshAgainAt, RxFixedAgainAt, NtripLeftStreamingAt, StreamingAgainAt;
        public double AutoSteerOffAt, SectionsOffAt, MaxAge, ScreenFixLag;
        public bool RtkLostSound, RtkBackSound;
        public List<string> Told = new();
        public string States = "", RxStates = "";
        public int CasterConnections;

        public string Summary() => F($"NTRIP {States}; receiver {RxStates}; RTK lost {S(RxFloatAt)} after the drop; corrections back at the receiver {S(RxFreshAgainAt)} after the return, RTK fixed {S(RxFixedAgainAt)} after it; screen age up to {MaxAge:0.0} s; AutoSteer off {S(AutoSteerOffAt)}, sections off {S(SectionsOffAt)}; sounds: RTK lost {(RtkLostSound ? "yes" : "no")}, back {(RtkBackSound ? "yes" : "no")}");

        public static string S(double v) => double.IsNaN(v) ? "never" : F($"{v:0.0} s");
    }

    /// <summary>The bytes that reached the receiver in the first seconds after the link returned
    /// (the #169 burst) and whether the newest epoch and the base position came first.</summary>
    public static void CheckBurst(Scenario sc, GpsSession s, double upAtSession, string what)
    {
        // The receiver's clock and the session's started together (within ms): use the session's.
        double rxUp = upAtSession - (s.Now - s.Rx.Now);
        var (d3, b3) = s.Rx.Window(rxUp, rxUp + 3);
        var after = s.Rx.MessageLog.Where(m => m.At >= rxUp && m.At < rxUp + 15).ToList();
        var firstObs = after.FirstOrDefault(m => Rtcm.IsObservation(m.Type));
        bool station = after.Any(m => Rtcm.IsStation(m.Type));
        int staleObs = after.Count(m => Rtcm.IsObservation(m.Type) && m.EpochAgeS > 3);
        sc.Step(F($"{what}: in the 3 s after the return the receiver got {d3} datagrams, {b3} bytes; first observation {(firstObs.Type == 0 ? "none" : F($"{firstObs.EpochAgeS:0.0} s old"))}; {staleObs} observation message(s) over 3 s old in the next 15 s; base position {(station ? "yes" : "no")}"));
        // An AiO's serial link to the receiver carries about 46 KB/s; AgIO's pacing 10 KB/s.
        if (b3 > 3 * 46_000)
            sc.Problem(Severity.Bug, "RTCM-BURST", F($"{what}: {b3} bytes reached the board in 3 s, more than its serial link to the receiver carries (about 46 KB/s): the #169 burst"));
        else if (staleObs > 8)
            sc.Problem(Severity.Improvement, "RTCM-STALE-AFTER-PAUSE", F($"{what}: {staleObs} observation messages over 3 s old went to the receiver after the pause, ahead of or among fresh ones"));
        else
            sc.Ok(F($"{what}: no burst (at most {b3} bytes in 3 s) and no stale backlog sent"));
    }

    /// <summary>Runs one scenario on a fresh AgOpenWeb: the body acts and judges; afterwards the
    /// logs are kept and any error AgOpenWeb logged becomes a finding.</summary>
    public static async Task<ScenarioRow> Run(string who, string code, string title, string story,
        Func<Scenario, GpsSession, Task> body, GpsSession.Options? options = null)
    {
        var sc = new Scenario(who, code, title, story);
        GpsSession? s = null;
        try
        {
            s = await GpsSession.StartAsync(NewDataRoot(code), options);
            await body(sc, s);
        }
        catch (Exception ex)
        {
            sc.Harness("the scenario threw: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
        }
        finally
        {
            if (s != null)
            {
                sc.Snapshot(s);
                if (Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_DUMP") is { Length: > 0 } dump)
                {
                    try { Directory.CreateDirectory(dump); s.WriteCsv(Path.Combine(dump, code + ".csv")); } catch { }
                }
                var errors = s.Events.Where(e => e.Kind is RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang or RigEventKind.OffLoop)
                    .Select(e => e.Kind + ": " + e.Text.Split('\n')[0]).Distinct().Take(5).ToList();
                foreach (var e in errors) sc.Problem(Severity.Bug, "APP-ERROR", e);
                await s.DisposeAsync();
            }
        }
        return sc.Row;
    }
}
