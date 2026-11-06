// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The careful operator: measures every machine right, types it in right, and saves it as a
/// profile. Each vehicle with the implements it would pull, on flat ground and across a 6°
/// slope, with the guidance hardware it carries (GPS, IMU, steering motor of the Boards
/// simulator). Then every profile is saved, AgOpenWeb restarted, and each one loaded back.
/// Anything short of "on the line, no gaps, sections on" is a finding.
/// </summary>
public static class CarefulOperator
{
    public const string Name = "careful operator";

    // A careful setup steers the implement onto its line within a few centimetres.
    private const double LineTolerance = 0.05, JoinTolerance = 0.10, ToolSideTolerance = 0.10;
    // The harness runs faster than real time: AgOpenWeb's implement can trail by two fixes (0.5 m).
    private const double ToolAlongTolerance = 0.6;

    public static async Task RunAsync(Journal journal, string? only)
    {
        foreach (var (v, t, slope, sys) in Machines.CarefulMatrix)
        {
            string label = $"{v.Name} + {t.Name}, {sys.Label}, {slope:0}°";
            if (only != null && !label.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var d = new DriveSpec("C" + journal.Drives.Count, label, v, t, sys, slope, Drive.Careful(v, t, sys), Passes: 4);
            var row = await Drive.RunAsync(journal, Name, d);
            Judge(journal, row, d);
        }
        if (only == null || "profiles".Contains(only, StringComparison.OrdinalIgnoreCase))
            await ProfilesAsync(journal);
    }

    private static void Judge(Journal journal, DriveRow row, DriveSpec d)
    {
        if (row.Passes.Count == 0) return;
        string where = Drive.F($"{d.V.Name} + {d.T.Name}");
        bool rollKnown = d.Sys.HasRoll || d.Slope == 0;
        double expected = rollKnown ? 0 : -d.V.AntennaHeight * Math.Sin(d.Slope * Math.PI / 180);
        if (!rollKnown)
            row.Note = Drive.F($"No roll source ({d.Sys.Label}): on a {d.Slope:0}° slope the antenna {d.V.AntennaHeight:0.00} m up leans {Math.Abs(expected):0.00} m downhill, and nothing tells AgOpenWeb. The same on every pass, so no gaps, but every pass is that far downhill of its line.");

        foreach (var p in row.Passes)
        {
            if (p.Samples == 0) continue;
            if (Math.Abs(p.LineError - expected) > LineTolerance)
                Drive.Add(row, journal, new Finding("CAREFUL-OFF-LINE", Severity.Bug,
                    Drive.F($"careful settings, yet the implement ran {p.LineError - expected:+0.00;-0.00} m off its line (pass {p.Index + 1}, {where}, {d.Sys.Label}, {d.Slope:0}°)")));
            if (p.Disengaged != null)
                Drive.Add(row, journal, new Finding("CAREFUL-DISENGAGED", Severity.Bug,
                    Drive.F($"AutoSteer went off by itself {p.Disengaged} (pass {p.Index + 1}, {where})")));
            if (p.AppToolSide > ToolSideTolerance + Math.Abs(expected))
                Drive.Add(row, journal, new Finding("CAREFUL-TOOL-SIDE", Severity.Inconsistency,
                    Drive.F($"AgOpenWeb's implement is {p.AppToolSide:0.00} m to the side of the real one ({where}, {d.Sys.Label}, {d.Slope:0}°): coverage and sections are drawn where the implement is not")));
            if (p.AppToolAlong > ToolAlongTolerance)
                Drive.Add(row, journal, new Finding("CAREFUL-TOOL-ALONG", Severity.Inconsistency,
                    Drive.F($"AgOpenWeb's implement is {p.AppToolAlong:0.00} m ahead of or behind the real one ({where}): sections switch that far late or early")));
            if (!double.IsNaN(p.SectionsOnShare) && p.SectionsOnShare < 0.95)
                Drive.Add(row, journal, new Finding("CAREFUL-SECTIONS-OFF", Severity.Bug,
                    Drive.F($"automatic sections were on only {p.SectionsOnShare * 100:0} % of a pass in open ground ({where})")));
        }
        if (row.Joins.Gap > JoinTolerance)
            Drive.Add(row, journal, new Finding("CAREFUL-GAP", Severity.Bug, Drive.F($"careful settings, yet a {row.Joins.Gap:0.00} m gap between passes ({where}, {d.Sys.Label}, {d.Slope:0}°)")));
        if (row.Joins.Overlap < -JoinTolerance)
            Drive.Add(row, journal, new Finding("CAREFUL-OVERLAP", Severity.Bug, Drive.F($"careful settings, yet a {-row.Joins.Overlap:0.00} m overlap between passes ({where}, {d.Sys.Label}, {d.Slope:0}°)")));
        foreach (var m in row.Said)
            if (m.Contains("did not engage", StringComparison.Ordinal))
                Drive.Add(row, journal, new Finding("CAREFUL-NO-ENGAGE", Severity.Bug, "AutoSteer did not engage on an active AB line (" + where + ")"));
    }

    /// <summary>
    /// Every machine as a profile, through the Vehicle &amp; Tool picker the way the web client
    /// drives it: type a name and New (a copy of what is loaded), Load it, type its
    /// measurements, Save Profile. Then AgOpenWeb restarts on the same data and each
    /// vehicle is loaded with one of its implements: every value must come back.
    /// </summary>
    private static async Task ProfilesAsync(Journal journal)
    {
        var row = new DriveRow { Operator = Name, Code = "PROFILES", Mistake = "Every machine saved as a profile, AgOpenWeb restarted, each loaded back", Vehicle = "all", Implement = "all", System = "–" };
        journal.Drives.Add(row);
        string dir = Path.Combine(Drive.DataRootBase, "profiles-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var vehicles = new[] { Machines.Puma, Machines.Vineyard, Machines.Big, Machines.Articulated, Machines.Combine };
        var systems = new Dictionary<VehicleSpec, SystemSpec>
        {
            [Machines.Puma] = Machines.PumaSys, [Machines.Vineyard] = Machines.VineyardSys, [Machines.Big] = Machines.BigSys,
            [Machines.Articulated] = Machines.ArticulatedSys, [Machines.Combine] = Machines.CombineSys,
        };
        var tools = Machines.CarefulMatrix.Select(m => m.T).Distinct().ToList();
        foreach (var t in new[] { Machines.Rear3Shift, Machines.Trailing3Shift, Machines.Rake1, Machines.Rake2, Machines.Rake4, Machines.Front6, Machines.Tbt6 })
            if (!tools.Contains(t)) tools.Add(t);
        try
        {
            await using (var rig = await VehicleToolRig.StartAsync(dir))
            {
                // Tools first, on the default vehicle (a tractor: a harvester would force every tool to front).
                foreach (var t in tools)
                {
                    await rig.SendAsync("profile.new", "tool\t" + t.Name);
                    await rig.SendAsync("profile.load", rig.Store.ActiveVehicleProfileName + "\t" + t.Name);
                    foreach (var kv in t.Settings()) await rig.SendAsync("config.set", kv);
                    await rig.SendAsync("profile.save");
                }
                foreach (var v in vehicles)
                {
                    await rig.SendAsync("profile.new", "vehicle\t" + v.Name);
                    await rig.SendAsync("profile.load", v.Name + "\t" + rig.Store.ActiveToolProfileName);
                    foreach (var kv in v.Settings(systems[v])) await rig.SendAsync("config.set", kv);
                    await rig.SendAsync("profile.save");
                }
                var veh = await rig.ReadAsync(() => rig.Config.GetAvailableProfiles().ToList());
                var tls = await rig.ReadAsync(() => rig.Config.GetAvailableToolProfiles().ToList());
                foreach (var v in vehicles)
                    if (!veh.Contains(v.Name)) Drive.Add(row, journal, new Finding("PROFILE-MISSING", Severity.Bug, "vehicle profile '" + v.Name + "' is not in the list after New and Save"));
                foreach (var t in tools)
                    if (!tls.Contains(t.Name)) Drive.Add(row, journal, new Finding("PROFILE-MISSING", Severity.Bug, "tool profile '" + t.Name + "' is not in the list after New and Save"));
                row.Said.Add(Drive.F($"{veh.Count} vehicle and {tls.Count} tool profiles listed"));
            }

            await using (var rig = await VehicleToolRig.StartAsync(dir))
            {
                string active = rig.Store.ActiveVehicleProfileName + " / " + rig.Store.ActiveToolProfileName;
                string wantActive = Machines.Combine.Name + " / " + tools[^1].Name;
                if (!string.Equals(active, wantActive, StringComparison.Ordinal))
                    Drive.Add(row, journal, new Finding("PROFILE-ACTIVE-AFTER-RESTART", Severity.Bug, "after a restart the active pair is " + active + ", not the last one saved, " + wantActive));
                int compared = 0;
                foreach (var (v, t, _, _) in Machines.CarefulMatrix.DistinctBy(m => (m.V, m.T)))
                {
                    await rig.SendAsync("profile.load", v.Name + "\t" + t.Name);
                    await rig.WaitForVmAsync();
                    foreach (var kv in v.Settings(systems[v]).Concat(t.Settings()))
                    {
                        string key = Readback.KeyOf(kv), want = Readback.ValueOf(kv);
                        // A harvester takes only front tools (#111): its header is front by design.
                        if (key == "tool.type" && v.Type == 1) want = "front";
                        string got = await rig.ReadAsync(() => Readback.Get(rig.Store, key));
                        compared++;
                        if (!Readback.Same(want, got))
                            Drive.Add(row, journal, new Finding("PROFILE-ROUNDTRIP-" + key.Split(':')[0], Severity.Bug,
                                $"{key} saved as {want} came back as {got} after a restart ({v.Name} + {t.Name})"));
                    }
                }
                row.Said.Add(Drive.F($"{compared} values compared after the restart"));
                row.Said.AddRange(rig.TakeEvents().Where(e => e.Kind is RigEventKind.Error or RigEventKind.Exception or RigEventKind.Failure).Select(e => e.ToString()));
            }
        }
        catch (Exception ex)
        {
            Drive.Add(row, journal, new Finding("HARNESS", Severity.Bug, "the profile round trip threw " + ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
