// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using static AgOpenWeb.MachineModuleScenarios.Tests.Kit;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>
/// The careless operator, or just a normal day: modules switched on in the wrong order, a
/// module unplugged while settings change, Wi-Fi that drops, relay functions left at their
/// defaults, tram lights wired by AgOpenWeb's names, the GPS lost, more than 16 sections.
/// Each scenario has its own data folder and its own module.
/// </summary>
public static class CarelessOperator
{
    public const string Name = "Careless";

    private const string NotSentMeaning =
        "The module keeps what its flash had: with 0 sections the sprayer page has no width, so no L/ha and no dose alarm; relay functions and lift times changed in AgOpenWeb are not applied. Nothing on the screen says so.";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (Want(only, "K1")) journal.Add(await ModuleOnAfterAgOpenWeb());
        if (Want(only, "K2")) journal.Add(await ModuleOnBeforeAgOpenWebNoSteerModule());
        if (Want(only, "K3")) journal.Add(await SettingsChangedWhileModuleOff());
        if (Want(only, "K4")) journal.Add(await WiFiDrops());
        if (Want(only, "K5")) journal.Add(await DefaultRelayFunctions());
        if (Want(only, "K6")) journal.Add(await TramRelaysByAgOpenWebNames());
        if (Want(only, "K7")) journal.Add(await GpsLost());
        if (Want(only, "K8")) journal.Add(await NineteenSections());
    }

    // ------------------------------------------------------------------ K1
    private static async Task<ScenarioRow> ModuleOnAfterAgOpenWeb()
    {
        var sc = new Scenario(Name, "K1", "New sprayer module switched on after AgOpenWeb",
            "First install. The 8 m boom profile (8 × 100 cm, relays 7-8 set) was saved on an earlier day. The steer module is on; AgOpenWeb starts and the job begins; the new sprayer module (factory flash) is switched on 5 s later. Then the operator tries what anyone would: Save, restart AgOpenWeb, and at last change a width.");
        string root = NewDataRoot("k1");
        await PrepareProfileAsync(root, 8, 100, Relays78);
        using var module = new EmulatedSprayerModule();
        module.Start();
        using var steer = new SteerModuleStub { On = true };
        await using (var s = await MachineSession.StartAsync(root, module))
        {
            if (await s.StartJobAsync("K1") is { } why) sc.Harness(why);
            sc.Step("AgOpenWeb started with the saved 8 m profile; job open, master Auto; sprayer module still off");
            await DriveAsync(sc, s, 4, "Driving 4 s with the sprayer module off", expectFollow: false);
            module.PowerOn();
            sc.Step("The operator switches the sprayer module on");
            double seen = await WaitModuleSeenAsync(s);
            sc.Step(F($"Machine module seen by AgOpenWeb after {seen:0.0} s"));
            await DriveAsync(sc, s, 12, "Spraying 12 s (longer than the 10 s settle timer)");
            sc.CheckConfig(s, "12 s after the module came on", Severity.Bug, "MODULE-APPEARS-NO-SETTINGS",
                "AgOpenWeb sends its settings again when the STEER module appears (PGN 253 after 2 s of silence), never when the machine module does; here 236/238 went out when the steer module first talked, while the sprayer module was still off. " + NotSentMeaning);

            await s.SendAsync("profile.save");
            sc.Step("The operator presses Save on the Vehicle & Tool page");
            await Task.Delay(1500);
            bool ok = sc.CheckConfig(s, "after Save", Severity.Bug, "SAVE-DOES-NOT-RESEND",
                "Save sends PGN 235 only when it differs from the last one AgOpenWeb built (SendSectionDimensions), and the one built at start, before the UDP port was open, counts as sent.");
            sc.ModuleSnapshot(module, sc.Seconds);
        }

        await using (var s = await MachineSession.StartAsync(root, module, startNorth: 300))
        {
            sc.Step("The operator restarts AgOpenWeb (modules left on)");
            await WaitModuleSeenAsync(s);
            await Task.Delay(3000);
            bool ok = sc.CheckConfig(s, "3 s after the restart", Severity.Bug, "RESTART-DOES-NOT-SEND-WIDTHS",
                "At start AgOpenWeb sends its settings before its UDP port is open; the steer module's first PGN 253 brings 238/236/251/252/247 again, but PGN 235 (section widths) is never part of that.");
            if (!ok)
            {
                await s.SendAsync("config.set", "tool.sectionWidth:0,101");
                await s.SendAsync("profile.save");
                sc.Step("The operator changes section 1 to 101 cm and saves (a real change)");
                await Task.Delay(1500);
                if (sc.CheckConfig(s, "after a real width change", Severity.Bug, "SETTINGS-NOT-SENT", NotSentMeaning))
                    sc.Note("Only a real change of the section layout got the widths to the module.");
            }
            CheckWire(sc, module);
            sc.ModuleSnapshot(module, sc.Seconds);
        }
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K2
    private static async Task<ScenarioRow> ModuleOnBeforeAgOpenWebNoSteerModule()
    {
        var sc = new Scenario(Name, "K2", "Sprayer module on before AgOpenWeb, tractor without a steer module",
            "A tractor with only the sprayer module (no AutoSteer). The new module (factory flash) is on first, then AgOpenWeb starts with the saved 8 m profile; job and spraying.");
        string root = NewDataRoot("k2");
        await PrepareProfileAsync(root, 8, 100, Relays78);
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        double seen = await WaitModuleSeenAsync(s);
        sc.Step(F($"AgOpenWeb started; machine module seen after {seen:0.0} s"));
        await Task.Delay(3000);
        sc.Step(F($"Sent to the module since AgOpenWeb started: 235 ×{module.Count(235)}, 236 ×{module.Count(236)}, 238 ×{module.Count(238)}"));
        sc.CheckConfig(s, "3 s after AgOpenWeb started", Severity.Bug, "START-SETTINGS-LOST",
            "AutoSteerService.Start sends PGN 235 (and 251/252/238/236/247) before MainViewModel has opened the UDP port, so SendToModules drops them, but the 235 is kept as the last one sent, so no later Save sends the same widths. (236/238 did arrive here, from the settings debounce timer a start-up change happened to arm.) " + NotSentMeaning);
        if (await s.StartJobAsync("K2") is { } why) sc.Harness(why);
        await DriveAsync(sc, s, 10, "Spraying 10 s");
        sc.CheckConfig(s, "after 10 s of spraying", Severity.Bug, "START-SETTINGS-LOST", NotSentMeaning);
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K3
    private static async Task<ScenarioRow> SettingsChangedWhileModuleOff()
    {
        var sc = new Scenario(Name, "K3", "Settings changed while the sprayer module is unplugged",
            "Mid-job the operator unplugs the sprayer module to move its box, and meanwhile shortens section 8 to 50 cm and sets the lowering time to 6 s, and saves. Then the module goes back on.");
        string root = NewDataRoot("k3");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        await s.SetToolAsync(8, 100, Relays78);
        await Task.Delay(1500);
        sc.CheckConfig(s, "set-up (8 × 100 cm, relays 7-8, Save, module on)", Severity.Bug, "SETTINGS-NOT-SENT", "");
        if (await s.StartJobAsync("K3") is { } why) sc.Harness(why);
        await DriveAsync(sc, s, 5, "Spraying 5 s");

        module.PowerOff();
        sc.Step("The operator unplugs the sprayer module");
        var w = await DriveAsync(sc, s, 5, "Driving 5 s with the module off", expectFollow: false);
        sc.Step(F($"AgOpenWeb's module status went to 'not seen' after {w.FirstModuleLostAt:0.0} s"));
        var said = Said(s, t => t.Contains("achine", StringComparison.OrdinalIgnoreCase) || t.Contains("odule", StringComparison.OrdinalIgnoreCase));
        if (w.AreaPaintedLinkDown > 1 && said.Count == 0)
            sc.Problem(Severity.Improvement, "MODULE-LOST-SILENT", F($"with the sprayer module gone, AgOpenWeb kept the sections on and painted {w.AreaPaintedLinkDown:0} m² of coverage that was not sprayed; the only sign is the module dot in the status bar (no alarm, no message)."));

        await s.SendAsync("config.set", "tool.sectionWidth:7,50");
        await s.SendAsync("config.set", "machine.lowerTime:6");
        await s.SendAsync("profile.save");
        sc.Step("Section 8 → 50 cm, lowering time → 6 s; Save (module still off)");
        await Task.Delay(1000);
        module.PowerOn();
        sc.Step("The sprayer module goes back on");
        double seen = await WaitModuleSeenAsync(s);
        sc.Step(F($"Machine module seen again after {seen:0.0} s"));
        await DriveAsync(sc, s, 12, "Spraying 12 s");
        sc.CheckConfig(s, "12 s after the module came back", Severity.Bug, "MODULE-APPEARS-NO-SETTINGS",
            "AgOpenWeb resends its settings when the STEER module comes back, never when the machine module does; what was saved while the module was off is lost. " + NotSentMeaning);
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K4
    private static async Task<ScenarioRow> WiFiDrops()
    {
        var sc = new Scenario(Name, "K4", "Wi-Fi drops while spraying: 1 s, 5 s, 30 s",
            "The sprayer module is on Wi-Fi. While spraying, its link drops three times. The module shuts the valves 2 s after its last PGN 239 (Sprayer_ESP32 NO_DATA_TIMEOUT); what does AgOpenWeb show and paint?");
        string root = NewDataRoot("k4");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        await s.SetToolAsync(8, 100, Relays78);
        if (await s.StartJobAsync("K4") is { } why) sc.Harness(why);
        await DriveAsync(sc, s, 5, "Spraying 5 s");
        s.Rig.TakeEvents();
        double worst = 0, worstArea = 0;
        var noticed = new List<string>();
        foreach (double d in new[] { 1.0, 5.0, 30.0 })
        {
            module.NetworkUp = false;
            var w = await s.WatchAsync(d);
            module.NetworkUp = true;
            var r = await s.WatchAsync(6);
            int n = s.Store.NumSections;
            sc.Step(F($"Wi-Fi down {d:0} s: {w.Summary(n)}"));
            sc.Step(F($"  6 s after it came back: {r.Summary(n)}"));
            double painted = w.PaintWhileValvesClosed + r.PaintWhileValvesClosed;
            double area = w.AreaPaintedLinkDown + r.AreaPaintedLinkDown;
            noticed.Add(double.IsNaN(w.FirstModuleLostAt) ? F($"{d:0} s drop: module status never changed") : F($"{d:0} s drop: module status 'not seen' after {w.FirstModuleLostAt:0.0} s"));
            if (painted > worst) { worst = painted; worstArea = area; }
            bool back = r.ModuleOkSeconds > 0 && Enumerable.Range(0, n).All(i => r.OnButClosed[i] < 1.5);
            if (!back) sc.Problem(Severity.Bug, "WIFI-NO-RECOVERY", F($"after a {d:0} s drop the valves did not follow the screen again within 6 s"));
        }
        var all = Said(s);
        var said = all.Where(t => !t.StartsWith("Status:", StringComparison.Ordinal)).ToList();
        var status = all.Where(t => t.StartsWith("Status:", StringComparison.Ordinal)).ToList();
        sc.Step("Failures and warnings from AgOpenWeb meanwhile: " + (said.Count == 0 ? "none" : string.Join(" | ", said))
            + "; its status text (not shown by the web client) went " + (status.Count == 0 ? "unchanged" : string.Join(" → ", status.Select(t => t[8..]))));
        foreach (var t in noticed) sc.Note(t);
        if (worst > 1)
            sc.Problem(Severity.Improvement, "MODULE-LOST-SILENT",
                F($"while the module's link was down, AgOpenWeb kept the sections on and painted coverage for {worst:0.0} s ({worstArea:0} m²) with the valves shut. The operator sees the field as sprayed; the only sign is the module dot in the status bar (no alarm, sections not turned off, no message)."));
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K5
    private static async Task<ScenarioRow> DefaultRelayFunctions()
    {
        var sc = new Scenario(Name, "K5", "8 sections, relay functions left at AgOpenWeb's defaults",
            "The operator types the 8 m boom (8 × 100 cm) and saves, but never opens the relay page: AgOpenWeb's defaults give relays 1-6 to sections 1-6.");
        string root = NewDataRoot("k5");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        await s.SetToolAsync(8, 100);
        await Task.Delay(1500);
        sc.Step("Tool: 8 × 100 cm; Save; relay functions untouched (" + string.Join(" ", s.Store.Machine.PinAssignments.Take(8).Select(p => (int)p)) + ")");
        sc.CheckConfig(s, "1.5 s after Save", Severity.Bug, "SETTINGS-NOT-SENT", "");
        if (await s.StartJobAsync("K5") is { } why) sc.Harness(why);
        await DriveAsync(sc, s, 10, "Spraying 10 s");
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K6
    private static async Task<ScenarioRow> TramRelaysByAgOpenWebNames()
    {
        var sc = new Scenario(Name, "K6", "Tram light relays chosen by AgOpenWeb's names",
            "A 6-section boom (6 × 133 cm). The operator wires the LEFT tram light to relay 7 and the RIGHT one to relay 8, and on the relay page picks the function AgOpenWeb names 'TramLeft' for relay 7 and 'TramRight' for relay 8. Tram lines on; the operator presses the manual Left tram button, then Right.");
        string root = NewDataRoot("k6");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        // By AgOpenWeb's names, as the operator picks them from the list.
        int left = (int)AgOpenWeb.Models.Configuration.PinFunction.TramLeft, right = (int)AgOpenWeb.Models.Configuration.PinFunction.TramRight;
        await s.SetToolAsync(6, 133, new[] { (6, left), (7, right) });
        await Task.Delay(1500);
        sc.Step(F($"Relay page: relay 7 = TramLeft ({left}), relay 8 = TramRight ({right}); Save"));
        sc.CheckConfig(s, "1.5 s after Save", Severity.Bug, "SETTINGS-NOT-SENT", "");
        if (await s.StartJobAsync("K6") is { } why) sc.Harness(why);
        var (noTram, modeAfterToggle) = await s.TramOnAsync();
        if (noTram != null) { sc.Harness(noTram); sc.ModuleSnapshot(module, sc.Seconds); return sc.Row; }
        sc.Step(F($"Tram system 24 m on the AB line; the config page's 'Tram lights' toggle shows {(s.Store.Guidance.TramDisplay ? "On" : "Off")}, so the operator leaves it; tram display mode {modeAfterToggle}"));
        if (modeAfterToggle == "Off" && s.Store.Guidance.TramDisplay)
        {
            sc.Problem(Severity.Inconsistency, "TRAM-TOGGLE-SAYS-ON",
                "the config page's 'Tram lights' toggle shows On from the start (it shows GuidanceConfig.TramDisplay, true by default and loaded from the profile), but the tram display mode that drives the lights and the tram lines on the map (TramConfig.DisplayMode) starts Off and only follows the toggle when the toggle changes. So with the toggle showing On, no tram byte goes to the module and the manual Left/Right buttons are cleared on every fix; the first tap on the toggle turns it Off (no change), the second turns the lights on. The map's tram button works, but leaves the toggle as it was.");
            await s.SendAsync("tram.cycle");
            sc.Step(F($"The operator presses the map's tram button: display mode {s.Store.Tram.DisplayMode}"));
        }
        await Task.Delay(1500);
        sc.Step(F($"Tram lines on; before any button: PGN 239 tram byte {module.Tramline}, relays 7/8 = {Bit(module.Relays, 6)}/{Bit(module.Relays, 7)}"));
        byte baseTram = module.Tramline;

        await s.SendAsync("tram.manualLeft");
        await Task.Delay(800);
        byte leftByte = module.Tramline;
        sc.Step(F($"  AgOpenWeb: tram display {s.Store.Tram.DisplayMode}, tram lines {s.Rig.Tram.HasTramLines}, manual left {s.Rig.Tram.IsLeftManualOn}, tool {s.Store.ActualToolWidth:0.00} m, track {s.Store.Vehicle.TrackWidth:0.00} m, tram state {s.Rig.AutoSteer.LatestSnapshot?.TramState}"));
        bool r7 = Bit(module.Relays, 6), r8 = Bit(module.Relays, 7);
        sc.Step(F($"Manual LEFT on: tram byte {leftByte} (bit 0 = right wheel, bit 1 = left), relay 7 ('Tram Left') {(r7 ? "ON" : "off")}, relay 8 ('Tram Right') {(r8 ? "ON" : "off")}"));
        await s.SendAsync("tram.manualLeft");
        await s.SendAsync("tram.manualRight");
        await Task.Delay(800);
        byte rightByte = module.Tramline;
        bool q7 = Bit(module.Relays, 6), q8 = Bit(module.Relays, 7);
        sc.Step(F($"Manual RIGHT on (left off): tram byte {rightByte}, relay 7 ('Tram Left') {(q7 ? "ON" : "off")}, relay 8 ('Tram Right') {(q8 ? "ON" : "off")}"));
        await s.SendAsync("tram.manualRight");

        if (baseTram != 0) sc.Note(F($"the wheels were on a tram line before the buttons (tram byte {baseTram}); the relay check below may be blurred"));
        if (!r7 && r8 && q7 && !q8)
            sc.Problem(Severity.Bug, "TRAM-LABELS-SWAPPED",
                F($"the Left button lit the relay AgOpenWeb calls 'TramRight', and the Right button the one it calls 'TramLeft'. AgOpenWeb names output function {left} 'TramLeft' and {right} 'TramRight'; AgOpenGPS (ConfigModule: 19 = Tram Right, 20 = Tram Left), the official Machine_UDP firmware and Sprayer_ESP32 use 19 = right, 20 = left. AgOpenWeb itself sends bit 0 = right wheel, bit 1 = left, as AgOpenGPS does; only the names on the relay page are swapped."));
        else if (r7 && !r8 && !q7 && q8)
            sc.Ok("each tram button lit the relay with its name");
        else
            sc.Note(F($"unclear tram result: left → relays 7/8 {r7}/{r8}, right → {q7}/{q8}"));
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    private static bool Bit(uint v, int i) => ((v >> i) & 1) != 0;

    // ------------------------------------------------------------------ K7
    private static async Task<ScenarioRow> GpsLost()
    {
        var sc = new Scenario(Name, "K7", "GPS lost for 6 s while spraying",
            "Under a line of trees the receiver stops sending for 6 s (the tractor keeps going). What do the sections, the valves and the coverage do?");
        string root = NewDataRoot("k7");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        await s.SetToolAsync(8, 100, Relays78);
        if (await s.StartJobAsync("K7") is { } why) sc.Harness(why);
        await DriveAsync(sc, s, 5, "Spraying 5 s");
        s.Rig.TakeEvents();
        s.GpsOn = false;
        var w = await s.WatchAsync(6);
        // the tractor kept moving: 6 s at 8 km/h
        s.N += 8 / 3.6 * 6;
        s.GpsOn = true;
        int n = s.Store.NumSections;
        sc.Step("GPS silent 6 s: " + w.Summary(n));
        sc.Step(F($"  valves all shut {(double.IsNaN(w.FirstAllClosedAt) ? "never" : w.FirstAllClosedAt.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s")} after the GPS went silent"));
        var r = await s.WatchAsync(6);
        sc.Step("6 s after the GPS came back: " + r.Summary(n));
        var said = Said(s, t => t.Contains("GPS", StringComparison.OrdinalIgnoreCase));
        sc.Step("What AgOpenWeb said: " + (said.Count == 0 ? "nothing about the GPS" : string.Join(" | ", said)));
        if (w.ValvesOpenWhileGpsOff > 3)
            sc.Problem(Severity.Bug, "GPS-LOST-VALVES-OPEN",
                F($"with no GPS the valves stayed open {w.ValvesOpenWhileGpsOff:0.0} s of 6 s: the 100 Hz control tick keeps sending PGN 239 with the last section state. Coverage painted meanwhile: {w.AreaPaintedGpsOff:0} m²."));
        else
            sc.Ok(F($"AgOpenWeb turned the sections off {w.FirstAllClosedAt:0.0} s after the GPS went silent (its 2 s GPS-lost rule), with a warning; the valves were open {w.ValvesOpenWhileGpsOff:0.0} s with no position, {w.AreaPaintedGpsOff:0} m² painted of about {8 / 3.6 * w.ValvesOpenWhileGpsOff * 8:0} m² sprayed"));
        if (r.ScreenOnSeconds < 1)
        {
            sc.Note("after the GPS came back the sections stayed off until the operator pressed Auto again (as designed: MainViewModel.GpsLost, 'the operator re-engages once data is back')");
            await s.SendAsync("section.master");
            sc.Step("The operator presses Auto again");
            await DriveAsync(sc, s, 5, "Spraying 5 s");
        }
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }

    // ------------------------------------------------------------------ K8
    private static async Task<ScenarioRow> NineteenSections()
    {
        var sc = new Scenario(Name, "K8", "More than 16 sections",
            "A 19-section tool (19 × 42 cm, 7.98 m) on a machine module. PGN 239 carries 16 sections, PGN 229 carries 64, PGN 235 has room for 16 widths. What reaches the module?");
        string root = NewDataRoot("k8");
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();
        using var steer = new SteerModuleStub { On = true };
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);
        await using var s = await MachineSession.StartAsync(root, module);
        await WaitModuleSeenAsync(s);
        await s.SetToolAsync(19, 42);
        await Task.Delay(1500);
        if (await s.StartJobAsync("K8") is { } why) sc.Harness(why);
        await s.WatchAsync(5);
        var f = module.FlashCopy();
        int widths = f.SectionCm.Count(c => c > 0);
        double sum = f.SectionCm.Sum(c => (double)c) / 100;
        sc.Step(F($"Module: {f.ToolSections} sections, {widths} widths that add up to {sum:0.00} m; PGN 229 ×{module.Count(229)}; sections asked {System.Numerics.BitOperations.PopCount(module.SectionsAsked)}"));
        if (module.Count(229) > 0 && System.Numerics.BitOperations.PopCount(module.SectionsAsked) == 19)
            sc.Ok("PGN 229 carried all 19 section states");
        else
            sc.Problem(Severity.Bug, "SECTIONS-OVER-16-LOST", F($"the module was asked for {System.Numerics.BitOperations.PopCount(module.SectionsAsked)} of 19 sections (PGN 229 ×{module.Count(229)})"));
        if (f.ToolSections == 19 && widths == 16)
            sc.Problem(Severity.Improvement, "PGN235-16-WIDTHS",
                F($"PGN 235 says 19 sections but holds 16 widths: a module that adds them up (the sprayer's L/ha) gets {sum:0.00} m of a 7.98 m tool. It is the protocol's size (AgOpenGPS's PGN 235 has 16); a module must not trust the count beyond 16, and AgOpenWeb could say so where the widths are typed."));
        CheckWire(sc, module);
        sc.ModuleSnapshot(module, sc.Seconds);
        try { Directory.Delete(root, true); } catch { }
        return sc.Row;
    }
}
