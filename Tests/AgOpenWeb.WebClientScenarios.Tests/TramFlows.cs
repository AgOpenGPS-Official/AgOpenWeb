// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// R: tram lines set up the operator's way, in Field Tools › Field Builder › Tram: a system added
/// on the active line, its width, passes, direction, offset and mode changed, the boundary as its
/// reference, switched off and on, the bottom bar's tram button, the line it follows renamed, the
/// field closed and opened, the system deleted. Where the lines are is read from what the screen
/// draws (the Scene's tram lines), measured square to the line they follow.
/// The careless operator types a width of 0 or 150 m, 1500 or -3 passes, and 0 passes on a
/// boundary system.
/// </summary>
public static class TramFlows
{
    public const string Group = "R: tram lines";
    private const int Slot = 2;
    private const string Field = "Flows Tram";
    private const string Reference = "Tram reference";
    /// <summary>The line the tram system follows: 'Tram reference' once R1 has renamed it, else its own name.</summary>
    private static string Line { get; set; } = Reference;

    public static async Task RunAsync(Run run)
    {
        Line = Reference;
        // The tram display (Off, All, Lines only, Outer only) is the app's, not the field's, and
        // every tram edit sets it to All. It is put back after the group, or the groups after this
        // one would draw tram lines and send tram bits that a run without R doesn't.
        var display = await run.Rig.ReadAsync(() => run.Rig.Store.Tram.DisplayMode);
        try
        {
            await Flow(run, "R1", "A tram system on the active line", "Careful operator: an A+ line due north, renamed; Field Tools › Field Builder › Tram › + Add.", AddAsync);
            await Flow(run, "R2", "Width, passes, direction, offset and mode", "Tram editor: 24 m typed as the width, 3 passes; Right; 2 m offset; Edge; then back to Both, 0 m, Wheel Track, 1 pass.", EditAsync);
            await Flow(run, "R3", "Tram values typed carelessly", "Careless operator in the tram editor: a width of 0, then 150 m; 1500 passes, then -3.", CarelessAsync);
            await Flow(run, "R4", "The boundary as the reference", "Tram editor › Reference › (Boundary); the careless operator types 0 passes; then back to the line.", BoundaryRefAsync);
            await Flow(run, "R5", "Switched off and on, and the bottom bar's tram button", "Tram editor: Enabled Off, On; Field Builder closed, the bottom bar's Tram button tapped until the lines show and again until they don't.", OnOffAsync);
            await Flow(run, "R6", "The line it follows renamed", "Tracks manager: the reference line renamed; the tram system follows it.", RenameRefAsync);
            await Flow(run, "R7", "The tram system after the field is closed and opened", "Field Operations › Close, then Open Field Only: the same system and lines.", ReopenAsync);
            await Flow(run, "R8", "The tram system deleted", "Tram tab: the system's row, Delete, Cancel; Delete, Confirm.", DeleteAsync);
        }
        finally
        {
            await run.Rig.RunAsync(() => run.Rig.Store.Tram.DisplayMode = display);
        }
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            if (!await FlowFields.EnsureOpenAsync(op, Slot, Field)) { s.Harness("the tram field could not be opened"); return; }
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
        }, onTheRunsLine: false);

    // ------------------------------------------------------------------ helpers

    private sealed record Sys(int Index, string Name, string RefLabel, double Width, int Mode, double Offset, int Direction, int Passes, bool Enabled, bool IsBoundary);

    private static async Task<List<Sys>> ScreenSystemsAsync(FlowOp op)
    {
        string json = await op.PageAsync<string>("() => JSON.stringify((scene && scene.tramSystems) || [])");
        var list = new List<Sys>();
        foreach (var s in JsonDocument.Parse(json).RootElement.EnumerateArray())
            list.Add(new Sys(s.GetProperty("index").GetInt32(), s.GetProperty("name").GetString()!, s.GetProperty("refLabel").GetString()!,
                s.GetProperty("width").GetDouble(), s.GetProperty("mode").GetInt32(), s.GetProperty("offset").GetDouble(),
                s.GetProperty("direction").GetInt32(), s.GetProperty("passCount").GetInt32(), s.GetProperty("enabled").GetBoolean(), s.GetProperty("isBoundary").GetBoolean()));
        return list;
    }

    private static async Task<List<List<(double E, double N)>>> ScreenLinesAsync(FlowOp op)
    {
        string json = await op.PageAsync<string>("() => JSON.stringify(((scene && scene.tramLines) || []).map(l => l.map(p => [p.e, p.n])))");
        return JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(l => l.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList()).ToList();
    }

    /// <summary>Each line's offset square to the reference line, right of A→B positive (east of a line due north),
    /// rounded to 0.05 m, distinct and sorted; null when a line isn't parallel to it.</summary>
    private static List<double>? Offsets(List<List<(double E, double N)>> lines, Track reference, out string why)
    {
        var a = reference.Points[0]; var b = reference.Points[^1];
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l = Math.Sqrt(dx * dx + dy * dy);
        dx /= l; dy /= l;
        var offs = new List<double>();
        why = "";
        foreach (var line in lines)
        {
            var d = line.Select(p => (p.E - a.Easting) * dy - (p.N - a.Northing) * dx).ToList();
            if (d.Max() - d.Min() > 0.15) { why = FlowOp.F($"a line runs {d.Min():0.00} to {d.Max():0.00} m from the reference: not parallel"); return null; }
            offs.Add(Math.Round(d.Average() * 20) / 20);
        }
        return offs.Distinct().OrderBy(x => x).ToList();
    }

    private static List<double> Expected(double width, int passes, int dir, double offset, int mode, double hw)
    {
        var o = new List<double>();
        for (int i = 0; i < passes; i++)
        {
            double b = (mode == 0 ? width * i : width * 0.5 + width * i) + offset;
            bool pos = dir is 0 or 2, neg = dir is 0 or 1;
            if (pos) { o.Add(b - hw); o.Add(b + hw); }
            if (neg && !(i == 0 && mode == 0 && dir == 0)) { o.Add(-b - hw); o.Add(-b + hw); }
        }
        return o.Select(x => Math.Round(x * 20) / 20).Distinct().OrderBy(x => x).ToList();
    }

    private static string Show(IEnumerable<double> o) => string.Join(", ", o.Select(x => FlowOp.F($"{x:0.00}")));

    /// <summary>The screen's tram lines are where the system's settings put them, measured from
    /// <paramref name="line"/> (the line the system follows when not given).</summary>
    private static async Task<bool> LinesAtAsync(FlowOp op, double width, int passes, int dir, double offset, int mode, string what, string? line = null)
    {
        string name = line ?? Line;
        var reference = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.FirstOrDefault(t => t.Name == name));
        if (reference == null) { op.S.Harness(TrackFlows.Masked($"{what}: the reference line '{name}' is gone")); return false; }
        double hw = await op.Rig.ReadAsync(() => op.Rig.Store.Vehicle.TrackWidth / 2);
        var want = Expected(width, passes, dir, offset, mode, hw);
        List<double>? got = null; string why = "";
        // The scene follows the edit within a frame or two.
        for (int k = 0; k < 15; k++)
        {
            got = Offsets(await ScreenLinesAsync(op), reference, out why);
            if (got != null && got.Count == want.Count && got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= 0.06)) break;
            await Task.Delay(200);
        }
        if (got == null) { op.S.Problem(Severity.Bug, "TRAM-LINES-NOT-PARALLEL", $"{what}: {why}"); return false; }
        if (got.Count == want.Count && got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= 0.06))
        {
            op.S.Ok($"{what}: the screen draws {got.Count} wheel tracks at {Show(got)} m from the line (east +)");
            return true;
        }
        op.S.Problem(Severity.Bug, "TRAM-LINES-MISPLACED", $"{what}: wheel tracks at {Show(got)} m from the line; the settings put them at {Show(want)} m");
        return false;
    }

    private static async Task<bool> OpenTramTabAsync(FlowOp op)
    {
        await op.CloseDialogAsync();
        if (!await op.OpenMenuAsync("ln-fieldtools", "fieldtools", "Field Tools")) return false;
        if (!await op.TapAsync("#ft-fieldbuilder", "Field Builder")) return false;
        if (!await op.WaitOpenAsync("fieldbuilder")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Field Builder did not open"); return false; }
        return await op.TapAsync("#fieldbuilder .fb-tab[data-fbtab=tram]", "the Tram tab");
    }

    private static async Task<bool> OpenEditorAsync(FlowOp op)
    {
        if (await op.SeeAsync("!document.getElementById('fb-tramedit').hidden && document.getElementById('fieldbuilder').classList.contains('open')", 200)) return true;
        if (!await OpenTramTabAsync(op)) return false;
        if (await op.PageAsync<int>("() => document.querySelectorAll('#fb-tramlist .fb-hlrow').length") == 0) { op.S.Harness("no tram system to edit"); return false; }
        if (!await op.TapAsync("#fb-tramlist .fb-hlrow:nth-of-type(1)", "the tram system's row")) return false;
        if (!await op.TapAsync("#fb-tram-edit", "Edit")) return false;
        if (await op.SeeAsync("!document.getElementById('fb-tramedit').hidden", 1500)) return true;
        op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Edit did not open the tram editor");
        return false;
    }

    private static Task<Models.Tram.TramSystem?> HostSystemAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.Vm.TramSystems.FirstOrDefault());

    /// <summary>A scenario that needs what an earlier one left: the system R1 adds and, with
    /// <paramref name="plain"/>, following the line and on (R4 and R5 put it back so). When it isn't
    /// there, a note names the scenario to look at and true is returned (skip): the fault is that
    /// scenario's, not this one's.</summary>
    private static async Task<bool> SkippedAsync(FlowOp op, string see = "R1", bool plain = false, string lineBy = "R4")
    {
        var s = await HostSystemAsync(op);
        string? why = s == null ? $"no tram system (see {see})"
            : !plain ? null
            : s.ReferenceBoundaryIndex >= 0 ? "the tram system still follows the boundary (see R4)"
            : s.ReferenceTrackName != Line ? $"the tram system doesn't follow the line (see {lineBy})"
            : !s.IsEnabled ? "the tram system is still switched off (see R5)"
            : null;
        if (why == null) return false;
        op.S.Note("skipped: " + why);
        return true;
    }

    private static async Task<bool> SetAsync(FlowOp op, string box, string value, string what)
    {
        if (!await OpenEditorAsync(op)) return false;
        if (!await op.TypeAsync(box, value, what, "Tab")) return false;
        await Task.Delay(500);
        return true;
    }

    private static async Task<bool> PickAsync(FlowOp op, string selector, string what)
    {
        if (!await OpenEditorAsync(op)) return false;
        if (!await op.TapAsync(selector, what)) return false;
        await Task.Delay(500);
        return true;
    }

    // ------------------------------------------------------------------ R1

    private static async Task AddAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await TrackFlows.OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=aPlus]", "A+")) return;
        if (await TrackFlows.NewTrackAsync(op, before, "A+ for the tram lines") is not var (info, _)) return;
        int n0 = op.S.Row.Findings.Count;
        if (await TrackFlows.RenameAsync(op, info.Name, Reference, "Enter") != Reference)
        {
            // A row or button the rename couldn't reach is the helper's finding, and the name didn't
            // take; the scenarios after this one then skip (no tram system).
            if (op.S.Row.Findings.Count > n0) return;
            // The rename itself is T9's; here the tram system is, so go on with the line's own name.
            // That name carries the clock ('A+ 21:07'): every text that quotes it is masked.
            op.S.Problem(Severity.Bug, "RENAME-NOT-SAVED", TrackFlows.Masked($"'{info.Name}' renamed '{Reference}' with Enter (twice): the host has {string.Join(", ", (await op.TracksAsync()).Select(x => $"'{x.Name}'"))}"));
            Line = info.Name;
        }
        if (!await TrackFlows.PickRowAsync(op, Line)) return;
        await op.CloseDialogAsync();
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.SelectedTrack?.Name) != Line)
        { op.S.Problem(Severity.Bug, "TRACK-NOT-ACTIVE", TrackFlows.Masked($"the row of '{Line}' tapped in the Tracks manager: it is not the active line")); return; }

        double toolTram = await op.Rig.ReadAsync(() => op.Rig.Store.Tram.TramWidth);
        if (!await OpenTramTabAsync(op)) return;
        string empty = await op.PageAsync<string>("() => document.getElementById('fb-tramlist').textContent");
        op.S.Step($"the Tram tab says '{empty.Trim()}'");
        if (!await op.TapAsync("#fb-tram-add", "+ Add")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.TramSystems.Count == 1, 3000)) { op.S.Problem(Severity.Bug, "TRAM-NOT-ADDED", $"+ Add: no tram system (told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        var sys = (await HostSystemAsync(op))!;
        op.S.Step(FlowOp.F($"host: '{sys.Name}', {sys.TramWidth:0.0} m, {sys.PassCount} pass(es), {sys.Direction}, {sys.Mode}, follows '{sys.ReferenceTrackName}'"));
        if (sys.ReferenceTrackName == Line) op.S.Ok(TrackFlows.Masked($"the system follows the active line '{Line}'"));
        else op.S.Problem(Severity.Bug, "TRAM-WRONG-REFERENCE", TrackFlows.Masked($"+ Add with '{Line}' active: the system follows '{sys.ReferenceTrackName ?? "(Boundary)"}'"));
        if (Math.Abs(sys.TramWidth - toolTram) < 0.01) op.S.Ok(FlowOp.F($"its width is the tool's tram width, {toolTram:0.0} m (#675)"));
        else op.S.Problem(Severity.Bug, "TRAM-WIDTH-NOT-FROM-TOOL", FlowOp.F($"+ Add: {sys.TramWidth:0.0} m, the tool's tram width is {toolTram:0.0} m"));
        if (await op.SeeAsync("!document.getElementById('fb-tramedit').hidden", 2000))
        {
            string title = await op.PageAsync<string>("() => document.getElementById('fb-tram-title').textContent");
            string w = await op.PageAsync<string>("() => document.getElementById('fb-tram-w').value");
            string p = await op.PageAsync<string>("() => document.getElementById('fb-tram-passes').value");
            string r = await op.PageAsync<string>("() => document.getElementById('fb-tram-ref').value");
            op.S.Step($"the editor opens on it: '{title}', width {w}, passes {p}, reference '{r}'");
            if (r == Line && double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wv) && Math.Abs(wv - sys.TramWidth) < 0.05 && p == sys.PassCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                op.S.Ok("the editor shows the host's values");
            else op.S.Problem(Severity.Inconsistency, "TRAM-EDITOR-STALE", TrackFlows.Masked(FlowOp.F($"the editor shows width {w}, passes {p}, reference '{r}'; the host has {sys.TramWidth:0.0}, {sys.PassCount}, '{sys.ReferenceTrackName}'")));
        }
        else op.S.Problem(Severity.Inconsistency, "TRAM-EDITOR-NOT-OPENED", "+ Add did not open the editor on the new system");
        await LinesAtAsync(op, sys.TramWidth, sys.PassCount, 0, 0, 0, "1 pass, Both");
        await op.Run.ShotAsync(op.S, op.Screen, "R1-tram-added.png");
    }

    // ------------------------------------------------------------------ R2, R3

    private static async Task EditAsync(FlowOp op)
    {
        if (await SkippedAsync(op)) return;
        if (!await SetAsync(op, "#fb-tram-w", "24", "Tram width")) return;
        if (!await SetAsync(op, "#fb-tram-passes", "3", "Passes")) return;
        bool saved = await op.HostAsync(() => op.Rig.Vm.TramSystems.Count > 0 && Math.Abs(op.Rig.Vm.TramSystems[0].TramWidth - 24) < 0.01 && op.Rig.Vm.TramSystems[0].PassCount == 3, 2000);
        var s = (await HostSystemAsync(op))!;
        if (saved) op.S.Ok("24 m and 3 passes reach the host");
        else op.S.Problem(Severity.Bug, "TRAM-EDIT-NOT-SAVED", FlowOp.F($"24 m, 3 passes typed: the host has {s.TramWidth:0.0} m, {s.PassCount} passes"));
        await LinesAtAsync(op, 24, 3, 0, 0, 0, "24 m, 3 passes, Both");
        await op.Run.ShotAsync(op.S, op.Screen, "R2-three-passes.png");

        if (!await PickAsync(op, "#fb-tramedit [data-tdir='2']", "Right")) return;
        await LinesAtAsync(op, 24, 3, 2, 0, 0, "Right");
        if (!await SetAsync(op, "#fb-tram-off", "2", "Offset")) return;
        await LinesAtAsync(op, 24, 3, 2, 2, 0, "Right, 2 m offset");
        if (!await PickAsync(op, "#fb-tramedit [data-tmode='1']", "Edge")) return;
        await LinesAtAsync(op, 24, 3, 2, 2, 1, "Right, 2 m offset, Edge");
        bool sel = await op.PageAsync<bool>("() => document.querySelector(\"#fb-tramedit [data-tmode='1']\").classList.contains('sel') && document.querySelector(\"#fb-tramedit [data-tdir='2']\").classList.contains('sel')");
        if (sel) op.S.Ok("Edge and Right show as picked"); else op.S.Problem(Severity.Inconsistency, "TRAM-EDITOR-STALE", "the editor doesn't show Edge and Right as picked");

        // Back to the plain set-up for the next scenarios.
        await PickAsync(op, "#fb-tramedit [data-tmode='0']", "Wheel Track");
        await PickAsync(op, "#fb-tramedit [data-tdir='0']", "Both");
        await SetAsync(op, "#fb-tram-off", "0", "Offset");
        await SetAsync(op, "#fb-tram-passes", "1", "Passes");
        await LinesAtAsync(op, 24, 1, 0, 0, 0, "back to Both, 0 m, Wheel Track, 1 pass");
    }

    private static async Task CarelessAsync(FlowOp op)
    {
        if (await SkippedAsync(op)) return;
        async Task Try(string box, string value, string what, Func<Models.Tram.TramSystem, string> hostValue, string code)
        {
            await op.ToldAsync(); op.HostSaid();
            var s0 = (await HostSystemAsync(op))!;
            string before = hostValue(s0);
            if (!await SetAsync(op, box, value, what)) return;
            await Task.Delay(500);
            var told = await op.ToldAsync();
            var host = op.HostSaid();
            var s1 = (await HostSystemAsync(op))!;
            string after = hostValue(s1);
            string shown = await op.PageAsync<string>($"() => document.querySelector('{box}').value");
            string said = TrackFlows.Said(told, host);
            op.S.Step($"{what} '{value}': host {before} → {after}, the box shows '{shown}', told {said}");
            bool kept = after == before;
            bool told1 = said != "nothing";
            // One code per cause, so the report doesn't count one fault four times: the editor's box
            // keeps what was typed when the system did not take it; a value dropped or changed without a word.
            if (kept && shown == value) op.S.Problem(Severity.Inconsistency, "TRAM-BOX-SHOWS-UNUSED-VALUE", $"{what} '{value}' {(told1 ? $"refused ({said})" : "not taken")}, but the box still shows '{shown}' while the system keeps {after}");
            else if (kept && told1) op.S.Ok($"{what} '{value}' refused and said ({said}); the box shows the value in use ({shown})");
            if (kept && !told1) op.S.Problem(Severity.Improvement, "TRAM-VALUE-DROPPED-SILENTLY", $"{what} '{value}': not taken and nothing was said", FindingKind.Enhancement);
            // A value out of range taken as another one (-3 passes as 'all', which reaches the
            // module's tram bits) is the app doing what wasn't asked: a defect, not a nicety.
            else if (!kept && !told1 && value.StartsWith('-')) op.S.Problem(Severity.Bug, code + "-CHANGED-SILENTLY", $"{what} '{value}' typed: the system now has {after} and nothing was said (1500 is refused: '1 to 999')", FindingKind.AppDefect);
            else if (!kept && !told1) op.S.Problem(Severity.Improvement, code + "-CHANGED-SILENTLY", $"{what} '{value}' typed: the system now has {after} and nothing was said", FindingKind.Enhancement);
            else if (!kept) op.S.Note($"{what} '{value}': the system now has {after}; told {said}");
        }
        string Inv(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Try("#fb-tram-w", "0", "Tram width", s => Inv(s.TramWidth) + " m", "TRAM-WIDTH-ZERO");
        await Try("#fb-tram-w", "150", "Tram width", s => Inv(s.TramWidth) + " m", "TRAM-WIDTH-150");
        await Try("#fb-tram-passes", "1500", "Passes", s => s.PassCount + " pass(es)", "TRAM-PASSES-1500");
        await Try("#fb-tram-passes", "-3", "Passes", s => s.PassCount == 0 ? "all passes (0)" : s.PassCount + " pass(es)", "TRAM-PASSES-NEGATIVE");
        // Whatever the careless values left, back to 24 m and 1 pass.
        await op.CloseAllAsync();
        await SetAsync(op, "#fb-tram-w", "24", "Tram width");
        await SetAsync(op, "#fb-tram-passes", "1", "Passes");
    }

    // ------------------------------------------------------------------ R4, R5

    private static async Task BoundaryRefAsync(FlowOp op)
    {
        if (await SkippedAsync(op)) return;
        if (!await OpenEditorAsync(op)) return;
        if (!await op.SelectAsync("#fb-tram-ref", "(Boundary)", "Reference")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.TramSystems[0].ReferenceBoundaryIndex >= 0, 2000))
        { op.S.Problem(Severity.Bug, "TRAM-REF-NOT-SAVED", "Reference (Boundary): the host still follows the line"); return; }
        var s = (await HostSystemAsync(op))!;
        op.S.Ok($"the system follows the boundary: '{s.Name}'");
        // The Scene of the boundary system: until it comes, the editor and the map still have the line's.
        bool shown = await op.SeeAsync("scene && scene.tramSystems && scene.tramSystems[0] && scene.tramSystems[0].isBoundary", 3000);
        if (!shown) op.S.Step("the screen's tram system still follows the line");
        await Task.Delay(200);
        bool hidden = await op.PageAsync<bool>("() => document.getElementById('fb-tram-offrow').hidden && document.getElementById('fb-tram-dir').hidden");
        string lbl = await op.PageAsync<string>("() => document.getElementById('fb-tram-passes-lbl').textContent");
        if (hidden && lbl == "Passes") op.S.Ok("offset and direction are hidden, the passes box says 'Passes' (no 0 = all)");
        else op.S.Problem(Severity.Inconsistency, "TRAM-EDITOR-STALE", $"a boundary system: offset/direction hidden {hidden}, passes label '{lbl}'");
        if (shown) await BoundaryLinesAtAsync(op, s);
        await op.Run.ShotAsync(op.S, op.Screen, "R4-boundary-tram.png");

        // Careless: 0 passes on a boundary system means one lap (#112).
        await op.ToldAsync(); op.HostSaid();
        if (!await SetAsync(op, "#fb-tram-passes", "0", "Passes")) return;
        var s1 = (await HostSystemAsync(op))!;
        string box = await op.PageAsync<string>("() => document.getElementById('fb-tram-passes').value");
        if (s1.PassCount >= 1) op.S.Ok($"0 passes on a boundary system: {s1.PassCount} lap(s) kept (#112)");
        else op.S.Problem(Severity.Bug, "TRAM-BOUNDARY-ZERO-PASSES", $"0 passes on a boundary system: the host has {s1.PassCount}");
        if (box != "0") op.S.Ok("the box doesn't keep the 0");
        else op.S.Problem(Severity.Inconsistency, "TRAM-BOX-SHOWS-UNUSED-VALUE", $"Passes '0' on a boundary system: the box still shows '0' while the system keeps {s1.PassCount} lap(s)");

        // Back to the line.
        if (!await OpenEditorAsync(op)) return;
        if (!await op.SelectAsync("#fb-tram-ref", Line, "Reference")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.TramSystems[0].ReferenceTrackName == Line, 2000))
        {
            // The boundary's lines measured from the line would be a second finding for this one;
            // R6 and R7 skip on a system left on the boundary.
            op.S.Problem(Severity.Bug, "TRAM-REF-NOT-SAVED", TrackFlows.Masked($"Reference '{Line}': the host follows '{(await HostSystemAsync(op))?.ReferenceTrackName ?? "(Boundary)"}'"));
            return;
        }
        op.S.Ok(TrackFlows.Masked($"Reference '{Line}': the system follows the line again"));
        var s2 = (await HostSystemAsync(op))!;
        await LinesAtAsync(op, s2.TramWidth, s2.PassCount, (int)s2.Direction, s2.Offset, (int)s2.Mode, "back on the line");
    }

    /// <summary>A boundary system's laps on the screen are where its settings put them, measured
    /// in from the fence. In Wheel Track the lap is driven on the fence: its outer wheel track is the
    /// fence itself, its inner one half the wheel track in. In Edge the lap's middle is half the
    /// tram width in, its wheel tracks half the wheel track either side. Each further lap is a tram
    /// width further in.</summary>
    private static async Task BoundaryLinesAtAsync(FlowOp op, Models.Tram.TramSystem sys)
    {
        var ring = await op.Rig.ReadAsync(() => op.Rig.Vm.OuterBoundaryPoints()?.ToList());
        if (ring == null) { op.S.Harness("the tram field has no boundary"); return; }
        double hw = await op.Rig.ReadAsync(() => op.Rig.Store.Vehicle.TrackWidth / 2);
        bool edge = sys.Mode == Models.Tram.TramSystemMode.Edge;
        int laps = Math.Max(1, sys.PassCount);
        var want = new List<double>();
        for (int p = 0; p < laps; p++)
        {
            double mid = edge ? sys.TramWidth * (0.5 + p) : sys.TramWidth * p;
            want.Add(mid - hw > 0.1 ? mid - hw : 0); // a wheel track outside the fence runs on it
            want.Add(mid + hw);
        }
        want.Sort();
        static bool Near(List<double> a, List<double> b) => a.Count == b.Count && a.Zip(b).All(x => Math.Abs(x.First - x.Second) <= 0.1);
        var got = new List<double>();
        // The scene follows the edit within a frame or two.
        for (int k = 0; k < 15; k++)
        {
            got = (await ScreenLinesAsync(op)).Select(l => l.Average(p => InFromRing(p.E, p.N, ring))).OrderBy(x => x).ToList();
            if (Near(got, want)) break;
            await Task.Delay(200);
        }
        string how = FlowOp.F($"{(edge ? "Edge" : "Wheel Track")}, {laps} lap(s)");
        op.S.Step($"boundary tram lines on the screen: {Show(got)} m in from the fence (minus: outside it); the settings put them at {Show(want)} m");
        if (got.Count == 0) op.S.Problem(Severity.Bug, "TRAM-BOUNDARY-NO-LINES", "a boundary tram system: no lines on the screen");
        else if (Near(got, want)) op.S.Ok($"a boundary system, {how}: the screen draws its {want.Count} wheel tracks at {Show(want)} m in from the fence");
        else op.S.Problem(Severity.Bug, "TRAM-BOUNDARY-LINES-MISPLACED", $"a boundary system, {how}: the screen's tram lines are not where the settings put them ({Show(want)} m in from the fence)");
    }

    private static double DistanceToRing(double e, double n, List<(double E, double N)> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double dx = b.E - a.E, dy = b.N - a.N, l2 = dx * dx + dy * dy;
            double u = l2 > 0 ? Math.Clamp(((e - a.E) * dx + (n - a.N) * dy) / l2, 0, 1) : 0;
            best = Math.Min(best, Math.Sqrt(Math.Pow(a.E + u * dx - e, 2) + Math.Pow(a.N + u * dy - n, 2)));
        }
        return best;
    }

    /// <summary>How far in from the fence a point is, negative outside the field: a lap drawn
    /// outside the fence must not pass for one the same distance inside.</summary>
    private static double InFromRing(double e, double n, List<(double E, double N)> ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if (Models.Base.GeometryMath.CrossesSouth(ring[i].E, ring[i].N, ring[j].E, ring[j].N, e, n)) inside = !inside;
        double d = DistanceToRing(e, n, ring);
        return inside ? d : -d;
    }

    private static async Task OnOffAsync(FlowOp op)
    {
        if (await SkippedAsync(op)) return;
        if (!await PickAsync(op, "#fb-tram-en", "Enabled")) return;
        bool off = await op.HostAsync(() => !op.Rig.Vm.TramSystems[0].IsEnabled, 2000);
        bool gone = await op.SeeAsync("!(scene.tramLines || []).length", 2000);
        string txt = await op.PageAsync<string>("() => document.getElementById('fb-tram-en').textContent");
        if (off && gone) op.S.Ok($"Enabled Off: no tram lines; the button says '{txt}'");
        else op.S.Problem(Severity.Bug, "TRAM-OFF-NOT-APPLIED", $"Enabled Off: host enabled {!off}, screen lines {(gone ? 0 : (await ScreenLinesAsync(op)).Count)}");
        if (!await PickAsync(op, "#fb-tram-en", "Enabled")) return;
        if (await op.HostAsync(() => op.Rig.Vm.TramSystems[0].IsEnabled, 2000) && await op.SeeAsync("(scene.tramLines || []).length > 0", 2000)) op.S.Ok("Enabled On: the lines are back");
        else op.S.Problem(Severity.Bug, "TRAM-ON-NOT-APPLIED", "Enabled On: no tram lines");

        // The bottom bar's tram button: the lines drawn with the Field Builder closed.
        await op.CloseAllAsync();
        int m0 = await op.PageAsync<int>("() => (tick && tick.tools && tick.tools.tramMode) || 0");
        var seen = new List<int> { m0 };
        for (int k = 0; k < 4; k++)
        {
            if (!await op.TapAsync("#bn-tram", "the bottom bar's Tram button")) return;
            await Task.Delay(400);
            int m = await op.PageAsync<int>("() => (tick && tick.tools && tick.tools.tramMode) || 0");
            seen.Add(m);
        }
        op.S.Step($"tram display modes seen: {string.Join(" → ", seen)} (0 off, 1 all, 2 lines only, 3 boundary only)");
        // With only parallel lines (no boundary tram) the button toggles Off and Lines only, as AgOpenGPS.
        if (seen.Skip(1).Any(m => m > 0) && seen.Skip(1).Any(m => m == 0)) op.S.Ok("the Tram button switches the lines on the map off and on again");
        else op.S.Problem(Severity.Bug, "TRAM-BUTTON-DEAD", $"the Tram button: display modes {string.Join(" → ", seen)}");
    }

    // ------------------------------------------------------------------ R6, R7, R8

    private static async Task RenameRefAsync(FlowOp op)
    {
        if (await SkippedAsync(op, plain: true)) return;
        const string to = "Tram line, east";
        await op.ToldAsync(); op.HostSaid();
        int n0 = op.S.Row.Findings.Count;
        if (await TrackFlows.RenameAsync(op, Line, to, "Enter") != to)
        {
            // The rename itself is T9's; without it there is nothing for the system to follow. A row
            // or button the rename couldn't reach is already the helper's finding.
            if (op.S.Row.Findings.Count == n0)
                op.S.Problem(Severity.Bug, "RENAME-NOT-SAVED", TrackFlows.Masked($"'{Line}' renamed '{to}' with Enter (twice): the host has {string.Join(", ", (await op.TracksAsync()).Select(x => $"'{x.Name}'"))}"));
            return;
        }
        var s = (await HostSystemAsync(op))!;
        var said = TrackFlows.Said(await op.ToldAsync(), op.HostSaid());
        bool follows = s.ReferenceTrackName == to && s.Name.Contains(to, StringComparison.Ordinal);
        if (follows) op.S.Ok($"the system follows the renamed line: '{s.Name}'; told {said}");
        else op.S.Problem(Severity.Bug, "TRAM-LOST-ITS-LINE", TrackFlows.Masked($"the reference line renamed '{to}': the system '{s.Name}' follows '{s.ReferenceTrackName}'"));
        if (follows)
        {
            // The Scene that carries the rename: until it comes, the screen still has the lines of
            // before, and a check on them proves nothing.
            if (!await op.SeeAsync($"scene && scene.tramSystems && scene.tramSystems[0] && scene.tramSystems[0].refLabel === {JsonSerializer.Serialize(to)}", 3000))
                op.S.Problem(Severity.Inconsistency, "TRAM-LIST-STALE", $"the reference line renamed '{to}': the screen's tram system still follows the old name");
            else if (!await op.SeeAsync("(scene.tramLines || []).length > 0", 2000))
                op.S.Problem(Severity.Bug, "TRAM-LINES-GONE-ON-RENAME", "after the rename the screen has no tram lines");
            else await LinesAtAsync(op, s.TramWidth, s.PassCount, (int)s.Direction, s.Offset, (int)s.Mode, "after the rename", to);
        }
        // Name it back so the other scenarios find it; if that doesn't take, they go on with the new name.
        n0 = op.S.Row.Findings.Count;
        if (await TrackFlows.RenameAsync(op, to, Line, "Enter") != Line)
        {
            if (op.S.Row.Findings.Count == n0)
                op.S.Problem(Severity.Bug, "RENAME-NOT-SAVED", TrackFlows.Masked($"'{to}' renamed back '{Line}' with Enter (twice): the host has {string.Join(", ", (await op.TracksAsync()).Select(x => $"'{x.Name}'"))}"));
            if (await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Any(t => t.Name == to))) Line = to;
        }
        await op.CloseDialogAsync();
    }

    private static string Key(Models.Tram.TramSystem s) => FlowOp.F($"{s.Name}|{s.ReferenceTrackName}|{s.ReferenceBoundaryIndex}|{s.TramWidth:0.00}|{s.PassCount}|{s.Direction}|{s.Mode}|{s.Offset:0.00}|{s.IsEnabled}");

    private static async Task ReopenAsync(FlowOp op)
    {
        // Something worth keeping: 2 passes, Left.
        if (await SkippedAsync(op, plain: true, lineBy: "R4, R6")) return;
        await SetAsync(op, "#fb-tram-passes", "2", "Passes");
        await PickAsync(op, "#fb-tramedit [data-tdir='1']", "Left");
        await op.CloseAllAsync();
        if (!await op.HostAsync(() => op.Rig.Vm.TramSystems.Count > 0 && op.Rig.Vm.TramSystems[0].PassCount == 2 && op.Rig.Vm.TramSystems[0].Direction == Models.Tram.TramDirection.Left, 2000))
            op.S.Step("the host doesn't have 2 passes, Left: the system is kept as it is");
        var before = await op.Rig.ReadAsync(() => op.Rig.Vm.TramSystems.Select(Key).ToList());
        // The lines of the system as it is, on the screen: this waits for the Scene that has them.
        var s0 = (await HostSystemAsync(op))!;
        bool at0 = await LinesAtAsync(op, s0.TramWidth, s0.PassCount, (int)s0.Direction, s0.Offset, (int)s0.Mode, "before closing the field");
        // The line as it is now, kept: the lines after opening again are measured from it, so lines
        // that come back moved, or on its other side (its A and B read the other way round), don't
        // pass for the same lines because there are as many.
        string name = Line;
        var line0 = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.FirstOrDefault(x => x.Name == name) is { } t ? t.WithPoints(t.Points.ToList()) : null);
        var screen0 = await ScreenLinesAsync(op);
        int lines0 = screen0.Count;
        var offs0 = line0 == null ? null : Offsets(screen0, line0, out _);
        op.S.Step($"before: {string.Join("; ", before)}; {lines0} lines");
        if (!await FlowFields.CloseFieldAsync(op, "discard")) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close did not close the field"); return; }
        bool cleared = await op.SeeAsync("!(scene.tramSystems || []).length && !(scene.tramLines || []).length", 3000);
        if (cleared) op.S.Ok("with the field closed, no tram systems or lines on the screen");
        else op.S.Problem(Severity.Inconsistency, "TRAM-LEFT-AFTER-CLOSE", "the field is closed but the screen still has tram systems or lines");
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        // The field's tram systems are read as it opens; their lines reach the screen with the next Scene.
        await op.HostAsync(() => op.Rig.Vm.TramSystems.Count == before.Count, 3000);
        var after = await op.Rig.ReadAsync(() => op.Rig.Vm.TramSystems.Select(Key).ToList());
        op.S.Step($"after: {string.Join("; ", after)}");
        bool same = after.SequenceEqual(before);
        if (same) op.S.Ok("the tram system came back the same");
        else op.S.Problem(Severity.Bug, "TRAM-CHANGED-ON-REOPEN", TrackFlows.Masked($"before {string.Join("; ", before)}, after {string.Join("; ", after)}"));
        // A system that came back changed (the finding above), or lines that were wrong before
        // closing (a finding further up), say nothing more about the lines after the reopening.
        if (!same) { op.S.Note("the lines after opening the field again are not compared: the tram system came back changed"); return; }
        if (!at0 || offs0 == null || lines0 == 0) { op.S.Note("the lines after opening the field again are not compared: they were not right before closing it"); return; }
        static bool Matches(List<double>? a, List<double> b) => a != null && a.Count == b.Count && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 0.06);
        var screen1 = new List<List<(double E, double N)>>();
        List<double>? offs1 = null; string why = "";
        // The lines reach the screen with the Scene after the systems: wait for as many where they
        // were (the offsets are distinct, so a line drawn twice shows only in the count).
        for (int k = 0; k < 15; k++)
        {
            screen1 = await ScreenLinesAsync(op);
            offs1 = Offsets(screen1, line0!, out why);
            if (screen1.Count == lines0 && Matches(offs1, offs0)) break;
            await Task.Delay(200);
        }
        if (screen1.Count == lines0 && Matches(offs1, offs0)) { op.S.Ok($"and its {lines0} lines, where they were"); return; }
        string at1 = screen1.Count == 0 ? "no tram lines" : offs1 == null ? why : $"{screen1.Count} lines, wheel tracks at {Show(offs1)} m";
        op.S.Step($"after opening again, from the line as it was (east +): {at1}; before: {lines0} lines at {Show(offs0)} m");
        op.S.Problem(Severity.Bug, "TRAM-LINES-CHANGED-ON-REOPEN", screen1.Count == lines0
            ? $"its {lines0} tram lines are not where they were before the field was closed and opened again"
            : $"{lines0} tram lines before, {screen1.Count} after opening the field again");
    }

    private static async Task DeleteAsync(FlowOp op)
    {
        if (await SkippedAsync(op, see: "R1, R7")) return;
        if (!await OpenTramTabAsync(op)) return;
        if (!await op.TapAsync("#fb-tramlist .fb-hlrow:nth-of-type(1)", "the system's row")) return;
        if (!await op.TapAsync("#fb-tram-delete", "Delete")) return;
        if (!await op.ConfirmAsync(false, "Delete Tram System")) return;
        await Task.Delay(400);
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.TramSystems.Count) == 1) op.S.Ok("Delete, Cancel: kept");
        else { op.S.Problem(Severity.Bug, "TRAM-DELETE-CANCEL-DELETED", "Delete then Cancel deleted the tram system"); return; }
        if (!await OpenTramTabAsync(op)) return;
        if (!await op.TapAsync("#fb-tramlist .fb-hlrow:nth-of-type(1)", "the system's row")) return;
        if (!await op.TapAsync("#fb-tram-delete", "Delete")) return;
        if (!await op.ConfirmAsync(true, "Delete Tram System")) return;
        if (await op.HostAsync(() => op.Rig.Vm.TramSystems.Count == 0, 2000) && await op.SeeAsync("!(scene.tramLines || []).length && !(scene.tramSystems || []).length", 2000))
            op.S.Ok("Delete, Confirm: no tram system and no lines");
        else op.S.Problem(Severity.Bug, "TRAM-NOT-DELETED", "Delete, Confirm: a tram system or its lines are left");
        bool file = File.Exists(Path.Combine(op.FieldDir(Field), "TramSystems.json"));
        if (!file) op.S.Ok("TramSystems.json removed from the field");
        else op.S.Problem(Severity.Bug, "TRAM-FILE-LEFT", "TramSystems.json is still in the field after the last system was deleted");
        string emptyMsg = await op.PageAsync<string>("() => document.getElementById('fb-tramlist').textContent");
        op.S.Step($"the Tram tab says '{emptyMsg.Trim()}'");
    }
}
