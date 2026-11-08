// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>What happens in the day besides driving: refills, the phone in a pocket, the
/// tablet's Wi-Fi, lunch, and the look at the process every five minutes.</summary>
public sealed partial class Day
{
    private bool _inEvent, _phoneDone, _tabletDone, _tablet2Done, _lunchDone;
    /// <summary>The day's events are set for an 8-hour day; a shorter day (a quick run) has them
    /// at the same share of the day, shorter in proportion.</summary>
    private static double Scale => Hours / 8.0;
    private DateTime At(double hoursIntoDay) => DayStart.AddHours(hoursIntoDay * Scale);
    private static double Minutes(double m) => m * 60 * Scale;
    private static TimeSpan DayMinutes(double m) => TimeSpan.FromSeconds(Minutes(m));
    private double RefillEveryS => Minutes(60);

    /// <summary>The tablet's longest wait for a Tick while the phone was in the pocket (and the
    /// tablet itself reading), ms.</summary>
    public double PhoneStallMaxGapMs;
    public DateTime? PhoneStalledAt, PhoneWokeAt;
    /// <summary>When the tablet first waited over a second for a map update after the phone went
    /// into the pocket, and how long after it in wall time (the host's socket buffers filling).</summary>
    public DateTime? MapFrozeAt;
    public double MapFrozeAfterWallS = double.NaN;
    /// <summary>The tablet went silent with AutoSteer on: how long until AutoSteer was disengaged
    /// (ms of wall time, NaN = not within the wait) and the reason given. <see cref="Deadman"/>
    /// with the phone reading, <see cref="DeadmanPhoneAway"/> with the phone in the pocket.</summary>
    public (DateTime At, double OffMs, string? Reason)? Deadman, DeadmanPhoneAway;
    /// <summary>The phone's stall had stopped the tablet's map (the broadcast loop stuck) when
    /// the tablet went silent the second time: else that drop did not test the stuck loop.</summary>
    public bool DeadmanPhoneAwayLoopStuck;
    /// <summary>Why a deadman test was not made (null: it was): the tablet did not hold control
    /// when its Wi-Fi was to drop, so there was nothing for the deadman to revoke. A pause of the
    /// process longer than the deadman (a heap dump suspends it) revokes the tablet; it takes the
    /// free seat back as the web client does, but a test is only a test when it holds control.</summary>
    public string? DeadmanNotTested, DeadmanPhoneAwayNotTested;
    /// <summary>While the deadman was waited for with the phone reading, the phone's longest wait
    /// for a map update, ms: over a second, the broadcast loop (where the deadman's sweep runs)
    /// was stuck on the silent tablet's own send, its socket buffers full.</summary>
    public double DeadmanPhoneMaxGapMs;
    public ProcessState? LunchStart, LunchEnd;
    public DateTime LunchStartClock, LunchEndClock;
    public double LunchHeapAtStartMB;

    private async Task DayEventsAsync()
    {
        var now = Now;
        bool driving = _job != null && !Parked && !InTurn && Rig.LastCycle is { IsAutoSteerEngaged: true };
        bool phoneInPocket = PhoneStalledAt != null && PhoneWokeAt == null;
        // Sockets fill in wall time, not in the day's time: the phone stays in the pocket at least
        // a minute of wall time whatever the pace, and the tablet's second Wi-Fi drop waits until
        // the phone's stall is felt (the tablet waiting 3 s for a map update: 20 to 55 s of wall
        // time on the loopback, by how fast the frames fill the phone's socket buffers), or 3
        // minutes of wall time if it never is (the report then says the case was not tested).
        double stalledWallS = WallSeconds - _phoneStalledWall;

        // 09:00: the tablet's Wi-Fi drops while the phone reads: the deadman on its own.
        if (!_tabletDone && driving && now >= At(2.0) && Phone.Connected && Phone.Reading)
            await TabletSilentAsync(phoneAway: false);
        // 10:00: the phone goes into a pocket with the page open.
        else if (PhoneStalledAt == null && !_phoneDone && (_tabletDone || now >= At(4.0)) && now >= At(3.0) && Phone.Connected)
            PhonePocket();
        // Then the tablet's Wi-Fi drops again, the phone still in the pocket.
        else if (phoneInPocket && !_tablet2Done && driving && now >= At(3.0) + DayMinutes(5) && (PhoneStallMaxGapMs > 3000 || stalledWallS >= 180))
            await TabletSilentAsync(phoneAway: true);
        else if (phoneInPocket && (_tablet2Done || now >= At(4.0)) && now >= At(3.0) + DayMinutes(20) && stalledWallS >= 60)
            PhoneWake();
        else if (!_lunchDone && driving && now >= At(5.5) && !phoneInPocket)
            await LunchAsync();
        else if (driving && _drivenSinceRefillS >= RefillEveryS)
            await RefillAsync();
    }

