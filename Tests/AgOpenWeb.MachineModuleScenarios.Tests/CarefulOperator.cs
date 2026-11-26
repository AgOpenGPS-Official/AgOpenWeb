// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using static AgOpenWeb.MachineModuleScenarios.Tests.Kit;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>
/// The careful operator: switches the modules on before AgOpenWeb, types the 8 m boom in
/// right, saves, and works a day: a width change, another tool profile and back, then a
/// restart of AgOpenWeb with the modules left on. Everything here should just work.
/// </summary>
public static class CarefulOperator
{
    public const string Name = "Careful";

    public static async Task RunAsync(Journal journal, string? only)
    {
        if (!Want(only, "C")) return;
        using var module = new EmulatedSprayerModule();
        module.Start();
        module.PowerOn();                       // a new board: its flash has the factory values
        using var steer = new SteerModuleStub { On = true };
        string root = NewDataRoot("careful");
        await Task.Delay(EmulatedSprayerModule.BootMs + 200);

        var sc = new Scenario(Name, "C1", "First set-up with the modules on",
            "Steer and sprayer modules on (the sprayer module new, factory flash), then AgOpenWeb. The operator types the 8 m boom (8 × 100 cm), sets relays 7 and 8 to sections 7 and 8, saves, opens a field and sprays with master Auto.");
        await using (var s = await MachineSession.StartAsync(root, module))
        {
            double seen = await WaitModuleSeenAsync(s);
            sc.Step(double.IsNaN(seen) ? "AgOpenWeb started; the machine module was NOT seen within 8 s" : F($"AgOpenWeb started; machine module seen after {seen:0.0} s"));
            if (double.IsNaN(seen)) sc.Harness("AgOpenWeb never saw the emulated machine module (hello 200 → 123 on the loopback)");
            await s.SetToolAsync(8, 100, Relays78);
            sc.Step("Tool: 8 sections × 100 cm; relays 7-8 → sections 7-8; Save");
            await Task.Delay(1500);
            sc.CheckConfig(s, "1.5 s after Save", Severity.Bug, "SETTINGS-NOT-SENT", "The module works with other settings than the ones on the screen.");
            if (await s.StartJobAsync("Careful") is { } why) sc.Harness(why);
            sc.Step("Field, AB line, job, master Auto");
            var w = await DriveAsync(sc, s, 15, "Spraying 15 s");
            double rate = module.Count(239) / Math.Max(1, sc.Seconds);
            sc.Note(F($"AgOpenWeb sent PGN 239 about {rate:0} times a second (the 100 Hz control tick plus each GPS fix; AgOpenGPS sends it once per fix). The module's 2 s timeout never fired: link down {w.LinkDownSeconds:0.0} s"));
            CheckWire(sc, module);
            sc.ModuleSnapshot(module, sc.Seconds);
            journal.Add(sc.Row);

            sc = new Scenario(Name, "C2", "Change the section widths and save",
                "Mid-morning the operator measures the boom again and types 75 cm for every section, then Save.");
            int before235 = module.Count(235);
            await s.SetToolAsync(8, 75);
            sc.Step("Tool: 8 × 75 cm; Save");
            await Task.Delay(1500);
            sc.CheckConfig(s, "1.5 s after Save", Severity.Bug, "SETTINGS-NOT-SENT", "The module still works with the old widths.");
            sc.Step(F($"PGN 235 received since the change: {module.Count(235) - before235}"));
            await DriveAsync(sc, s, 6, "Spraying 6 s");
            CheckWire(sc, module);
            sc.ModuleSnapshot(module, sc.Seconds);
            journal.Add(sc.Row);

            sc = new Scenario(Name, "C3", "Another tool profile, then back",
                "The operator makes a second tool profile for a 6 m boom (6 × 100 cm), saves it, then loads the first profile again.");
            string vehicle = s.Store.ActiveVehicleProfileName, tool = s.Store.ActiveToolProfileName;
            await s.SendAsync("profile.new", "tool\tBoom 6 m");
            await s.SetToolAsync(6, 100);
            sc.Step(F($"New tool profile 'Boom 6 m': 6 × 100 cm; Save (active: {s.Store.ActiveToolProfileName})"));
            await Task.Delay(1500);
            sc.CheckConfig(s, "after saving 'Boom 6 m'", Severity.Bug, "SETTINGS-NOT-SENT", "The module still works with the other profile's settings.");
            await s.SendAsync("profile.load", vehicle + "\t" + tool);
            sc.Step(F($"Load '{vehicle}' + '{tool}' (now {s.Store.NumSections} sections)"));
            await Task.Delay(1500);
            sc.CheckConfig(s, "after loading the first profile", Severity.Bug, "SETTINGS-NOT-SENT", "The module still works with the 6 m profile's settings.");
            await DriveAsync(sc, s, 6, "Spraying 6 s");
            CheckWire(sc, module);
            sc.ModuleSnapshot(module, sc.Seconds);
            journal.Add(sc.Row);
        }

        sc = new Scenario(Name, "C4", "Restart AgOpenWeb with the modules left on",
            "At lunch the operator closes AgOpenWeb (the tablet's battery), the modules stay on. After the restart: a new field and on with the work.");
        var counts = (module.Count(235), module.Count(236), module.Count(238));
        await using (var s = await MachineSession.StartAsync(root, module, startNorth: 500))
        {
            double seen = await WaitModuleSeenAsync(s);
            sc.Step(F($"AgOpenWeb restarted; machine module seen after {seen:0.0} s; tool {s.Store.NumSections} sections"));
            if (s.Store.NumSections != 8) sc.Harness(F($"the saved profile did not come back after a restart ({s.Store.NumSections} sections)"));
            await Task.Delay(3000);
            sc.Step(F($"Sent to the module since the restart: 235 ×{module.Count(235) - counts.Item1}, 236 ×{module.Count(236) - counts.Item2}, 238 ×{module.Count(238) - counts.Item3}"));
            sc.CheckConfig(s, "3 s after the restart", Severity.Bug, "SETTINGS-NOT-SENT", "The module works with other settings than the ones on the screen.");
            if (await s.StartJobAsync("Careful afternoon") is { } why) sc.Harness(why);
            await DriveAsync(sc, s, 10, "Spraying 10 s");
            CheckWire(sc, module);
            sc.ModuleSnapshot(module, sc.Seconds);
            journal.Add(sc.Row);
        }
        try { Directory.Delete(root, true); } catch { }
    }
}
