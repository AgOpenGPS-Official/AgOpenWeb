// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>What the scenarios share: data folders, a saved profile, the valve check.</summary>
public static class Kit
{
    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static bool Want(string? only, string code) =>
        only == null || only.Split(',').Any(o => code.StartsWith(o.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NewDataRoot(string code) =>
        Path.Combine(Path.GetTempPath(), "aow-machine-" + code + "-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The 8 m boom: 8 sections of 1 m, relays 7 and 8 set to sections 7 and 8
    /// (AgOpenWeb's default relay functions stop at section 6).</summary>
    public static readonly (int, int)[] Relays78 = { (6, 7), (7, 8) };

    /// <summary>A profile saved on an earlier day, with no machine module listening.</summary>
    public static async Task PrepareProfileAsync(string root, int sections, double cm, IEnumerable<(int, int)>? relays)
    {
        using var nobody = new EmulatedSprayerModule();   // never started: nothing listens on 8888
        await using var s = await MachineSession.StartAsync(root, nobody);
        await s.SetToolAsync(sections, cm, relays);
        await Task.Delay(500);
    }

    /// <summary>Wait until AgOpenWeb's module status shows the machine module (hello answered).</summary>
    public static async Task<double> WaitModuleSeenAsync(MachineSession s, bool seen = true, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (s.Rig.State.Connections.IsMachineDataOk == seen) return sw.Elapsed.TotalSeconds;
            await Task.Delay(100);
        }
        return double.NaN;
    }

    /// <summary>Drive and compare the screen with the valves; record what disagreed and why.</summary>
    public static async Task<MachineSession.Watch> DriveAsync(Scenario sc, MachineSession s, double seconds, string what,
        bool expectFollow = true)
    {
        var w = await s.WatchAsync(seconds);
        int n = s.Store.NumSections;
        sc.Step(what + ": " + w.Summary(n));
        if (!expectFollow) return w;
        var noRelay = new List<int>();
        var lag = new List<string>();
        for (int i = 0; i < Math.Min(n, 64); i++)
        {
            if (w.OnButClosed[i] < 0.7 && w.OffButOpen[i] < 0.7) continue;
            bool hasRelay = Enumerable.Range(0, EmulatedSprayerModule.NumRelays).Any(r => s.Module.RelayFunction(r) == i + 1);
            if (!hasRelay) noRelay.Add(i + 1);
            else lag.Add(F($"{i + 1} (on screen, valve shut {w.OnButClosed[i]:0.0} s; off on screen, valve open {w.OffButOpen[i]:0.0} s)"));
        }
        if (noRelay.Count == 0 && lag.Count == 0)
            sc.Ok(what + ": every valve followed the screen");
        if (lag.Count > 0)
            sc.Problem(Severity.Bug, "VALVES-NOT-FOLLOWING", what + ": with the module online, the valves did not follow the screen for section(s) " + string.Join(", ", lag));
        if (noRelay.Count > 0)
            sc.Problem(Severity.Robustness, "SECTION-WITHOUT-RELAY", what + F($": section(s) {string.Join(", ", noRelay)} showed on and painted coverage, but no relay of the module has that function, so their valves never opened. AgOpenWeb lets the tool have {n} sections while the relay functions it sends (PGN 236) stop at section {MaxSectionRelay(s)}, and says nothing."));
        return w;
    }

    public static int MaxSectionRelay(MachineSession s) =>
        s.Store.Machine.PinAssignments.Select(p => (int)p).Where(p => p is >= 1 and <= 16).DefaultIfEmpty(0).Max();

    /// <summary>The run's protocol health: no bad checksums, no cut packets.</summary>
    public static void CheckWire(Scenario sc, EmulatedSprayerModule m)
    {
        if (m.ChecksumErrors > 0 || m.ShortPackets > 0)
            sc.Problem(Severity.Bug, "PGN-BAD-PACKETS", F($"the module dropped {m.ChecksumErrors} packet(s) with a wrong checksum and {m.ShortPackets} cut short"));
    }

    /// <summary>What AgOpenWeb told the operator meanwhile (failures, warnings, status lines).</summary>
    public static List<string> Said(MachineSession s, Func<string, bool>? about = null)
    {
        return s.Rig.TakeEvents()
            .Where(e => e.Kind is RigEventKind.Failure or RigEventKind.Status or RigEventKind.Warning or RigEventKind.Error)
            .Select(e => e.Kind + ": " + e.Text.Split('\n')[0])
            .Where(t => about == null || about(t))
            .Distinct()
            .Take(8)
            .ToList();
    }
}