    /// <summary>Each fix while the phone is in the pocket: how long the tablet, reading, has been
    /// waiting for a map update.</summary>
    private void WatchPhoneStall()
    {
        if (Phone is not { } ph || ph.Reading || PhoneStalledAt == null) return;
        double g = Tablet.SinceLastTickMs;
        if (!double.IsFinite(g)) return;
        PhoneStallMaxGapMs = Math.Max(PhoneStallMaxGapMs, g);
        if (g > 1000 && MapFrozeAt == null)
        {
            MapFrozeAt = Now;
            MapFrozeAfterWallS = WallSeconds - _phoneStalledWall - g / 1000;
            Event("MAP-FROZEN", F($"the tablet has had no map update for {g / 1000:0.0} s ({Math.Max(0, MapFrozeAfterWallS):0} s of wall time after the phone went into the pocket)"));
        }
    }

    // ------------------------------------------------------------------ the phone

    private double _phoneStalledWall;

    private void PhonePocket()
    {
        PhoneStalledAt = Now;
        _phoneStalledWall = WallSeconds;
        PhoneStallMaxGapMs = 0;
        Phone.Stall();
        Event("PHONE-POCKET", "the helper puts the phone in a pocket with the page open: it stops reading, the connection stays open");
    }

    private void PhoneWake()
    {
        PhoneWokeAt = Now;
        _phoneDone = true;
        Phone.Resume();
        Event("PHONE-WAKES", F($"the phone is taken out and reads again; while it was in the pocket the tablet's longest wait for a map update was {PhoneStallMaxGapMs / 1000:0.0} s"));
    }

    private async Task PhoneOffAsync(string why)
    {
        if (!Phone.Connected) return;
        await Phone.CloseAsync();
        Event("PHONE-OFF", why);
    }

    // ------------------------------------------------------------------ the tablet's Wi-Fi

    /// <summary>The tablet's Wi-Fi drops without a word while AutoSteer drives: its connection
    /// stays open on the host's side, nothing more is read or sent. The host's deadman should
    /// see the presence heartbeat stop and disengage AutoSteer within 1.5 s. Once with the phone
    /// reading, once with the phone in the pocket.</summary>
    private async Task TabletSilentAsync(bool phoneAway)
    {
        if (phoneAway) { _tablet2Done = true; DeadmanPhoneAwayLoopStuck = PhoneStallMaxGapMs > 3000; }
        else _tabletDone = true;
        string n = phoneAway ? "-2" : "";
        _inEvent = true;
        try
        {
            // The deadman revokes the holder of control: the tablet must hold it. A seat freed by
            // a pause of the process is claimed back within a frame or two; give it two seconds.
            var hold = Stopwatch.StartNew();
            while (!Tablet.HoldsControl && hold.Elapsed.TotalSeconds < 2) await StepAsync(false);
            if (!Tablet.HoldsControl)
            {
                string why = F($"the tablet did not hold control (connected: {Tablet.Connected}, reading: {Tablet.Reading}, seat claims so far: {Tablet.SeatClaims})");
                if (phoneAway) DeadmanPhoneAwayNotTested = why; else DeadmanNotTested = why;
                Event("DEADMAN" + n + "-NOT-TESTED", "the tablet's Wi-Fi drop was not made: " + why + ", so the deadman had nothing to revoke");
                return;
            }

            // The deadman is a wall-clock watch: the day runs in real time while it is waited
            // for, so the tractor goes as far as it would on the field.
            RealTime = true;
            Tablet.Stall();
            var sw = Stopwatch.StartNew();
            Event("TABLET-SILENT" + n, "the tablet's Wi-Fi drops without a word (its connection open, nothing read or sent) with AutoSteer driving"
                + (phoneAway ? ", the phone still in the pocket" : ", the phone reading"));
            var at = Now;
            double off = double.NaN; string? reason = null;
            while (sw.Elapsed.TotalSeconds < 8)
            {
                await StepAsync(false);
                if (!phoneAway && Phone.SinceLastTickMs is var pg && double.IsFinite(pg))
                    DeadmanPhoneMaxGapMs = Math.Max(DeadmanPhoneMaxGapMs, pg);
                var c = Rig.LastCycle;
                if (c != null && !c.IsAutoSteerEngaged) { off = sw.Elapsed.TotalMilliseconds; reason = c.DisengageReason; break; }
            }
            RealTime = false;
            if (phoneAway) DeadmanPhoneAway = (at, off, reason); else Deadman = (at, off, reason);
            Event("DEADMAN" + n, double.IsNaN(off)
                ? "AutoSteer was still engaged 8 s (wall clock) after the tablet went silent"
                : F($"AutoSteer disengaged {off / 1000:0.0} s (wall clock) after the tablet went silent: {reason ?? "no reason given"}"));

            // With a dead screen the operator stops the tractor and waits for the tablet.
            await ParkAsync();
            for (int i = 0; i < Minutes(2) / FixSeconds; i++) await StepAsync(false);
            // The Wi-Fi is back: the tablet's old connection is reset, the page reconnects.
            await Tablet.Abort();
            await Tablet.ConnectAsync();
            if (phoneAway) Tablet.Seeds[^1].Note = "the phone in the pocket";
            await WaitSeedAsync(Tablet);
            var seed = Tablet.Seeds.LastOrDefault();
            Event("TABLET-BACK" + n, seed != null && Tablet.Ticked == Tablet.Connects
                ? F($"the tablet's Wi-Fi is back: it reconnects, seed {seed.Bytes / 1024.0:0} KB, first map update after {seed.FirstTickMs / 1000:0.0} s")
                : "the tablet's Wi-Fi is back: it reconnects but gets no map update within 10 s");
            await EngageAsync();
        }
        finally { _inEvent = false; RealTime = false; }
    }

