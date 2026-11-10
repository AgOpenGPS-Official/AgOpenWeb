// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using static AgOpenWeb.GpsNtripScenarios.Tests.Kit;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>
/// The careful operator: the right login and mount point, Dual GPS on for a dual receiver, a
/// pass with sections on Auto and AutoSteer on. Then the day goes wrong in ways the operator
/// cannot help: the internet drops, the caster restarts, the tablet moves to the phone's
/// hotspot, the receiver loses RTK under a hedge, an antenna cable comes off, the position jumps.
/// </summary>
public static class CarefulOperator
{
    public const string Who = "Careful";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (Want(only, "C1")) journal.Add(await NormalDayAsync());
        if (Want(only, "C2")) journal.Add(await ShortDropAsync());
        if (Want(only, "C3")) journal.Add(await LongDropAsync());
        if (Want(only, "C4")) journal.Add(await FlappingAsync());
        if (Want(only, "C5")) journal.Add(await CasterRestartAsync());
        if (Want(only, "C6")) journal.Add(await CasterStallAsync());
        if (Want(only, "C7")) journal.Add(await WifiToPhoneAsync());
        if (Want(only, "C8")) journal.Add(await RtkDegradesAsync());
        if (Want(only, "C9")) journal.Add(await DualAntennaLostAsync());
        if (Want(only, "C10")) journal.Add(await PositionJumpAsync(dual: true));
        if (Want(only, "C11")) journal.Add(await PositionJumpAsync(dual: false));
        if (Want(only, "C12")) journal.Add(await FallbackCasterAsync());
    }

    private static Task<ScenarioRow> RunScenario(string code, string title, string story,
        Func<Scenario, GpsSession, Task> body, GpsSession.Options? options = null) =>
        Run(Who, code, title, story, body, options);

    /// <summary>C1: a normal day, the baseline every other scenario is measured against.</summary>
    private static Task<ScenarioRow> NormalDayAsync() => RunScenario("C1", "A normal day",
        "The profile is saved with the right login; corrections flow; the receiver reaches RTK fixed; a pass of 40 s with AutoSteer on and sections on Auto.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            double t0 = s.Now;
            await GpsSession.WaitAsync(40);
            double t1 = s.Now;
            var w = s.Between(t0, t1);
            sc.Step(F($"drove 40 s: receiver fixed {w.Count(x => x.RxQuality == 4) * 0.1:0.0} s, screen showed RTK fixed {w.Count(x => x.AowFix == 4) * 0.1:0.0} s, age at most {w.Max(x => x.AowAge):0.0} s"));
            if (w.All(x => x.AutoSteer) && w.All(x => x.Sections != 0)) sc.Ok("AutoSteer and the sections stayed on");
            else sc.Problem(Severity.Bug, "BASELINE-DROPPED", "AutoSteer or the sections went off on a normal day");
            if (w.Any(x => x.Ntrip != NtripState.Streaming)) sc.Problem(Severity.Bug, "BASELINE-NTRIP", "the NTRIP session left Streaming on a normal day");
            CheckNoFreeze(sc, s, t0, t1, "normal day");
            CheckWire(sc, s);
            sc.Note("Told meanwhile: " + Describe(s.Told(t0, t1)));
        });

    /// <summary>The checks every outage gets: no freeze, the receiver fed again, AutoSteer and
    /// sections as AgOpenGPS keeps them (an RTK loss alone turns neither off unless "Alarm stops
    /// AutoSteer" is set), and the RTK lost alarm when the receiver dropped out of RTK fixed.</summary>
    private static Outage CheckOutage(Scenario sc, GpsSession s, double down, double up, double end, string what,
        double maxBackSeconds, string? expectedSlowBecause = null)
    {
        var o = Measure(s, down, up, end);
        sc.Step(what + ": " + o.Summary());
        CheckNoFreeze(sc, s, down, end, what);
        if (double.IsNaN(o.RxFreshAgainAt))
            sc.Problem(Severity.Bug, "NO-RECONNECT", F($"{what}: corrections never reached the receiver again within {end - up:0} s of the internet's return. Told: {Describe(o.Told)}"));
        else if (o.RxFreshAgainAt > maxBackSeconds)
        {
            if (expectedSlowBecause != null)
                sc.Note(F($"{what}: corrections back at the receiver {o.RxFreshAgainAt:0.0} s after the return ({expectedSlowBecause})"));
            else
                sc.Problem(Severity.Bug, "SLOW-RECONNECT", F($"{what}: corrections back at the receiver only {o.RxFreshAgainAt:0.0} s after the internet returned (expected within {maxBackSeconds:0} s)"));
        }
        else sc.Ok(F($"{what}: corrections back at the receiver {o.RxFreshAgainAt:0.0} s after the return"));

        if (!double.IsNaN(o.AutoSteerOffAt))
            sc.Problem(Severity.Bug, "AUTOSTEER-OFF-ON-OUTAGE", F($"{what}: AutoSteer went off {o.AutoSteerOffAt:0.0} s after the drop although only the corrections were missing (\"Alarm stops AutoSteer\" is off, AgOpenGPS keeps steering). Told: {Describe(o.Told)}"));
        else sc.Ok(what + ": AutoSteer stayed on (as AgOpenGPS with \"Alarm stops AutoSteer\" off)");
        if (!double.IsNaN(o.SectionsOffAt))
            sc.Problem(Severity.Bug, "SECTIONS-OFF-ON-OUTAGE", F($"{what}: the sections went off {o.SectionsOffAt:0.0} s after the drop although the position kept coming"));
        else sc.Ok(what + ": the sections stayed on");
        if (!double.IsNaN(o.RxFloatAt))
        {
            if (!o.RtkLostSound)
                sc.Problem(Severity.Bug, "NO-RTK-LOST-ALARM", F($"{what}: the receiver left RTK fixed but no RTK lost alarm sounded (the alarm is on)"));
            else sc.Ok(F($"{what}: the RTK lost alarm sounded (the receiver left RTK fixed {o.RxFloatAt:0.0} s after the drop; the screen followed in {Outage.S(o.ScreenFixLag)})"));
        }
        sc.Note(what + ": told the operator: " + Describe(o.Told, 10));
        return o;
    }

    /// <summary>C2: the internet drops for 10 s (a dead spot on the field).</summary>
    private static Task<ScenarioRow> ShortDropAsync() => RunScenario("C2", "Internet drops for 10 s",
        "Mid-pass the mobile link drops for 10 s and comes back. TCP survives that, so what the caster sent meanwhile arrives at once (the #169 burst). The receiver holds RTK fixed for 10 s without corrections.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(8);
            double down = s.Now;
            s.Net.Drop();
            await GpsSession.WaitAsync(10);
            double up = s.Now;
            s.Net.Restore();
            await GpsSession.WaitAsync(25);
            CheckOutage(sc, s, down, up, s.Now, "10 s drop", maxBackSeconds: 5);
            CheckBurst(sc, s, up, "10 s drop");
            if (s.Caster.Accepted > 1) sc.Note(F($"AgOpenWeb opened {s.Caster.Accepted} connections to the caster in all (the stream survived the drop, so one was enough)"));
            CheckWire(sc, s);
        });

    /// <summary>C3: the internet is gone for 2 minutes (the far end of the farm).</summary>
    private static Task<ScenarioRow> LongDropAsync() => RunScenario("C3", "Internet gone for 2 minutes",
        "Mid-pass the link is gone for 2 minutes. AgOpenWeb's stream goes silent; the caster drops the silent client after 60 s. Every new connection meanwhile gets no answer. Then the link returns.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(5);
            double down = s.Now;
            s.Net.Drop();
            await GpsSession.WaitAsync(120);
            double up = s.Now;
            s.Net.Restore();
            await GpsSession.WaitAsync(45);
            var o = CheckOutage(sc, s, down, up, s.Now, "2 min outage", maxBackSeconds: 20);
            CheckAgeShown(sc, s, down, s.Now, "2 min outage");
            double silentFor = o.NtripLeftStreamingAt;
            sc.Note(F($"AgOpenWeb noticed the silent stream after {Outage.S(silentFor)} (NoData timeout; AgIO's watchdog is 30 s too) and tried {s.Net.Connections - 1} new connection(s) during the outage"));
            var w = s.Between(down, up);
            double waitingShown = w.Count(x => x.Ntrip is NtripState.Waiting or NtripState.Connecting) * 0.1;
            if (waitingShown < 30) sc.Problem(Severity.Improvement, "OUTAGE-NOT-SHOWN", F($"the NTRIP status showed a problem for only {waitingShown:0} s of a 120 s outage"));
            else sc.Ok(F($"the NTRIP status showed the problem for {waitingShown:0} s of the 120 s outage"));
            CheckWire(sc, s);
        });

    /// <summary>C4: the link comes and goes, 5 s off and 5 s on, for 90 s.</summary>
    private static Task<ScenarioRow> FlappingAsync() => RunScenario("C4", "Internet flapping for 90 s",
        "At the edge of coverage the link is lost for 5 s and back for 5 s, nine times. The stream survives each gap.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(5);
            double down = s.Now;
            int before = s.Caster.Accepted;
            for (int i = 0; i < 9; i++)
            {
                s.Net.Drop();
                await GpsSession.WaitAsync(5);
                s.Net.Restore();
                await GpsSession.WaitAsync(5);
            }
            double up = s.Now - 5;
            await GpsSession.WaitAsync(20);
            CheckOutage(sc, s, down, up, s.Now, "flapping link", maxBackSeconds: 5);
            int extra = s.Caster.Accepted - before;
            if (s.Caster.MaxStreaming > 1)
                sc.Problem(Severity.Bug, "TWO-STREAMS", F($"the caster had {s.Caster.MaxStreaming} streams open at once for one AgOpenWeb (a login used twice is often refused)"));
            else sc.Ok(F($"never more than one stream open at the caster ({extra} new connection(s) during the flapping)"));
            var w = s.Between(down, s.Now);
            sc.Step(F($"receiver RTK fixed {w.Count(x => x.RxQuality == 4) * 0.1:0} s of {(s.Now - down):0} s; corrections age up to {w.Max(x => double.IsNaN(x.RxAge) ? 0 : x.RxAge):0.0} s"));
            CheckWire(sc, s);
        });

    /// <summary>C5: the caster restarts (down 20 s).</summary>
    private static Task<ScenarioRow> CasterRestartAsync() => RunScenario("C5", "The caster restarts",
        "The caster closes every connection and refuses new ones for 20 s, then is back. The internet is fine.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(5);
            double down = s.Now;
            s.Caster.Stop();
            await GpsSession.WaitAsync(20);
            double up = s.Now;
            s.Caster.Start();
            await GpsSession.WaitAsync(40);
            CheckOutage(sc, s, down, up, s.Now, "caster restart", maxBackSeconds: 16);
            int tries = s.Net.Connections - 1;
            sc.Note(F($"AgOpenWeb tried {tries} connection(s) while the caster was down (backoff 1, 2, 4, 8, 15 s)"));
            CheckWire(sc, s);
        });

    /// <summary>C6: the caster stays connected but sends nothing for 45 s (its base station went quiet).</summary>
    private static Task<ScenarioRow> CasterStallAsync() => RunScenario("C6", "The caster goes silent for 45 s",
        "The connection stays open but no corrections come for 45 s (the base station behind the mount point stopped), then they resume.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(5);
            double down = s.Now;
            s.Caster.Stalled = true;
            await GpsSession.WaitAsync(45);
            double up = s.Now;
            s.Caster.Stalled = false;
            await GpsSession.WaitAsync(40);
            CheckOutage(sc, s, down, up, s.Now, "silent caster", maxBackSeconds: 5);
            var w = s.Between(down, up);
            double sayStreaming = w.Count(x => x.Ntrip == NtripState.Streaming) * 0.1;
            sc.Note(F($"while the caster was silent the NTRIP headline said \"Corrections arriving\" for {sayStreaming:0} s of 45 s (the Network IO panel warns after 10 s without observations)"));
            CheckWire(sc, s);
        });

    /// <summary>C7: the tablet moves from the tractor's Wi-Fi to the phone's hotspot.</summary>
    private static Task<ScenarioRow> WifiToPhoneAsync() => RunScenario("C7", "Wi-Fi to phone hotspot",
        "The tablet leaves the tractor's Wi-Fi router for the phone's hotspot (the router's SIM lost signal). The address changes and the system reports it; the old connection is dead for good and a new one works at once.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(5);
            double down = s.Now;
            s.Net.SwitchNetwork();
            s.Rig.NetworkWatcher.NotifyChanged();   // as Android and iOS report a network change
            await GpsSession.WaitAsync(70);
            var o = CheckOutage(sc, s, down, down, s.Now, "network switch", maxBackSeconds: 10, expectedSlowBecause: "judged below");
            if (o.RxFreshAgainAt > 10)
                sc.Problem(Severity.Bug, "DEAD-STREAM-AFTER-NETWORK-CHANGE", F($"after the network changed, AgOpenWeb kept waiting on the old, dead connection: corrections came back only {o.RxFreshAgainAt:0.0} s later (the 30 s no-data timeout), so the receiver went {o.RxStates}. A network change wakes only a retry that is waiting, not a stream. AgIO also waits 30 s, then about 5 s more"));
            else sc.Ok(F($"after the network changed, corrections were back at the receiver in {o.RxFreshAgainAt:0.0} s"));
        });

    /// <summary>C8: the receiver drops to float, then DGPS, under a hedge (corrections fine).</summary>
    private static Task<ScenarioRow> RtkDegradesAsync() => RunScenario("C8", "RTK fixed to float to DGPS mid-pass",
        "Along a hedge the receiver drops from RTK fixed to float for 8 s, then to DGPS for 8 s, then gets RTK fixed back; the corrections never stop. First with the RTK settings as they come (alarm on, \"Alarm stops AutoSteer\" off), then with \"Alarm stops AutoSteer\" on.",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            for (int round = 0; round < 2; round++)
            {
                bool stops = round == 1;
                string label = stops ? "with \"Alarm stops AutoSteer\"" : "default settings";
                if (stops)
                {
                    await s.SendAsync("config.set", "gps.rtkLostAction:1");
                    if (!await s.AutoSteerOnAsync()) await s.SendAsync("autosteer.toggle");
                }
                await GpsSession.WaitAsync(4);
                double t0 = s.Now;
                s.Rx.ForcedQuality = 5;
                await GpsSession.WaitAsync(8);
                double tDgps = s.Now;
                s.Rx.ForcedQuality = 2;
                await GpsSession.WaitAsync(8);
                double tBack = s.Now;
                s.Rx.ForcedQuality = null;
                await GpsSession.WaitAsync(6);
                double end = s.Now;
                var w = s.Between(t0, end);
                double floatShown = s.FirstAt(t0, x => x.AowFix == 5, tDgps + 1) - t0;
                double dgpsShown = s.FirstAt(tDgps, x => x.AowFix == 2, tBack + 1) - tDgps;
                double steerOff = s.FirstAt(t0, x => !x.AutoSteer, end) - t0;
                bool lost = s.EventsBetween(t0, end, RigEventKind.Sound).Any(e => e.Text == "RtkLost");
                bool back = s.EventsBetween(t0, end, RigEventKind.Sound).Any(e => e.Text == "RtkRecovered");
                bool sectionsOff = w.Any(x => x.Sections == 0);
                var status = Compress(w.Select(x => x.Status)).Where(x => x.Length > 0).ToList();
                sc.Step(F($"{label}: screen showed float after {Outage.S(floatShown)}, DGPS after {Outage.S(dgpsShown)}; AutoSteer off {Outage.S(steerOff)}; sounds RTK lost {(lost ? "yes" : "no")}, back {(back ? "yes" : "no")}; sections off {(sectionsOff ? "yes" : "no")}; status line: {string.Join(" | ", status.Take(4))}"));
                if (!lost) sc.Problem(Severity.Bug, "NO-RTK-LOST-ALARM", label + ": no RTK lost alarm when the receiver went to float");
                else sc.Ok(label + ": the RTK lost alarm sounded on float");
                if (!back) sc.Problem(Severity.Improvement, "NO-RTK-BACK-SOUND", label + ": no sound when RTK fixed came back");
                if (sectionsOff) sc.Problem(Severity.Bug, "SECTIONS-OFF-ON-FLOAT", label + ": the sections went off on float/DGPS (AgOpenGPS keeps them)");
                if (stops)
                {
                    if (double.IsNaN(steerOff)) sc.Problem(Severity.Safety, "AUTOSTEER-KEPT-ON-FLOAT", label + ": AutoSteer stayed on in float and DGPS although the operator asked the alarm to stop it");
                    else
                    {
                        var why2 = s.EventsBetween(t0, end).Select(e => e.Text).FirstOrDefault(t => t.Contains("RTK", StringComparison.OrdinalIgnoreCase) && t.Contains("AutoSteer", StringComparison.OrdinalIgnoreCase));
                        sc.Ok(F($"{label}: AutoSteer went off {steerOff:0.0} s after float began") + (why2 != null ? " (\"" + why2 + "\")" : ""));
                    }
                }
                else
                {
                    if (!double.IsNaN(steerOff)) sc.Problem(Severity.Bug, "AUTOSTEER-OFF-ON-FLOAT", F($"{label}: AutoSteer went off {steerOff:0.0} s after float began, although the alarm is set not to stop it"));
                    else sc.Ok(label + ": AutoSteer kept steering in float and DGPS (as AgOpenGPS)");
                    double maxSteer = s.Between(tDgps, tBack).Select(x => Math.Abs(x.SteerAngle)).DefaultIfEmpty(0).Max();
                    double maxXte = s.Between(tDgps, tBack).Select(x => Math.Abs(x.Xte)).DefaultIfEmpty(0).Max();
                    var dgpsStatus = Compress(s.Between(tDgps, tBack).Select(x => x.Status.Trim())).Where(x => x.Length > 0).ToList();
                    sc.Note(F($"in DGPS (the position wanders about half a metre) AgOpenWeb steered up to {maxSteer:0.0}° for a distance from the line of up to {maxXte * 100:0} cm; the status line said: {(dgpsStatus.Count == 0 ? "nothing" : string.Join(" | ", dgpsStatus.Take(3)))}"));
                }
                CheckNoFreeze(sc, s, t0, end, label);
            }
            sc.Note("AgOpenGPS writes \"RTK Fix Lost\" on the map while the fix is not RTK fixed (DrawLostRTK) and \"Age: n\" past 20 s; AgOpenWeb shows the fix colour and text in the status bar and plays the sound");
        });

    /// <summary>C9: an antenna cable comes off a dual receiver while driving east.</summary>
    private static Task<ScenarioRow> DualAntennaLostAsync() => RunScenario("C9", "One antenna of a dual receiver lost",
        "Dual GPS on, AutoSteer on, driving east. The second antenna is lost for 8 s: first the $PAOGI comes with an empty heading field, later the module falls back to $PANDA with no heading (two firmwares, two ways).",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(4);
            // a) $PAOGI with an empty heading
            double t0 = s.Now;
            s.Rx.DualLost = EmulatedReceiver.DualLoss.EmptyHeading;
            await GpsSession.WaitAsync(8);
            double t1 = s.Now;
            s.Rx.DualLost = EmulatedReceiver.DualLoss.None;
            await GpsSession.WaitAsync(4);
            var a = s.Between(t0 + 0.3, t1);
            double worstHeading = a.Select(x => Math.Abs(Wrap180(x.HeadingDeg - 90))).DefaultIfEmpty(0).Max();
            double worstSteer = a.Select(x => Math.Abs(x.SteerAngle)).DefaultIfEmpty(0).Max();
            double steerOffA = s.FirstAt(t0, x => !x.AutoSteer, t1 + 2) - t0;
            var toldA = Describe(s.Told(t0, t1 + 2));
            sc.Step(F($"$PAOGI with an empty heading for 8 s: AgOpenWeb's heading off by up to {worstHeading:0}° (true 90°), steer angle up to {worstSteer:0.0}°, AutoSteer off {Outage.S(steerOffA)}, dual heading flagged missing {(a.Any(x => x.DualMissing) ? "yes" : "no")}; told: {toldA}"));
            if (worstHeading > 20)
                sc.Problem(double.IsNaN(steerOffA) ? Severity.Safety : Severity.Bug, "EMPTY-DUAL-HEADING-READ-AS-NORTH",
                    F($"a $PAOGI with an empty heading field (no dual solution) is read as heading 0° (north): driving east the heading was off by {worstHeading:0}° and AutoSteer {(double.IsNaN(steerOffA) ? "kept steering" : F($"went off after {steerOffA:0.0} s"))} with up to {worstSteer:0.0}° of steer; nothing said the heading was missing. AgIO reads the empty field as 0 too (float.TryParse), so AgOpenGPS does the same"));
            else sc.Ok("an empty $PAOGI heading did not move AgOpenWeb's heading");
            if (!await s.AutoSteerOnAsync()) await s.SendAsync("autosteer.toggle");
            await GpsSession.WaitAsync(4);
            // b) $PANDA instead
            double u0 = s.Now;
            s.Rx.DualLost = EmulatedReceiver.DualLoss.PandaInstead;
            await GpsSession.WaitAsync(14);
            double u1 = s.Now;
            s.Rx.DualLost = EmulatedReceiver.DualLoss.None;
            await GpsSession.WaitAsync(4);
            var b = s.Between(u0 + 0.3, u1);
            double hb = b.Select(x => Math.Abs(Wrap180(x.HeadingDeg - 90))).DefaultIfEmpty(0).Max();
            double sb2 = b.Select(x => Math.Abs(x.SteerAngle)).DefaultIfEmpty(0).Max();
            double offB = s.FirstAt(u0, x => !x.AutoSteer, u1 + 2) - u0;
            bool warned = s.EventsBetween(u0, u1 + 2).Any(e => e.Text.Contains("dual", StringComparison.OrdinalIgnoreCase));
            bool flagged = b.Any(x => x.DualMissing);
            sc.Step(F($"$PANDA (no heading) for 14 s: heading off by up to {hb:0}°, steer up to {sb2:0.0}°, AutoSteer off {Outage.S(offB)}, dual heading flagged missing {(flagged ? "yes" : "no")}; told: {Describe(s.Told(u0, u1 + 2))}"));
            if (hb > 10) sc.Problem(Severity.Bug, "PANDA-FALLBACK-HEADING", F($"with $PANDA instead of $PAOGI the heading was off by up to {hb:0}°"));
            else sc.Ok(F($"with $PANDA instead of $PAOGI AgOpenWeb steered on the single-antenna heading (off by at most {hb:0}°)"));
            if (!flagged) sc.Problem(Severity.Improvement, "DUAL-LOSS-NOT-FLAGGED", "the dual heading was not flagged missing while $PANDA came instead of $PAOGI");
            else sc.Ok("the status frame flagged the dual heading missing (the web client toasts it)");
            CheckNoFreeze(sc, s, t0, s.Now, "lost antenna");
        }, new GpsSession.Options { HeadingDeg = 90 });

    private static double Wrap180(double d) { d %= 360; if (d > 180) d -= 360; if (d < -180) d += 360; return d; }

    /// <summary>C10 / C11: the position jumps 1 m sideways (a new integer fix, another base).</summary>
    private static Task<ScenarioRow> PositionJumpAsync(bool dual) => RunScenario(dual ? "C10" : "C11",
        dual ? "A 1 m position jump, dual receiver" : "A 1 m position jump, single antenna without IMU",
        "Driving north with AutoSteer on, the reported position jumps 1 m to the east at once and stays there (the receiver re-fixed on another solution, or the corrections now come from another base). The tractor itself did not move." +
        (dual ? " Dual receiver ($PAOGI heading)." : " Single antenna, $PANDA without IMU: the heading comes from the fixes."),
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s) is { } why) { sc.Harness(why); return; }
            await GpsSession.WaitAsync(6);
            double t0 = s.Now;
            s.Rx.ShiftE = 1.0;
            await GpsSession.WaitAsync(10);
            double end = s.Now;
            var w = s.Between(t0, end);
            var before = s.Between(t0 - 3, t0);
            double maxSteer = w.Select(x => Math.Abs(x.SteerAngle)).DefaultIfEmpty(0).Max();
            double maxHeading = w.Select(x => Math.Abs(Wrap180(x.HeadingDeg))).DefaultIfEmpty(0).Max();
            bool reverse = w.Any(x => x.Reverse);
            double steerOff = s.FirstAt(t0, x => !x.AutoSteer, end) - t0;
            double sectionsOff = s.FirstAt(t0, x => x.Sections == 0, end) - t0;
            double xte = w.Select(x => Math.Abs(x.Xte)).DefaultIfEmpty(0).Max();
            sc.Step(F($"jump of 1 m east: distance from the line up to {xte * 100:0} cm, steer angle up to {maxSteer:0.0}° (before: {before.Select(x => Math.Abs(x.SteerAngle)).DefaultIfEmpty(0).Max():0.0}°), heading off by up to {maxHeading:0}°, reversing seen {(reverse ? "yes" : "no")}, AutoSteer off {Outage.S(steerOff)}, sections off {Outage.S(sectionsOff)}; told: {Describe(s.Told(t0, end))}"));
            if (reverse) sc.Problem(Severity.Bug, "JUMP-READ-AS-REVERSING", "a 1 m sideways jump of the position was taken for reversing");
            if (!double.IsNaN(sectionsOff)) sc.Problem(Severity.Bug, "JUMP-SECTIONS-OFF", F($"the sections went off {sectionsOff:0.0} s after a 1 m position jump"));
            if (maxHeading > 30)
                sc.Problem(dual ? Severity.Bug : Severity.Improvement, "JUMP-HEADING-SPIKE", F($"a 1 m sideways jump turned AgOpenWeb's heading by up to {maxHeading:0}° (the tractor drove straight); steer angle up to {maxSteer:0.0}°"));
            else sc.Ok(F($"the heading stayed within {maxHeading:0}° of the truth"));
            sc.Note(F($"a jump is the receiver's error: AgOpenWeb, like AgOpenGPS (its jump check is commented out), steers to the reported position; the steer angle went to {maxSteer:0.0}° and the tractor would move 1 m to follow it. No warning was given"));
            CheckNoFreeze(sc, s, t0, end, "position jump");
        }, new GpsSession.Options { Dual = dual });

    /// <summary>C12: the main caster dies; the profile has a fallback service with auto-switch.</summary>
    private static Task<ScenarioRow> FallbackCasterAsync() => RunScenario("C12", "Main caster down, fallback service",
        "The profile has a fallback service (another caster) with Auto-switch after 2 failed attempts. The main caster goes down for 90 s. AgOpenWeb should move to the fallback, keep the receiver fed, and ask before going back when the main service answers again.",
        async (sc, s) =>
        {
            var c2 = s.Caster2!;
            string fallback = string.Join("\t", "1", "127.0.0.1", c2.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), FakeCaster.Mount, FakeCaster.User, FakeCaster.Password, "1", "2");
            await s.SaveProfileAsync("Farm RTK", "127.0.0.1", s.Net.Port, FakeCaster.Mount, FakeCaster.User, FakeCaster.Password, fallback: fallback);
            if (double.IsNaN(await s.WaitForAsync(() => s.Rx.Quality == 4, 45))) { sc.Harness("no RTK fixed on the main service"); return; }
            string? pass = await s.StartPassAsync("Field C12");
            if (pass != null) { sc.Harness(pass); return; }
            await GpsSession.WaitAsync(4);
            double down = s.Now;
            s.Caster.Stop();
            double switched = await s.WaitForAsync(() => c2.Streaming > 0 && s.Rig.Ntrip.Using == NtripService.Fallback, 60);
            await GpsSession.WaitAsync(10);
            var mid = s.Between(down + (double.IsNaN(switched) ? 60 : switched) + 3, s.Now);
            bool fedOnFallback = mid.Count > 0 && mid.All(x => x.RxAge < 3);
            sc.Step(F($"main caster down: on the fallback after {Outage.S(switched)}; receiver fed on the fallback {(fedOnFallback ? "yes" : "no")}, receiver {string.Join(" → ", Compress(s.Between(down, s.Now).Select(x => EmulatedReceiver.Name(x.RxQuality))))}; told: {Describe(s.Told(down, s.Now))}"));
            if (double.IsNaN(switched)) sc.Problem(Severity.Bug, "NO-FALLBACK", "AgOpenWeb did not move to the fallback service within 60 s");
            else if (switched > 20) sc.Problem(Severity.Improvement, "SLOW-FALLBACK", F($"the switch to the fallback took {switched:0.0} s"));
            else sc.Ok(F($"moved to the fallback service after {switched:0.0} s"));
            await GpsSession.WaitAsync(60);
            double up = s.Now;
            s.Caster.Start();
            double asked = await s.WaitForAsync(() => s.Rig.ReadAsync(() => s.Rig.Vm.IsPromptPending).Result, 400);
            sc.Step(F($"main caster back: AgOpenWeb asked to switch back after {Outage.S(asked)} (it tries the main service every 30, 60, 120, 300 s)"));
            if (double.IsNaN(asked)) sc.Note("no question to switch back within 400 s");
            else sc.Ok(F($"asked before going back to the main service ({asked:0} s after it returned)"));
            CheckOutage(sc, s, down, down, s.Now, "main caster down", maxBackSeconds: 20);
        }, new GpsSession.Options { SecondCaster = true });
}
