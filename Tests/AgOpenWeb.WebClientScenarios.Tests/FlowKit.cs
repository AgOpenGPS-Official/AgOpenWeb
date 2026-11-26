// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>What kind of thing a flow finding is (the harness plan, section 5).</summary>
public enum FindingKind
{
    /// <summary>AgOpenWeb does the wrong thing.</summary>
    AppDefect,
    /// <summary>The harness, not the app (reported only while the harness is being evaluated).</summary>
    HarnessArtifact,
    /// <summary>AgOpenGPS does the same; listed so it is a choice, not an accident.</summary>
    Parity,
    /// <summary>It works, and could be better.</summary>
    Enhancement,
}

/// <summary>
/// The operator at the cab tablet, doing a flow the way a farmer does it: opening the menus with
/// their own buttons, tapping list rows, typing names on the keyboard, tapping points on the map.
/// Every tap goes to the centre of the control on the screen (a control under another, or off the
/// screen, is not reached and says so); what the operator was told (toasts, hints, host prompts)
/// is recorded from the page; what changed is read on the host, where the guidance runs.
/// </summary>
public sealed class FlowOp : IAsyncDisposable
{
    public Run Run { get; }
    public Scenario S { get; }
    public Screen Screen { get; }
    public WebClientRig Rig => Run.Rig;

    private FlowOp(Run run, Scenario s, Screen screen) { Run = run; S = s; Screen = screen; }

    /// <summary>The cab tablet, connected and holding control, with the page's messages recorded.</summary>
    public static async Task<FlowOp?> OpenAsync(Run run, Scenario s, Device? device = null)
    {
        var screen = await run.OpenAsync(device ?? Device.Tablet10, "cab tablet");
        if (!await screen.WaitReadyAsync() || !await screen.WaitForAsync("iHoldControl", 5000))
        {
            s.Harness("the cab tablet did not connect and take control");
            await screen.DisposeAsync();
            return null;
        }
        var op = new FlowOp(run, s, screen);
        await op.HookMessagesAsync();
        return op;
    }

    public async ValueTask DisposeAsync()
    {
        Run.PageErrors(S, Screen);
        await Screen.DisposeAsync();
        await RestoreZoomAsync();
    }

    /// <summary>The page's zoom before <see cref="TapMapAsync"/> first zoomed out, or null.</summary>
    private double? _zoomBefore;

    /// <summary>Put the zoom back on the host once the page is closed. The host keeps the last
    /// zoom a page saved (view.save) and gives it to every page that connects, so a later
    /// scenario would start zoomed out, and tap the map in other places.</summary>
    private async Task RestoreZoomAsync()
    {
        if (_zoomBefore is not double zoom) return;
        // A save of the zoomed view the page sent just before it closed is handled first.
        await Task.Delay(100);
        await Rig.DrainAsync();
        var restore = Rig.RunAsync(() =>
        {
            var ps = Rig.Services.GetRequiredService<IPersistentStateService>();
            if (ps.State.WebCameraZoom == zoom) return;
            ps.State.WebCameraZoom = zoom;
            ps.Save();
        });
        // A host loop that hangs is reported by DrainAsync; the scenario's end doesn't wait on it.
        await Task.WhenAny(restore, Task.Delay(5000));
    }

    /// <summary>Record every toast, map hint and host prompt the page shows, in order, so a check
    /// can ask what the operator was told.</summary>
    private Task HookMessagesAsync() => Screen.Page.EvaluateAsync(@"() => {
        if (window.__told) return;
        window.__told = [];
        const keep = (kind, m) => { if (m) window.__told.push(kind + ': ' + m); };
        const t = window.showToast; window.showToast = function (m, ms) { keep('toast', m); return t.apply(this, arguments); };
        const h = window.flashHint; window.flashHint = function (m) { keep('hint', m); return h.apply(this, arguments); };
        // The track commands the page sends, for a step that didn't take (was it sent at all?).
        window.__sent = [];
        // And when its presence went out, and the page's long tasks: a control lost to the deadman is
        // either the page not sending (a stalled page) or the host not reading (a stalled connection).
        window.__presence = []; window.__long = [];
        const snd = transport.send; transport.send = function (m) {
          if (typeof m === 'string' && m.startsWith('track.')) window.__sent.push(m);
          if (m === 'control.presence') { window.__presence.push(Math.round(performance.now())); if (window.__presence.length > 400) window.__presence.splice(0, 200); }
          return snd.apply(this, arguments);
        };
        try { new PerformanceObserver(l => { for (const e of l.getEntries()) if (e.duration >= 300) window.__long.push([Math.round(e.startTime), Math.round(e.duration)]); }).observe({ type: 'longtask', buffered: true }); } catch (_) {}
        new MutationObserver(() => {
          const p = document.getElementById('hostprompt');
          if (p && p.classList.contains('open')) keep('prompt', (document.getElementById('hp-title').textContent || '') + ' / ' + (document.getElementById('hp-msg').textContent || ''));
        }).observe(document.getElementById('hostprompt'), { attributes: true, attributeFilter: ['class'] });
        new MutationObserver(() => {
          const el = document.getElementById('maptap-hint');
          if (el && el.classList.contains('show') && el.textContent) keep('banner', el.textContent);
        }).observe(document.getElementById('maptap-hint'), { attributes: true, childList: true, characterData: true, subtree: true });
    }");