    /// <summary>Wait, the day going on, until the browser has its first Tick after connecting
    /// (10 s of wall time at most).</summary>
    private async Task WaitSeedAsync(WebClient b)
    {
        var sw = Stopwatch.StartNew();
        while ((b.Ticked < b.Connects || b.Loading) && sw.Elapsed.TotalSeconds < (b.Loading ? 60 : 10)) await StepAsync(false);
    }

    /// <summary>Each fix while the phone loads its page on a slow Wi-Fi: how long the tablet,
    /// reading, has been waiting for a map update.</summary>
    private void WatchPhoneLoading()
    {
        if (!_phoneLoadWatch || Phone is not { } ph) return;
        if (!ph.Loading) { _phoneLoadWatch = false; return; }
        double g = Tablet.SinceLastTickMs;
        if (double.IsFinite(g)) PhoneLoadTabletMaxGapMs = Math.Max(PhoneLoadTabletMaxGapMs, g);
    }

    private bool _phoneLoadWatch;
    /// <summary>After lunch the phone loads the map over a slow Wi-Fi: its seed, and the tablet's
    /// longest wait for a map update meanwhile, ms.</summary>
    public WebClient.Seed? PhoneSlowSeed;
    public double PhoneLoadTabletMaxGapMs;
    public DateTime? PhoneSlowAt;
    /// <summary>The phone's Wi-Fi after lunch, bytes a second (20 Mbit/s: a phone a few metres
    /// from the tractor's access point, or a cab router's 2.4 GHz band in use).</summary>
    public const double SlowWifiBytesPerSecond = 2.5e6;

    // ------------------------------------------------------------------ lunch

