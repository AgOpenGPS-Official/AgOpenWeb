// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// V: the vehicle profile's settings reach the disk when the settings panel closes, as AgOpenGPS
/// saves when its settings form closes. Before, they were written only at Save Profile, a profile
/// switch or the exit save, and a key-off lost them (the update harness's
/// POWERCUT-UNSAVED-PROFILE-EDIT-LOST). The value is typed and the panel left at once, with no
/// Enter or Tab: the box sends its value only as it loses focus.
/// </summary>
public static class ProfileFlows
{
    public const string Group = "V: vehicle settings";

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "V1", "A setting typed, the panel closed with ✕",
            "Vehicle › Configure Vehicle…: antenna height typed, then ✕ at once (no Enter, no Save Profile).", op => TypeThenAsync(op, 3.37, ".ln-closex", "✕"));
        await Flow(run, "V2", "A setting typed, back to the vehicle list",
            "Vehicle › Configure Vehicle…: antenna height typed, then ← back to the vehicle and tool list.", op => TypeThenAsync(op, 3.21, ".ln-back", "←"));
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.CloseAllAsync();
        }, onTheRunsLine: false);

    private static async Task TypeThenAsync(FlowOp op, double metres, string leaveButton, string leaveName)
    {
        if (!await op.OpenMenuAsync("ln-vehicle", "vehtoolhub", "Vehicle")) return;
        if (!await op.TapAsync("#vth-vehconfig", "Configure Vehicle…") || !await op.WaitOpenAsync("vehiclecfg"))
        {
            op.S.Harness("the vehicle settings panel did not open");
            return;
        }
        string box = "#vehiclecfg .cfg-num[data-key=\"vehicle.antennaHeight\"]";
        if (!await op.TypeAsync(box, metres.ToString("0.00", CultureInfo.InvariantCulture), "Antenna height")) return;
        if (!await op.TapAsync("#vehiclecfg " + leaveButton, leaveName)) return;

        string name = await op.Rig.ReadAsync(() => op.Rig.Store.ActiveVehicleProfileName ?? "");
        string path = Path.Combine(op.Rig.DataRoot, "AgOpenWeb", "Vehicles", name + ".json");
        double? live = await op.Rig.ReadAsync(() => op.Rig.Store.Vehicle.AntennaHeight);
        double? onDisk = null;
        await op.HostAsync(() => (onDisk = OnDisk(path)) is { } d && Math.Abs(d - metres) < 0.005, 3000);
        if (live is not { } l || Math.Abs(l - metres) > 0.005)
            op.S.Harness(Run.F($"the antenna height typed did not reach the host ({live:0.00} m)"));
        else if (onDisk is { } d && Math.Abs(d - metres) < 0.005)
            op.S.Ok(Run.F($"leaving the panel with {leaveName} wrote the antenna height ({metres:0.00} m) to {Path.GetFileName(path)}"));
        else
            op.S.Problem(Severity.Bug, "PROFILE-NOT-SAVED-ON-CLOSE",
                Run.F($"the antenna height was typed as {metres:0.00} m and the panel left with {leaveName}; the profile file {Path.GetFileName(path)} still holds {(onDisk is { } x ? x.ToString("0.00", CultureInfo.InvariantCulture) + " m" : "no value")}: a key-off now loses it (AgOpenGPS saves when its settings form closes)"));
    }

    /// <summary>The antenna height in the profile file, wherever its JSON keeps it.</summary>
    private static double? OnDisk(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return Find(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return null; }

        static double? Find(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.Equals("antennaHeight", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetDouble();
                    if (Find(p.Value) is { } v) return v;
                }
            return null;
        }
    }
}
