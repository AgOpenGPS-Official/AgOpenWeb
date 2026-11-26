// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// C: the screen holding control goes away while the tractor works (AutoSteer on, sections on,
/// 8 km/h): the tab closes, the Wi-Fi goes out of range or drops the tablet, the tablet sleeps,
/// the page reloads. The host must stop the steering and close the sections (the deadman,
/// ControlAuthority: 1.5 s without presence), and the screen must not show a working tractor
/// that isn't. What the machine got is read at the module port.
/// </summary>
public static class ControlLostScenarios
{
    public const string Group = "C: the operator's screen goes";

    /// <summary>The deadman (1.5 s) plus the broadcaster's sweep (0.1 s) and a margin.</summary>
    private const long DeadmanMs = 2000;

    /// <summary>ControlAuthority's deadman: control ends this long after the last presence.</summary>
    private const int ControlDeadmanMs = 1500;

    /// <summary>How often the screen holding control sends its presence (js/control.js).</summary>
    private const int PresenceMs = 500;

    public static async Task RunAsync(Run run)
    {
        await run.ScenarioAsync(Group, "C1", "The tab is closed",
            "The operator's browser tab closes (or the browser crashes) while working.",
            s => TabClosedAsync(run, s));
        await run.ScenarioAsync(Group, "C2", "The tablet's Wi-Fi goes out of range",
            "The tablet's link goes silent for 10 s: nothing passes, nobody is told (TCP keeps waiting). Then it comes back.",
            s => SilentAsync(run, s, 10000, tapsDuring: false));
        await run.ScenarioAsync(Group, "C3", "Taps during the outage",
            "As C2, and the operator, seeing a screen that still looks live, taps AutoSteer and the section master during the outage. The link comes back after 6 s.",
            s => SilentAsync(run, s, 6000, tapsDuring: true));
        await run.ScenarioAsync(Group, "C4", "The tablet goes to sleep",
            "The tablet's screen turns off (or the app goes to the background): the page's script stops for 8 s (no timers, no presence sent, the socket left open), then it wakes.",
            s => FrozenAsync(run, s));
        await run.ScenarioAsync(Group, "C5", "A 0.7 s Wi-Fi hiccup",
            "The link goes silent for 0.7 s, as when the tablet roams between access points.",
            s => HiccupAsync(run, s, 700));
        await run.ScenarioAsync(Group, "C6", "A 2.5 s Wi-Fi hiccup",
            "The link goes silent for 2.5 s.",
            s => HiccupAsync(run, s, 2500));
        await run.ScenarioAsync(Group, "C7", "The access point drops the tablet",
            "The tablet's connections are reset and new ones are refused for 5 s, then the Wi-Fi is back. The host is told nothing: its side of the old connection goes quiet, as when a tablet leaves the network.",
            s => CutAsync(run, s));
        await run.ScenarioAsync(Group, "C8", "The page is reloaded",
            "The operator pulls to refresh (or the app reloads its web view) while working.",
            s => ReloadAsync(run, s));
        await run.ScenarioAsync(Group, "C9", "Free Drive on, then the screen goes",
            "AutoSteer settings > Free Drive: the operator turns it on and steers 10° right with the RIGHT button (the motor holds that angle), then the tab closes.",
            s => FreeDriveAsync(run, s));
        await run.ScenarioAsync(Group, "C10", "A section switched on by hand, then the screen goes",
            "The operator taps section 1 twice (Off → Auto → On: forced on, as for a headland strip), master off, driving; then the tab closes.",
            s => ForcedSectionAsync(run, s));
        await run.ScenarioAsync(Group, "C11", "Two screens, the tablet's Wi-Fi goes out of range",
            "The cab tablet holds control, a second tablet observes. The tablet's link goes silent for 6 s, then comes back.",
            s => TwoScreensSilentAsync(run, s));
        await run.ScenarioAsync(Group, "C12", "A minute out of range, a second tablet watching",
            "The cab tablet holds control with sections painting, a second tablet observes. The tablet's link goes silent for 60 s: the host's sends to it fill the small buffers of a dead radio link and then wait. Does the host still stop the work within the deadman, and does the second tablet keep getting frames?",
            s => LongSilentAsync(run, s, 60000));
    }