    /// <summary>What the page told the operator since the last call (toasts, hints, prompts).</summary>
    public async Task<List<string>> ToldAsync()
    {
        try { return (await Screen.EvalAsync<string[]>("() => { const t = (window.__told || []).slice(); window.__told = []; return t; }")).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>The failures and status lines the host raised since the last call.
    /// An exception or a hang on the host taken from the queue here is reported at once (as
    /// Run reports what is left at the end), or a flow that clears the queue would hide it.</summary>
    public List<string> HostSaid()
    {
        var events = Rig.TakeEvents();
        foreach (var e in events.Where(e => e.Kind is RigEventKind.Exception or RigEventKind.Hang))
            if (_hostExceptions++ < 3) S.Problem(Severity.Bug, "HOST-EXCEPTION", e.Text.Split('\n')[0]);
        return events.Where(e => e.Kind is RigEventKind.Failure or RigEventKind.Status or RigEventKind.Error or RigEventKind.Exception)
                     .Select(e => e.Kind + ": " + e.Text.Split('\n')[0]).ToList();
    }
    private int _hostExceptions;

    // ------------------------------------------------------------------ taps

    /// <summary>Tap a control as a finger does (scrolling its bar first if it scrolls). A control
    /// that is missing, off the screen or covered is a finding about the flow, and false.</summary>
    public async Task<bool> TapAsync(string selector, string what)
    {
        await Screen.ScrollToAsync(selector);
        var r = await Screen.TapAsync(selector);
        if (r.Hit) { S.Step("tap " + what); await Task.Delay(150); return true; }
        if (!r.Found) S.Harness($"{what} ({selector}) is not in the page");
        else S.Problem(Severity.Layout, "FLOW-CONTROL-UNREACHABLE", $"{what} ({selector}) can't be tapped on the {Screen.Device.Name}: {r.Why}");
        return false;
    }

    /// <summary>Tap the first row of a list whose text contains <paramref name="text"/>.</summary>
    public async Task<bool> TapRowAsync(string rowsSelector, string text, string what)
    {
        // The row whose own name is the text ('Flows Field', not 'Flows Field B'); a row that only
        // contains it if none is.
        int i = await Screen.EvalAsync<int>(@"([sel, t]) => {
            const rows = [...document.querySelectorAll(sel)];
            const named = r => [r, ...r.querySelectorAll('*')].some(e => e.childElementCount === 0 && (e.textContent || '').trim() === t);
            const k = rows.findIndex(named);
            return k >= 0 ? k : rows.findIndex(r => (r.textContent || '').includes(t));
        }", new object[] { rowsSelector, text });
        if (i < 0)
        {
            var rows = await RowsAsync(rowsSelector);
            S.Problem(Severity.Bug, "FLOW-ROW-MISSING", $"{what}: no row with '{text}' in the list ({string.Join(" | ", rows.Take(8))})");
            return false;
        }
        return await TapAsync(F($"{rowsSelector}:nth-of-type({i + 1})"), what);
    }

    /// <summary>The text of each row of a list, as the screen shows it.</summary>
    public async Task<List<string>> RowsAsync(string rowsSelector)
        => (await Screen.EvalAsync<string[]>("sel => [...document.querySelectorAll(sel)].map(r => (r.textContent || '').trim())", rowsSelector)).ToList();

    /// <summary>Between two keys, as a quick thumb on a tablet types (about 6 keys a second). At
    /// machine speed (15 ms) the stream of key events starved the page's 500 ms presence timer for
    /// over 1.5 s in a busy field, the deadman took control away and the name typed was dropped.</summary>
    public const int KeyDelayMs = 150;

    /// <summary>Type into a text box the way a person does: tap it, select what is there, type.
    /// <paramref name="then"/> is the key that ends the typing ("Enter", "Tab"), or null.</summary>
    public async Task<bool> TypeAsync(string selector, string text, string what, string? then = null)
    {
        if (!await TapAsync(selector, what)) return false;
        bool focused = await Screen.EvalAsync<bool>("sel => document.activeElement === document.querySelector(sel)", selector);
        if (!focused)
        {
            // A tap that doesn't focus the box (on a phone the keyboard opens; on some layouts the
            // tap lands on the label): focus it as the second tap would.
            await Screen.Page.FocusAsync(selector);
        }
        await Screen.Page.Keyboard.PressAsync("Control+A");
        await Screen.Page.Keyboard.PressAsync("Delete");
        if (text.Length > 0) await Screen.Page.Keyboard.TypeAsync(text, new() { Delay = KeyDelayMs });
        if (then != null) await Screen.Page.Keyboard.PressAsync(then);
        S.Step($"type '{text}' in {what}" + (then != null ? $", then {then}" : ""));
        await Task.Delay(150);
        return true;
    }

    /// <summary>Pick an option of a select box by its label.</summary>
    public async Task<bool> SelectAsync(string selector, string label, string what)
    {
        try
        {
            await Screen.Page.SelectOptionAsync(selector, new Microsoft.Playwright.SelectOptionValue { Label = label });
            S.Step($"pick '{label}' in {what}");
            await Task.Delay(150);
            return true;
        }
        catch (Exception ex)
        {
            var opts = await Screen.EvalAsync<string[]>("sel => [...(document.querySelector(sel) || { options: [] }).options].map(o => o.textContent)", selector);
            S.Problem(Severity.Bug, "FLOW-OPTION-MISSING", $"{what}: no option '{label}' ({string.Join(" | ", opts)}): {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Tap the map at a field point (metres in the field's plane). The point must be on
    /// the map and not under a control; the view is zoomed out until it is (and the zoom put
    /// back on the host when this operator's page closes).</summary>
    public async Task<bool> TapMapAsync(double e, double n, string what)
    {
        bool dragDidNothing = false;
        for (int zoom = 0; zoom < 8; zoom++)
        {
            var at = await Screen.EvalAsync<JsonElement>(@"([e, n]) => {
                if (typeof w2s !== 'function') return { ok: false, why: 'no projection' };
                const s = w2s(e, n); if (!s) return { ok: false, why: 'behind the camera' };
                const x = s[0] + mapL, y = s[1];
                if (!(x >= 0 && y >= 0 && x < innerWidth && y < innerHeight)) return { ok: false, off: true, x, y };
                const h = document.elementFromPoint(x, y);
                return { ok: !!h && h.id === 'ck', x, y, by: h ? (h.id ? '#' + h.id : h.tagName.toLowerCase() + (h.className ? '.' + String(h.className).split(' ')[0] : '')) : '' };
            }", new[] { e, n });
            if (at.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                float x = (float)at.GetProperty("x").GetDouble(), y = (float)at.GetProperty("y").GetDouble();
                await Screen.Page.Touchscreen.TapAsync(x, y);
                S.Step(F($"tap the map at {e:0.0} E, {n:0.0} N ({what}), screen {x:0},{y:0}"));
                await Task.Delay(200);
                return true;
            }
            if (at.TryGetProperty("off", out var off) && off.GetBoolean())
            {
                // The operator zooms out to see the point.
                _zoomBefore ??= await Screen.EvalAsync<double>("() => pxPerM");
                var z = await Screen.TapAsync("#cp-zoomout");
                if (!z.Hit) break;
                await Task.Delay(250);
                continue;
            }
            if (at.TryGetProperty("x", out var px) && zoom < 7)
            {
                // Under a bar or a button: the operator drags the map so the point comes to the
                // middle of the map (a drag pans, it doesn't count as a tap).
                var mid = await Screen.EvalAsync<double[]>("() => [mapL + (innerWidth - mapL - mapR) / 2, innerHeight / 2]");
                double dx = mid[0] - px.GetDouble(), dy = mid[1] - at.GetProperty("y").GetDouble();
                float sx = (float)mid[0], sy = (float)mid[1];
                // The page drops a mouse press within 800 ms of a finger lifting near it (#465:
                // the press some touch stacks add after a tap), so on a touch screen the drag
                // waits that out: else it doesn't pan, and the map stays where it was.
                if (Screen.Device.Touch) await Task.Delay(850);
                var camBefore = await Screen.EvalAsync<double[]>("() => [camE, camN]");
                // Start the drag where the map is (its middle) and move by that much.
                await Screen.Page.Mouse.MoveAsync(sx, sy);
                await Screen.Page.Mouse.DownAsync();
                await Screen.Page.Mouse.MoveAsync(sx + (float)dx, sy + (float)dy, new() { Steps = 12 });
                await Screen.Page.Mouse.UpAsync();
                S.Step(F($"drag the map {dx:0},{dy:0} px to bring {what} into view"));
                await Task.Delay(250);
                var camAfter = await Screen.EvalAsync<double[]>("() => [camE, camN]");
                dragDidNothing = Math.Abs(camAfter[0] - camBefore[0]) < 0.01 && Math.Abs(camAfter[1] - camBefore[1]) < 0.01;
                if (dragDidNothing) S.Step(F($"the drag did not move the map (centre {camAfter[0]:0.0} E, {camAfter[1]:0.0} N)"));
                zoom = Math.Max(zoom, 6);
                continue;
            }
            string by = at.TryGetProperty("by", out var b) ? b.GetString() ?? "" : at.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "";
            // A drag the page never saw is the harness's, not a map that can't be dragged.
            if (dragDidNothing) S.Harness(F($"{what}: the map at {e:0.0} E, {n:0.0} N is under {by}, and the harness's drag did not move the map"));
            else S.Problem(Severity.Layout, "FLOW-MAP-POINT-COVERED", F($"{what}: the map at {e:0.0} E, {n:0.0} N is under {by} and can't be dragged clear"));
            return false;
        }
        S.Harness(F($"{what}: the map point {e:0.0} E, {n:0.0} N could not be brought onto the screen"));
        return false;
    }

    /// <summary>Is a menu panel or dialog card open (class 'open')?</summary>
    public Task<bool> IsOpenAsync(string id) => Screen.EvalAsync<bool>("id => " + OpenJs("id"), id);

    public Task<bool> WaitOpenAsync(string id, int ms = 3000) => Screen.WaitForAsync("(" + "id => " + OpenJs("id") + ")('" + id + "')", ms);

    /// <summary>A panel is open by its class; a dialog card only while the dialog host is open
    /// too (closeDialog closes the host and leaves the card's class).</summary>
    private static string OpenJs(string id) => "{ const el = document.getElementById(" + id + "); if (!el || !el.classList.contains('open')) return false; "
        + "return !el.classList.contains('dlg-card') || document.getElementById('dialoghost').classList.contains('open'); }";

    /// <summary>Close every menu and dialog: the harness tidying the screen between steps. An
    /// Unsaved Coverage question still open is answered Cancel through the page: closing its
    /// card without an answer leaves the host waiting on it, and the page never shows it again.
    /// True when that question was open.</summary>
    public async Task<bool> CloseAllAsync()
    {
        bool asked = await Screen.EvalAsync<bool>(@"() => {
            const asked = typeof _ucovOpen !== 'undefined' && _ucovOpen && typeof ucovResolve === 'function';
            if (asked) ucovResolve('coverage.cancelClose');
            if (typeof lnCloseAll === 'function') lnCloseAll();
            if (typeof closeDialog === 'function') closeDialog();
            return asked;
        }");
        if (!asked) return false;
        S.Step("the Unsaved Coverage question was still open: Cancel");
        // A Status frame sent before the host had the answer opens the question again for a
        // moment; once the host stops asking, the page closes it itself.
        if (!await Screen.WaitForAsync("!(statusBar && statusBar.unsavedCoveragePrompt)", 2000)
            && await Rig.ReadAsync(() => Rig.State.UI.IsUnsavedCoverageDialogVisible))
        {
            // The Cancel did not reach the host (dropped with the control lost, or still queued
            // behind a long save): the next Status frame would open the question again over
            // whatever the flow taps next. Answered once more.
            S.Harness("the Unsaved Coverage question was still asked 2 s after Cancel");
            await Screen.EvalAsync<bool>("() => { ucovResolve('coverage.cancelClose'); return true; }");
            await Screen.WaitForAsync("!(statusBar && statusBar.unsavedCoveragePrompt)", 2000);
        }
        await Screen.EvalAsync<bool>("() => { if (_ucovOpen) ucovResolve('coverage.cancelClose'); else closeDialog(); return true; }");
        return true;
    }

    /// <summary>Close the open dialog card, leaving the menus as they are; an Unsaved Coverage
    /// question is answered Cancel as in <see cref="CloseAllAsync"/>, not hidden.</summary>
    public async Task CloseDialogAsync()
    {
        bool asked = await Screen.EvalAsync<bool>("() => typeof _ucovOpen !== 'undefined' && _ucovOpen");
        if (asked) { await CloseAllAsync(); return; }
        await Screen.EvalAsync<bool>("() => { closeDialog(); return true; }");
    }

    /// <summary>Open a left-nav menu by its button (closing whatever is open first).</summary>
    public async Task<bool> OpenMenuAsync(string navButton, string panel, string what)
    {
        await CloseAllAsync();
        if (!await TapAsync("#" + navButton, what)) return false;
        if (await WaitOpenAsync(panel)) return true;
        S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", $"{what}: the {panel} panel did not open");
        return false;
    }

    /// <summary>Answer the shared confirm (Delete …? Confirm / Cancel); false when none was asked.</summary>
    public async Task<bool> ConfirmAsync(bool yes, string what)
    {
        if (!await WaitOpenAsync("dlg-confirm", 2000))
        {
            S.Problem(Severity.Bug, "FLOW-NO-CONFIRM", $"{what}: no confirmation was asked");
            return false;
        }
        string title = await Screen.EvalAsync<string>("() => document.getElementById('dlgc-title').textContent + ': ' + document.getElementById('dlgc-msg').textContent");
        S.Step($"asked \"{title}\"");
        return await TapAsync(yes ? "#dlgc-ok" : "#dlgc-cancel", yes ? "Confirm" : "Cancel");
    }

    /// <summary>The page's own model, as the screen draws it.</summary>
    public Task<T> PageAsync<T>(string js) => Screen.EvalAsync<T>(js);

    /// <summary>Wait until the page's scene shows a condition (a JS expression).</summary>
    public Task<bool> SeeAsync(string js, int ms = 3000) => Screen.WaitForAsync(js, ms);

    // ------------------------------------------------------------------ the tractor

    /// <summary>The operator drives: the simulator at a speed, wheels straight, for a distance.</summary>
    public async Task DriveAsync(double metres, double kmh = 8, double steerDeg = 0)
    {
        var start = await PoseAsync();
        await Rig.RunAsync(() => { Rig.Vm.SimulatorSteerAngle = steerDeg; Rig.Vm.SimulatorSpeedKph = kmh; });
        double travelled = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var last = start;
        while (travelled < metres && sw.Elapsed.TotalSeconds < metres / (kmh / 3.6) * 2 + 5)
        {
            await Task.Delay(50);
            var p = await PoseAsync();
            travelled += Math.Sqrt((p.E - last.E) * (p.E - last.E) + (p.N - last.N) * (p.N - last.N));
            last = p;
        }
        S.Step(F($"drive {travelled:0} m at {kmh:0} km/h") + (steerDeg != 0 ? F($", wheels at {steerDeg:0}°") : ""));
    }

    /// <summary>Turn the wheel until the heading has turned by <paramref name="degrees"/> (+ right),
    /// at a field speed; then straight.</summary>
    public async Task TurnAsync(double degrees, double kmh = 6, double steerDeg = 25)
    {
        var start = await PoseAsync();
        await Rig.RunAsync(() => { Rig.Vm.SimulatorSteerAngle = Math.Sign(degrees) * steerDeg; Rig.Vm.SimulatorSpeedKph = kmh; });
        double turned = 0, prev = start.HeadingDeg;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Math.Abs(turned) < Math.Abs(degrees) && sw.Elapsed.TotalSeconds < 90)
        {
            await Task.Delay(30);
            var p = await PoseAsync();
            double d = ((p.HeadingDeg - prev + 540) % 360) - 180;
            turned += d; prev = p.HeadingDeg;
        }
        await Rig.RunAsync(() => Rig.Vm.SimulatorSteerAngle = 0);
        S.Step(F($"turn {turned:0}° at {kmh:0} km/h"));
    }

    public Task StopAsync() => Rig.RunAsync(() => { Rig.Vm.SimulatorSpeedKph = 0; Rig.Vm.SimulatorSteerAngle = 0; });

    /// <summary>The tractor on the host: easting, northing (m, the open field's plane), heading (degrees true).</summary>
    public Task<Pose> PoseAsync() => Rig.ReadAsync(() =>
    {
        var v = Rig.State.Vehicle; // the cycle's pivot; heading in degrees true (CreateAPlusLine reads it so)
        return new Pose(v.Easting, v.Northing, (v.Heading % 360 + 360) % 360);
    });

    /// <summary>Put the tractor somewhere, standing, facing a heading: the harness driving it there.
    /// The jump is no travel, so the direction learned from earlier travel is forgotten (Reset
    /// Direction, as a tap on the tractor does): else the first fix at the new spot is measured
    /// from the old one, and that bearing pulls the heading off (or reads as reversing).</summary>
    public async Task PlaceAsync((double Lat, double Lon) at, double headingDeg)
    {
        await Rig.RunAsync(() =>
        {
            if (Rig.Vm.IsAutoSteerEngaged) Rig.Vm.ToggleAutoSteerCommand?.Execute("flow start");
            if (Rig.Vm.IsSectionMasterOn) Rig.Vm.ToggleSectionMasterCommand?.Execute(null);
            Rig.Vm.SimulatorSpeedKph = 0;
            Rig.Vm.SimulatorSteerAngle = 0;
        });
        var p = await JumpAsync(at, headingDeg);
        if (AngleOff(p.HeadingDeg, headingDeg) <= HeadingTolDeg) return;
        // Standing, the heading is the IMU's plus the IMU-to-GPS offset, and a jump keeps that
        // offset: one learned in a turn stays until travel gives a first heading again and snaps
        // it (GpsHeadingFusionService.TryStartHeading). So the tractor drives off a little, wheels
        // straight, as the app tells the operator to ("Drive forward above 1.5 kph"), and is put back.
        S.Step(F($"placed facing {headingDeg:0.0}°, but the tractor's heading is {p.HeadingDeg:0.0}°: drive off straight, then place it again"));
        await DriveAsync(1.5, 4);
        await StopAsync();
        p = await JumpAsync(at, headingDeg);
        if (AngleOff(p.HeadingDeg, headingDeg) <= HeadingTolDeg) return;
        S.Step(F($"placed again facing {headingDeg:0.0}°, but the tractor's heading is {p.HeadingDeg:0.0}°"));
        S.Harness("the tractor's heading did not follow where it was placed, even after driving off straight and placing it again (see the steps)");
    }

    /// <summary>How close the tractor's heading must come to the one it was placed at, degrees:
    /// standing with no offset left it is exact, and T3 judges a line "a hair west of north" by it.</summary>
    private const double HeadingTolDeg = 0.02;

    /// <summary>The jump itself, standing: the tractor's pose once its heading has followed the
    /// simulator's, or after 2 s.</summary>
    private async Task<Pose> JumpAsync((double Lat, double Lon) at, double headingDeg)
    {
        var sim = Rig.Services.GetRequiredService<IGpsSimulationService>();
        var fusion = Rig.Services.GetRequiredService<IGpsHeadingFusionService>();
        // Standing: no fix from here is kept for the first fix after the jump to be measured from.
        fusion.ResetDirection();
        // sim.setCoords starts the simulator over facing north, so the heading is set after it.
        await Rig.SendAsync("sim.setCoords", F($"{at.Lat:F9},{at.Lon:F9}"));
        await Rig.RunAsync(() => sim.SetHeading(headingDeg * Math.PI / 180));
        // Again at the new spot, in case a fix from before the stop learned a direction meanwhile.
        fusion.ResetDirection();
        await Task.Delay(500);
        await Rig.DrainAsync();
        // The tractor's heading follows the simulator's within a few fixes.
        var p = await PoseAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (AngleOff(p.HeadingDeg, headingDeg) > HeadingTolDeg && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(50);
            p = await PoseAsync();
        }
        return p;
    }

    /// <summary>How far apart two headings are, degrees (0 to 180).</summary>
    private static double AngleOff(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    // ------------------------------------------------------------------ host reads

    public Task<List<TrackInfo>> TracksAsync() => Rig.ReadAsync(() => Rig.Vm.SavedTracks.Select((t, i) => TrackInfo.Of(i, t, Rig.Vm.SelectedTrack)).ToList());

    /// <summary>The track list as the screen has it (scene.trackList).</summary>
    public async Task<List<(string Name, string Type, bool Active, bool Visible)>> ScreenTracksAsync()
    {
        var e = await Screen.EvalAsync<JsonElement>("() => (scene && scene.trackList || []).map(t => [t.name, t.type || '', !!t.active, !!t.visible])");
        return e.EnumerateArray().Select(a => (a[0].GetString() ?? "", a[1].GetString() ?? "", a[2].GetBoolean(), a[3].GetBoolean())).ToList();
    }

    /// <summary>Wait for the host to have a condition (polling on its loop).</summary>
    public Task<bool> HostAsync(Func<bool> done, int ms = 3000) => Rig.WaitUntilAsync(done, ms);

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The field folder on disk (what a restart reads back).</summary>
    public string FieldDir(string field) => Path.Combine(Rig.DataRoot, "AgOpenWeb", "Fields", field);
}

public sealed record Pose(double E, double N, double HeadingDeg)
{
    public override string ToString() => FlowOp.F($"{E:0.0} E, {N:0.0} N, heading {HeadingDeg:0.0}°");
}

/// <summary>One track as the host holds it.</summary>
public sealed record TrackInfo(int Index, string Name, TrackType Type, bool Visible, bool Active, int Points,
    double AE, double AN, double BE, double BN, double HeadingDeg, bool Closed)
{
    public static TrackInfo Of(int i, Track t, Track? selected)
    {
        var p = t.Points;
        double ae = p.Count > 0 ? p[0].Easting : double.NaN, an = p.Count > 0 ? p[0].Northing : double.NaN;
        double be = p.Count > 1 ? p[^1].Easting : double.NaN, bn = p.Count > 1 ? p[^1].Northing : double.NaN;
        double h = p.Count > 1 ? Math.Atan2(p[1].Easting - p[0].Easting, p[1].Northing - p[0].Northing) * 180 / Math.PI : double.NaN;
        return new TrackInfo(i, t.Name, t.Type, t.IsVisible, ReferenceEquals(t, selected), p.Count, ae, an, be, bn,
            double.IsNaN(h) ? h : (h % 360 + 360) % 360, t.IsClosed);
    }

    /// <summary>Distance (m) from a point to this track: the line through A and B for an AB line,
    /// the nearest segment for a curve.</summary>
    public static double DistanceTo(Track t, double e, double n)
    {
        var p = t.Points;
        if (p.Count < 2) return double.NaN;
        if (p.Count == 2 && t.Type == TrackType.ABLine)
        {
            double dx = p[1].Easting - p[0].Easting, dn = p[1].Northing - p[0].Northing, len = Math.Sqrt(dx * dx + dn * dn);
            return len < 1e-9 ? double.NaN : Math.Abs((e - p[0].Easting) * dn - (n - p[0].Northing) * dx) / len;
        }
        double best = double.MaxValue;
        for (int i = 0; i + 1 < p.Count; i++)
        {
            double ax = p[i].Easting, ay = p[i].Northing, bx = p[i + 1].Easting, by = p[i + 1].Northing;
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double u = l2 > 0 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / l2, 0, 1) : 0;
            double qx = ax + u * dx - e, qy = ay + u * dy - n;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }

    public override string ToString() => FlowOp.F($"#{Index} '{Name}' {Type}{(Active ? " active" : "")}{(Visible ? "" : " hidden")} {Points} pts heading {HeadingDeg:0.0}°");
}

/// <summary>
/// The fields the flows work in, each its own made-up field away from the others (so a group's
/// tracks and boundaries never mix with another's), made by the operator from a KML file in the
/// Import folder (Field Operations › Fields and Jobs › From KML), as a farmer brings a field over.
/// </summary>
public static class FlowFields
{
    /// <summary>Where a flow group's field starts: 1 km steps east of the run's field.</summary>
    public static (double Lat, double Lon) Corner(int slot) => WebClientRig.At(1000.0 * (slot + 1), 0);

    /// <summary>A point of that field (metres east and north of its corner).</summary>
    public static (double Lat, double Lon) At(int slot, double eastM, double northM) => WebClientRig.At(1000.0 * (slot + 1) + eastM, northM);

    /// <summary>The flows' field: 240 m wide, its west side 420 m long, its east side 380 m (a
    /// slanted north edge), so the longest edge is the west one.</summary>
    public static readonly (double E, double N)[] Shape = { (0, 0), (240, 0), (240, 380), (0, 420) };

    /// <summary>The KML the farmer copies into the Import folder.</summary>
    public static async Task<string> WriteKmlAsync(WebClientRig rig, int slot, string name, (double E, double N)[]? shape = null)
    {
        Directory.CreateDirectory(rig.ImportDir);
        var ring = (shape ?? Shape).Append((shape ?? Shape)[0]).Select(p => At(slot, p.E, p.N)).ToArray();
        string coords = string.Join(" ", ring.Select(p => FlowOp.F($"{p.Lon:F9},{p.Lat:F9},0")));
        string file = name.Replace(' ', '-') + ".kml";
        await File.WriteAllTextAsync(Path.Combine(rig.ImportDir, file),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>"
            + name + "</name><Placemark><name>" + name + "</name><Polygon><outerBoundaryIs><LinearRing><coordinates>"
            + coords + "</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Document></kml>\n");
        return file;
    }

    /// <summary>The operator makes the field from the KML through the web client and it opens.
    /// The tractor is driven to the field's corner first (GPS far from the field asks first).</summary>
    public static async Task<bool> MakeFromKmlAsync(FlowOp op, int slot, string name, (double E, double N)[]? shape = null)
    {
        string file = await WriteKmlAsync(op.Rig, slot, name, shape);
        await op.PlaceAsync(At(slot, 20, 30), 0);
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return false;
        if (!await op.TapAsync("#fo-fields", "Fields and Jobs")) return false;
        if (!await op.WaitOpenAsync("fieldsandjobs")) { op.S.Harness("Fields and Jobs did not open"); return false; }
        if (!await op.TapAsync("#fj-kml", "From KML")) return false;
        if (!await op.WaitOpenAsync("kmlimport")) { op.S.Harness("From KML did not open"); return false; }
        // The Import folder is listed when the picker opens (and every 3 s while it stays open).
        if (!await op.SeeAsync($"[...document.querySelectorAll('#kml-file option')].some(o => o.value === '{file}')", 8000))
        {
            op.S.Problem(Severity.Bug, "FLOW-IMPORT-FILE-NOT-LISTED", $"From KML: '{file}' copied into the Import folder is not in the list");
            return false;
        }
        if (!await op.SelectAsync("#kml-file", file, "the KML file")) return false;
        if (!await op.TypeAsync("#kml-name", name, "the new field's name")) return false;
        if (!await op.TapAsync("#kml-create", "Create Field")) return false;
        await LeaveUnsavedAsync(op, name);
        if (!await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == name && !op.Rig.State.UI.IsBusy, 20000))
        {
            op.S.Problem(Severity.Bug, "FLOW-FIELD-NOT-OPENED", $"From KML: field '{name}' did not open (open: '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}')");
            return false;
        }
        await op.SeeAsync($"scene && scene.hasField && scene.fieldName === {JsonSerializer.Serialize(name)}", 5000);
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
        op.S.Step($"field '{name}' made from the KML and open");
        return true;
    }

    /// <summary>The group's field open with the tractor in it: made from its KML the first time,
    /// opened from Fields and Jobs (Open Field Only) when it exists but another is open.</summary>
    public static async Task<bool> EnsureOpenAsync(FlowOp op, int slot, string name, (double E, double N)[]? shape = null)
    {
        bool open = await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == name);
        if (open) { await op.PlaceAsync(At(slot, 20, 30), 0); return true; }
        if (!Directory.Exists(op.FieldDir(name))) return await MakeFromKmlAsync(op, slot, name, shape);
        await op.PlaceAsync(At(slot, 20, 30), 0);
        return await OpenFieldOnlyAsync(op, name);
    }

    /// <summary>Field Operations › Fields and Jobs › the field's row › Open Field Only.</summary>
    public static async Task<bool> OpenFieldOnlyAsync(FlowOp op, string name)
    {
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return false;
        if (!await op.TapAsync("#fo-fields", "Fields and Jobs")) return false;
        if (!await op.WaitOpenAsync("fieldsandjobs")) { op.S.Harness("Fields and Jobs did not open"); return false; }
        if (!await op.SeeAsync($"[...document.querySelectorAll('#fj-fieldlist .fj-frow .fj-fname')].some(r => r.textContent === {JsonSerializer.Serialize(name)})", 5000))
        {
            var rows = await op.RowsAsync("#fj-fieldlist .fj-frow");
            op.S.Problem(Severity.Bug, "FLOW-FIELD-NOT-LISTED", $"Fields and Jobs: '{name}' is not in the field list ({string.Join(" | ", rows.Take(8))})");
            return false;
        }
        if (!await op.TapRowAsync("#fj-fieldlist .fj-frow", name, $"the row '{name}'")) return false;
        if (!await op.TapAsync("#fj-openonly", "Open Field Only")) return false;
        await LeaveUnsavedAsync(op, name);
        if (!await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == name && !op.Rig.State.UI.IsBusy, 15000))
        {
            op.S.Problem(Severity.Bug, "FLOW-FIELD-NOT-OPENED", $"Open Field Only: '{name}' did not open (open: '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}')");
            return false;
        }
        await op.SeeAsync($"scene && scene.hasField && scene.fieldName === {JsonSerializer.Serialize(name)}", 5000);
        op.S.Step($"field '{name}' open");
        return true;
    }

    /// <summary>The group's A+ line due north, made from Quick AB with the tractor standing at
    /// (<paramref name="e"/>, 40) of its field when the field has no visible AB line, and active.</summary>
    public static async Task<Track?> NorthLineAsync(FlowOp op, int slot, double e)
    {
        var line = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.FirstOrDefault(t => t.IsVisible && t.Type == TrackType.ABLine));
        if (line == null)
        {
            await op.PlaceAsync(At(slot, e, 40), 0);
            int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
            if (!await TrackFlows.OpenQuickAbAsync(op)) return null;
            if (!await op.TapAsync("[data-qab=aPlus]", "A+")) return null;
            if (await TrackFlows.NewTrackAsync(op, before, "A+") is not var (_, t)) return null;
            line = t;
        }
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.SelectedTrack) != line)
        {
            if (!await TrackFlows.PickRowAsync(op, line.Name)) return null;
        }
        await op.CloseAllAsync();
        return line;
    }

    /// <summary>The tractor on the pass the A+ line was made from (half a boom left of the line), <paramref name="n"/> m
    /// into the field facing along the line (<paramref name="headingDeg"/> 0) or back (180), moving at
    /// <paramref name="kmh"/> with AutoSteer on from its button. The line can be a little off north (Quick AB
    /// takes the tractor's heading), so the pass is found square to the line where the tractor is put, not at the
    /// line's own easting. False when it could not (a harness note).</summary>
    public static async Task<bool> SteerOnPassAsync(FlowOp op, int slot, Track line, double n, double headingDeg, double kmh, double passOffset = 0)
    {
        // Where the field's corner is in the plane, from a known spot.
        await op.PlaceAsync(At(slot, 0, 0), headingDeg);
        var corner = await op.PoseAsync();
        var a = line.Points[0];
        double h = line.Heading, d = -4.0 + passOffset; // + = right of A→B
        double north = corner.N + n;
        double eOnLine = a.Easting + (north - a.Northing) * Math.Tan(h);
        double e = eOnLine + d * Math.Cos(h), nn = north - d * Math.Sin(h);
        double faceDeg = (h * 180 / Math.PI + headingDeg + 360) % 360;
        await op.PlaceAsync(At(slot, e - corner.E, nn - corner.N), faceDeg);
        await op.Rig.SetSpeedAsync(kmh);
        await Task.Delay(800);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-steer", () => op.Rig.Vm.IsAutoSteerEngaged))
        { op.S.Harness($"AutoSteer did not engage (told {string.Join(" / ", await op.ToldAsync())})"); return false; }
        return true;
    }

    /// <summary>Leaving a field worked with no job asks first (Unsaved Coverage); a group's set-up
    /// opening its own field answers Discard: the work left behind was another group's.</summary>
    private static async Task LeaveUnsavedAsync(FlowOp op, string name)
    {
        if (!await AskedUnsavedAsync(op, () => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == name)) return;
        op.S.Step("asked: Unsaved Coverage (work left by an earlier scenario): Discard");
        await op.TapAsync("#ucov-discard", "Unsaved Coverage: discard");
    }

    /// <summary>Whether the Unsaved Coverage question comes up, looking for up to 2 s or until
    /// the host has done what was asked without it (<paramref name="done"/>). The page shows it
    /// when a Status frame says the host asks, and those come only twice a second.</summary>
    private static async Task<bool> AskedUnsavedAsync(FlowOp op, Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (await op.IsOpenAsync("dlg-unsavedcov")) return true;
            if (sw.ElapsedMilliseconds >= 2000 || await op.Rig.ReadAsync(done)) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>Field Operations › Close, answering the unsaved-coverage question with
    /// <paramref name="unsaved"/> ("save", "discard", "cancel") if it is asked.</summary>
    public static async Task<bool> CloseFieldAsync(FlowOp op, string unsaved = "save")
    {
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return false;
        if (!await op.TapAsync("#fo-close", "Close")) return false;
        if (await AskedUnsavedAsync(op, () => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy))
        {
            op.S.Step("asked: Unsaved Coverage");
            await op.TapAsync(unsaved switch { "discard" => "#ucov-discard", "cancel" => "#ucov-cancel", _ => "#ucov-save" }, "Unsaved Coverage: " + unsaved);
        }
        bool closed = await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 10000);
        if (closed) op.S.Step("field closed");
        return closed;
    }
}
