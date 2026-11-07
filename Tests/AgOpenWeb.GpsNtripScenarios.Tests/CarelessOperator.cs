// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using static AgOpenWeb.GpsNtripScenarios.Tests.Kit;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>
/// The careless operator: a typo in the password, the mount point in the wrong case, a wrong
/// address or port, NTRIP switched off and on in a hurry, a VRS mount point saved before the
/// receiver has a position, the receiver on another box than the one AgOpenWeb hears. Each
/// time: what AgOpenWeb says, whether it hammers the caster, and how fast it works once fixed.
/// </summary>
public static class CarelessOperator
{
    public const string Who = "Careless";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (Want(only, "K1")) journal.Add(await WrongPasswordAsync());
        if (Want(only, "K2")) journal.Add(await WrongMountAsync());
        if (Want(only, "K3")) journal.Add(await HostTypoAsync());
        if (Want(only, "K4")) journal.Add(await WrongPortAsync());
        if (Want(only, "K5")) journal.Add(await OnOffInAHurryAsync());
        if (Want(only, "K6")) journal.Add(await VrsBeforePositionAsync());
        if (Want(only, "K7")) journal.Add(await ReceiverOnAnotherBoxAsync());
    }

    private static Task<ScenarioRow> RunScenario(string code, string title, string story,
        Func<Scenario, GpsSession, Task> body, GpsSession.Options? options = null) =>
        Run(Who, code, title, story, body, options);

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Test button of the profile editor, as the web client sends it; the text it showed.</summary>
    private static async Task<string> TestButtonAsync(GpsSession s, string host, int port, string mount, string user, string pass)
    {
        await s.SendAsync("ntrip.test", string.Join("\t", host, Inv(port), mount, user, pass, "", "0"));
        string text = "";
        await s.WaitForAsync(() =>
        {
            text = s.Rig.ReadAsync(() => s.Rig.State.Connections.NtripTestStatus ?? "").Result;
            return text.Length > 0 && !text.StartsWith("Testing", StringComparison.Ordinal);
        }, 25);
        return text;
    }

    /// <summary>After the operator corrects the profile: seconds to corrections and to RTK fixed.</summary>
    private static async Task FixAndCheckAsync(Scenario sc, GpsSession s, string what, string mount = FakeCaster.Mount,
        string host = "127.0.0.1", int port = 0, double maxStreaming = 8)
    {
        string? id = await s.ProfileIdAsync();
        if (id == null) { sc.Harness("no profile to correct"); return; }
        double t0 = s.Now;
        await s.SaveProfileAsync("Farm RTK", host, port == 0 ? s.Net.Port : port, mount, FakeCaster.User, FakeCaster.Password, id: id);
        double streaming = await s.WaitForAsync(() => s.NtripStatus.State == NtripState.Streaming && s.Rx.CorrectionAge < 2.5, 40);
        if (!double.IsNaN(streaming)) streaming = s.Now - t0;   // from the operator's Save
        double fixedAt = double.IsNaN(streaming) ? double.NaN : await s.WaitForAsync(() => s.Rx.Quality == 4, 40);
        sc.Step(F($"{what} corrected and saved: corrections at the receiver after {Outage.S(streaming)}, RTK fixed {Outage.S(fixedAt)} later"));
        if (double.IsNaN(streaming))
            sc.Problem(Severity.Bug, "NO-CONNECT-AFTER-FIX", F($"after correcting the {what}, no corrections within 40 s: {s.NtripStatus.State}, \"{s.NtripStatus.Message}\""));
        else if (streaming > maxStreaming)
            sc.Problem(Severity.Improvement, "SLOW-AFTER-FIX", F($"after correcting the {what}, corrections came only after {streaming:0.0} s (the saved profile waited for a retry)"));
        else sc.Ok(F($"corrections flowed {streaming:0.0} s after the corrected profile was saved"));
        CheckNoFreeze(sc, s, t0, s.Now, what + " corrected");
    }

    /// <summary>How the session behaved over a stretch: connections tried, the last state and message.</summary>
    private static string Attempts(GpsSession s, double from, double to, int connectionsBefore, out int tries)
    {
        tries = s.Net.Connections - connectionsBefore;
        var states = Compress(s.Between(from, to).Select(x => x.Ntrip.ToString()));
        return F($"{tries} connection(s) in {to - from:0} s; states {string.Join(" → ", states)}; last message \"{s.NtripStatus.Message}\"");
    }

    /// <summary>K1: the password with one letter in the wrong case.</summary>
    private static Task<ScenarioRow> WrongPasswordAsync() => RunScenario("K1", "Wrong password",
        "The operator types the password with one capital letter wrong, presses Test, then saves anyway. A minute later they notice and correct it.",
        async (sc, s) =>
        {
            const string wrong = "s3cret-PW";
            string test = await TestButtonAsync(s, "127.0.0.1", s.Net.Port, FakeCaster.Mount, FakeCaster.User, wrong);
            sc.Step("Test button: \"" + test + "\"");
            if (!test.Contains("password", StringComparison.OrdinalIgnoreCase) && !test.Contains("login", StringComparison.OrdinalIgnoreCase) && !test.Contains("user", StringComparison.OrdinalIgnoreCase))
                sc.Problem(Severity.Improvement, "TEST-UNCLEAR-LOGIN", "the Test button did not say the login was wrong: \"" + test + "\"");
            else sc.Ok("the Test button said the login was refused");

            int before = s.Net.Connections;
            double t0 = s.Now;
            await s.SaveProfileAsync("Farm RTK", "127.0.0.1", s.Net.Port, FakeCaster.Mount, FakeCaster.User, wrong);
            await GpsSession.WaitAsync(60);
            string a = Attempts(s, t0, s.Now, before, out int tries);
            sc.Step("saved with the wrong password, waited 60 s: " + a + "; told: " + Describe(s.Told(t0, s.Now)));
            if (tries > 3) sc.Problem(Severity.Bug, "LOGIN-HAMMERING", F($"with a wrong password AgOpenWeb tried {tries} times in 60 s (casters lock out a login that keeps failing)"));
            else sc.Ok(F($"stopped after {tries} attempt(s): a wrong login does not fix itself"));
            if (s.NtripStatus.LastError != NtripCategory.LoginRejected)
                sc.Problem(Severity.Bug, "LOGIN-NOT-RECOGNISED", F($"the session ended as {s.NtripStatus.LastError} instead of a wrong login"));
            else if (!s.NtripStatus.Message.Contains("password", StringComparison.OrdinalIgnoreCase) && !s.NtripStatus.Message.Contains("user", StringComparison.OrdinalIgnoreCase))
                sc.Problem(Severity.Improvement, "LOGIN-MESSAGE", "the message for a wrong login does not name the user name or password: \"" + s.NtripStatus.Message + "\"");
            else sc.Ok("the NTRIP status says the user name or password is wrong");
            if (!s.Told(t0, s.Now).Any(t => t.Contains("toast", StringComparison.Ordinal) || t.Contains("failure", StringComparison.Ordinal)))
                sc.Note("nothing on the main screen said NTRIP had stopped (no toast; the GPS fix stays single): the reason is in the Network IO and NTRIP Settings panels only");
            await FixAndCheckAsync(sc, s, "password");
        });

    /// <summary>K2: the mount point typed in lower case.</summary>
    private static Task<ScenarioRow> WrongMountAsync() => RunScenario("K2", "Mount point in the wrong case",
        "The operator types the mount point \"vrs3\" instead of \"VRS3\". The caster answers with its source table. Then they open the mount point list and pick the right one.",
        async (sc, s) =>
        {
            int before = s.Net.Connections;
            double t0 = s.Now;
            await s.SaveProfileAsync("Farm RTK", "127.0.0.1", s.Net.Port, "vrs3", FakeCaster.User, FakeCaster.Password);
            await GpsSession.WaitAsync(40);
            string a = Attempts(s, t0, s.Now, before, out int tries);
            sc.Step("saved with mount point \"vrs3\", waited 40 s: " + a);
            if (s.NtripStatus.LastError != NtripCategory.UnknownMountpoint)
                sc.Problem(Severity.Bug, "MOUNT-NOT-RECOGNISED", F($"a source table for an unknown mount point ended as {s.NtripStatus.LastError}"));
            else sc.Ok("recognised as an unknown mount point");
            if (tries > 3) sc.Problem(Severity.Bug, "MOUNT-HAMMERING", F($"tried {tries} times in 40 s with a mount point the caster does not have"));
            if (!s.NtripStatus.Message.Contains("VRS3", StringComparison.Ordinal))
                sc.Problem(Severity.Improvement, "MOUNT-CASE-HINT", "the caster has \"VRS3\" and the operator typed \"vrs3\"; mount points are case-sensitive, and the message did not point at the near miss: \"" + s.NtripStatus.Message + "\"");
            else sc.Ok("the message named the mount point the caster does have");

            // The mount point list (the editor's search button).
            await s.SendAsync("ntrip.mountpoints", string.Join("\t", "127.0.0.1", Inv(s.Net.Port), FakeCaster.User, FakeCaster.Password, "", "0"));
            string list = "";
            await s.WaitForAsync(() =>
            {
                var m = s.Rig.ReadAsync(() => s.Rig.State.Connections.NtripMountpoints).Result;
                list = m == null ? "" : string.Join(", ", m.Items.Select(i => i.Mount)) + (m.Status.Length > 0 ? " (" + m.Status + ")" : "");
                return list.Contains("VRS3", StringComparison.Ordinal);
            }, 20);
            if (!list.Contains("VRS3", StringComparison.Ordinal)) sc.Problem(Severity.Bug, "MOUNT-LIST", "the mount point list did not show VRS3: " + list);
            else sc.Ok("the mount point list showed VRS3");
            await FixAndCheckAsync(sc, s, "mount point");
        });

    /// <summary>K3: a typo in the caster's name.</summary>
    private static Task<ScenarioRow> HostTypoAsync() => RunScenario("K3", "Typo in the caster's address",
        "The operator types the caster's name with a typo, so the name does not exist. A minute later they correct it.",
        async (sc, s) =>
        {
            int before = s.Net.Connections;
            double t0 = s.Now;
            await s.SaveProfileAsync("Farm RTK", "rtk.caster-typo.invalid", s.Net.Port, FakeCaster.Mount, FakeCaster.User, FakeCaster.Password);
            await GpsSession.WaitAsync(45);
            var st = s.NtripStatus;
            sc.Step(F($"saved with a host that does not exist, waited 45 s: {st.State}, {st.LastError}, \"{st.Message}\", {st.Attempts} attempt(s)"));
            if (!st.Message.Contains("rtk.caster-typo.invalid", StringComparison.Ordinal))
                sc.Problem(Severity.Improvement, "HOST-MESSAGE", "the message does not name the address that could not be found: \"" + st.Message + "\"");
            else sc.Ok("the message names the address that could not be found");
            await FixAndCheckAsync(sc, s, "address", maxStreaming: 17);
        });

    /// <summary>K4: the wrong port (2102 for 2101).</summary>
    private static Task<ScenarioRow> WrongPortAsync() => RunScenario("K4", "Wrong port",
        "The operator types the port one digit off. Nothing listens there, so every connection is refused at once. Then they correct it.",
        async (sc, s) =>
        {
            int wrongPort;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); wrongPort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            double t0 = s.Now;
            await s.SaveProfileAsync("Farm RTK", "127.0.0.1", wrongPort, FakeCaster.Mount, FakeCaster.User, FakeCaster.Password);
            await GpsSession.WaitAsync(45);
            var st = s.NtripStatus;
            sc.Step(F($"saved with a port nothing listens on, waited 45 s: {st.State}, {st.LastError}, \"{st.Message}\", {st.Attempts} attempt(s)"));
            if (st.Attempts > 10) sc.Problem(Severity.Bug, "PORT-HAMMERING", F($"{st.Attempts} attempts in 45 s"));
            else sc.Ok(F($"{st.Attempts} attempts in 45 s (backoff)"));
            sc.Note("the message for a refused connection asks whether the caster is switched on; a wrong port gives the same answer");
            await FixAndCheckAsync(sc, s, "port", maxStreaming: 17);
        });

    /// <summary>K5: NTRIP switched off and on five times in three seconds.</summary>
    private static Task<ScenarioRow> OnOffInAHurryAsync() => RunScenario("K5", "NTRIP off and on in a hurry",
        "While corrections flow, the operator taps the NTRIP button five times off and on within three seconds (a glove, a bump).",
        async (sc, s) =>
        {
            if (await StartDayAsync(sc, s, autoSteer: false) is { } why) { sc.Harness(why); return; }
            int acceptedBefore = s.Caster.Accepted;
            double t0 = s.Now;
            for (int i = 0; i < 5; i++)
            {
                await s.SendAsync("config.set", "conn.ntripEnabled:0");
                await Task.Delay(300);
                await s.SendAsync("config.set", "conn.ntripEnabled:1");
                await Task.Delay(300);
            }
            await GpsSession.WaitAsync(20);
            int open = s.Caster.Streaming;
            sc.Step(F($"after the taps: NTRIP {s.NtripStatus.State}, {s.Caster.Accepted - acceptedBefore} new connection(s) at the caster, {open} stream(s) open now, at most {s.Caster.MaxStreaming} at once; receiver {EmulatedReceiver.Name(s.Rx.Quality)}"));
            if (open > 1) sc.Problem(Severity.Bug, "LEAKED-STREAMS", F($"{open} streams stay open at the caster after the taps (one login used twice; casters often refuse that)"));
            else if (open == 0 || s.NtripStatus.State != NtripState.Streaming) sc.Problem(Severity.Bug, "ONOFF-STUCK", F($"NTRIP ended {s.NtripStatus.State} with {open} stream(s) although the last tap switched it on"));
            else sc.Ok("one stream open and corrections flowing after the taps");
            CheckNoFreeze(sc, s, t0, s.Now, "on/off taps");
            CheckWire(sc, s);
        });

    /// <summary>K6: a VRS mount point saved before the receiver has a position.</summary>
    private static Task<ScenarioRow> VrsBeforePositionAsync() => RunScenario("K6", "VRS mount point before a position",
        "The operator sets up NTRIP in the yard with the receiver still off, and no field open. The mount point is a VRS: the caster sends nothing until it knows where the tractor is (a GGA). Twenty seconds later the receiver is switched on.",
        async (sc, s) =>
        {
            s.Rx.Sending = false;
            double t0 = s.Now;
            await s.SaveProfileAsync("Farm VRS", "127.0.0.1", s.Net.Port, FakeCaster.VrsMount, FakeCaster.User, FakeCaster.Password);
            await GpsSession.WaitAsync(20);
            sc.Step(F($"20 s with no position: NTRIP {s.NtripStatus.State}, \"{s.NtripStatus.Message}\", GGAs at the caster {s.Caster.GgasReceived}"));
            double on = s.Now;
            s.Rx.Sending = true;
            double fresh = await s.WaitForAsync(() => s.Rx.CorrectionAge < 2.5, 90);
            double fixedAt = double.IsNaN(fresh) ? double.NaN : await s.WaitForAsync(() => s.Rx.Quality == 4, 40);
            sc.Step(F($"receiver on: first corrections at the receiver after {Outage.S(fresh)}, RTK fixed {Outage.S(fixedAt)} later; states {string.Join(" → ", Compress(s.Between(on, s.Now).Select(x => x.Ntrip.ToString())))}; told: {Describe(s.Told(t0, s.Now))}"));
            if (double.IsNaN(fresh)) sc.Problem(Severity.Bug, "VRS-NEVER-STARTS", "a VRS profile saved before there was a position never got corrections once the receiver was on (90 s)");
            else if (fresh > 15) sc.Problem(Severity.Improvement, "VRS-SLOW-START", F($"corrections came {fresh:0.0} s after the receiver was switched on: the open connection got no GGA until the next one (AgOpenWeb sends a GGA every 10 s once it has a position)"));
            else sc.Ok(F($"corrections came {fresh:0.0} s after the receiver was switched on"));
            CheckNoFreeze(sc, s, t0, s.Now, "VRS start");
        });

    /// <summary>K7: the receiver takes corrections on a box other than the one sending positions.</summary>
    private static Task<ScenarioRow> ReceiverOnAnotherBoxAsync() => RunScenario("K7", "Corrections to the wrong box",
        "The positions come from one box (a GPS module at 127.0.0.2) but the receiver's corrections input is on another (127.0.0.1), as when a second board or an old module is on the network. AgOpenWeb sends the corrections to the box it hears the positions from.",
        async (sc, s) =>
        {
            await s.SaveGoodProfileAsync();
            double streaming = await s.WaitForAsync(() => s.NtripStatus.State == NtripState.Streaming, 20);
            if (double.IsNaN(streaming)) { sc.Harness("NTRIP did not start streaming"); return; }
            double t0 = s.Now;
            await GpsSession.WaitAsync(45);
            var w = s.Between(t0, s.Now);
            var dest = s.Rig.Ntrip.RtcmDestination;
            sc.Step(F($"45 s: NTRIP {s.NtripStatus.State} (\"{s.NtripStatus.Message}\"), corrections sent to {dest.Address} ({(dest.Unicast ? "the GPS module" : "broadcast")}); the receiver got {s.Rx.Datagrams} datagram(s) and stayed {string.Join(" → ", Compress(w.Select(x => EmulatedReceiver.Name(x.RxQuality))))}; the screen showed fix {string.Join(" → ", Compress(w.Select(x => x.AowFix.ToString(CultureInfo.InvariantCulture))))}, correction age {(w.All(x => double.IsNaN(x.AowAge) || x.AowAge <= 0) ? "none" : "some")}"));
            if (s.Rx.Datagrams > 0) { sc.Harness("the receiver got corrections; the scenario needs them to go elsewhere"); return; }
            bool saysArriving = w.Count(x => x.Ntrip == NtripState.Streaming) > w.Count / 2;
            sc.Problem(Severity.Improvement, "CORRECTIONS-UNUSED-SILENT",
                F($"for 45 s the NTRIP status said \"{s.NtripStatus.Message}\" while the receiver reported no correction age and stayed {EmulatedReceiver.Name(s.Rx.Quality)}: the corrections were not reaching it. AgOpenWeb knows both facts (it forwards {s.Caster.BytesSent / 45 / 1024.0:0.0} KB/s and the receiver's GGA has an empty age) but nothing says \"the receiver is not using the corrections\"; the Network IO line shows \"correction age {(double.IsNaN(w[^1].AowAge) ? "—" : w[^1].AowAge.ToString("0", CultureInfo.InvariantCulture))} s\" without the red mark it gets past 5 s. AgIO does not warn either")
                + (saysArriving ? "" : " (the headline did not even say corrections were arriving)"));
            CheckAgeShown(sc, s, t0, s.Now, "corrections to the wrong box");
            await s.SendAsync("config.set", "conn.rtcmBroadcast:1");
            await GpsSession.WaitAsync(3);
            var d2 = s.Rig.Ntrip.RtcmDestination;
            sc.Step(F($"\"Broadcast corrections\" on: now sent to {d2.Address} ({(d2.Unicast ? "the GPS module" : "broadcast")})"));
            CheckNoFreeze(sc, s, t0, s.Now, "wrong box");
        }, new GpsSession.Options { NmeaFrom = IPAddress.Parse("127.0.0.2") });
}
