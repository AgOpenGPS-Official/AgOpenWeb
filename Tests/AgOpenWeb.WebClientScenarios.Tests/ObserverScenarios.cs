// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// O: a second screen beside the operator's (a phone in the cab, a PC in the farm office).
/// It must see everything and change nothing (#397, checklist 13.11); it must not take control
/// from a live operator; and when the operator's screen goes, someone has to be able to carry on.
/// </summary>
public static class ObserverScenarios
{
    public const string Group = "O: a second screen";

    public static async Task RunAsync(Run run)
    {
        await run.ScenarioAsync(Group, "O1", "The second screen sees what the operator does",
            "The cab tablet opens first and holds control; a phone joins. The operator engages AutoSteer and the sections.",
            s => SeesTheSameAsync(run, s));
        await run.ScenarioAsync(Group, "O2", "The second screen changes nothing",
            "From the phone: every button of its bars tapped, and every command the web client can send (read from its scripts) sent, with plausible values. The host's state and files are compared before and after.",
            s => ChangesNothingAsync(run, s));
        await run.ScenarioAsync(Group, "O3", "An edit on the second screen",
            "On the phone: Vehicle & Tool > Tool settings > Hitch, a number changed in the first number box. Does the phone show it as saved?",
            s => EditOnObserverAsync(run, s));
        await run.ScenarioAsync(Group, "O4", "The second screen asks for control",
            "The phone sends control.acquire and control.release while the operator is live.",
            s => AsksForControlAsync(run, s));
        await run.ScenarioAsync(Group, "O5", "The operator's tablet closes: the phone carries on",
            "Working with AutoSteer and sections on, the cab tablet's tab is closed; then the tablet opens the page again.",
            s => OperatorLeavesAsync(run, s));
        await run.ScenarioAsync(Group, "O6", "Three screens, the operator leaves",
            "A tablet holds control, a phone and an office PC observe; the tablet's tab closes.",
            s => ThreeScreensAsync(run, s));
    }

    private static async Task<(Screen Op, Screen Obs)?> PairAsync(Run run, Scenario s, Device? obsDevice = null)
    {
        var op = await run.OpenAsync(Device.Tablet10, "cab tablet");
        if (!await op.WaitReadyAsync() || !await op.WaitForAsync("iHoldControl", 5000))
        { s.Harness("the cab tablet did not connect and take control"); await op.DisposeAsync(); return null; }
        var obs = await run.OpenAsync(obsDevice ?? Device.PhoneLandscape, "phone");
        if (!await obs.WaitReadyAsync()) { s.Harness("the phone did not connect"); await op.DisposeAsync(); await obs.DisposeAsync(); return null; }
        await Task.Delay(300);
        return (op, obs);
    }

