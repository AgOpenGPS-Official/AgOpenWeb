// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// X: the section bar and the section master buttons the operator's way, read where a machine
/// module reads them (the section bits of PGN 254): one section's button tapped round Off, Auto,
/// On, Off at 8 km/h; the master Auto on fresh ground and back over the strip it just covered;
/// Manual over the same strip. The careless operator puts a section On by hand and then switches
/// the master off.
/// </summary>
public static class SectionBarFlows
{
    public const string Group = "X: section bar";
    private const int Slot = 6;
    private const string Field = "Flows Sections";

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "X1", "One section's button", "Careful operator at 8 km/h, master off: section 3's button tapped once (Auto), again (On), again (Off).", OneSectionAsync);
        await Flow(run, "X2", "Auto on fresh ground and over the strip it covered", "Master Auto, 40 m at 8 km/h; then back to the start of that strip and over it again.", AutoOverlapAsync);
        await Flow(run, "X3", "Manual over covered ground", "Master Manual over the covered strip, then Manual off.", ManualAsync);
        await Flow(run, "X4", "A section On by hand, then the master off", "Careless operator at 8 km/h: master Auto, section 5 tapped to On by hand, then master Auto off.", MasterOffAsync);
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            if (!await FlowFields.EnsureOpenAsync(op, Slot, Field)) { s.Harness("the section field could not be opened"); return; }
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
            await op.Rig.RunAsync(() =>
            {
                var vm = op.Rig.Vm;
                if (vm.IsAutoSteerEngaged) vm.ToggleAutoSteerCommand?.Execute("scenario end");
                if (vm.IsSectionMasterOn) vm.ToggleSectionMasterCommand?.Execute(null);
                if (vm.IsManualSectionMode) vm.ToggleManualModeCommand?.Execute(null);
                op.Rig.Sections.SetAllSections(SectionButtonState.Off);
            });
        }, onTheRunsLine: false);

    private static string Bits(int b) => Convert.ToString(b & 0xFF, 2).PadLeft(8, '0');

    private static Task<SectionButtonState[]> HostButtonsAsync(FlowOp op) =>
        op.Rig.ReadAsync(() => op.Rig.Sections.SectionStates.Take(op.Rig.Sections.NumSections).Select(s => s.ButtonState).ToArray());

    private static Task<int[]> ScreenCodesAsync(FlowOp op) => op.PageAsync<int[]>("() => (tick && tick.sections) ? Array.from(tick.sections) : []");

    /// <summary>The section bits the module gets over the next <paramref name="ms"/>: the union and the intersection.</summary>
    private static async Task<(int Any, int All)> SampleAsync(FlowOp op, int ms)
    {
        long from = ModuleListener.NowMs;
        await Task.Delay(ms);
        return SectionsSince(op, from);
    }

    /// <summary>The section bits the module got from <paramref name="fromMs"/> to now, the union and the
    /// intersection, from the listener's change log: the state in force at the start and every change
    /// after it, so a section asked on for one 10 ms control tick counts too. Nothing heard: (0, 0).</summary>
    private static (int Any, int All) SectionsSince(FlowOp op, long fromMs)
    {
        var states = op.Rig.Module.Since(fromMs);
        if (op.Rig.Module.First(fromMs, _ => true) is { } atStart) states.Add(atStart);
        if (states.Count == 0) return (0, 0);
        int any = 0, all = 0xFF;
        foreach (var m in states) { any |= m.Sections; all &= m.Sections; }
        return (any & 0xFF, all & 0xFF);
    }

    private static async Task<bool> TapSectionAsync(FlowOp op, int idx, SectionButtonState want, string what)
    {
        if (!await op.TapAsync($"#sectionbar button[data-idx='{idx}']", what)) return false;
        if (await op.HostAsync(() => op.Rig.Sections.SectionStates[idx].ButtonState == want, 1500)) return true;
        op.S.Problem(Severity.Bug, "SECTION-BUTTON-DEAD", $"{what}: section {idx + 1} is {(await HostButtonsAsync(op))[idx]}, {want} expected");
        return false;
    }

    /// <summary>The tractor <paramref name="n"/> m into the field, in its middle, facing north at <paramref name="kmh"/>
    /// (no line: the sections don't need one).</summary>
    private static async Task<bool> OnLineAsync(FlowOp op, double n, double kmh)
    {
        await op.CloseAllAsync();
        await op.PlaceAsync(FlowFields.At(Slot, 120, n), 0);
        await op.Rig.SetSpeedAsync(kmh);
        return true;
    }

    // ------------------------------------------------------------------ X1

    private static async Task OneSectionAsync(FlowOp op)
    {
        if (!await OnLineAsync(op, 60, 0)) return;
        await op.CloseAllAsync();
        int n = await op.PageAsync<int>("() => document.querySelectorAll('#sectionbar button[data-idx]').length");
        bool open = await op.PageAsync<bool>("() => document.getElementById('sectionbar').classList.contains('open')");
        if (open && n == 8) op.S.Ok("the section bar shows 8 buttons with the field open");
        else op.S.Problem(Severity.Bug, "SECTION-BAR-WRONG", $"the section bar: open {open}, {n} buttons for 8 sections");
        var codes = await ScreenCodesAsync(op);
        op.S.Step($"standing, master off: the screen's section codes {string.Join("", codes)}, the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}");

        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(1500);
        if (!await TapSectionAsync(op, 2, SectionButtonState.Auto, "section 3's button")) return;
        await Task.Delay(1800);
        var (any, all) = await SampleAsync(op, 1000);
        if (all == 0b100 && any == 0b100) op.S.Ok($"section 3 on Auto: the module gets {Bits(all)} (only section 3)");
        else
        {
            op.S.Step($"section 3 on Auto, over 1 s: the module got {Bits(any)} (any) / {Bits(all)} (all), {Bits(0b100)} expected");
            op.S.Problem(Severity.Bug, "SECTION-AUTO-WRONG", "section 3 tapped to Auto on fresh ground at 8 km/h: the module does not keep section 3 open and the others closed");
        }
        codes = await ScreenCodesAsync(op);
        if (codes.Length == 8 && codes[2] == 2 && codes.Where((c, i) => i != 2).All(c => c == 0)) op.S.Ok("the screen shows section 3 green (auto on), the rest red");
        else
        {
            op.S.Step($"section 3 on Auto: the screen's codes {string.Join("", codes)}");
            op.S.Problem(Severity.Inconsistency, "SECTION-BAR-STALE", "section 3 on Auto: the screen does not show section 3 green and the rest red");
        }

        if (!await TapSectionAsync(op, 2, SectionButtonState.On, "section 3's button again")) return;
        await Task.Delay(500);
        (any, all) = await SampleAsync(op, 800);
        codes = await ScreenCodesAsync(op);
        if (all == 0b100) op.S.Ok($"section 3 On: the module gets {Bits(all)}; the screen's code {codes.ElementAtOrDefault(2)} (1 = yellow, on by hand)");
        else
        {
            op.S.Step($"section 3 On, over 0.8 s: the module got {Bits(any)} (any) / {Bits(all)} (all), {Bits(0b100)} expected");
            op.S.Problem(Severity.Bug, "SECTION-ON-WRONG", "section 3 On: the module does not get section 3, and no other section, open throughout");
        }

        if (!await TapSectionAsync(op, 2, SectionButtonState.Off, "section 3's button a third time")) return;
        long t0 = ModuleListener.NowMs;
        var off = await op.Run.WaitModuleAsync(m => m.Sections == 0, t0, 2000);
        if (off is { } o) { op.S.Step($"section 3 Off: the module closed it after {o - t0} ms"); op.S.Ok("section 3 Off: the module closes it within 2 s"); }
        else
        {
            op.S.Step($"section 3 Off: 2 s later the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}");
            op.S.Problem(Severity.Bug, "SECTION-OFF-IGNORED", "section 3 tapped Off: the module still gets sections open 2 s later");
        }
        await op.StopAsync();
    }

    // ------------------------------------------------------------------ X2, X3

    private static async Task AutoOverlapAsync(FlowOp op)
    {
        if (!await OnLineAsync(op, 100, 8)) return;
        await Task.Delay(1000);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-auto", () => op.Rig.Vm.IsSectionMasterOn)) { op.S.Problem(Severity.Bug, "MASTER-AUTO-DEAD", "the master Auto button did nothing"); return; }
        var buttons = await HostButtonsAsync(op);
        if (buttons.All(b => b == SectionButtonState.Auto)) op.S.Ok("master Auto: all 8 sections on Auto");
        else op.S.Problem(Severity.Bug, "MASTER-AUTO-WRONG", $"master Auto: the sections are {string.Join(", ", buttons)}");
        await Task.Delay(2000);
        var (any, all) = await SampleAsync(op, 2500);
        if (all == 0xFF) op.S.Ok($"on fresh ground every section sprays ({Bits(all)})");
        else
        {
            op.S.Step($"master Auto on fresh ground, over 2.5 s: the module got {Bits(any)} (any) / {Bits(all)} (all)");
            op.S.Problem(Severity.Bug, "AUTO-FRESH-GROUND-OFF", "master Auto on fresh ground at 8 km/h: not every section stays open at the module");
        }
        await op.DriveAsync(20, 8);
        var end = await op.PoseAsync();
        await op.StopAsync();
        await Task.Delay(1500);

        // Back to 10 m into that strip, over it again: Auto keeps off what is covered. Moving the
        // tractor there switches the master off (the jump would paint), so the operator taps Auto again.
        // The tractor and its tool (rear-fixed, the default) are put down on covered ground, so
        // every section must stay closed from the tap on: no settling time. Counted from just
        // before the tap (the master is off then, so the module gets 0), over the module's change
        // log, so a section asked on for one 10 ms control tick counts too.
        await op.PlaceAsync(FlowFields.At(Slot, 120, 110), 0);
        await Task.Delay(500);
        await op.Rig.SetSpeedAsync(8);
        long tAuto = ModuleListener.NowMs;
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-auto", () => op.Rig.Vm.IsSectionMasterOn)) { op.S.Problem(Severity.Bug, "MASTER-AUTO-DEAD", "the master Auto button did nothing the second time"); return; }
        await Task.Delay(5000);
        (any, _) = SectionsSince(op, tAuto);
        var pose = await op.PoseAsync();
        buttons = await HostButtonsAsync(op);
        op.S.Step(FlowOp.F($"over the covered strip for 5 s from the Auto tap (now {pose.N - end.N:0} m from where the first run stopped), the sections {string.Join(", ", buttons.Distinct())}: the module got {Bits(any)} at most"));
        if (!buttons.All(b => b == SectionButtonState.Auto)) { op.S.Problem(Severity.Bug, "MASTER-AUTO-WRONG", $"master Auto again: the sections are {string.Join(", ", buttons)}"); return; }
        if (any == 0) op.S.Ok("over ground already covered, master Auto keeps every section closed");
        else op.S.Problem(Severity.Bug, "AUTO-SPRAYS-COVERED", "over the strip it covered a moment ago, master Auto opens sections at the module");
        // Past the end of the strip, they open again.
        var on = await op.Run.WaitModuleAsync(m => (m.Sections & 0xFF) == 0xFF, ModuleListener.NowMs, 12000);
        var p2 = await op.PoseAsync();
        op.S.Step(FlowOp.F($"past the covered strip: the antenna {p2.N - end.N:+0.0;-0.0} m from where the first run stopped, the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}"));
        if (on != null) op.S.Ok("past the covered strip every section opens again");
        else op.S.Problem(Severity.Bug, "AUTO-NOT-BACK-ON", "past the covered strip not every section opens again");
        await op.StopAsync();
        await op.Run.TapUntilAsync(op.Screen, "#rn-auto", () => !op.Rig.Vm.IsSectionMasterOn);
        await op.Run.ShotAsync(op.S, op.Screen, "X2-auto-overlap.png");
    }

    private static async Task ManualAsync(FlowOp op)
    {
        if (!await OnLineAsync(op, 108, 8)) return;
        await Task.Delay(1000);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => op.Rig.Vm.IsManualSectionMode)) { op.S.Problem(Severity.Bug, "MASTER-MANUAL-DEAD", "the Manual button did nothing"); return; }
        await Task.Delay(800);
        var (any, all) = await SampleAsync(op, 2000);
        if (all == 0xFF) op.S.Ok($"Manual over covered ground: every section sprays ({Bits(all)}), as the operator asked");
        else
        {
            op.S.Step($"Manual over covered ground, over 2 s: the module got {Bits(any)} (any) / {Bits(all)} (all)");
            op.S.Problem(Severity.Bug, "MANUAL-NOT-ALL-ON", "Manual over covered ground: not every section stays open at the module");
        }
        var codes = await ScreenCodesAsync(op);
        if (codes.Length == 8 && codes.All(c => c == 1)) op.S.Ok("the screen shows all 8 yellow (on by hand)");
        else
        {
            op.S.Step($"Manual: the screen's codes {string.Join("", codes)}");
            op.S.Problem(Severity.Inconsistency, "SECTION-BAR-STALE", "Manual: the screen does not show all 8 yellow");
        }
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => !op.Rig.Vm.IsManualSectionMode)) { op.S.Problem(Severity.Bug, "MASTER-MANUAL-DEAD", "Manual off did nothing"); return; }
        long t0 = ModuleListener.NowMs;
        var off = await op.Run.WaitModuleAsync(m => m.Sections == 0, t0, 2000);
        if (off is { } o) { op.S.Step($"Manual off: every section closed after {o - t0} ms"); op.S.Ok("Manual off: every section closes within 2 s"); }
        else
        {
            op.S.Step($"Manual off: 2 s later the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}");
            op.S.Problem(Severity.Safety, "MANUAL-OFF-STILL-SPRAYING", "Manual off at 8 km/h: the module still gets sections open 2 s later");
        }
    }

    // ------------------------------------------------------------------ X4

    private static async Task MasterOffAsync(FlowOp op)
    {
        if (!await OnLineAsync(op, 200, 8)) return;
        await Task.Delay(1000);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-auto", () => op.Rig.Vm.IsSectionMasterOn)) { op.S.Problem(Severity.Bug, "MASTER-AUTO-DEAD", "the master Auto button did nothing"); return; }
        await Task.Delay(1500);
        if (!await TapSectionAsync(op, 4, SectionButtonState.On, "section 5's button (Auto → On)")) return;
        await Task.Delay(800);
        op.S.Step($"master Auto with section 5 On by hand: the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}");
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-auto", () => !op.Rig.Vm.IsSectionMasterOn)) { op.S.Problem(Severity.Bug, "MASTER-AUTO-DEAD", "master Auto off did nothing"); return; }
        long t0 = ModuleListener.NowMs;
        var off = await op.Run.WaitModuleAsync(m => m.Sections == 0, t0, 2500);
        var buttons = await HostButtonsAsync(op);
        op.S.Step($"after master off the sections are {string.Join(", ", buttons.Select(b => b.ToString()))}");
        if (off is { } o) { op.S.Step($"master off: every section closed at the module after {o - t0} ms"); op.S.Ok("master off: every section closes at the module within 2.5 s, the one On by hand too"); }
        else
        {
            op.S.Step($"master off: 2.5 s later the module gets {Bits(op.Rig.Module.Last?.Sections ?? 0)}");
            // AgOpenGPS: the master Auto button sets every section's button (Off), so one put On by hand closes too.
            op.S.Problem(Severity.Safety, "MASTER-OFF-SECTION-SPRAYS", "master Auto switched off at 8 km/h: the module still gets sections open 2.5 s later (section 5 was On by hand)");
        }
        await op.StopAsync();
    }
}
