// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using static AgOpenWeb.IsobusScenarios.Tests.Kit;

namespace AgOpenWeb.IsobusScenarios.Tests;

/// <summary>
/// The careful operator: the task controller (the HAT) is on before AgOpenWeb, the implement
/// is plugged in, the tool is typed in with the implement's section count, and the day goes as
/// it should: a sprayer in the morning, a planter after, section control automatic, a few
/// switches to manual and back, the task controller restarted once, the planter unplugged and
/// plugged back, AgOpenWeb restarted at lunch. Everything here should just work.
/// </summary>
public static class CarefulOperator
{
    public const string Name = "Careful";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (!Want(only, "C")) return;
        using var tc = new EmulatedTaskController();
        using var steer = new SteerModuleStub { On = true };
        var sprayer = new Implement("Sprayer", 8, 12);
        var planter = new Implement("Planter", 24, 12);
        tc.Connect(sprayer);
        tc.Start();
        await Task.Delay(500);

        // ------------------------------------------------------------------ C1
        var sc = new Scenario(Name, "C1", "An 8-section sprayer on the task controller, section control automatic",
            "The task controller and the steer module are on, the sprayer (8 sections, 12 m) is registered; then AgOpenWeb starts. The operator types the tool (8 × 150 cm), opens a field, starts the job with master Auto and taps the ISOBUS button for automatic section control.");
        string root1 = NewDataRoot("careful-sprayer");
        await using (var s = await IsobusSession.StartAsync(root1, tc))
        {
            int msgs = tc.MessagesSent.Count;
            double seen = await s.WaitAliveAsync();
            sc.Step(double.IsNaN(seen) ? "AgOpenWeb started; the task controller was NOT heard within 5 s" : F($"AgOpenWeb started; task controller heard after {seen:0.0} s"));
            if (double.IsNaN(seen)) { sc.Harness("AgOpenWeb never heard the emulated task controller (PGN 240 on the loopback)"); journal.Add(sc.Row); return; }
            var st = s.Isobus.Status;
            var dto = s.Dto();
            if (st.SectionCount == 8 && dto is { Sections: 8 } && !st.SectionControlEnabled)
                sc.Ok(F($"the ISOBUS button shows: {st.SectionCount} sections, section control {(st.SectionControlEnabled ? "on" : "off")}, implement sections {Bits(st.ReportedSections, 8)}"));
            else
                sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", F($"after the first heartbeats the status says {st.SectionCount} sections, control {(st.SectionControlEnabled ? "on" : "off")}, frame {(dto == null ? "none" : dto.Sections.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sections")} (expected 8, off)"));
            await s.SetToolAsync(8, 150);
            sc.Step("Tool: 8 sections × 150 cm; Save");
            if (await s.StartJobAsync("Careful sprayer") is { } why) sc.Harness(why);
            sc.Step("Field, AB line, job, master Auto");
            long t0 = tc.NowMs;
            double got = await s.WantModeAsync(true);
            sc.Step(double.IsNaN(got) ? "ISOBUS button tapped; section control did not show on within 2 s" : F($"ISOBUS button tapped; section control on after {got:0.00} s"));
            if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller obeys PGN 241 at once, but AgOpenWeb did not show section control on within 2 s");
            await Task.Delay(1200);
            int reqs = tc.ModeRequestsSince(t0);
            if (reqs == 1) sc.Ok("one PGN 241 for the tap, none repeated once the heartbeat showed it");
            else sc.Problem(Severity.Bug, "ISOBUS-REQUEST-REPEATED", F($"{reqs} PGN 241 for one tap although the heartbeat showed the mode within 0.1 s"));
            var w = await DriveAsync(sc, s, 10, "Spraying 10 s", sprayer);
            if (tc.Count(229) == 0)
                sc.Note(F($"the task controller received PGN 254 ×{tc.Count(254)} and 239 ×{tc.Count(239)} in this time (they carry sections 1-16 too, but the task controller does not read them)"));
            await s.TapMasterAsync();
            sc.Step("Master off");
            await DriveAsync(sc, s, 3, "Driving 3 s with the master off", sprayer, expectFollow: false);
            CheckMessages(sc, s, msgs, 0, "while AgOpenWeb ran");
            CheckWire(sc, tc);
            CheckRig(sc, s, "the sprayer morning");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);
        }
        try { Directory.Delete(root1, true); } catch { }

        // ------------------------------------------------------------------ C2
        tc.Disconnect(sprayer);
        tc.Connect(planter);
        tc.SectionControlEnabled = false;   // the planter starts the day in manual (chosen on the terminal)
        await Task.Delay(300);
        sc = new Scenario(Name, "C2", "A 24-section planter, section control automatic",
            "After the sprayer, the planter (24 sections, 12 m) is plugged into the bus and AgOpenWeb starts for it. The operator types the tool (24 × 50 cm), opens a field, job, master Auto, ISOBUS button for automatic section control, and plants.");
        string root2 = NewDataRoot("careful-planter");
        await using (var s = await IsobusSession.StartAsync(root2, tc))
        {
            double seen = await s.WaitAliveAsync();
            sc.Step(F($"AgOpenWeb started; task controller heard after {seen:0.0} s; {s.Isobus.Status.SectionCount} sections"));
            await s.SetToolAsync(24, 50);
            sc.Step("Tool: 24 sections × 50 cm; Save");
            if (await s.StartJobAsync("Careful planter") is { } why) sc.Harness(why);
            sc.Step("Field, AB line, job, master Auto");
            double got = await s.WantModeAsync(true);
            sc.Step(double.IsNaN(got) ? "ISOBUS button tapped; section control did not show on within 2 s" : F($"ISOBUS button tapped; section control on after {got:0.00} s"));
            if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller obeys PGN 241 at once, but AgOpenWeb did not show section control on within 2 s");
            int c229 = tc.Count(229), c254 = tc.Count(254), c239 = tc.Count(239);
            double tStart = sc.Seconds;
            var w = await DriveAsync(sc, s, 10, "Planting 10 s", planter);
            double dt = Math.Max(0.1, sc.Seconds - tStart);
            sc.Note(F($"the task controller received PGN 229 about {(tc.Count(229) - c229) / dt:0} times a second ({tc.SetpointChanges} setpoint changes in all), 254 ×{(tc.Count(254) - c254) / dt:0}/s, 239 ×{(tc.Count(239) - c239) / dt:0}/s (AgOpenGPS sends each once per fix, 10/s; the task controller parses every one)"));
            if (tc.Count(214) == 0)
                sc.Problem(Severity.Improvement, "ISOBUS-NO-GNSS-QUALITY", F($"in {sc.Seconds:0} s the task controller received no PGN 214 (AgIO's GPS data, source 0x7C): AgOpenWeb sends it to the loopback port 15555 only (SendGpsDataToLoopback), and the task controller listens on 8888, so it never learns the fix quality and tells every implement DDI 514 = 1 (plain GPS) whatever the receiver has, as the AOG-TaskController protocol notes for a default AgIO install; some implements gate section control on the GNSS quality"));
            else sc.Ok(F($"PGN 214 (fix quality) reached the task controller ×{tc.Count(214)}"));
            if (tc.Count(242) == 0)
                sc.Note("no PGN 242 (process data: speed DDI 156, distance 597, cross-track error) reached the task controller; AgOpenWeb does not send it, so a task controller acting as the tractor ECU has no speed to give the implements (AgIO sends none either)");
            CheckWire(sc, tc);
            CheckRig(sc, s, "the planter set-up");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);

            // -------------------------------------------------------------- C3
            sc = new Scenario(Name, "C3", "Master off and on, section control manual and back",
                "While planting: master off at the end of a pass and on again; then the operator switches the implement to manual (ISOBUS button), drives a bit, and back to automatic.");
            await s.TapMasterAsync();
            sc.Step("Master off");
            await DriveAsync(sc, s, 3, "3 s with the master off", planter);
            await s.TapMasterAsync();
            sc.Step("Master Auto");
            await DriveAsync(sc, s, 3, "3 s planting again", planter);
            long t0 = tc.NowMs;
            got = await s.WantModeAsync(false);
            sc.Step(double.IsNaN(got) ? "ISOBUS button tapped; manual did not show within 2 s" : F($"ISOBUS button tapped; section control off (manual) after {got:0.00} s"));
            if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller obeyed PGN 241 (manual), but AgOpenWeb did not show it within 2 s");
            w = await DriveAsync(sc, s, 4, "4 s in manual", planter, expectFollow: false);
            if (w.ScreenOnSeconds > 3 && w.AreaPainted > 1)
                sc.Note(F($"in manual AgOpenWeb keeps its own sections on and paints coverage ({w.AreaPainted:0} m²) while the implement's own switches decide what it does (its sections {Bits(planter.Actual, 24)}); the ISOBUS row shows the difference, as AgOpenGPS"));
            got = await s.WantModeAsync(true);
            sc.Step(double.IsNaN(got) ? "ISOBUS button tapped; automatic did not show within 2 s" : F($"ISOBUS button tapped; section control on after {got:0.00} s"));
            if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-MODE-NOT-SET", "the task controller obeyed PGN 241 (automatic), but AgOpenWeb did not show it within 2 s");
            int reqs = tc.ModeRequestsSince(t0);
            if (reqs == 2) sc.Ok("one PGN 241 per tap (two taps, two requests)");
            else sc.Problem(Severity.Bug, "ISOBUS-REQUEST-REPEATED", F($"{reqs} PGN 241 for two taps that the task controller obeyed at once"));
            await DriveAsync(sc, s, 3, "3 s planting again", planter);
            CheckWire(sc, tc);
            CheckRig(sc, s, "the mode changes");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);

            // -------------------------------------------------------------- C4
            sc = new Scenario(Name, "C4", "The task controller restarts",
                "The task controller process restarts (the HAT's daemon updated, or a crash): 3 s away, then back with section control off for every implement, as after any restart. The operator does nothing.");
            tc.Stop();
            sc.Step("Task controller stopped");
            w = await DriveAsync(sc, s, 3, "3 s without the task controller", planter, expectFollow: false);
            if (double.IsNaN(w.FirstNotAliveAt)) sc.Problem(Severity.Bug, "ISOBUS-LOST-NOT-NOTICED", "3 s without a heartbeat and AgOpenWeb still showed the task controller (the button stays while a heartbeat arrived in the last second)");
            else if (w.FirstNotAliveAt <= 1.3) sc.Ok(F($"the ISOBUS button went away {w.FirstNotAliveAt:0.0} s after the last heartbeat"));
            else sc.Problem(Severity.Bug, "ISOBUS-LOST-LATE", F($"the ISOBUS button stayed {w.FirstNotAliveAt:0.0} s after the last heartbeat (1 s expected)"));
            long t1 = tc.NowMs;
            tc.Restart();
            sc.Step("Task controller back (section control off, as after a restart)");
            double back = await s.WaitAliveAsync();
            got = await s.WaitModeAsync(true, 3000);
            sc.Step(F($"heard again after {back:0.0} s; section control on again after {got:0.00} s"));
            int again = tc.ModeRequestsSince(t1);
            if (!double.IsNaN(got) && again == 1) sc.Ok("AgOpenWeb asked for automatic once more when the task controller came back in manual (the operator's last choice), and once only");
            else if (double.IsNaN(got)) sc.Problem(Severity.Bug, "ISOBUS-RESTART-NOT-REQUESTED", F($"after the restart the task controller reported manual and AgOpenWeb did not ask for automatic again within 3 s (PGN 241 sent ×{again})"));
            else sc.Problem(Severity.Bug, "ISOBUS-REQUEST-REPEATED", F($"{again} PGN 241 after the restart for one wanted mode"));
            await DriveAsync(sc, s, 4, "4 s planting after the restart", planter);
            CheckWire(sc, tc);
            CheckRig(sc, s, "the restart");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);

            // -------------------------------------------------------------- C5
            sc = new Scenario(Name, "C5", "The planter unplugged and plugged back",
                "The planter's ISOBUS plug comes out for 4 s (the task controller stays up with no implement, its beacon on), then goes back in and the planter registers again.");
            int msgs = tc.MessagesSent.Count;
            double tm = s.Seconds;
            tc.Disconnect(planter);
            sc.Step("Planter unplugged");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !(s.Isobus.Status.IsAlive && s.Isobus.Status.SectionCount == 0)) await Task.Delay(20);
            var st = s.Isobus.Status;
            if (st.IsAlive && st.SectionCount == 0) sc.Ok(F($"after {sw.Elapsed.TotalSeconds:0.00} s the button shows the task controller with no implement (0 sections)"));
            else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", F($"2 s after the implement left, the status says alive {st.IsAlive}, {st.SectionCount} sections (expected alive, 0)"));
            w = await DriveAsync(sc, s, 4, "4 s with no implement", planter, expectFollow: false);
            CheckMessages(sc, s, msgs, tm, "when the planter left");
            msgs = tc.MessagesSent.Count;
            tm = s.Seconds;
            tc.Connect(planter);
            sc.Step("Planter plugged back");
            sw.Restart();
            while (sw.ElapsedMilliseconds < 2000 && s.Isobus.Status.SectionCount != 24) await Task.Delay(20);
            st = s.Isobus.Status;
            if (st.SectionCount == 24) sc.Ok(F($"after {sw.Elapsed.TotalSeconds:0.00} s the button shows 24 sections again, section control {(st.SectionControlEnabled ? "on" : "off")}"));
            else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", F($"2 s after the implement came back the status says {st.SectionCount} sections"));
            await Task.Delay(500);
            await DriveAsync(sc, s, 4, "4 s planting again", planter);
            CheckMessages(sc, s, msgs, tm, "when the planter came back");
            CheckWire(sc, tc);
            CheckRig(sc, s, "the implement swap");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);
        }

        // ------------------------------------------------------------------ C6
        sc = new Scenario(Name, "C6", "AgOpenWeb restarted with the task controller on",
            "At lunch the operator closes AgOpenWeb (the tablet's battery), the task controller and the planter stay on in automatic. After the restart: a new field and on with the work. AgOpenWeb has nothing to ask: it should show what the task controller reports and not send a mode of its own.");
        int before = tc.ModeRequests.Count;
        await using (var s = await IsobusSession.StartAsync(root2, tc, startNorth: 600))
        {
            double seen = await s.WaitAliveAsync();
            var st = s.Isobus.Status;
            sc.Step(F($"AgOpenWeb restarted; task controller heard after {seen:0.0} s; {st.SectionCount} sections, section control {(st.SectionControlEnabled ? "on" : "off")}; tool {s.Store.NumSections} sections"));
            if (s.Store.NumSections != 24) sc.Harness(F($"the saved profile did not come back after a restart ({s.Store.NumSections} sections)"));
            await Task.Delay(2000);
            int sent = tc.ModeRequests.Count - before;
            if (sent == 0 && st.SectionControlEnabled) sc.Ok("the restarted AgOpenWeb shows the task controller's mode (automatic) and sent no PGN 241 of its own");
            else if (sent > 0) sc.Problem(Severity.Bug, "ISOBUS-RESTART-REQUESTS", F($"the restarted AgOpenWeb sent PGN 241 ×{sent} with no tap from the operator"));
            else sc.Problem(Severity.Bug, "ISOBUS-STATUS-WRONG", "after the restart the button does not show the automatic mode the task controller reports");
            if (await s.StartJobAsync("Careful afternoon") is { } why) sc.Harness(why);
            await DriveAsync(sc, s, 5, "5 s planting after lunch", planter);
            CheckWire(sc, tc);
            CheckRig(sc, s, "the afternoon");
            sc.TcSnapshot(tc);
            journal.Add(sc.Row);
        }
        try { Directory.Delete(root2, true); } catch { }
    }
}