    /// <summary>An hour parked with the field open: AgOpenWeb runs on, the GPS sends 10 fixes a
    /// second, NTRIP flows, the tablet sleeps and the phone is put away. Nothing is worked, so
    /// nothing should grow.</summary>
    private async Task LunchAsync()
    {
        _lunchDone = true;
        _inEvent = true;
        try
        {
            await ParkAsync();
            for (int i = 0; i < 50; i++) await StepAsync(false);
            await Tablet.CloseAsync();
            await PhoneOffAsync("lunch: the phone's page is closed");
            Event("LUNCH", "lunch: parked in the field with the job open, AutoSteer and sections off; the tablet sleeps (its page closed)");
            FileSizes["lunch-start"] = DiskUse.Files(Rig.DataRoot);
            LunchStartClock = Now;
            LunchStart = ProcessState.Take();
            ResetPace();
            for (int i = 0; i < Minutes(60) / FixSeconds; i++) await StepAsync(false);
            LunchEnd = ProcessState.Take();
            LunchEndClock = Now;
            FileSizes["lunch-end"] = DiskUse.Files(Rig.DataRoot);
            await Tablet.ConnectAsync();
            await WaitSeedAsync(Tablet);
            var ts = Tablet.Seeds.LastOrDefault();
            Event("AFTER-LUNCH", F($"back from lunch: the tablet reconnects (seed {(ts?.Bytes ?? 0) / 1024.0:0} KB, loaded in {(ts?.LoadedMs ?? double.NaN) / 1000:0.0} s, first map update after {(ts?.FirstTickMs ?? double.NaN) / 1000:0.0} s)"));
            // The helper opens the page again on the phone, out by the sprayer: a slower Wi-Fi.
            Phone.RateBytesPerSecond = SlowWifiBytesPerSecond;
            PhoneSlowAt = Now;
            PhoneLoadTabletMaxGapMs = 0;
            await Phone.ConnectAsync();
            _phoneLoadWatch = true;
            await WaitSeedAsync(Phone);
            _phoneLoadWatch = false;
            var ps = PhoneSlowSeed = Phone.Seeds.LastOrDefault();
            Phone.RateBytesPerSecond = 0;
            Event("PHONE-SLOW-WIFI", F($"the phone opens the page on a {SlowWifiBytesPerSecond * 8 / 1e6:0} Mbit/s Wi-Fi: seed {(ps?.Bytes ?? 0) / 1048576.0:0.0} MB loaded in {(ps?.LoadedMs ?? double.NaN) / 1000:0.0} s; meanwhile the tablet's longest wait for a map update was {PhoneLoadTabletMaxGapMs / 1000:0.0} s"));
            await EngageAsync();
        }
        finally { _inEvent = false; }
    }

    // ------------------------------------------------------------------ refills

    private async Task RefillAsync()
    {
        _inEvent = true;
        try
        {
            _drivenSinceRefillS = 0;
            _refills++;
            await ParkAsync();
            Event("REFILL-" + _refills, "the tank is empty: AutoSteer and sections off, the tractor stands 15 minutes in the field while the tank is filled");
            for (int i = 0; i < Minutes(15) / FixSeconds; i++) await StepAsync(false);
            await EngageAsync();
        }
        finally { _inEvent = false; }
    }

    // ------------------------------------------------------------------ every five minutes

    private async Task SampleAsync()
    {
        _nextSampleFix = Fixes + SampleEveryFixes;
        await Task.Yield();
        // The tablet's window first: the look's forced collections pause the process, and that
        // pause is the harness's, not a gap in the host's frames (WebClient.Paused).
        var tw = Tablet.Connected && Tablet.Reading ? Tablet.TakeWindow() : (double.NaN, double.NaN, double.NaN);
        double pausedFrom = Tablet.WallMs;
        var ps = ProcessState.Take();
        Tablet.Paused(pausedFrom);
        Phone?.Paused(pausedFrom);
        var disk = DiskUse.Take(Rig.DataRoot);
        double hostMax;
        lock (_hostLock) { hostMax = _hostLoopMax; _hostLoopMax = 0; }
        var n = Ntrip;
        var s = new Sample(Now, WallSeconds, Phase, ps,
            _cycleWin.P(50), _cycleWin.P(95), _cycleWin.P(99), _cycleWin.MaxMs, _cycleWin.Count,
            _tickWin.P(99), _tickWin.MaxMs, hostMax, tw.Item1, tw.Item2, tw.Item3,
            disk.Values.Sum(v => v.Bytes) / 1048576.0, disk.Values.Sum(v => v.Files),
            n.Status.IsOpen, n.TotalBytesReceived, n.Events.Count, _errors, _warnings);
        Samples.Add(s);
        _cycleWin = new Timings(); _tickWin = new Timings(); _errors = 0; _warnings = 0;
        if (Verbose)
            TestContext.Progress.WriteLine(F($"[day] {Now:HH:mm} {Phase,-15} heap {ps.HeapMB,6:0.0} MB rss {ps.RssMB,5:0} thr {ps.Threads,3} tmr {ps.Timers,3} fd {ps.Fds,3} cyc p95 {s.CycleP95,6:0.000} max {s.CycleMax,6:0.0} tick p99 {s.TickP99,6:0.000} host {hostMax,6:0.0} tab {s.TabletTicksPerSec,4:0.0}/s gap {s.TabletMaxGapMs,6:0} data {s.DataMB,6:0.0} MB ntrip {(s.NtripOpen ? "on" : "OFF")}"));
        ResetPace();
    }
}
