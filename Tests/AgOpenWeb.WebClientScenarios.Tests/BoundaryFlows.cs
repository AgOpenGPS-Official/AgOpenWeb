// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// B: the boundary and the headland, made the operator's ways: a new field at the tractor, the
/// boundary recorded by driving round it, deleted, drawn on the map, imported from a KML file;
/// then the headland round the whole boundary, its width changed, a headland line on one side,
/// the headland switched off and on, and both read back after the field is closed and opened.
/// The careless operator asks for an obstacle before there is a boundary (#621), types a headland
/// wider than half the field, or a width of 0.
/// </summary>
public static class BoundaryFlows
{
    public const string Group = "B: boundary and headland";
    private const int Slot = 1;
    private const string Field = "Flows Boundary";

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "B1", "A new field, and an obstacle before the boundary", "Careless operator: Field Operations › Fields and Jobs › New Field at the tractor; then Field Tools › Boundary › drive around an obstacle, with no boundary yet (#621).", NewFieldObstacleFirstAsync, fresh: true);
        await Flow(run, "B2", "The boundary recorded by driving", "Careful operator: Boundary › Drive around field, Record, a 60 × 40 m loop at 10 km/h, Stop and save.", RecordAsync);
        await Flow(run, "B3", "The boundary deleted, then drawn on the map", "Boundary › the Outer row, Delete, Confirm; then Draw on map: five taps on the map and Finish.", DeleteAndDrawAsync);
        await Flow(run, "B4", "The boundary imported from a KML file", "Boundary › Import KML: the file copied into the Import folder, Confirm (it replaces the outer boundary).", ImportKmlAsync);
        await Flow(run, "B5", "The headland round the whole boundary", "Careful operator: Field Tools › Field Builder › Headland › Add › 2 tool widths › Whole Boundary; then its width changed to 24 m.", WholeHeadlandAsync);
        await Flow(run, "B6", "A headland line on one side", "Field Builder › Headland: Delete All, Confirm; Add › Line, two taps on the west side's fence.", HeadlandLineAsync);
        await Flow(run, "B7", "Headland and section control in the headland, on and off", "Bottom bar: Headland off and on; Section control in headland off and on.", HeadlandButtonsAsync);
        await Flow(run, "B8", "A headland typed carelessly", "Careless operator: Whole Boundary with 0 typed as the width; then with 150 m typed in a 240 m wide field.", CarelessHeadlandAsync);
        await Flow(run, "B9", "Boundary and headland after the field is closed and opened", "Field Operations › Close, then Open Field Only: the same boundary and headland.", ReopenAsync);
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body, bool fresh = false)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            if (!fresh && !await FlowFields.EnsureOpenAsync(op, Slot, Field)) { s.Harness("the boundary field could not be opened"); return; }
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
        }, onTheRunsLine: false);

    private static string Said(List<string> page, List<string> host)
    {
        var all = page.Concat(host.Where(h => h.StartsWith("Failure", StringComparison.Ordinal) || h.StartsWith("Exception", StringComparison.Ordinal))).ToList();
        return all.Count == 0 ? "nothing" : string.Join(" / ", all.Take(4).Select(x => "'" + x + "'"));
    }

    private static Task<List<(double E, double N)>?> RingAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.Vm.OuterBoundaryPoints()?.ToList());

    private static double Area(List<(double E, double N)> r)
    {
        double a = 0;
        for (int i = 0; i < r.Count; i++) { var p = r[i]; var q = r[(i + 1) % r.Count]; a += p.E * q.N - q.E * p.N; }
        return Math.Abs(a) / 2;
    }

    private static double ToRing(double e, double n, List<(double E, double N)> ring)
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

    private static async Task<bool> OpenBoundaryMenuAsync(FlowOp op)
    {
        if (!await op.OpenMenuAsync("ln-fieldtools", "fieldtools", "Field Tools")) return false;
        if (!await op.TapAsync("#ft-boundary", "Boundary")) return false;
        if (await op.WaitOpenAsync("boundarymenu")) return true;
        op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Field Tools › Boundary did not open");
        return false;
    }

    private static async Task<bool> OpenHeadlandTabAsync(FlowOp op)
    {
        if (!await op.OpenMenuAsync("ln-fieldtools", "fieldtools", "Field Tools")) return false;
        if (!await op.TapAsync("#ft-fieldbuilder", "Field Builder")) return false;
        if (!await op.WaitOpenAsync("fieldbuilder")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Field Builder did not open"); return false; }
        return await op.TapAsync("#fieldbuilder .fb-tab[data-fbtab=headland]", "the Headland tab");
    }

    /// <summary>The headland's line: every point of it is about <paramref name="offset"/> m inside the boundary.</summary>
    private static async Task HeadlandAtAsync(FlowOp op, double offset, string what)
    {
        var ring = await RingAsync(op);
        var hl = await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentHeadlandLineForPreview?.Select(p => (p.Easting, p.Northing)).ToList());
        if (ring == null || hl == null || hl.Count < 3) { op.S.Problem(Severity.Bug, "HEADLAND-NOT-BUILT", $"{what}: no headland line on the host"); return; }
        var d = hl.Select(p => ToRing(p.Easting, p.Northing, ring)).ToList();
        double lo = d.Min(), hi = d.Max();
        // The corners of an inset polygon are √2 × the width from the corner, but their distance to the nearest side is the width.
        if (lo >= offset - 0.6 && hi <= offset + 0.6) op.S.Ok(FlowOp.F($"{what}: the headland line is {lo:0.0}-{hi:0.0} m inside the boundary ({hl.Count} points)"));
        else op.S.Problem(Severity.Bug, "HEADLAND-WIDTH-WRONG", FlowOp.F($"{what}: the headland line is {lo:0.0}-{hi:0.0} m inside the boundary, {offset:0.0} m asked"));
        // The screen draws the host's line, not an older one: after a width change the old line
        // stays in the page's scene until a new Scene frame comes.
        int m = hl.Count / 2;
        string same = FlowOp.F($"scene && scene.headland && scene.headland.length === {hl.Count}")
            + FlowOp.F($" && Math.abs(scene.headland[0].e - ({hl[0].Easting})) < 0.05 && Math.abs(scene.headland[0].n - ({hl[0].Northing})) < 0.05")
            + FlowOp.F($" && Math.abs(scene.headland[{m}].e - ({hl[m].Easting})) < 0.05 && Math.abs(scene.headland[{m}].n - ({hl[m].Northing})) < 0.05");
        if (await op.SeeAsync(same, 2000)) op.S.Ok("the screen draws the host's headland line");
        else
        {
            int shown = await op.PageAsync<int>("() => scene && scene.headland ? scene.headland.length : 0");
            op.S.Step($"the screen's scene has a headland of {shown} points, the host's {hl.Count}");
            op.S.Problem(Severity.Inconsistency, "HEADLAND-NOT-SHOWN", $"{what}: the screen does not draw the host's headland line");
        }
    }

    /// <summary>Field Builder › Headland › Delete All, Confirm: true when the host has no headland left.</summary>
    private static async Task<bool> DeleteAllAsync(FlowOp op)
    {
        if (!await OpenHeadlandTabAsync(op)) return false;
        if (!await op.TapAsync("#fb-hl-deleteall", "Delete All")) return false;
        if (!await op.ConfirmAsync(true, "Delete all headland")) return false;
        if (await op.HostAsync(() => op.Rig.Vm.HeadlandSegments.Count == 0 && !op.Rig.Vm.HasHeadland, 3000)) return true;
        op.S.Step(FlowOp.F($"Delete All: {await op.Rig.ReadAsync(() => op.Rig.Vm.HeadlandSegments.Count)} segment(s) left, headland {await op.Rig.ReadAsync(() => op.Rig.Vm.HasHeadland)}"));
        op.S.Problem(Severity.Bug, "HEADLAND-NOT-DELETED", "Delete All, Confirm: the headland is still there");
        return false;
    }

    /// <summary>The headland back round the whole boundary at 2 tool widths (16 m), for the scenarios after.
    /// A step that fails has said so itself; a host that builds no headland after the taps went through is a finding.</summary>
    private static async Task<bool> RestoreWholeAsync(FlowOp op)
    {
        // Without a boundary the host refuses Whole Boundary, as it should.
        if (await RingAsync(op) == null) { op.S.Note("no boundary to put the headland round for the next scenarios"); return false; }
        bool tapped = await DeleteAllAsync(op) && await OpenHeadlandTabAsync(op);
        // What the page and the host say from here on is about Whole Boundary.
        if (tapped) { await op.ToldAsync(); op.HostSaid(); }
        if (!tapped || !await AddWholeAsync(op, "", "2 tool widths"))
        {
            op.S.Note("the headland round the whole boundary could not be put back for the next scenarios");
            return false;
        }
        if (await op.HostAsync(() => op.Rig.Vm.HasHeadland && op.Rig.Vm.HeadlandSegments.Count == 1, 3000))
        {
            op.S.Step("the headland is round the whole boundary again, at 2 tool widths");
            return true;
        }
        var r = await op.Rig.ReadAsync(() => (op.Rig.Vm.HeadlandSegments.Count, op.Rig.Vm.HasHeadland));
        var host = op.HostSaid();
        op.S.Step($"Whole Boundary at 2 tool widths: {r.Count} segment(s), headland {r.HasHeadland}; told {Said(await op.ToldAsync(), host)}");
        // HostSaid has reported an exception as HOST-EXCEPTION; no headland after it is that fault.
        if (host.Any(h => h.StartsWith("Exception", StringComparison.Ordinal))) { op.S.Note("Whole Boundary at 2 tool widths: no headland after the host's exception (HOST-EXCEPTION)"); return false; }
        op.S.Problem(Severity.Bug, "HEADLAND-NOT-BUILT", "Whole Boundary at 2 tool widths, after Delete All: no headland");
        return false;
    }

    // ------------------------------------------------------------------ B1

    private static async Task NewFieldObstacleFirstAsync(FlowOp op)
    {
        // The tractor at the new field's place; whatever field is open closes for the new one.
        await op.PlaceAsync(FlowFields.At(Slot, 30, 10), 0);
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-fields", "Fields and Jobs")) return;
        if (!await op.WaitOpenAsync("fieldsandjobs")) { op.S.Harness("Fields and Jobs did not open"); return; }
        if (!await op.TapAsync("#fj-newfield", "New Field")) return;
        if (!await op.WaitOpenAsync("newfield")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "New Field did not open"); return; }
        string pos = await op.PageAsync<string>("() => document.getElementById('nf-pos').textContent");
        op.S.Step($"New Field shows the position '{pos}'");
        if (!await op.TypeAsync("#nf-name", Field, "the field's name")) return;
        if (!await op.TapAsync("#nf-create", "Create Field")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == Field && !op.Rig.State.UI.IsBusy, 15000))
        {
            op.S.Problem(Severity.Bug, "FLOW-FIELD-NOT-OPENED", $"New Field '{Field}' is not open (open: '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}')");
            return;
        }
        op.S.Ok($"New Field: '{Field}' made and open");
        bool listed = await op.SeeAsync($"fieldOps && fieldOps.fields.some(f => f.name === '{Field}')", 4000);
        if (listed) op.S.Ok("it is in the Fields and Jobs list");
        else op.S.Problem(Severity.Inconsistency, "FIELD-LIST-STALE", $"'{Field}' is not in the Fields and Jobs list after New Field");
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");

        // An obstacle with no boundary yet: refused and said, and the recorder stays closed (#621).
        if (!await OpenBoundaryMenuAsync(op)) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await op.TapAsync("#bm-driveinner", "Drive around obstacle")) return;
        await Task.Delay(700);
        var told = await op.ToldAsync();
        bool recorder = await op.IsOpenAsync("boundaryplayer");
        bool recording = await op.Rig.ReadAsync(() => op.Rig.Vm.IsBoundaryRecording || op.Rig.Vm.IsBoundaryPlayerPanelVisible);
        if (!recorder && !recording && told.Count > 0) op.S.Ok($"an obstacle before the boundary is refused: {Said(told, op.HostSaid())}");
        else op.S.Problem(Severity.Bug, "OBSTACLE-WITHOUT-BOUNDARY", $"an obstacle before the boundary: recorder open {recorder}, host recording {recording}, told {Said(told, op.HostSaid())}");
    }

    // ------------------------------------------------------------------ B2

    private static async Task RecordAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 30, 10), 0);
        if (!await OpenBoundaryMenuAsync(op)) return;
        if (!await op.TapAsync("#bm-drivearound", "Drive around field")) return;
        if (!await op.WaitOpenAsync("boundaryplayer")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "the boundary recorder did not open"); return; }
        string offset = await op.PageAsync<string>("() => document.getElementById('bp-offset').value");
        op.S.Step($"the recorder's offset box says {offset} cm");
        if (!await op.TapAsync("#bp-record", "Record")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.IsBoundaryRecording, 2000)) { op.S.Problem(Severity.Bug, "BOUNDARY-NOT-RECORDING", "Record: the host is not recording"); return; }
        // A 60 × 40 m loop to the right at 10 km/h, turning at 6 km/h (small, to keep the run short).
        await op.DriveAsync(60, 10);
        await op.TurnAsync(90, 6, 25);
        await op.DriveAsync(40, 10);
        string midPts = await op.PageAsync<string>("() => document.getElementById('bp-points').textContent");
        string midArea = await op.PageAsync<string>("() => document.getElementById('bp-area').textContent");
        op.S.Step($"half way the recorder shows {midPts} points, {midArea}");
        await op.TurnAsync(90, 6, 25);
        await op.DriveAsync(60, 10);
        await op.TurnAsync(90, 6, 25);
        await op.DriveAsync(36, 10);
        await op.StopAsync();
        string pts = await op.PageAsync<string>("() => document.getElementById('bp-points').textContent");
        string area = await op.PageAsync<string>("() => document.getElementById('bp-area').textContent");
        int hostPts = await op.Rig.ReadAsync(() => op.Rig.Vm.BoundaryPointCount);
        op.S.Step($"the recorder shows {pts} points and {area}; the host has {hostPts} points");
        if (int.TryParse(pts, out var shown) && shown > 10 && Math.Abs(shown - hostPts) <= 5) op.S.Ok("the recorder's point count follows the host's");
        else op.S.Problem(Severity.Inconsistency, "RECORDER-COUNT-STALE", "after the loop the recorder's point count is not the host's (10 or fewer, or more than 5 apart)");
        if (!await op.TapAsync("#bp-stop", "Stop and save")) return;
        await op.HostAsync(() => !op.Rig.Vm.IsBoundaryRecording, 3000);
        await Task.Delay(800);
        var ring = await RingAsync(op);
        if (ring == null || ring.Count < 3) { op.S.Problem(Severity.Bug, "BOUNDARY-NOT-SAVED", $"Stop and save: no outer boundary (told {Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        double ha = Area(ring) / 10000;
        op.S.Step(FlowOp.F($"the saved boundary: {ring.Count} points, {ha:0.000} ha"));
        // The straight legs are 60 × 40 m, but the simulator turns on a radius of about 4 m at 25°,
        // and each 90° turn adds that radius to both sides: about 69 × 49 m (0.33 ha) at most. The
        // heading runs on past 90° while the wheels come back straight, which bends the next leg in,
        // and the recording offset is inside the loop; how much depends on the host's timing
        // (0.25-0.26 ha seen). The window allows for all of it.
        if (ha > 0.20 && ha < 0.32) op.S.Ok("the boundary is saved, its area right for a 60 × 40 m loop");
        else op.S.Problem(Severity.Bug, "BOUNDARY-AREA-WRONG", "the recorded boundary's area is outside 0.20-0.32 ha for a 60 × 40 m loop");
        if (await op.WaitOpenAsync("boundarymenu", 2000) && await op.SeeAsync("[...document.querySelectorAll('#bm-list .bm-row .bm-name')].some(r => r.textContent === 'Outer')", 3000))
            op.S.Ok("the boundary menu lists the Outer boundary");
        else
        {
            // The rows carry the recorded area, which wobbles with the host's timing: a step, not the finding.
            var rows = await op.RowsAsync("#bm-list .bm-row");
            op.S.Step($"the boundary menu lists: {(rows.Count == 0 ? "nothing" : string.Join(" | ", rows))}");
            op.S.Problem(Severity.Inconsistency, "BOUNDARY-LIST-STALE", "after Stop and save the boundary menu does not list the Outer boundary");
        }
        if (await op.SeeAsync("scene && scene.boundaries && scene.boundaries.length > 0 && scene.boundaries[0].length > 2", 2000)) op.S.Ok("the screen draws the boundary");
        else op.S.Problem(Severity.Inconsistency, "BOUNDARY-NOT-SHOWN", "the screen's scene has no boundary");
        await op.Run.ShotAsync(op.S, op.Screen, "B2-recorded-boundary.png");
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
    }

    // ------------------------------------------------------------------ B3, B4

    private static async Task DeleteAndDrawAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 60, 40), 0);
        if (!await OpenBoundaryMenuAsync(op)) return;
        if (!await op.TapRowAsync("#bm-list .bm-row", "Outer", "the Outer row")) return;
        await Task.Delay(300);
        if (!await op.TapAsync("#bm-delete", "Delete")) return;
        if (!await op.ConfirmAsync(true, "Delete boundary")) return;
        if (await op.HostAsync(() => op.Rig.Vm.OuterBoundaryPoints() == null, 3000)) op.S.Ok("Delete, Confirm: the outer boundary is gone");
        else { op.S.Problem(Severity.Bug, "BOUNDARY-NOT-DELETED", "Delete, Confirm: the outer boundary is still there"); return; }

        if (!await OpenBoundaryMenuAsync(op)) return;
        if (!await op.TapAsync("#bm-drawmap", "Draw on map")) return;
        var me = await op.PoseAsync();
        var corners = new[] { (-50.0, -30.0), (60.0, -35.0), (75.0, 40.0), (10.0, 70.0), (-55.0, 45.0) };
        foreach (var (de, dn) in corners)
            if (!await op.TapMapAsync(me.E + de, me.N + dn, "a corner")) return;
        string hint = await op.PageAsync<string>("() => document.getElementById('maptap-hint').textContent");
        op.S.Step($"the hint says '{hint}'");
        if (!await op.TapAsync("#draw-finish", "Finish")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.OuterBoundaryPoints() != null, 4000)) { op.S.Problem(Severity.Bug, "BOUNDARY-NOT-SAVED", $"Draw on map: no boundary (told {Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        var ring = (await RingAsync(op))!;
        double worst = corners.Max(c => ring.Min(p => Math.Sqrt(Math.Pow(p.E - (me.E + c.Item1), 2) + Math.Pow(p.N - (me.N + c.Item2), 2))));
        double expect = Area(corners.Select(c => (me.E + c.Item1, me.N + c.Item2)).ToList());
        double got = Area(ring);
        if (worst < 0.5 && Math.Abs(got - expect) / expect < 0.02) op.S.Ok(FlowOp.F($"the boundary has a corner at each tap (at most {worst:0.00} m off), {got / 10000:0.00} ha"));
        else op.S.Problem(Severity.Bug, "DRAWN-BOUNDARY-OFF-TAPS", FlowOp.F($"Draw on map: a tap is {worst:0.0} m from the nearest boundary point; {got / 10000:0.00} ha for {expect / 10000:0.00} ha tapped"));
        await op.Run.ShotAsync(op.S, op.Screen, "B3-drawn-boundary.png");
    }

    private static async Task ImportKmlAsync(FlowOp op)
    {
        string file = await FlowFields.WriteKmlAsync(op.Rig, Slot, "Flows Boundary import");
        if (!await OpenBoundaryMenuAsync(op)) return;
        if (!await op.TapAsync("#bm-importkml", "Import KML")) return;
        if (!await op.WaitOpenAsync("kmlboundary")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Import KML did not open"); return; }
        if (!await op.SeeAsync($"[...document.querySelectorAll('#kb-list .fb-tname')].some(r => r.textContent === '{file}')", 8000))
        { op.S.Problem(Severity.Bug, "FLOW-IMPORT-FILE-NOT-LISTED", $"Import KML: '{file}' is not listed ({string.Join(" | ", await op.RowsAsync("#kb-list .fb-trkrow"))})"); return; }
        if (!await op.TapRowAsync("#kb-list .fb-trkrow", file, "the KML file")) return;
        if (!await op.ConfirmAsync(true, "Import KML Boundary")) return;
        await Task.Delay(1500);
        var ring = await RingAsync(op);
        double expect = 0.5 * (420 + 380) * 240; // the shape's area: 9.6 ha
        if (ring != null && Math.Abs(Area(ring) - expect) / expect < 0.01) op.S.Ok(FlowOp.F($"the KML's boundary replaced the drawn one: {Area(ring) / 10000:0.00} ha"));
        else op.S.Problem(Severity.Bug, "KML-BOUNDARY-NOT-IMPORTED", FlowOp.F($"Import KML: the boundary is {(ring == null ? 0 : Area(ring)) / 10000:0.00} ha, the file's is {expect / 10000:0.00} ha (told {Said(await op.ToldAsync(), op.HostSaid())})"));
        // One Outer row, with the KML's area: the drawn boundary's row (1.08 ha) is not the import's.
        string outerRows = "(boundary && boundary.items || []).filter(i => i.boundaryType === 'Outer')";
        if (await op.SeeAsync(FlowOp.F($"{outerRows}.length === 1 && Math.abs({outerRows}[0].areaHa - {expect / 10000}) < 0.1"), 3000))
            op.S.Ok("one Outer boundary in the list, with the KML's area");
        else
        {
            var outers = await op.PageAsync<JsonElement>("() => " + outerRows + ".map(i => i.areaHa)");
            var rows = outers.EnumerateArray().Select(a => a.GetDouble().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ha").ToList();
            op.S.Step($"the boundary list's Outer rows: {(rows.Count == 0 ? "none" : string.Join(", ", rows))}");
            op.S.Problem(Severity.Inconsistency, "BOUNDARY-LIST-STALE", "after the import the boundary list does not have one Outer boundary with the KML's area");
        }
    }

    // ------------------------------------------------------------------ B5, B6, B7, B8

    private static async Task<bool> AddWholeAsync(FlowOp op, string typed, string widths)
    {
        if (!await op.TapAsync("#fb-hl-add", "+ Add")) return false;
        if (widths.Length > 0 && !await op.SelectAsync("#fb-hl-tw", widths, "Inset")) return false;
        if (typed.Length > 0 && !await op.TypeAsync("#fb-hl-newoff", typed, "the headland width (m)", "Tab")) return false;
        string box = await op.PageAsync<string>("() => document.getElementById('fb-hl-newoff').value");
        op.S.Step($"the width box holds '{box}'");
        return await op.TapAsync("#fb-addhl [data-fbhl=whole]", "Whole Boundary");
    }

    private static async Task WholeHeadlandAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        if (!await OpenHeadlandTabAsync(op)) return;
        if (!await AddWholeAsync(op, "", "2 tool widths")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.HasHeadland && op.Rig.Vm.HeadlandSegments.Count > 0, 4000))
        { op.S.Problem(Severity.Bug, "HEADLAND-NOT-BUILT", $"Whole Boundary at 2 tool widths: no headland (told {Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        var seg = await op.Rig.ReadAsync(() => (op.Rig.Vm.HeadlandSegments[0].Name, op.Rig.Vm.HeadlandSegments[0].Offset));
        op.S.Step(FlowOp.F($"segment '{seg.Name}' at {seg.Offset:0.0} m"));
        if (Math.Abs(seg.Offset - 16) < 0.05) op.S.Ok("2 tool widths of an 8 m boom: 16 m");
        else op.S.Problem(Severity.Bug, "HEADLAND-WIDTH-WRONG", FlowOp.F($"2 tool widths of an 8 m boom made a {seg.Offset:0.0} m headland"));
        await HeadlandAtAsync(op, 16, "Whole Boundary");
        if (await op.SeeAsync("[...document.querySelectorAll('#fb-hllist .fb-hlrow')].length === 1", 2000)) op.S.Ok("the Headland tab lists the segment");
        else op.S.Problem(Severity.Inconsistency, "HEADLAND-LIST-STALE", $"the Headland tab lists: {string.Join(" | ", await op.RowsAsync("#fb-hllist .fb-hlrow"))}");

        // Its width changed to 24 m in the segment's box.
        if (!await op.TapAsync("#fb-hllist .fb-hlrow:nth-of-type(1)", "the segment's row")) return;
        if (!await op.SeeAsync("!document.getElementById('fb-hl-offrow').hidden", 1500)) { op.S.Problem(Severity.Bug, "HEADLAND-NO-WIDTH-BOX", "a tapped segment shows no width box"); return; }
        if (!await op.TypeAsync("#fb-hl-off", "24", "the segment's width", "Enter")) return;
        await op.TapAsync("#fieldbuilder .fb-tab[data-fbtab=headland]", "the Headland tab (leaving the box)");
        bool saved = await op.HostAsync(() => Math.Abs(op.Rig.Vm.HeadlandSegments[0].Offset - 24) < 0.05, 3000);
        if (saved) op.S.Ok("the width is 24 m");
        else
        {
            op.S.Step(FlowOp.F($"24 typed in the segment's box: the host has {await op.Rig.ReadAsync(() => op.Rig.Vm.HeadlandSegments.Count > 0 ? op.Rig.Vm.HeadlandSegments[0].Offset : double.NaN):0.0} m"));
            op.S.Problem(Severity.Bug, "HEADLAND-WIDTH-NOT-SAVED", "24 typed in the segment's box: the host did not take the width");
        }
        // A width the host did not take leaves the 16 m line, checked above: measuring it against 24 m would report the same fault again.
        await Task.Delay(500);
        if (saved) await HeadlandAtAsync(op, 24, "width changed to 24 m");
        await op.Run.ShotAsync(op.S, op.Screen, "B5-headland.png");
    }

    private static async Task HeadlandLineAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 40, 200), 0);
        if (!await DeleteAllAsync(op)) return;
        op.S.Ok("Delete All: no headland");

        var ring = await RingAsync(op);
        if (ring == null) { op.S.Note("skipped: the field has no boundary (see B4)"); return; }
        await WestLineAsync(op, ring);
        // Back to the whole boundary for the next scenarios, however far the line got.
        await RestoreWholeAsync(op);
    }

    /// <summary>Add › Line, two taps on the west fence, and the line read back; it stops at a step that fails (which has said so).</summary>
    private static async Task WestLineAsync(FlowOp op, List<(double E, double N)> ring)
    {
        var sw = ring.OrderBy(p => p.E + p.N).First();
        if (!await OpenHeadlandTabAsync(op)) return;
        if (!await op.TapAsync("#fb-hl-add", "+ Add")) return;
        if (!await op.TapAsync("#fb-addhl [data-fbhl=line]", "Line")) return;
        if (!await op.TapMapAsync(sw.E + 0.5, sw.N + 120, "a point on the west fence")) return;
        if (!await op.TapMapAsync(sw.E + 0.5, sw.N + 300, "another point on the west fence")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.HeadlandSegments.Count == 1 && op.Rig.Vm.HeadlandSegments[0].Type == AgOpenWeb.Models.Headland.HeadlandSegmentType.Line, 4000))
        { op.S.Problem(Severity.Bug, "HEADLAND-NOT-BUILT", $"Line: no line segment (told {Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        var seg = await op.Rig.ReadAsync(() => (op.Rig.Vm.HeadlandSegments[0].Name, op.Rig.Vm.HeadlandSegments[0].Offset, op.Rig.Vm.HeadlandSegments[0].Type.ToString(),
            op.Rig.Vm.HeadlandSegments[0].OffsetPoints.Select(p => (p.Easting, p.Northing)).ToList()));
        op.S.Step(FlowOp.F($"segment '{seg.Name}' ({seg.Item3}) at {seg.Offset:0.0} m, {seg.Item4.Count} points"));
        // + Add starts at 2 tool widths of the 8 m boom, as for the whole boundary (B5).
        if (Math.Abs(seg.Offset - 16) < 0.05) op.S.Ok("a Line at 2 tool widths of an 8 m boom: 16 m");
        else op.S.Problem(Severity.Bug, "HEADLAND-WIDTH-WRONG", FlowOp.F($"a Line at 2 tool widths of an 8 m boom made a {seg.Offset:0.0} m headland line"));
        // The line runs along the west side, its segment's width inside the fence: a wrong width is
        // the finding above, a line not where its width puts it this one.
        if (seg.Item4.Count >= 2)
        {
            var d = seg.Item4.Select(p => p.Easting - sw.E).ToList();
            op.S.Step(FlowOp.F($"the line is {d.Min():0.0}-{d.Max():0.0} m in from the west fence, for a {seg.Offset:0.0} m segment"));
            if (d.Min() > seg.Offset - 1 && d.Max() < seg.Offset + 1) op.S.Ok("the line runs along the west fence, its width inside it");
            else op.S.Problem(Severity.Bug, "HEADLAND-LINE-OFF", "the headland line is not its segment's width in from the west fence");
        }
        if (await op.SeeAsync("(scene.headlandSegs || []).length === 1 && scene.headlandSegs[0].type === 'Line'", 2000)) op.S.Ok("the Headland tab has the line");
        else op.S.Problem(Severity.Inconsistency, "HEADLAND-LIST-STALE", "the screen's headland list doesn't have the line");
        await op.Run.ShotAsync(op.S, op.Screen, "B6-headland-line.png");
    }

    private static async Task HeadlandButtonsAsync(FlowOp op)
    {
        // A headland to switch: the one B6 put back, or made here (B6 could not put it back, or B7 runs alone).
        if (!await op.Rig.ReadAsync(() => op.Rig.Vm.HasHeadland))
        {
            op.S.Step("no headland yet: the operator makes one round the whole boundary at 2 tool widths");
            if (!await RestoreWholeAsync(op)) { op.S.Note("skipped: no headland to switch"); return; }
        }
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
        bool on0 = await op.Rig.ReadAsync(() => op.Rig.Vm.IsHeadlandOn);
        if (!await op.TapAsync("#bn-headland", "Headland")) return;
        if (await op.HostAsync(() => op.Rig.Vm.IsHeadlandOn != on0, 2000)) op.S.Ok($"Headland: {(on0 ? "on → off" : "off → on")}");
        else op.S.Problem(Severity.Bug, "HEADLAND-BUTTON-DEAD", "the Headland button changed nothing on the host");
        bool shows = await op.SeeAsync($"tick && tick.tools && tick.tools.headlandOn === {(!on0 ? "true" : "false")}", 2000);
        if (shows) op.S.Ok("the screen shows it"); else op.S.Problem(Severity.Inconsistency, "HEADLAND-BUTTON-STALE", "the screen's headland state did not follow");
        await op.TapAsync("#bn-headland", "Headland again");
        await op.HostAsync(() => op.Rig.Vm.IsHeadlandOn == on0, 2000);

        bool s0 = await op.PageAsync<bool>("() => !!(tick && tick.tools && tick.tools.sectionInHeadland)");
        if (!await op.TapAsync("#bn-secheadland", "Section control in headland")) return;
        if (await op.SeeAsync($"tick && tick.tools && tick.tools.sectionInHeadland === {(!s0 ? "true" : "false")}", 2000)) op.S.Ok($"Section control in headland: {(s0 ? "on → off" : "off → on")}");
        else op.S.Problem(Severity.Bug, "HEADLAND-BUTTON-DEAD", "Section control in headland: the screen's state did not change");
        await op.TapAsync("#bn-secheadland", "Section control in headland again");
        await op.SeeAsync($"tick && tick.tools && tick.tools.sectionInHeadland === {(s0 ? "true" : "false")}", 2000);
    }

    private static async Task CarelessHeadlandAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        if (!await DeleteAllAsync(op)) return;

        // 0 typed as the width.
        if (!await OpenHeadlandTabAsync(op)) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await AddWholeAsync(op, "0", "")) return;
        await Task.Delay(800);
        var told = await op.ToldAsync();
        var host = op.HostSaid();
        // A refusal is said: a toast on the screen, or a failure the host raised (it toasts it too).
        bool said = told.Count > 0 || host.Any(h => h.StartsWith("Failure", StringComparison.Ordinal));
        var seg = await op.Rig.ReadAsync(() => op.Rig.Vm.HeadlandSegments.Select(s => s.Offset).ToList());
        if (seg.Count == 0 && said) op.S.Ok($"0 m: no headland made; told {Said(told, host)}");
        else if (seg.Count == 0) op.S.Problem(Severity.Bug, "HEADLAND-REFUSED-SILENT", "0 typed as the headland width: Whole Boundary made no headland and nothing was said");
        else if (!said) op.S.Problem(Severity.Improvement, "HEADLAND-ZERO-SILENT", FlowOp.F($"0 typed as the headland width: a {seg[0]:0.0} m headland was made without a word"), FindingKind.Enhancement);
        else op.S.Note(FlowOp.F($"0 typed: a {seg[0]:0.0} m headland, told {Said(told, host)}"));
        if (!await DeleteAllAsync(op)) return;

        // 150 m in a 240 m wide field: nothing is left inside.
        if (!await OpenHeadlandTabAsync(op)) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await AddWholeAsync(op, "150", "")) return;
        await Task.Delay(1000);
        told = await op.ToldAsync();
        host = op.HostSaid();
        said = told.Count > 0 || host.Any(h => h.StartsWith("Failure", StringComparison.Ordinal));
        var r = await op.Rig.ReadAsync(() => (op.Rig.Vm.HeadlandSegments.Count, op.Rig.Vm.HasHeadland, op.Rig.Vm.CurrentHeadlandLineForPreview?.Count ?? 0));
        op.S.Step($"150 m: {r.Count} segment(s), headland {r.HasHeadland} ({r.Item3} points)");
        // HostSaid has already reported an exception as HOST-EXCEPTION, and what the headland came to
        // after one is part of that fault; an error the host only logged is this scenario's.
        bool threw = host.Any(h => h.StartsWith("Exception", StringComparison.Ordinal));
        var errors = host.Where(h => h.StartsWith("Error", StringComparison.Ordinal)).ToList();
        bool none = r.Count == 0 || !r.HasHeadland;
        if (threw) op.S.Note("150 m in a 240 m field: the host threw an exception (HOST-EXCEPTION)");
        else if (errors.Count > 0)
        {
            op.S.Step($"the host logged '{errors[0]}'");
            op.S.Problem(Severity.Bug, "HEADLAND-WIDE-ERROR", "150 m headland in a 240 m field: the host logged an error");
        }
        else if (none && said) op.S.Ok($"150 m in a 240 m field: no headland, told {Said(told, host)}");
        else if (none) op.S.Problem(Severity.Bug, "HEADLAND-REFUSED-SILENT", "150 m in a 240 m field: Whole Boundary made no headland and nothing was said");
        else if (!said) op.S.Problem(Severity.Improvement, "HEADLAND-WIDER-THAN-FIELD-SILENT", "150 m in a 240 m field: a headland was made and nothing was said", FindingKind.Enhancement);
        else op.S.Note($"150 m in a 240 m field: {r.Count} segment(s), headland {r.HasHeadland} ({r.Item3} points), told {Said(told, host)}");
        await op.Run.ShotAsync(op.S, op.Screen, "B8-wide-headland.png");
        // Back to 16 m.
        await RestoreWholeAsync(op);
    }

    // ------------------------------------------------------------------ B9

    private static async Task ReopenAsync(FlowOp op)
    {
        var before = await op.Rig.ReadAsync(() => (Ring: op.Rig.Vm.OuterBoundaryPoints()?.ToList(), Segs: op.Rig.Vm.HeadlandSegments.Select(s => FlowOp.F($"{s.Name}|{s.Type}|{s.Offset:0.0}")).ToList(),
            Hl: op.Rig.Vm.CurrentHeadlandLineForPreview?.Count ?? 0, On: op.Rig.Vm.IsHeadlandOn));
        if (before.Ring == null) { op.S.Note("skipped: the field has no boundary to read back (see B4)"); return; }
        // A headland to read back: the one B8 put back, or made here (B8 stopped early, or B9 runs alone).
        if (before.Segs.Count == 0 || before.Hl < 3)
        {
            op.S.Step("no headland yet: the operator makes one round the whole boundary at 2 tool widths");
            // A failed restore has reported why, here.
            if (!await RestoreWholeAsync(op)) { op.S.Note("skipped: no headland to read back"); return; }
            await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
            before = await op.Rig.ReadAsync(() => (Ring: op.Rig.Vm.OuterBoundaryPoints()?.ToList(), Segs: op.Rig.Vm.HeadlandSegments.Select(s => FlowOp.F($"{s.Name}|{s.Type}|{s.Offset:0.0}")).ToList(),
                Hl: op.Rig.Vm.CurrentHeadlandLineForPreview?.Count ?? 0, On: op.Rig.Vm.IsHeadlandOn));
            if (before.Ring == null || before.Segs.Count == 0 || before.Hl < 3)
            {
                op.S.Step($"after Whole Boundary: {before.Segs.Count} segment(s), a headland line of {before.Hl} points");
                op.S.Problem(Severity.Bug, "HEADLAND-NOT-BUILT", "Whole Boundary at 2 tool widths: no headland line on the host");
                return;
            }
        }
        op.S.Step(FlowOp.F($"before: boundary {before.Ring.Count} points {Area(before.Ring) / 10000:0.00} ha; headland {string.Join(", ", before.Segs)} ({before.Hl} points), headland on {before.On}"));
        if (!await FlowFields.CloseFieldAsync(op, "discard")) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close did not close the field"); return; }
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        await Task.Delay(800);
        var after = await op.Rig.ReadAsync(() => (Ring: op.Rig.Vm.OuterBoundaryPoints()?.ToList(), Segs: op.Rig.Vm.HeadlandSegments.Select(s => FlowOp.F($"{s.Name}|{s.Type}|{s.Offset:0.0}")).ToList(),
            Hl: op.Rig.Vm.CurrentHeadlandLineForPreview?.Count ?? 0, On: op.Rig.Vm.IsHeadlandOn));
        op.S.Step(FlowOp.F($"after: boundary {after.Ring?.Count ?? 0} points {(after.Ring == null ? 0 : Area(after.Ring)) / 10000:0.00} ha; headland {string.Join(", ", after.Segs)} ({after.Hl} points), headland on {after.On}"));
        if (after.Ring != null && Math.Abs(Area(after.Ring) - Area(before.Ring)) < 1) op.S.Ok("the boundary came back the same");
        else op.S.Problem(Severity.Bug, "BOUNDARY-CHANGED-ON-REOPEN", "the boundary changed when the field was opened again");
        if (after.Segs.SequenceEqual(before.Segs) && Math.Abs(after.Hl - before.Hl) <= 2) op.S.Ok("the headland came back the same");
        else op.S.Problem(Severity.Bug, "HEADLAND-CHANGED-ON-REOPEN", $"headland before {string.Join(", ", before.Segs)} ({before.Hl} points), after {string.Join(", ", after.Segs)} ({after.Hl} points)");
        if (after.On == before.On) op.S.Ok($"headland {(after.On ? "on" : "off")} as before");
        else op.S.Problem(Severity.Bug, "HEADLAND-SWITCH-CHANGED-ON-REOPEN", $"the headland was {(before.On ? "on" : "off")}, after opening again {(after.On ? "on" : "off")}");
    }
}
