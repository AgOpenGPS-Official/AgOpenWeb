// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using static AgOpenWeb.IsobusScenarios.Tests.Kit;

namespace AgOpenWeb.IsobusScenarios.Tests;

/// <summary>
/// The careless operator, or just a bad day: the task controller started after AgOpenWeb, a
/// task controller that refuses or delays the mode request, the mode changed on the tractor's
/// terminal, the task controller's Wi-Fi gone, malformed frames, 64 sections, a tool typed with
/// another section count than the implement, two implements, no beacon, the job closed with the
/// implement in automatic, the subnet changed. Each scenario has its own data folder and its own
/// task controller.
/// </summary>
public static class CarelessOperator
{
    public const string Name = "Careless";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (Want(only, "K1")) journal.Add(await TaskControllerAfterAgOpenWeb());
        if (Want(only, "K2")) journal.Add(await TaskControllerRefusesOrDelays());
        if (Want(only, "K3")) journal.Add(await ModeChangedOnTheTerminal());
        if (Want(only, "K4")) journal.Add(await LinkDrops());
        if (Want(only, "K5")) journal.Add(await MalformedFrames());
        if (Want(only, "K6")) journal.Add(await SixtyFourSections());
        if (Want(only, "K7")) journal.Add(await SectionCountMismatch());
        if (Want(only, "K8")) journal.Add(await TwoImplements());
        if (Want(only, "K9")) journal.Add(await NoBeacon());
        if (Want(only, "K10")) journal.Add(await JobClosedInAutomatic());
        if (Want(only, "K11")) journal.Add(await SubnetChanged());
    }

    /// <summary>A session with the tool typed in, a job open, master Auto.</summary>
    private static async Task<IsobusSession> WorkingAsync(Scenario sc, string root, EmulatedTaskController tc, int sections, double cm, string job)
    {
        var s = await IsobusSession.StartAsync(root, tc);
        await s.SetToolAsync(sections, cm);
        if (await s.StartJobAsync(job) is { } why) sc.Harness(why);
        return s;
    }

    /// <summary>Tap the ISOBUS button for automatic and wait for it.</summary>
    private static async Task AutomaticAsync(Scenario sc, IsobusSession s)
    {
        if (double.IsNaN(await s.WaitAliveAsync())) { sc.Harness("the task controller was never heard"); return; }
        double got = await s.WantModeAsync(true);
        sc.Step(double.IsNaN(got) ? "ISOBUS button tapped; automatic did not show within 2 s" : F($"ISOBUS button tapped; section control on after {got:0.00} s"));
        if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller obeys PGN 241 at once, but AgOpenWeb did not show section control on within 2 s");
    }

    // ------------------------------------------------------------------ K1
    private static async Task<ScenarioRow> TaskControllerAfterAgOpenWeb()
    {
        var sc = new Scenario(Name, "K1", "Task controller started after AgOpenWeb, mid-job",
            "AgOpenWeb is up with the 24-section planter's tool, the job open and master Auto; the HAT's task controller daemon only starts 4 s later (the planter already on its bus). The operator then taps the ISOBUS button.");
        string root = NewDataRoot("k1");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        tc.Stop();   // the socket exists, the process is not running yet
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K1");
        sc.Step("AgOpenWeb started; tool 24 × 50 cm; job open, master Auto; no task controller yet");
        var w = await DriveAsync(sc, s, 4, "4 s with no task controller", planter, expectFollow: false);
        if (w.AliveSeconds == 0 && s.Dto() == null) sc.Ok("no ISOBUS button while no task controller is heard");
        else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", F($"the button showed for {w.AliveSeconds:0.0} s with no task controller"));
        int msgs = tc.MessagesSent.Count;
        double tm = s.Seconds;
        tc.Restart();
        sc.Step("The task controller starts");
        double seen = await s.WaitAliveAsync();
        var st = s.Isobus.Status;
        sc.Step(F($"heard after {seen:0.0} s: {st.SectionCount} sections, section control {(st.SectionControlEnabled ? "on" : "off")}"));
        if (!double.IsNaN(seen) && seen < 1.5) sc.Ok("the ISOBUS button appeared within 1.5 s of the first heartbeat");
        else sc.Problem(Severity.Bug, "ISOBUS-NOT-HEARD", F($"the button took {seen:0.0} s to appear"));
        await AutomaticAsync(sc, s);
        await DriveAsync(sc, s, 5, "5 s planting", planter);
        CheckMessages(sc, s, msgs, tm, "when the task controller started");
        CheckWire(sc, tc);
        CheckRig(sc, s, "the late start");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K2
    private static async Task<ScenarioRow> TaskControllerRefusesOrDelays()
    {
        var sc = new Scenario(Name, "K2", "A task controller that ignores the request, then one that takes 1.5 s",
            "The implement refuses automatic section control (an implement that only works in manual, an older task controller): PGN 241 changes nothing. The operator taps the ISOBUS button and waits. Then the task controller is one that takes 1.5 s to apply the request.");
        string root = NewDataRoot("k2");
        using var tc = new EmulatedTaskController { OnModeRequest = EmulatedTaskController.ModeRequestHandling.Ignore };
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K2");
        await s.WaitAliveAsync();
        s.Rig.TakeEvents();
        long t0 = tc.NowMs;
        await s.TapIsobusAsync();
        sc.Step("ISOBUS button tapped (the task controller ignores PGN 241)");
        var w = await DriveAsync(sc, s, 4.5, "4.5 s waiting", planter, expectFollow: false);
        int reqs = tc.ModeRequestsSince(t0);
        int late = tc.ModeRequestsSince(t0 + 3600);
        var st = s.Isobus.Status;
        sc.Step(F($"PGN 241 sent ×{reqs} in 4.5 s ({late} after 3.6 s); the button now shows section control {(st.SectionControlEnabled ? "on" : "off")}"));
        if (reqs is >= 5 and <= 8 && late == 0) sc.Ok("AgOpenWeb asked every 0.5 s for 3 s, then stopped and showed the mode the task controller reports (manual)");
        else if (late > 0) sc.Problem(Severity.Bug, "ISOBUS-REQUEST-STORM", F($"AgOpenWeb kept sending PGN 241 after the 3 s it gives a request ({late} more)"));
        else sc.Problem(Severity.Bug, "ISOBUS-REQUEST-REPEATED", F($"PGN 241 ×{reqs} in 4.5 s for one tap (expected one every 0.5 s for 3 s)"));
        if (st.SectionControlEnabled) sc.Problem(Severity.Bug, "ISOBUS-MODE-SHOWN-WRONG", "the button shows automatic although the task controller never reported it");
        var said = Said(s, t => t.Contains("ISOBUS", StringComparison.OrdinalIgnoreCase) || t.Contains("section control", StringComparison.OrdinalIgnoreCase) || t.Contains("implement", StringComparison.OrdinalIgnoreCase));
        if (said.Count == 0)
            sc.Problem(Severity.Improvement, "ISOBUS-REQUEST-REFUSED-SILENT", "the implement did not take automatic section control and AgOpenWeb said nothing: the button just stays on manual, as if the tap had not registered (AgOpenGPS does the same)");
        else foreach (var t in said) sc.Note(t);

        tc.OnModeRequest = EmulatedTaskController.ModeRequestHandling.ObeyLate;
        tc.ObeyLateMs = 1500;
        t0 = tc.NowMs;
        await s.TapIsobusAsync();
        double got = await s.WaitModeAsync(true, 3000);
        reqs = tc.ModeRequestsSince(t0);
        sc.Step(double.IsNaN(got) ? F($"ISOBUS button tapped (the task controller applies it after 1.5 s): automatic did not show within 3 s, PGN 241 ×{reqs}") : F($"ISOBUS button tapped (the task controller applies it after 1.5 s): section control on after {got:0.00} s, PGN 241 ×{reqs}"));
        if (!double.IsNaN(got) && got < 2.5) sc.Ok(F($"a slow task controller still ends in automatic; AgOpenWeb repeated the request {reqs - 1} time(s) meanwhile"));
        else sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller applied the request after 1.5 s, but AgOpenWeb did not show automatic within 3 s");
        await DriveAsync(sc, s, 4, "4 s planting", planter);
        CheckWire(sc, tc);
        CheckRig(sc, s, "the refusals");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K3
    private static async Task<ScenarioRow> ModeChangedOnTheTerminal()
    {
        var sc = new Scenario(Name, "K3", "Section control switched on the tractor's terminal",
            "Planting in automatic. The operator switches the implement to manual on the tractor's own terminal (the task controller reports it, AgOpenWeb asked nothing), drives 3 s, then back to automatic on the terminal.");
        string root = NewDataRoot("k3");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K3");
        await AutomaticAsync(sc, s);
        await DriveAsync(sc, s, 3, "3 s planting", planter);
        long t0 = tc.NowMs;
        tc.SectionControlEnabled = false;
        sc.Step("Manual chosen on the terminal");
        double got = await s.WaitModeAsync(false, 1000);
        if (!double.IsNaN(got)) sc.Ok(F($"AgOpenWeb showed manual {got:0.00} s later"));
        else sc.Problem(Severity.Bug, "ISOBUS-MODE-SHOWN-WRONG", "1 s after the terminal switched to manual AgOpenWeb still showed automatic");
        var w = await DriveAsync(sc, s, 3, "3 s in manual", planter, expectFollow: false);
        int fights = tc.ModeRequestsSince(t0);
        if (fights == 0) sc.Ok("AgOpenWeb followed the terminal and sent no PGN 241 to undo it");
        else sc.Problem(Severity.Bug, "ISOBUS-FIGHTS-TERMINAL", F($"after the terminal switched to manual AgOpenWeb sent PGN 241 ×{fights} to put automatic back"));
        t0 = tc.NowMs;
        tc.SectionControlEnabled = true;
        sc.Step("Automatic chosen on the terminal");
        got = await s.WaitModeAsync(true, 1000);
        if (!double.IsNaN(got)) sc.Ok(F($"AgOpenWeb showed automatic {got:0.00} s later"));
        else sc.Problem(Severity.Bug, "ISOBUS-MODE-SHOWN-WRONG", "1 s after the terminal switched to automatic AgOpenWeb still showed manual");
        await DriveAsync(sc, s, 3, "3 s planting again", planter);
        if (tc.ModeRequestsSince(t0) > 0) sc.Problem(Severity.Bug, "ISOBUS-FIGHTS-TERMINAL", F($"after the terminal switched to automatic AgOpenWeb sent PGN 241 ×{tc.ModeRequestsSince(t0)}"));
        CheckWire(sc, tc);
        CheckRig(sc, s, "the terminal");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K4
    private static async Task<ScenarioRow> LinkDrops()
    {
        var sc = new Scenario(Name, "K4", "The task controller's link drops for 5 s and for 30 s",
            "Planting in automatic; the HAT's Wi-Fi to the tablet drops (nothing in or out for the task controller) for 5 s, then later for 30 s. The implement keeps its last state. The operator keeps driving.");
        string root = NewDataRoot("k4");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K4");
        await AutomaticAsync(sc, s);
        await DriveAsync(sc, s, 3, "3 s planting", planter);
        s.Rig.TakeEvents();
        foreach (double d in new[] { 5.0, 30.0 })
        {
            tc.NetworkUp = false;
            sc.Step(F($"Link down for {d:0} s"));
            long t0 = tc.NowMs;
            var w = await DriveAsync(sc, s, d, F($"{d:0} s with the link down"), planter, expectFollow: false);
            if (double.IsNaN(w.FirstNotAliveAt)) sc.Problem(Severity.Bug, "ISOBUS-LOST-NOT-NOTICED", F($"{d:0} s without a heartbeat and AgOpenWeb still showed the task controller"));
            else sc.Ok(F($"the ISOBUS button went away {w.FirstNotAliveAt:0.0} s after the last heartbeat"));
            var said = Said(s, t => t.Contains("ISOBUS", StringComparison.OrdinalIgnoreCase) || t.Contains("task controller", StringComparison.OrdinalIgnoreCase) || t.Contains("implement", StringComparison.OrdinalIgnoreCase));
            if (d >= 30 && said.Count == 0 && w.AreaPaintedNotAlive > 1)
                sc.Problem(Severity.Improvement, "ISOBUS-TC-LOST-SILENT", F($"with the task controller gone for {d:0} s AgOpenWeb kept the sections on and painted {w.AreaPaintedNotAlive:0} m² of coverage no implement was told about; the only sign is the ISOBUS button disappearing (no alarm, no message), while a lost machine module at least changes the status dot"));
            else foreach (var t in said) sc.Note(t);
            tc.NetworkUp = true;
            sc.Step("Link back");
            double back = await s.WaitAliveAsync();
            int reqs = tc.ModeRequestsSince(t0);
            sc.Step(F($"heard again after {back:0.0} s; PGN 241 sent since the drop ×{reqs}"));
            if (reqs > 0) sc.Problem(Severity.Bug, "ISOBUS-REQUEST-REPEATED", F($"the task controller came back reporting the mode AgOpenWeb wanted, yet AgOpenWeb sent PGN 241 ×{reqs}"));
            await DriveAsync(sc, s, 3, "3 s planting after the drop", planter);
        }
        CheckWire(sc, tc);
        CheckRig(sc, s, "the drops");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K5
    private static async Task<ScenarioRow> MalformedFrames()
    {
        var sc = new Scenario(Name, "K5", "Malformed and odd frames from the task controller",
            "A task controller with its beacon off and no implement sends, one at a time: a heartbeat whose length does not match its section count, one for 70 sections, one with a wrong checksum, one from another source address, one with an extra byte, one cut short, one with no payload. AgOpenWeb must ignore what is wrong, as AgOpenGPS does, and must not fall over.");
        string root = NewDataRoot("k5");
        using var tc = new EmulatedTaskController { BeaconEnabled = false };
        using var steer = new SteerModuleStub { On = true };
        tc.Start();
        await using var s = await IsobusSession.StartAsync(root, tc);
        await Task.Delay(1500);
        s.Rig.TakeEvents();

        async Task<IsobusTaskControllerStatusSnapshot> Try(string what, byte[] frame)
        {
            // let the previous one expire first (1 s alive timeout)
            await s.WaitAliveAsync(false, 1500);
            tc.SendRaw(frame);
            await Task.Delay(300);
            var st = s.Isobus.Status;
            var dto = s.Dto();
            sc.Step(F($"{what}: alive {st.IsAlive}, {st.SectionCount} sections, reported {Bits(st.ReportedSections, Math.Min(st.SectionCount, 64))}, frame {(dto == null ? "none" : dto.Sections + " sections")}"));
            return new IsobusTaskControllerStatusSnapshot(st.IsAlive, st.SectionCount, st.ReportedSections, dto?.Sections);
        }

        var r = await Try("length 3 for 12 sections (needs 4)", EmulatedTaskController.Frame(0x80, 0xF0, 1, 12, 0x0F));
        if (!r.Alive) sc.Ok("a heartbeat whose length does not match its section count is ignored (as AgOpenGPS: length must be 2 + ceil(n / 8))");
        else sc.Problem(Severity.Robustness, "ISOBUS-BAD-LENGTH-ACCEPTED", "a heartbeat with the wrong length for its section count was taken");

        r = await Try("70 sections", EmulatedTaskController.HeartbeatFrame(true, 70, ulong.MaxValue));
        if (r.Alive && r.Sections == 70) sc.Note("a heartbeat for 70 sections is taken: the status says 70, the reported states stop at 64 (the protocol's maximum), the browser builds a 64-cell row and the tooltip says 70");
        else if (!r.Alive) sc.Note("a heartbeat for 70 sections is ignored");

        var bad = EmulatedTaskController.HeartbeatFrame(false, 8, 0b1010_1010);
        bad[^1] ^= 0xFF;
        r = await Try("wrong checksum", bad);
        if (r.Alive) sc.Note("a heartbeat with a wrong checksum is taken (as AgOpenGPS, which does not check it; the task controller does not check AgOpenWeb's either)");
        else sc.Ok("a heartbeat with a wrong checksum is ignored");

        var other = EmulatedTaskController.HeartbeatFrame(true, 8, 0xFF);
        other[2] = 0x7B;
        other[^1] = (byte)(other[^1] - 0x80 + 0x7B);
        r = await Try("PGN 240 from source 0x7B (a machine module)", other);
        if (!r.Alive) sc.Ok("PGN 240 from another source than the task controller (0x80) is ignored");
        else sc.Problem(Severity.Robustness, "ISOBUS-ANY-SOURCE", "PGN 240 from a machine module's source address was taken as the task controller's");

        r = await Try("no implement, one extra byte (length 3)", EmulatedTaskController.Frame(0x80, 0xF0, 0, 0, 0));
        sc.Note(r.Alive ? "a beacon with an extra byte is taken as alive with no implement" : "a beacon with an extra byte is ignored");

        var cut = EmulatedTaskController.HeartbeatFrame(true, 24, ulong.MaxValue);
        r = await Try("cut short (length says 5, 3 bytes there)", cut[..8]);
        if (!r.Alive) sc.Ok("a frame cut short is ignored");
        else sc.Problem(Severity.Robustness, "ISOBUS-SHORT-FRAME-ACCEPTED", "a frame cut short was taken");

        r = await Try("no payload (length 0)", EmulatedTaskController.Frame(0x80, 0xF0));
        if (!r.Alive) sc.Ok("a heartbeat with no payload is ignored");
        else sc.Problem(Severity.Robustness, "ISOBUS-EMPTY-FRAME-ACCEPTED", "a heartbeat with no payload was taken");

        r = await Try("a good one (8 sections, 1-4 on)", EmulatedTaskController.HeartbeatFrame(true, 8, 0x0F));
        if (r.Alive && r.Sections == 8 && r.Reported == 0x0F && r.FrameSections == 8) sc.Ok("a good heartbeat after the bad ones is taken: 8 sections, 1-4 on, frame 8 sections");
        else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", F($"after the bad frames a good heartbeat gave alive {r.Alive}, {r.Sections} sections, reported {r.Reported:X}, frame {r.FrameSections}"));

        CheckRig(sc, s, "the bad frames");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    private sealed record IsobusTaskControllerStatusSnapshot(bool Alive, int Sections, ulong Reported, int? FrameSections);

    // ------------------------------------------------------------------ K6
    private static async Task<ScenarioRow> SixtyFourSections()
    {
        var sc = new Scenario(Name, "K6", "64 sections",
            "A 24 m planter with 64 sections (the protocol's maximum): tool 64 × 37.5 cm, automatic section control. Section 64 (bit 63 of PGN 229 and of PGN 240) must get through both ways.");
        string root = NewDataRoot("k6");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter 64", 64, 24);
        tc.Connect(planter);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 64, 37.5, "K6");
        await AutomaticAsync(sc, s);
        var w = await DriveAsync(sc, s, 6, "6 s planting", planter);
        ulong screen = s.Sections.GetSectionBits64();
        var st = s.Isobus.Status;
        var dto = s.Dto();
        bool s64 = ((screen >> 63) & 1) != 0, i64 = ((planter.Actual >> 63) & 1) != 0, r64 = ((st.ReportedSections >> 63) & 1) != 0, d64 = dto != null && ((dto.Reported >> 63) & 1) != 0;
        sc.Step(F($"section 64: screen {(s64 ? "on" : "off")}, implement {(i64 ? "on" : "off")}, reported {(r64 ? "on" : "off")}, frame {(d64 ? "on" : "off")}; status {st.SectionCount} sections"));
        if (s64 && i64 && r64 && d64) sc.Ok("section 64 flows: screen → PGN 229 → implement → PGN 240 → status → frame");
        else if (s64) sc.Problem(Severity.Bug, "ISOBUS-SECTION-64-LOST", F($"section 64 on screen, implement {(i64 ? "on" : "off")}, reported {(r64 ? "on" : "off")}, frame {(d64 ? "on" : "off")}"));
        else sc.Note("section 64 was not on on the screen at the check (the tool may be narrower than 64 sections allow)");
        CheckWire(sc, tc);
        CheckRig(sc, s, "64 sections");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K7
    private static async Task<ScenarioRow> SectionCountMismatch()
    {
        var sc = new Scenario(Name, "K7", "Tool typed with 20 sections, implement with 12",
            "The operator's tool profile says 20 sections (the old planter); the implement on the bus has 12. Automatic section control. The task controller forwards the first 12 of AgOpenWeb's 20 to the implement; the screen paints 20.");
        string root = NewDataRoot("k7");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var impl = new Implement("Planter 12", 12, 6);
        tc.Connect(impl);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 20, 50, "K7");
        await AutomaticAsync(sc, s);
        s.Rig.TakeEvents();
        var w = await DriveAsync(sc, s, 6, "6 s planting", impl);
        var st = s.Isobus.Status;
        sc.Step(F($"status: implement {st.SectionCount} sections, tool {s.Store.NumSections}; the ISOBUS row has {st.SectionCount} cells under {s.Store.NumSections} buttons"));
        var said = Said(s, t => t.Contains("section", StringComparison.OrdinalIgnoreCase) || t.Contains("ISOBUS", StringComparison.OrdinalIgnoreCase) || t.Contains("implement", StringComparison.OrdinalIgnoreCase));
        if (said.Count == 0)
            sc.Problem(Severity.Improvement, "ISOBUS-SECTION-COUNT-MISMATCH-SILENT", F($"the tool has {s.Store.NumSections} sections and the implement reports {st.SectionCount}; AgOpenWeb paints {s.Store.NumSections} and says nothing: sections 13-20 show as worked on the map but no implement has them (the heartbeat carries the implement's count, so a one-line warning is possible)"));
        else foreach (var t in said) sc.Note(t);
        CheckWire(sc, tc);
        CheckRig(sc, s, "the mismatch");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K8
    private static async Task<ScenarioRow> TwoImplements()
    {
        var sc = new Scenario(Name, "K8", "Two implements with sections on the bus",
            "A 24-section planter and a 12-section fertiliser hopper, both on the task controller (it sends one PGN 240 per implement every 100 ms). The tool is the planter's 24 sections, automatic section control.");
        string root = NewDataRoot("k8");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        var hopper = new Implement("Fertiliser", 12, 12);
        tc.Connect(planter);
        tc.Connect(hopper);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K8");
        await AutomaticAsync(sc, s);
        // What the status says, sampled faster than the heartbeats come.
        int changes = 0, last = -1;
        var seen = new SortedSet<int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            var st = s.Isobus.Status;
            if (st.IsAlive) { if (last >= 0 && st.SectionCount != last) changes++; last = st.SectionCount; seen.Add(st.SectionCount); }
            await Task.Delay(15);
        }
        sc.Step(F($"in 3 s the status's section count took the values {string.Join(", ", seen)} and changed {changes} times"));
        if (seen.Count > 1 && changes > 5)
            sc.Problem(Severity.Bug, "ISOBUS-TWO-IMPLEMENTS-FLICKER", F($"with two implements the task controller sends one PGN 240 per implement every 100 ms and AgOpenWeb keeps only the last one: the ISOBUS button's count flipped between {string.Join(" and ", seen)} sections {changes} times in 3 s, the ISOBUS row is rebuilt at every flip (the browser rebuilds it when the count changes) and the implement's sections shown belong to one or the other at random (AgOpenGPS has the same single-implement model)"));
        else sc.Ok("the status stayed on one implement");
        await DriveAsync(sc, s, 5, "5 s planting", planter);
        CheckWire(sc, tc);
        CheckRig(sc, s, "two implements");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K9
    private static async Task<ScenarioRow> NoBeacon()
    {
        var sc = new Scenario(Name, "K9", "Task controller with its beacon off and no implement yet",
            "The task controller runs with aogHeartbeatEnabled false (the setting for AgOpenGPS before 6.8.2 beta 5) and no implement is plugged in yet. 3 s later the planter is plugged in.");
        string root = NewDataRoot("k9");
        using var tc = new EmulatedTaskController { BeaconEnabled = false };
        using var steer = new SteerModuleStub { On = true };
        tc.Start();
        await using var s = await IsobusSession.StartAsync(root, tc);
        double seen = await s.WaitAliveAsync(true, 3000);
        if (double.IsNaN(seen)) sc.Note("no ISOBUS button for 3 s: with the beacon off and no implement the task controller sends nothing, so the operator cannot tell it is running (the protocol has no other sign; AgOpenGPS is the same)");
        else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", "the button showed although the task controller sent nothing");
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        sc.Step("Planter plugged in");
        seen = await s.WaitAliveAsync();
        if (!double.IsNaN(seen) && seen < 1) sc.Ok(F($"the button appeared {seen:0.00} s after the implement registered"));
        else sc.Problem(Severity.Bug, "ISOBUS-NOT-HEARD", F($"the button took {seen:0.0} s to appear after the implement registered"));
        CheckRig(sc, s, "the beacon");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K10
    private static async Task<ScenarioRow> JobClosedInAutomatic()
    {
        var sc = new Scenario(Name, "K10", "Field closed with the implement in automatic",
            "Planting in automatic, sections on; the operator closes the field (end of the day) without touching the master or the ISOBUS button. The implement must stop.");
        string root = NewDataRoot("k10");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        await using var s = await WorkingAsync(sc, root, tc, 24, 50, "K10");
        await AutomaticAsync(sc, s);
        await DriveAsync(sc, s, 3, "3 s planting", planter);
        await s.SendAsync("field.close");
        string? prompt = await s.Rig.AnswerPromptAsync(true);
        if (prompt != null) sc.Step("Prompt answered yes: " + prompt);
        sc.Step("Field closed");
        await Task.Delay(1500);
        ulong screen = s.Sections.GetSectionBits64();
        sc.Step(F($"1.5 s later: screen {Bits(screen, 24)}, setpoints at the task controller {Bits(tc.LastSetpoints, 24)} ({(tc.LastSetpointsMs < 0 ? "never" : (tc.NowMs - tc.LastSetpointsMs) + " ms old")}), implement {Bits(planter.Actual, 24)}; field open {s.Rig.Vm.IsFieldOpen}"));
        if (planter.Actual == 0 && tc.LastSetpoints == 0) sc.Ok("after the field closed the task controller got all sections off and the implement stopped");
        else if (planter.Actual != 0) sc.Problem(Severity.Safety, "ISOBUS-SECTIONS-ON-AFTER-CLOSE", F($"1.5 s after the field closed the implement still has sections {Bits(planter.Actual, 24)} on (setpoints at the task controller {Bits(tc.LastSetpoints, 24)})"));
        else sc.Note("the implement stopped but the last setpoints the task controller holds are not all off");
        await DriveAsync(sc, s, 3, "3 s driving with no field", planter, expectFollow: false);
        CheckWire(sc, tc);
        CheckRig(sc, s, "the close");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K11
    private static async Task<ScenarioRow> SubnetChanged()
    {
        var sc = new Scenario(Name, "K11", "Module subnet changed from the network page",
            "The operator sets the module subnet to 192.168.5 on the network page. AgOpenWeb sends PGN 201 to every module; the task controller rebinds to that subnet (on the loopback here, it just notes it).");
        string root = NewDataRoot("k11");
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(planter);
        tc.Start();
        await using var s = await IsobusSession.StartAsync(root, tc);
        await s.WaitAliveAsync();
        await s.SendAsync("net.subnet", "192.168.5");
        sc.Step("Subnet 192.168.5 set");
        await Task.Delay(1000);
        if (tc.LastSubnet == "192.168.5") sc.Ok("the task controller received PGN 201 in the layout it reads (0xC9 0xC9 192 168 5)");
        else sc.Problem(Severity.Bug, "ISOBUS-SUBNET-PGN", F($"the task controller received {(tc.Count(201) == 0 ? "no PGN 201" : "PGN 201 it could not read")} (last subnet it took: {tc.LastSubnet ?? "none"})"));
        CheckRig(sc, s, "the subnet change");
        sc.TcSnapshot(tc);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }
}