    private static async Task SeesTheSameAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, obs)) return;
        await using var _1 = op; await using var _2 = obs;
        var v = await obs.ViewAsync();
        if (!v.HoldsControl && v.Role == "Observer") s.Ok($"the phone joins as {v}");
        else s.Problem(Severity.Bug, "OBSERVER-ROLE-WRONG", $"the phone joined as {v} while the tablet holds control");
        string ro = await obs.EvalAsync<string>("() => (document.getElementById('rn-readonly') || {}).textContent || ''");
        if (ro.Length > 0) s.Ok($"its right nav says '{ro}'");

        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        long t0 = ModuleListener.NowMs;
        bool seen = await obs.WaitForAsync("tick && tick.op && tick.op.autoSteer", 3000);
        long dt = ModuleListener.NowMs - t0;
        if (seen) s.Ok($"the phone shows AutoSteer on ({dt} ms after the module got it)");
        else s.Problem(Severity.Inconsistency, "OBSERVER-STATE-STALE", "the phone did not show AutoSteer on within 3 s");

        // Both screens draw the tractor where the host has it.
        await Task.Delay(1000);
        var host = await run.Rig.ReadAsync(() => (run.Rig.State.Vehicle.Easting, run.Rig.State.Vehicle.Northing));
        const string poseJs = "() => tick && tick.pose ? [tick.pose.e ?? tick.pose.x ?? 0, tick.pose.n ?? tick.pose.y ?? 0] : null";
        var a = await op.EvalAsync<double[]?>(poseJs);
        var b = await obs.EvalAsync<double[]?>(poseJs);
        if (a is { Length: 2 } && b is { Length: 2 })
        {
            double gap = Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
            s.Step(Run.F($"tractor on the tablet {a[0]:0.0},{a[1]:0.0}, on the phone {b[0]:0.0},{b[1]:0.0}, host {host.Easting:0.0},{host.Northing:0.0} (m)"));
            if (gap > 3) s.Problem(Severity.Inconsistency, "OBSERVER-POSITION-DIFFERS", Run.F($"the two screens put the tractor {gap:0.0} m apart at 8 km/h"));
            else s.Ok(Run.F($"both screens put the tractor within {gap:0.0} m of each other"));
        }
        else s.Note("the tick frame carries no pose in e/n form; position not compared");
        await run.ShotAsync(s, obs, "O1-phone-observing.png");
        Run.PageErrors(s, op); Run.PageErrors(s, obs);
    }

    // ------------------------------------------------------------------ O2

    /// <summary>The ids a page can send: data-cmd buttons, transport.send / rnSend / wireRn ids in its scripts.</summary>
    private const string CommandIdsJs = @"async () => {
        const ids = new Set();
        for (const el of document.querySelectorAll('[data-cmd]')) ids.add(el.getAttribute('data-cmd'));
        const srcs = [...document.querySelectorAll('script[src]')].map(s => s.src).filter(u => !u.includes('/vendor/'));
        for (const u of srcs) {
          let t = ''; try { t = await (await fetch(u)).text(); } catch (e) { continue; }
          for (const m of t.matchAll(/(?:transport\.send|rnSend|vtSend)\(\s*['""`]([a-z][a-zA-Z]*\.[a-zA-Z.]+)/g)) ids.add(m[1]);
          for (const m of t.matchAll(/wireRn\('[^']+',\s*'([a-z][a-zA-Z]*\.[a-zA-Z.]+)'/g)) ids.add(m[1]);
        }
        return [...ids].sort();
    }";

    /// <summary>Commands that would do the most harm if an observer got through, with real arguments.</summary>
    private static readonly string[] Dangerous =
    {
        "autosteer.toggle", "section.master", "section.manual", "section.toggle|0", "section.toggle|3",
        "youturn.toggle", "contour.toggle", "track.cycle", "track.nudgeRight", "track.activate|0", "track.select|0",
        "config.set|tool.numSections:3", "config.set|tool.sectionWidth:0,250", "config.set|vehicle.wheelbase:9.5",
        "profile.save", "autosteer.freedrive.toggle", "autosteer.freedrive.right", "autosteer.sendSave",
        "sim.speedUp", "sim.setCoords|41.6,-5.4", "flag.placeHere", "headland.toggle", "prompt.answer|1,1,0",
        "wizard.open", "field.close", "field.deleteJob|" + WebClientRig.FieldName + "\tSpraying", "field.deleteField|" + WebClientRig.FieldName,
        "app.setLanguage|de", "app.resetSettings", "boundary.clear", "track.deleteAll", "ntrip.delete|0", "webcard.delete|0",
        "control.release",
    };

    private static async Task ChangesNothingAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, obs)) return;
        await using var _1 = op; await using var _2 = obs;
        // The tablet works, then everything stands still so nothing changes by itself.
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        await run.Rig.SetSpeedAsync(0);
        await Task.Delay(800);
        var before = await FingerprintAsync(run.Rig);

        // Taps on every button of the phone's bars (the menus' buttons are behind panels).
        var lay = await Layout.MeasureAsync(obs);
        int tapped = 0;
        foreach (var c in lay.Controls.Where(c => c.Reachable && c.Group is "right nav" or "bottom nav" or "section bar"))
        {
            if (c.Name.StartsWith('#')) { await obs.TapAsync(c.Name); tapped++; await Task.Delay(120); }
        }
        await obs.EvalAsync<object?>("() => { if (typeof lnCloseAll === 'function') lnCloseAll(); return null; }");

        // Every command the client knows, then the dangerous ones with real arguments.
        var ids = await obs.EvalAsync<string[]>(CommandIdsJs);
        foreach (var id in ids) { await obs.SendAsync(id); await obs.SendAsync(id + "|0"); }
        foreach (var c in Dangerous) await obs.SendAsync(c);
        await Task.Delay(1500);
        var after = await FingerprintAsync(run.Rig);
        s.Step($"the phone tapped {tapped} buttons and sent {ids.Length * 2 + Dangerous.Length} commands ({ids.Length} ids from its scripts)");

        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var x) || !after.TryGetValue(k, out var y) || x != y).ToList();
        if (changed.Count == 0) s.Ok("nothing changed on the host: settings, field, track, sections, AutoSteer and files are as before");
        else s.Problem(Severity.Safety, "OBSERVER-CHANGED-SOMETHING", "the observing phone changed the host: "
            + string.Join("; ", changed.Take(8).Select(k => $"{k}: {(before.TryGetValue(k, out var x) ? x : "(none)")} → {(after.TryGetValue(k, out var y) ? y : "(gone)")}")));
        var v = await op.ViewAsync();
        if (!v.HoldsControl) s.Problem(Severity.Bug, "OBSERVER-TOOK-CONTROL", $"after the phone's commands the tablet shows {v}");
        else s.Ok("the tablet still holds control");
        Run.PageErrors(s, op); Run.PageErrors(s, obs);
    }

    private static readonly HashSet<string> Moving = new(StringComparer.Ordinal)
    {
        "IsReversing", "IsGpsDataOk", "IsImuDataOk", "IsAutoSteerDataOk", "IsMachineDataOk", "IsAutoSteerHelloOk",
        "IsImuHelloOk", "IsMachineHelloOk", "IsBatteryCharging", "IsBatteryAvailable", "IsNtripConnected",
        "IsTestingNtripConnection", "IsToolPositionReady", "IsAutoSteerActive", "IsUTurnDistanceVisible",
        "IsUTurnOverlayVisible", "IsUTurnButtonVisible", "IsManualUTurnVisible", "IsSimulatorSpeed",
    };

    /// <summary>Everything an observer must not be able to change: the VM's switches, the active track,
    /// the tool and vehicle, the speed, and the files of settings, profiles and fields.</summary>
    private static async Task<Dictionary<string, string>> FingerprintAsync(WebClientRig rig)
    {
        var d = await rig.ReadAsync(() =>
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in typeof(AgOpenWeb.ViewModels.MainViewModel).GetProperties())
                if (p.PropertyType == typeof(bool) && p.Name.StartsWith("Is", StringComparison.Ordinal) && !Moving.Contains(p.Name) && p.GetIndexParameters().Length == 0)
                    try { m["vm." + p.Name] = p.GetValue(rig.Vm)?.ToString() ?? ""; } catch { }
            m["field"] = rig.Vm.CurrentFieldName;
            m["track"] = rig.Vm.SelectedTrack?.Name ?? "";
            m["nudge"] = Run.F($"{rig.Vm.SelectedTrack?.NudgeDistance ?? 0:0.000}");
            m["sim speed"] = Run.F($"{rig.Vm.SimulatorSpeedKph:0.0}");
            m["host"] = rig.HostNow().ToString();
            m["tool sections"] = rig.Store.NumSections.ToString(System.Globalization.CultureInfo.InvariantCulture);
            m["tool widths"] = string.Join(",", rig.Store.Tool.SectionWidths.Select(w => Run.F($"{w:0}")));
            m["wheelbase"] = Run.F($"{rig.Store.Vehicle.Wheelbase:0.00}");
            return m;
        });
        foreach (var f in Directory.EnumerateFiles(rig.DataRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rig.DataRoot, f).Replace('\\', '/');
            if (rel.Contains("/jobs/", StringComparison.Ordinal) || rel.Contains("appstate.json", StringComparison.Ordinal)
                || rel.EndsWith(".bak", StringComparison.Ordinal) || rel.Contains("/Logs/", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith("save_error.txt", StringComparison.Ordinal) || rel.EndsWith(".log", StringComparison.Ordinal))
                continue;
            try { d["file " + rel] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(f)))[..12]; }
            catch (IOException) { d["file " + rel] = "(busy)"; }
        }
        return d;
    }

    // ------------------------------------------------------------------ O3

    private static async Task EditOnObserverAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, obs)) return;
        await using var _1 = op; await using var _2 = obs;
        foreach (var sel in new[] { "#ln-vehicle", "#vth-toolconfig", "#toolcfg button.cfg-tab[data-tab='tcs-hitch']" })
        {
            await obs.ScrollToAsync(sel);
            if (!(await obs.TapAsync(sel)).Hit) { s.Harness($"the phone could not tap {sel}"); return; }
            await Task.Delay(400);
        }
        // The first number box in Tool settings: what it shows, what the phone shows after an edit.
        var e = await obs.EvalAsync<JsonElement>(@"() => {
            const p = document.getElementById('toolcfg');
            const box = [...p.querySelectorAll('input')].find(i => i.offsetParent !== null && !i.disabled && !i.readOnly
                 && (i.type === 'number' || i.dataset.numField !== undefined || /^-?\d+(\.\d+)?$/.test(i.value)));
            if (!box) return null;
            const prev = box.previousElementSibling;
            const lab = (prev && prev.tagName === 'LABEL' ? prev : (box.closest('label') || box.parentElement)).textContent.trim().replace(/\s+/g, ' ').slice(0, 40);
            box.id = box.id || 'web-harness-box';
            return { id: box.id, label: lab, value: box.value, disabled: box.disabled };
        }");
        if (e.ValueKind != JsonValueKind.Object) { s.Harness("no number box found in Tool settings"); return; }
        string id = e.GetProperty("id").GetString()!, label = e.GetProperty("label").GetString()!, was = e.GetProperty("value").GetString()!;
        string typed = double.TryParse(was, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Run.F($"{v + 1:0.##}") : "1";
        var configBefore = await ConfigFrameAsync(op);
        await obs.Page.FillAsync("#" + id, typed);
        await obs.Page.Locator("#" + id).DispatchEventAsync("change");
        await obs.Page.Locator("#" + id).BlurAsync();
        await Task.Delay(1500);
        string shows = await obs.EvalAsync<string>("id => document.getElementById(id).value", id);
        var configAfter = await ConfigFrameAsync(op);
        bool hostChanged = configBefore != configAfter;
        s.Step($"Tool settings '{label}': was {was}, typed {typed} on the phone; after 1.5 s the phone shows {shows}; the host's config {(hostChanged ? "changed" : "is unchanged")}");
        if (hostChanged) s.Problem(Severity.Safety, "OBSERVER-CHANGED-SOMETHING", $"an edit of '{label}' on the observing phone reached the host");
        else if (shows == typed)
            s.Problem(Severity.Improvement, "OBSERVER-EDIT-LOOKS-SAVED", $"the phone keeps showing {typed} for '{label}' as if saved; the host still has {was} and the phone is not told the edit was refused");
        else s.Ok($"the phone went back to {shows}: the refused edit does not look saved");
        bool disabled = await obs.EvalAsync<bool>("id => document.getElementById(id).disabled || document.getElementById(id).readOnly", id);
        if (!disabled) s.Note("the observer's setting boxes are editable (not shown as read-only)");
        await run.ShotAsync(s, obs, "O3-phone-tool-settings.png");
        Run.PageErrors(s, op); Run.PageErrors(s, obs);
    }

    /// <summary>The config frame as the operator's page has it (JSON), for "did the host change".</summary>
    private static Task<string> ConfigFrameAsync(Screen op) => op.EvalAsync<string>("() => JSON.stringify(config)");

    // ------------------------------------------------------------------ O4

    private static async Task AsksForControlAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, obs)) return;
        await using var _1 = op; await using var _2 = obs;
        int changes = run.Rig.ControlLog.Count;
        await obs.SendAsync("control.acquire|Phone");
        await obs.SendAsync("control.release");
        await Task.Delay(800);
        var vo = await op.ViewAsync(); var vp = await obs.ViewAsync();
        if (vo.HoldsControl && !vp.HoldsControl && run.Rig.ControlLog.Count == changes)
            s.Ok($"the operator keeps control ({vo}); the phone stays {vp}");
        else s.Problem(Severity.Safety, "OBSERVER-TOOK-CONTROL", $"after the phone asked: tablet {vo}, phone {vp}");
        Run.PageErrors(s, op); Run.PageErrors(s, obs);
    }

    // ------------------------------------------------------------------ O5

    private static async Task OperatorLeavesAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, obs)) return;
        await using var _1 = op; await using var _2 = obs;
        if (await run.StartWorkAsync(s, op) is { } why) { s.Harness(why); return; }
        await obs.EvalAsync<object?>("() => { toastMsg = ''; return null; }");
        long t0 = ModuleListener.NowMs;
        await op.CloseTabAsync();
        s.Step("the tablet's tab is closed");
        var (steerOff, secOff) = await run.MachineStopsAsync(t0, 3000);
        Verdict(s, "tab closed", steerOff, secOff, 500);
        bool took = await obs.WaitForAsync("iHoldControl", 5000);
        long tookMs = ModuleListener.NowMs - t0;
        string toast = await obs.EvalAsync<string>("() => toastMsg || ''");
        if (took)
        {
            s.Step($"{tookMs} ms: the phone holds control ({await obs.ViewAsync()}); its last notice: '{toast}'");
            if (await run.Rig.ReadAsync(() => run.Rig.Vm.IsAutoSteerEngaged)) s.Problem(Severity.Safety, "CONTROL-HANDOVER-KEEPS-STEERING", "the phone took over with AutoSteer still on");
            if (toast.Length == 0)
                s.Problem(Severity.Improvement, "CONTROL-MOVED-SILENTLY", "the phone became the operator without being told (no notice); only its role line turned to 'Operator'");
            else s.Ok($"the phone is told: '{toast}'");
        }
        else s.Problem(Severity.Bug, "NOBODY-TAKES-OVER", "the phone did not get control within 5 s of the tablet leaving");

        // The tablet comes back: it finds the seat taken.
        await op.NewTabAsync();
        if (!await op.WaitReadyAsync()) { s.Harness("the tablet's new tab did not connect"); return; }
        await Task.Delay(1500);
        var v = await op.ViewAsync();
        s.Step($"the cab tablet opens the page again: {v}");
        if (!v.HoldsControl && took)
            s.Problem(Severity.Improvement, "CAB-SCREEN-LOCKED-OUT", $"the cab tablet comes back as {v}: the phone keeps control and the tablet has no way to take it back while the phone's page stays open (by design, #397: no take-over from a live screen)");
        Run.PageErrors(s, obs);
    }

    // ------------------------------------------------------------------ O6

    private static async Task ThreeScreensAsync(Run run, Scenario s)
    {
        if (await PairAsync(run, s) is not var (op, phone)) return;
        await using var _1 = op; await using var _2 = phone;
        await using var pc = await run.OpenAsync(Device.Pc720, "office PC");
        if (!await pc.WaitReadyAsync()) { s.Harness("the office PC did not connect"); return; }
        await Task.Delay(300);
        await op.CloseTabAsync();
        await Task.Delay(3000);
        var vp = await phone.ViewAsync(); var vc = await pc.ViewAsync();
        int holders = (vp.HoldsControl ? 1 : 0) + (vc.HoldsControl ? 1 : 0);
        s.Step($"3 s after the tablet left: phone {vp}, office PC {vc}");
        if (holders == 1) s.Ok($"exactly one screen took over: the {(vp.HoldsControl ? "phone" : "office PC")}");
        else s.Problem(holders == 0 ? Severity.Bug : Severity.Safety, holders == 0 ? "NOBODY-TAKES-OVER" : "TWO-OPERATORS",
            $"{holders} screens hold control 3 s after the operator left");
        if (vc.HoldsControl) s.Note("the office PC, not a screen in the cab, now drives the tractor");
        Run.PageErrors(s, phone); Run.PageErrors(s, pc);
    }

    /// <summary>The verdict on how fast the machine stopped after the operator's screen went.</summary>
    public static void Verdict(Scenario s, string what, long? steerOff, long? secOff, long withinMs)
    {
        s.Step($"after the {what}: AutoSteer off at the module in {Run.Ms(steerOff)}, sections closed in {Run.Ms(secOff)}");
        if (steerOff is null) s.Problem(Severity.Safety, "STEERING-NOT-STOPPED", $"{what}: the module still gets AutoSteer on");
        else if (steerOff > withinMs) s.Problem(Severity.Safety, "STEERING-STOPS-LATE", $"{what}: AutoSteer off only after {Run.Ms(steerOff)} (expected within {Run.Ms(withinMs)})");
        else s.Ok($"{what}: AutoSteer off at the module within {Run.Ms(steerOff)}");
        if (secOff is null) s.Problem(Severity.Safety, "SECTIONS-NOT-CLOSED", $"{what}: sections still open at the module");
        else if (secOff > withinMs) s.Problem(Severity.Safety, "SECTIONS-CLOSE-LATE", $"{what}: sections closed only after {Run.Ms(secOff)} (expected within {Run.Ms(withinMs)})");
        else s.Ok($"{what}: every section closed within {Run.Ms(secOff)}");
    }
}