    private static async Task<Screen?> OperatorAsync(Run run, Scenario s, bool wifi = true)
    {
        var op = await run.OpenAsync(Device.Tablet10, "cab tablet", ownWifi: wifi);
        if (!await op.WaitReadyAsync() || !await op.WaitForAsync("iHoldControl", 5000))
        { s.Harness("the cab tablet did not connect and take control"); await op.DisposeAsync(); return null; }
        return op;
    }

    private static async Task TabClosedAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s, wifi: false) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        await op.CloseTabAsync();
        var (steer, secs) = await run.MachineStopsAsync(t0, 3000);
        ObserverScenarios.Verdict(s, "tab closed", steer, secs, 500);
        await BackAsync(run, s, op, newTab: true);
    }

    /// <summary>The operator opens the page again: does it get control back, and is it told why the
    /// steering went off?</summary>
    private static async Task BackAsync(Run run, Scenario s, Screen op, bool newTab)
    {
        if (newTab) await op.NewTabAsync();
        if (!await op.WaitReadyAsync()) { s.Problem(Severity.Bug, "NO-RECONNECT", "the screen did not connect again within 20 s"); return; }
        bool control = await op.WaitForAsync("iHoldControl", 5000);
        var v = await op.ViewAsync();
        var host = await run.Rig.HostAsync();
        string toast = await op.EvalAsync<string>("() => toastMsg || ''");
        s.Step($"back: the screen shows {v}; host: {host}; its last notice: '{toast}'");
        if (!control) s.Problem(Severity.Bug, "NO-CONTROL-AFTER-RETURN", $"the operator's screen came back as {v} with no other screen open");
        else s.Ok("the screen takes control back");
        if (host.AutoSteer) s.Problem(Severity.Safety, "STEERING-BACK-BY-ITSELF", "AutoSteer is on again without a tap");
        if (toast.Length == 0)
            s.Problem(Severity.Improvement, "CONTROL-LOSS-NOT-EXPLAINED", "AutoSteer and the sections are off when the screen comes back, with no word why (the reason is only in the log)");
        else s.Ok($"the screen says why: '{toast}'");
        Run.PageErrors(s, op);
    }

    private static async Task SilentAsync(Run run, Scenario s, int outageMs, bool tapsDuring)
    {
        if (await OperatorAsync(run, s) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        op.Wifi!.Mode = LinkMode.Silent;
        s.Step("the tablet's link goes silent");
        var (steer, secs) = await run.MachineStopsAsync(t0, (int)DeadmanMs + 1500);
        ObserverScenarios.Verdict(s, "link silent", steer, secs, DeadmanMs);

        // What the screen shows while nothing reaches it.
        await Task.Delay(Math.Max(0, 4000 - (int)(ModuleListener.NowMs - t0)));
        var v = await op.ViewAsync();
        bool stillOn = await op.EvalAsync<bool>("() => !!(tick && tick.op && tick.op.autoSteer)");
        s.Step($"{ModuleListener.NowMs - t0} ms into the outage the tablet shows {v}, AutoSteer button {(stillOn ? "ON" : "off")}");
        await run.ShotAsync(s, op, $"{s.Row.Code}-tablet-during-outage.png");
        if (v.ClaimsOperator)
            s.Problem(Severity.Safety, "SILENT-LINK-LOOKS-LIVE",
                $"4 s into the outage the tablet still says {v} with AutoSteer shown {(stillOn ? "ON" : "off")} and a still map, while the host stopped the steering {Run.Ms(steer)} after the link went: no warning that the screen is out of date");
        else s.Ok($"the tablet shows the outage: {v}");

        if (tapsDuring)
        {
            await op.TapAsync("#rn-steer");
            await op.TapAsync("#rn-auto");
            s.Step("the operator taps AutoSteer and the section master on the frozen screen");
        }
        await Task.Delay(Math.Max(0, outageMs - (int)(ModuleListener.NowMs - t0)));
        long back = ModuleListener.NowMs;
        op.Wifi.Mode = LinkMode.Up;
        s.Step("the link is back");
        await Task.Delay(3000);
        var late = run.Rig.Module.Since(back).FirstOrDefault(m => m.Steering || m.Sections != 0);
        if (late.Ms != 0)
            s.Problem(Severity.Safety, "STALE-TAP-RUN-LATE", $"{late.Ms - back} ms after the link came back the module got {late}: a tap made on the frozen screen ran late");
        else if (tapsDuring) s.Ok("the taps made during the outage were not run when the link came back");
        await BackAsync(run, s, op, newTab: false);
    }

    private static async Task FrozenAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s, wifi: false) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        await op.FreezeAsync();
        s.Step("the tablet's page is frozen (screen off)");
        var (steer, secs) = await run.MachineStopsAsync(t0, (int)DeadmanMs + 1500);
        ObserverScenarios.Verdict(s, "page frozen", steer, secs, DeadmanMs);
        await Task.Delay(Math.Max(0, 8000 - (int)(ModuleListener.NowMs - t0)));
        await op.ResumeAsync();
        s.Step("the tablet wakes");
        await Task.Delay(1000);
        await BackAsync(run, s, op, newTab: false);
        await run.ShotAsync(s, op, "C4-tablet-after-wake.png");
    }

    private static async Task HiccupAsync(Run run, Scenario s, int ms)
    {
        if (await OperatorAsync(run, s) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        op.Wifi!.Mode = LinkMode.Silent;
        await Task.Delay(ms);
        op.Wifi.Mode = LinkMode.Up;
        await Task.Delay(2500);
        var off = run.Rig.Module.First(t0, m => !m.Steering);
        bool engaged = await run.Rig.ReadAsync(() => run.Rig.Vm.IsAutoSteerEngaged);
        s.Step($"{ms} ms silent: AutoSteer {(off is { } o ? $"dropped at the module {o.Ms - t0} ms in" : "held")}; now {(engaged ? "on" : "off")}");
        // The last presence before the hiccup can be up to one presence period old, so a hiccup
        // holds only when it and that period together stay inside the deadman (with a margin).
        if (ms + PresenceMs + 200 <= ControlDeadmanMs)
        {
            if (off != null) s.Problem(Severity.Bug, "HICCUP-DROPS-STEERING", $"a {ms} ms Wi-Fi hiccup turned AutoSteer off (the deadman is 1.5 s, presence every {PresenceMs} ms)");
            else s.Ok($"a {ms} ms hiccup does not stop the work");
        }
        else if (ms < ControlDeadmanMs)
        {
            s.Note($"a {ms} ms hiccup may or may not reach the deadman, depending on when the last presence went out");
            if (off != null) await BackAsync(run, s, op, newTab: false);
        }
        else
        {
            if (off == null) s.Problem(Severity.Safety, "STEERING-NOT-STOPPED", $"a {ms} ms outage did not stop the steering");
            else s.Ok($"the {ms} ms outage stops the steering after {off.Value.Ms - t0} ms (the deadman)");
            await BackAsync(run, s, op, newTab: false);
        }
        Run.PageErrors(s, op);
    }

    private static async Task CutAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        op.Wifi!.Mode = LinkMode.Cut;
        // The host is not told, so the deadman ends the tablet's control.
        var (steer, secs) = await run.MachineStopsAsync(t0, (int)DeadmanMs + 1500);
        ObserverScenarios.Verdict(s, "access point drop", steer, secs, DeadmanMs);
        bool told = await op.WaitForAsync("connState !== 'connected'", 3000);
        var v = await op.ViewAsync();
        s.Step($"{ModuleListener.NowMs - t0} ms: the tablet shows {v}");
        if (told) s.Ok($"the tablet shows it lost the link: {v}");
        else s.Problem(Severity.Safety, "SILENT-LINK-LOOKS-LIVE", $"3 s after its connection was reset the tablet still shows {v}");
        await Task.Delay(Math.Max(0, 5000 - (int)(ModuleListener.NowMs - t0)));
        op.Wifi.Mode = LinkMode.Up;
        long back = ModuleListener.NowMs;
        bool again = await op.WaitForAsync("connState === 'connected' && iHoldControl", 8000);
        s.Step($"Wi-Fi back: {(again ? $"connected and holding control after {ModuleListener.NowMs - back} ms" : "not connected again within 8 s")}");
        await BackAsync(run, s, op, newTab: false);
    }

    private static async Task ReloadAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s, wifi: false) is not { } op) return;
        await using var _ = op;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        await op.ReloadAsync();
        var (steer, secs) = await run.MachineStopsAsync(t0, 3000);
        s.Step($"reload: AutoSteer off at the module after {Run.Ms(steer)}, sections after {Run.Ms(secs)} (a reload is a new connection: the old one's control ends)");
        s.Note("a reload stops AutoSteer and the sections, by design: the operator has to tap them on again");
        await BackAsync(run, s, op, newTab: false);
    }

    private static async Task FreeDriveAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s, wifi: false) is not { } op) return;
        await using var _ = op;
        // AutoSteer settings, Free Drive on, RIGHT five times (2° a tap), all by touch.
        foreach (var sel in new[] { "#ln-autosteer" })
        {
            await op.ScrollToAsync(sel);
            if (!(await op.TapAsync(sel)).Hit) { s.Harness($"could not tap {sel}"); return; }
            await Task.Delay(400);
        }
        if (!await run.TapUntilAsync(op, "#as-fd-toggle", () => run.Rig.AutoSteer.IsInFreeDriveMode))
        {
            // The Free Drive button may sit in a part of the panel shown only in full mode.
            await op.TapAsync("#as-expand"); await Task.Delay(300);
            if (!await run.TapUntilAsync(op, "#as-fd-toggle", () => run.Rig.AutoSteer.IsInFreeDriveMode))
            { s.Harness("Free Drive did not turn on from its button"); return; }
        }
        for (int i = 0; i < 5; i++)
        {
            await op.ScrollToAsync("[data-cmd='autosteer.freedrive.right']");
            await op.TapAsync("[data-cmd='autosteer.freedrive.right']");
            await Task.Delay(150);
        }
        await Task.Delay(400);
        var m = run.Rig.Module.Last;
        s.Step($"Free Drive on, steered right: the module gets {m}");
        if (m is not { Steering: true }) { s.Harness("the module never got the Free Drive steer command"); return; }
        await run.ShotAsync(s, op, "C9-free-drive.png");
        long t0 = ModuleListener.NowMs;
        await op.CloseTabAsync();
        s.Step("the tab is closed");
        await Task.Delay(5000);
        if (!HeardSince(run, s, t0 + 4500)) return;
        var still = run.Rig.Module.First(t0 + 4500, x => x.Steering);
        var host = await run.Rig.HostAsync();
        if (still is { } st)
            s.Problem(Severity.Safety, "FREE-DRIVE-KEEPS-STEERING",
                $"5 s after the screen went the module still gets {st} (Free Drive: status 1, a made-up 8 km/h): the motor keeps the wheels at {st.AngleDeg:0}° with nobody at the screen; host: {host}");
        else s.Ok($"Free Drive stopped when the screen went ({Run.Ms(run.Rig.Module.First(t0, x => !x.Steering)?.Ms - t0)})");
        await BackAsync(run, s, op, newTab: true);
    }

    private static async Task ForcedSectionAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s, wifi: false) is not { } op) return;
        await using var _ = op;
        await run.Rig.SetSpeedAsync(8);
        // Section 1's button, twice: Off → Auto → On.
        string sel = "#sectionbar button";
        for (int i = 0; i < 2; i++) { await op.TapAsync(sel); await Task.Delay(300); }
        var host = await run.Rig.HostAsync();
        await Task.Delay(500);
        var m = run.Rig.Module.Last;
        s.Step($"section 1 tapped twice: host {host}; the module gets {m}");
        if (host.SectionsForcedOn == 0 || m is not { } mm || (mm.Sections & 1) == 0) { s.Harness("section 1 did not turn on by hand"); return; }
        long t0 = ModuleListener.NowMs;
        await op.CloseTabAsync();
        s.Step("the tab is closed");
        await Task.Delay(5000);
        if (!HeardSince(run, s, t0 + 4500)) return;
        var still = run.Rig.Module.First(t0 + 4500, x => (x.Sections & 1) != 0);
        var after = await run.Rig.HostAsync();
        if (still is { } st)
            s.Problem(Severity.Safety, "FORCED-SECTION-STAYS-ON",
                $"5 s after the screen went the module still gets section 1 on ({st}): a section switched on by hand keeps spraying with nobody at the screen; host: {after}");
        else s.Ok("the section switched on by hand closed when the screen went");
        await BackAsync(run, s, op, newTab: true);
    }

    private static async Task TwoScreensSilentAsync(Run run, Scenario s)
    {
        if (await OperatorAsync(run, s) is not { } op) return;
        await using var _1 = op;
        await using var phone = await run.OpenAsync(Device.Tablet10, "second tablet");
        if (!await phone.WaitReadyAsync()) { s.Harness("the second tablet did not connect"); return; }
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        op.Wifi!.Mode = LinkMode.Silent;
        bool phoneTook = await phone.WaitForAsync("iHoldControl", 4000);
        long tookMs = ModuleListener.NowMs - t0;
        var vt = await op.ViewAsync(); var vp = await phone.ViewAsync();
        s.Step($"{tookMs} ms: tablet {vt}, second tablet {vp}");
        if (phoneTook && vt.ClaimsOperator)
        {
            await run.ShotAsync(s, op, "C11-tablet.png");
            await run.ShotAsync(s, phone, "C11-second tablet.png");
            s.Problem(Severity.Inconsistency, "TWO-SCREENS-SAY-OPERATOR",
                $"the second tablet took control {tookMs} ms after the tablet's link went silent, and the tablet still says {vt}: two screens show 'Operator'");
        }
        else if (phoneTook) s.Ok($"the second tablet took over and the tablet no longer says Operator: {vt}");
        await Task.Delay(Math.Max(0, 6000 - (int)(ModuleListener.NowMs - t0)));
        op.Wifi.Mode = LinkMode.Up;
        await Task.Delay(2500);
        vt = await op.ViewAsync(); vp = await phone.ViewAsync();
        s.Step($"link back: tablet {vt}, second tablet {vp}");
        if (vt.HoldsControl && vp.HoldsControl) s.Problem(Severity.Safety, "TWO-OPERATORS", "after the link came back both screens hold control");
        else if (!vt.HoldsControl && phoneTook)
            s.Problem(Severity.Improvement, "CAB-SCREEN-LOCKED-OUT", $"the cab tablet is back as {vt}; the second tablet keeps control and the tablet can't take it back (by design, #397: no take-over from a live screen), so the operator has to go and close the second tablet's page");
        Run.PageErrors(s, op); Run.PageErrors(s, phone);
    }

    /// <summary>The module's state is read from the PGN 254 changes, so a module that stopped
    /// hearing PGN 254 altogether would still read as steering. It must have heard one at or after
    /// <paramref name="fromMs"/>.</summary>
    private static bool HeardSince(Run run, Scenario s, long fromMs)
    {
        long heard = run.Rig.Module.LastHeardMs;
        if (heard >= fromMs) return true;
        s.Harness($"no PGN 254 reached the module port in the last {ModuleListener.NowMs - fromMs} ms (the last one {(heard < 0 ? "never came" : $"{ModuleListener.NowMs - heard} ms ago")}): what the module gets can't be read");
        return false;
    }

    private static async Task LongSilentAsync(Run run, Scenario s, int outageMs)
    {
        if (await OperatorAsync(run, s) is not { } op) return;
        await using var _1 = op;
        await using var phone = await run.OpenAsync(Device.Tablet10, "second tablet");
        if (!await phone.WaitReadyAsync()) { s.Harness("the second tablet did not connect"); return; }
        int atStart = run.Rig.ControlLog.Count;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        // A few seconds of painting first, so coverage frames are going out when the link goes.
        await Task.Delay(3000);
        int controlChanges = run.Rig.ControlLog.Count;
        // The work must still be going, or the outage tests nothing.
        var before = await op.ViewAsync();
        var hostBefore = await run.Rig.HostAsync();
        if (run.Rig.Module.Last is not { Steering: true } || !before.HoldsControl)
        {
            string log = string.Join("; ", run.Rig.ControlLog.Skip(atStart).Select(c => $"{c.Ms - (ModuleListener.NowMs - 3000)} ms {(c.Held ? "taken" : "ended")} ({c.Reason})"));
            s.Problem(Severity.Safety, "WORK-STOPPED-WITH-THE-LINK-UP",
                $"after 3 s of painting with the link up the work had stopped: the module gets {run.Rig.Module.Last}, the tablet shows {before}; host: {hostBefore}; control changes since the work started: {(log.Length == 0 ? "none" : log)}");
            return;
        }
        long t0 = ModuleListener.NowMs;
        op.Wifi!.Mode = LinkMode.Silent;
        s.Step("the tablet's link goes silent, sections painting");
        var (steer, secs) = await run.MachineStopsAsync(t0, (int)DeadmanMs + 1500);
        ObserverScenarios.Verdict(s, "minute out of range", steer, secs, DeadmanMs);
        var ended = run.Rig.ControlLog.Skip(controlChanges).Where(c => !c.Held).ToList();
        s.Step(ended.Count > 0
            ? $"the host ended the tablet's control {Run.Ms(ended[0].Ms - t0)} into the outage ({ended[0].Reason})"
            : "the host did not report the tablet's control ending");

        // The second tablet, on its own link, every second: how old its last frame is.
        long worstGap = 0, worstAt = 0;
        while (ModuleListener.NowMs - t0 < outageMs)
        {
            await Task.Delay(1000);
            long gap = (long)await phone.EvalAsync<double>("() => lastTick ? performance.now() - lastTick.t : 1e9");
            if (gap > worstGap) { worstGap = gap; worstAt = ModuleListener.NowMs - t0; }
        }
        var vp = await phone.ViewAsync();
        var host = await run.Rig.HostAsync();
        s.Step($"{outageMs / 1000} s into the outage: the second tablet went up to {Run.Ms(worstGap)} without a frame (read once a second; the longest at {Run.Ms(worstAt)}), the second tablet shows {vp}; host: {host}");
        await run.ShotAsync(s, phone, $"{s.Row.Code}-second tablet-during-outage.png");
        if (worstGap > 2000)
            s.Problem(Severity.Safety, "DEAD-LINK-STALLS-BROADCAST",
                $"while the tablet's link was dead the second tablet, on a working link, got no frame for {Run.Ms(worstGap)} ({Run.Ms(worstAt)} into the outage): the host's sends to the dead link held up the other screens' frames"
                + (vp.HoldsControl ? "; the second tablet holds control now, so its operator watches a still map" : ""));
        else s.Ok($"the second tablet kept getting frames during the outage (none older than {Run.Ms(worstGap)})");
        if (host.AutoSteer)
            s.Problem(Severity.Safety, "STEERING-NOT-STOPPED", $"{outageMs / 1000} s after the tablet's link went the host still has {host}");

        // The waiting minute of frames arrives at once; then both screens and the host.
        op.Wifi.Mode = LinkMode.Up;
        await Task.Delay(5000);
        var vt = await op.ViewAsync(); vp = await phone.ViewAsync();
        host = await run.Rig.HostAsync();
        s.Step($"5 s after the link came back: tablet {vt}, second tablet {vp}; host: {host}");
        if (vt.HoldsControl && vp.HoldsControl) s.Problem(Severity.Safety, "TWO-OPERATORS", "after the link came back both screens hold control");
        if (host.AutoSteer) s.Problem(Severity.Safety, "STEERING-BACK-BY-ITSELF", "AutoSteer is on again without a tap");
        Run.PageErrors(s, op); Run.PageErrors(s, phone);
    }
}
