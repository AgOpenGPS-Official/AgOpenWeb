// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// T: tracks, made every way the web client offers (Quick AB: A+, Drive AB, a driven curve,
/// coordinates, from the boundary, drawn on the map), then renamed, hidden, deleted, swapped and
/// read back after the field is closed and opened again. Each line is checked where the operator
/// needs it: where the first pass lies compared with where the tractor drove, its heading, and
/// what the screen's lists show. The careless operator types a comma for a decimal point, taps a
/// button in the middle of drawing, leaves a name empty, deletes the line AutoSteer is on.
/// </summary>
public static class TrackFlows
{
    public const string Group = "T: tracks";
    private const int Slot = 0;
    private const string Field = "Flows Tracks";
    private const double HalfTool = 4.0; // 8 m boom, first pass on the right of the driven line

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "T1", "A+ while driving", "Careful operator, driving north at 8 km/h: AB menu › Quick AB › A+.", AplusAsync);
        await Flow(run, "T2", "A+ with a typed heading", "Careful operator, standing: Quick AB, 45 typed in A+ heading, A+.", AplusTypedAsync);
        await Flow(run, "T3", "Drive AB", "Careful operator at 8 km/h, heading a hair west of north (359.97°, as a tractor driven up a field due north does): Quick AB › Drive AB, Set Point A, 50 m on, Set Point B.", DriveAbAsync);
        await Flow(run, "T4", "A curve recorded by driving", "Careful operator at 8 km/h: Quick AB › Curve, Set Point A, 30 m straight, a 60° bend to the right, 30 m, Set Point B.", RecordCurveAsync);
        await Flow(run, "T5", "A line from coordinates", "Careful operator: Quick AB › Lat/Lon + Heading (A filled with the tractor's position, heading 90 typed); then Lat/Lon A, B with two typed points.", LatLonAsync);
        await Flow(run, "T6", "Coordinates typed carelessly", "Careless operator: a comma for the decimal point, first in the heading (12,5, A as filled), then in the latitude; the heading left empty; B the same as A; latitude and longitude swapped.", LatLonCarelessAsync);
        await Flow(run, "T7", "Lines from the boundary", "Careful operator: Quick AB › Longest Edge, then All Edges, then Bnd. Curve (A on the south edge, B on the north edge, the shorter way round).", FromBoundaryAsync);
        await Flow(run, "T8", "Lines drawn on the map", "Careful operator: Quick AB › Straight, two taps on the map; Quick AB › Curve, four taps and Finish. Careless: a tap on the bottom bar's Flags button in the middle of drawing, then Cancel.", DrawOnMapAsync);
        await Flow(run, "T9", "Rename a track", "Tracks manager, on the tracks T1-T8 made: a row, Rename, a new name and Enter; a name with a comma and accents; the careless operator empties the name, then types another track's name.", RenameAsync);
        await Flow(run, "T10", "Hide and show a track", "Tracks manager, on the tracks T1-T9 left: the tick of a track that isn't active taken off, its row tapped; then the tick of the line AutoSteer is steering on, at 8 km/h.", HideAsync);
        await Flow(run, "T11", "Delete and swap", "Tracks manager, on the tracks T1-T10 left: Swap A/B on a line; Delete, Cancel; Delete, Confirm; then the careless operator deletes the line AutoSteer is steering on, at 8 km/h.", DeleteSwapAsync);
        await Flow(run, "T12", "The tracks after the field is closed and opened", "On the tracks T1-T11 left: Field Operations › Close, then Fields and Jobs › the field › Open Field Only: the same tracks, names, ticks and active line as before.", ReopenAsync);
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            if (!await FlowFields.EnsureOpenAsync(op, Slot, Field)) { s.Harness("the tracks field could not be opened"); return; }
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
        }, onTheRunsLine: false);

    // ------------------------------------------------------------------ helpers

    internal static async Task<bool> OpenQuickAbAsync(FlowOp op)
    {
        await op.CloseAllAsync();
        if (!await op.TapAsync("#bn-abmenu", "the AB line menu")) return false;
        if (!await op.SeeAsync("document.getElementById('bn-flyout-ab').classList.contains('open')", 2000))
        { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "the AB line menu did not open"); return false; }
        if (!await op.TapAsync("#bn-quickab", "Quick AB")) return false;
        if (await op.WaitOpenAsync("dlg-quickab", 2000)) return true;
        op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Quick AB did not open");
        return false;
    }

    internal static async Task<bool> OpenTracksAsync(FlowOp op)
    {
        await op.CloseAllAsync();
        if (!await op.TapAsync("#bn-abmenu", "the AB line menu")) return false;
        if (!await op.TapAsync("#bn-tracks", "Tracks")) return false;
        if (await op.WaitOpenAsync("dlg-tracks", 2000)) return true;
        op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "the Tracks manager did not open");
        return false;
    }

    /// <summary>Quick AB › a coordinates button; the dialog opens fresh (A filled with the tractor's position).</summary>
    private static async Task<bool> OpenLatLonAsync(FlowOp op, string mode, string what)
    {
        if (!await OpenQuickAbAsync(op)) return false;
        if (!await op.TapAsync($"[data-qab={mode}]", what)) return false;
        if (await op.WaitOpenAsync("dlg-latlonab", 2000)) return true;
        op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "the coordinates dialog did not open");
        return false;
    }

    /// <summary>The track that appeared since <paramref name="before"/>, or null (a finding).</summary>
    internal static async Task<(TrackInfo Info, Track Track)?> NewTrackAsync(FlowOp op, int before, string what, int ms = 4000)
    {
        if (!await op.HostAsync(() => op.Rig.Vm.SavedTracks.Count > before, ms))
        {
            var told = await op.ToldAsync();
            op.S.Problem(Severity.Bug, "TRACK-NOT-CREATED", $"{what}: no new track (the screen said: {Said(told, op.HostSaid())})");
            return null;
        }
        var t = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks[^1]);
        var info = await op.Rig.ReadAsync(() => TrackInfo.Of(op.Rig.Vm.SavedTracks.Count - 1, t, op.Rig.Vm.SelectedTrack));
        op.S.Step("new track: " + info);
        return (info, t);
    }

    internal static string Said(List<string> page, List<string> host)
    {
        var all = page.Concat(host.Where(h => h.StartsWith("Failure", StringComparison.Ordinal))).ToList();
        return all.Count == 0 ? "nothing" : Masked(string.Join(" / ", all.Take(4).Select(x => "'" + x + "'")));
    }

    /// <summary>Text that quotes track names, as a check or a finding says it. The app names A+,
    /// driven and drawn lines after the clock ('A+ 21:07', 'AB_0.0 21:07:33', and 'A+ 21:07 (2)'
    /// when two fall in one minute): the time and that ' (2)' are masked, so two nights' reports
    /// differ only where the app did.</summary>
    internal static string Masked(string s) => System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(s, @"\b\d{1,2}:\d{2}(:\d{2})?\b", "hh:mm"), @"hh:mm \(\d+\)", "hh:mm");

    /// <summary>How far apart two headings are, in degrees (0 to 180).</summary>
    private static double Apart(double a, double b) => Math.Abs(((a - b + 540) % 360) - 180);

    /// <summary>The track is on the screen's list, active as on the host.</summary>
    private static async Task ShownAsync(FlowOp op, TrackInfo t)
    {
        bool shown = await op.SeeAsync($"scene && (scene.trackList || []).some(x => x.name === {JsonSerializer.Serialize(t.Name)} && x.active === {(t.Active ? "true" : "false")})", 3000);
        if (shown) op.S.Ok(Masked($"the screen lists '{t.Name}'" + (t.Active ? " as the active line" : "")));
        else
        {
            var list = await op.ScreenTracksAsync();
            op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"'{t.Name}' (active {t.Active}) is not on the screen's track list: {string.Join(", ", list.Select(x => x.Name + (x.Active ? "*" : "")))}"));
        }
    }

    /// <summary>A driven line: parallel to the way the tractor drove, its first pass where the
    /// tractor drove (the reference edge half a boom to the right). The measures go in the steps.</summary>
    private static async Task DrivenLineAsync(FlowOp op, Track t, TrackInfo info, Pose p, string what)
    {
        double dh = Apart(info.HeadingDeg, p.HeadingDeg);
        op.S.Step(FlowOp.F($"{what}: the line runs at {info.HeadingDeg:0.00}°, the tractor at {p.HeadingDeg:0.00}°"));
        if (dh <= 1.5) op.S.Ok($"{what}: the line runs the way the tractor drove");
        else op.S.Problem(Severity.Bug, "TRACK-HEADING-WRONG", $"{what}: the line does not run the way the tractor drove (see the steps)");
        double d = TrackInfo.DistanceTo(t, p.E, p.N);
        // Right of the tractor: the cross product of the heading with (line point - tractor) is positive.
        double hr = p.HeadingDeg * Math.PI / 180;
        double side = Math.Sin(hr) * (info.AN - p.N) - Math.Cos(hr) * (info.AE - p.E); // > 0 left, < 0 right
        bool right = side < 0;
        op.S.Step(FlowOp.F($"{what}: the reference line is {d:0.00} m to the {(right ? "right" : "left")} of where the tractor drove"));
        if (Math.Abs(d - HalfTool) <= 0.6 && right)
            op.S.Ok($"{what}: the reference line is half the boom to the right of where the tractor drove, so the first pass is where it drove");
        else
            op.S.Problem(Severity.Bug, "TRACK-NOT-WHERE-DRIVEN", FlowOp.F($"{what}: the reference line is not half the boom ({HalfTool:0.0} m) to the right of where the tractor drove{(right ? "" : "; it is on the left")} (see the steps)"));
        double xte = await op.Rig.ReadAsync(() => op.Rig.State.Guidance.CrossTrackError);
        op.S.Step(FlowOp.F($"cross-track error on the screen's line: {xte:0.00}"));
    }

    // ------------------------------------------------------------------ T1, T2

    private static async Task AplusAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 60, 40), 0);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(1500);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=aPlus]", "A+")) return;
        if (await NewTrackAsync(op, before, "A+") is not var (info, t)) return;
        var p = await op.PoseAsync();
        await op.StopAsync();
        if (info.Active) op.S.Ok("the new line is the active one"); else op.S.Problem(Severity.Bug, "TRACK-NOT-ACTIVE", "A+: the new line is not the active one");
        await DrivenLineAsync(op, t, info, p, "A+ at 8 km/h");
        if (!await op.IsOpenAsync("dlg-quickab")) op.S.Ok("Quick AB closed");
        else op.S.Problem(Severity.Bug, "FLOW-DIALOG-STAYS", "Quick AB stays open after A+");
        await ShownAsync(op, info);
        await op.Run.ShotAsync(op.S, op.Screen, "T1-aplus.png");
    }

    private static async Task AplusTypedAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 100, 60), 0);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        string ph = await op.PageAsync<string>("() => document.getElementById('qab-aplus-heading').placeholder");
        op.S.Step($"A+ heading box shows the tractor's heading as its hint: '{ph}'");
        if (!await op.TypeAsync("#qab-aplus-heading", "45", "A+ heading")) return;
        if (!await op.TapAsync("[data-qab=aPlus]", "A+")) return;
        if (await NewTrackAsync(op, before, "A+ 45°") is not var (info, t)) return;
        if (Math.Abs(info.HeadingDeg - 45) <= 0.2) op.S.Ok(FlowOp.F($"the line runs at {info.HeadingDeg:0.0}°, as typed"));
        else op.S.Problem(Severity.Bug, "TRACK-HEADING-WRONG", FlowOp.F($"A+ with 45 typed: the line runs at {info.HeadingDeg:0.0}°"));
        await ShownAsync(op, info);
        // Opened again, the box is empty (the typed heading is not kept for the next line).
        if (!await OpenQuickAbAsync(op)) return;
        string again = await op.PageAsync<string>("() => document.getElementById('qab-aplus-heading').value");
        if (again.Length == 0) op.S.Ok("opened again, the A+ heading box is empty (the next A+ uses the tractor's heading)");
        else op.S.Problem(Severity.Bug, "APLUS-HEADING-KEPT", $"opened again, the A+ heading box still holds '{again}': the next A+ would not follow the tractor");
        await op.TapAsync("#dlg-qab-cancel", "Cancel");
    }

    // ------------------------------------------------------------------ T3, T4

    private static async Task DriveAbAsync(FlowOp op)
    {
        // 359.97°: the line's name then shows how a heading just under 360 is written (fixed, so the report doesn't flicker).
        await op.PlaceAsync(FlowFields.At(Slot, 150, 40), 359.97);
        // The name check rests on that heading: read back where the tractor really faces first.
        var placed = await op.PoseAsync();
        op.S.Step(FlowOp.F($"the tractor stands facing {placed.HeadingDeg:0.00}°"));
        bool hairWest = placed.HeadingDeg > 359.9 && placed.HeadingDeg < 360;
        if (!hairWest) op.S.Harness("the tractor could not be put facing a hair west of north (see the steps); the line's name is not checked");
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(1000);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=driveAB]", "Drive AB")) return;
        string label = await op.PageAsync<string>("() => document.getElementById('draw-setpoint').textContent");
        if (!await op.SeeAsync("document.getElementById('draw-toolbar').classList.contains('show')", 2000))
        { op.S.Problem(Severity.Bug, "DRAW-BAR-MISSING", "Drive AB: the bar with Set Point A did not show"); return; }
        op.S.Step($"the bar shows '{label}'");
        if (!await op.TapAsync("#draw-setpoint", "Set Point A")) return;
        var a = await op.PoseAsync();
        await op.DriveAsync(50, 8);
        string labelB = await op.PageAsync<string>("() => document.getElementById('draw-setpoint').textContent");
        if (!await op.TapAsync("#draw-setpoint", $"Set Point B ('{labelB}')")) return;
        if (await NewTrackAsync(op, before, "Drive AB") is not var (info, t)) return;
        var p = await op.PoseAsync();
        await op.StopAsync();
        await DrivenLineAsync(op, t, info, p, "Drive AB");
        // Only here, where the heading is set: a line drawn due north on the map comes out a hair
        // either side of 0° by the pixel, so its name would flicker from night to night.
        if (hairWest)
        {
            var m = System.Text.RegularExpressions.Regex.Match(info.Name, @"(?<![0-9.])360(\.0)?(?![0-9])");
            if (m.Success) op.S.Problem(Severity.Improvement, "TRACK-NAME-SAYS-360", $"Drive AB: a line a hair west of north is named '{info.Name[..(m.Index + m.Length)]} …' (0° in the other names)", FindingKind.Enhancement);
            else op.S.Ok($"Drive AB: a line a hair west of north is named '{Masked(info.Name)}'");
        }
        double dA = Math.Sqrt((info.AE - a.E) * (info.AE - a.E) + (info.AN - a.N) * (info.AN - a.N));
        op.S.Step(FlowOp.F($"A is {dA:0.0} m from where the tractor was at Set Point A"));
        if (await op.SeeAsync("!document.getElementById('draw-toolbar').classList.contains('show')", 2500)) op.S.Ok("the Set Point bar went away");
        else op.S.Problem(Severity.Bug, "DRAW-BAR-STAYS", "Drive AB: the Set Point bar is still up after B");
        await ShownAsync(op, info);
    }

    private static async Task RecordCurveAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 60, 120), 0);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(800);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=recordCurve]", "Curve (by driving)")) return;
        if (!await op.TapAsync("#draw-setpoint", "Set Point A")) return;
        var driven = new List<Pose>();
        var sampler = SampleAsync(op, driven);
        await op.DriveAsync(30, 8);
        await op.TurnAsync(60, 8, 15);
        await op.DriveAsync(30, 8);
        if (!await op.TapAsync("#draw-setpoint", "Set Point B")) { sampler.Dispose(); return; }
        sampler.Dispose();
        if (await NewTrackAsync(op, before, "a driven curve", 6000) is not var (info, t)) return;
        await op.StopAsync();
        op.S.Step($"the curve has {info.Points} points ({info.Type})");
        if (info.Points >= 10) op.S.Ok($"the curve has at least 10 points ({info.Type})");
        else op.S.Problem(Severity.Bug, "CURVE-TOO-FEW-POINTS", $"the driven curve has fewer than 10 points ({info.Type})");
        // Every place the tractor drove (away from the ends) is half a boom from the curve.
        var mid = driven.Skip(driven.Count / 8).Take(driven.Count * 3 / 4).ToList();
        if (mid.Count > 5)
        {
            var d = mid.Select(p => TrackInfo.DistanceTo(t, p.E, p.N)).ToList();
            double lo = d.Min(), hi = d.Max();
            op.S.Step(FlowOp.F($"along the drive the curve is {lo:0.00}-{hi:0.00} m beside the tractor's path"));
            if (lo >= HalfTool - 0.7 && hi <= HalfTool + 0.7)
                op.S.Ok("along the drive the curve is half the boom beside the tractor's path: the first pass is the path");
            else op.S.Problem(Severity.Bug, "CURVE-NOT-WHERE-DRIVEN", FlowOp.F($"along the drive the curve is not half the boom ({HalfTool:0.0} m) from the tractor's path (see the steps)"));
        }
        await ShownAsync(op, info);
        await op.Run.ShotAsync(op.S, op.Screen, "T4-driven-curve.png");
    }

    private sealed class Sampler : IDisposable
    {
        public CancellationTokenSource Cts = new();
        public void Dispose() => Cts.Cancel();
    }

    private static Sampler SampleAsync(FlowOp op, List<Pose> into)
    {
        var s = new Sampler();
        _ = Task.Run(async () =>
        {
            while (!s.Cts.IsCancellationRequested)
            {
                try { var p = await op.PoseAsync(); lock (into) into.Add(p); } catch { }
                try { await Task.Delay(200, s.Cts.Token); } catch { }
            }
        });
        return s;
    }

    // ------------------------------------------------------------------ T5, T6

    private static async Task LatLonAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=latLonHeading]", "Lat/Lon + Heading")) return;
        if (!await op.WaitOpenAsync("dlg-latlonab", 2000)) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "the coordinates dialog did not open"); return; }
        string latA = await op.PageAsync<string>("() => document.getElementById('ll-lat-a').value");
        op.S.Step($"A filled with the tractor's position: {latA}, {await op.PageAsync<string>("() => document.getElementById('ll-lon-a').value")}");
        if (!await op.TypeAsync("#ll-heading", "90", "Heading")) return;
        if (!await op.TapAsync("#ll-ok", "Create")) return;
        var p = await op.PoseAsync();
        if (await NewTrackAsync(op, before, "Lat/Lon + Heading") is var (info, t))
        {
            double d = TrackInfo.DistanceTo(t, p.E, p.N);
            if (Math.Abs(info.HeadingDeg - 90) <= 0.2 && d <= 0.3) op.S.Ok(FlowOp.F($"the line runs at {info.HeadingDeg:0.0}° through the tractor's position ({d:0.00} m)"));
            else op.S.Problem(Severity.Bug, "TRACK-NOT-AT-COORDINATES", FlowOp.F($"Lat/Lon + Heading 90: the line runs at {info.HeadingDeg:0.0}°, {d:0.00} m from point A"));
            await ShownAsync(op, info);
        }

        // Two typed points: 100 m apart along a heading of 30°.
        var a = FlowFields.At(Slot, 80, 150);
        var b = FlowFields.At(Slot, 80 + 100 * Math.Sin(Math.PI / 6), 150 + 100 * Math.Cos(Math.PI / 6));
        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=latLonAB]", "Lat/Lon A, B")) return;
        if (!await op.TypeAsync("#ll-lat-a", FlowOp.F($"{a.Lat:F8}"), "A latitude")) return;
        if (!await op.TypeAsync("#ll-lon-a", FlowOp.F($"{a.Lon:F8}"), "A longitude")) return;
        if (!await op.TypeAsync("#ll-lat-b", FlowOp.F($"{b.Lat:F8}"), "B latitude")) return;
        if (!await op.TypeAsync("#ll-lon-b", FlowOp.F($"{b.Lon:F8}"), "B longitude")) return;
        if (!await op.TapAsync("#ll-ok", "Create")) return;
        if (await NewTrackAsync(op, before, "Lat/Lon A, B") is var (i2, _))
        {
            if (Math.Abs(i2.HeadingDeg - 30) <= 0.3) op.S.Ok(FlowOp.F($"the line through the two points runs at {i2.HeadingDeg:0.0}° (30° typed)"));
            else op.S.Problem(Severity.Bug, "TRACK-NOT-AT-COORDINATES", FlowOp.F($"Lat/Lon A, B 30° apart: the line runs at {i2.HeadingDeg:0.0}°"));
        }
    }

    private static async Task LatLonCarelessAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        var pos = FlowFields.At(Slot, 120, 200);
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);

        // 1. A comma for the decimal point (a Portuguese or German keyboard). First in the heading
        // alone, A as the dialog fills it, so a comma the box drops (12,5 read as 125) shows.
        if (!await OpenLatLonAsync(op, "latLonHeading", "Lat/Lon + Heading")) return;
        if (!await op.TypeAsync("#ll-heading", "12,5", "Heading, with a comma")) return;
        string box = await op.PageAsync<string>("() => document.getElementById('ll-heading').value");
        op.S.Step($"the heading box holds '{box}'");
        await op.TapAsync("#ll-ok", "Create");
        await op.HostAsync(() => op.Rig.Vm.SavedTracks.Count > before, 1500);
        string err = await op.PageAsync<string>("() => document.getElementById('ll-err').textContent");
        int now = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        bool open = await op.IsOpenAsync("dlg-latlonab");
        TrackInfo? made = now > before ? (await op.TracksAsync())[^1] : null;
        if (made != null) op.S.Step("made: " + made);
        bool at125 = made != null && Apart(made.HeadingDeg, 125) < 1;
        // The box's value stays in the step above: the message quotes '125' only when that is what it holds.
        if (box == "125" || at125)
            op.S.Problem(Severity.Bug, "HEADING-COMMA-DROPPED", "'12,5' typed as the heading: "
                + (box == "125" ? "the box drops the comma as it is typed (it then holds '125')" : "the box drops the comma")
                + ", nothing is said" + (at125 ? ", and Create makes a line at 125°" : ""), FindingKind.AppDefect);
        else if (made != null && Apart(made.HeadingDeg, 12.5) < 0.2)
            op.S.Ok("'12,5' typed as the heading: the line runs at 12.5°, the comma read as a decimal point");
        else if (made != null)
            op.S.Problem(Severity.Bug, "LATLON-COMMA-MISREAD", "'12,5' typed as the heading made a line at neither 12.5° nor 125° (see the steps)", FindingKind.AppDefect);
        else if (open && err.Length > 0) op.S.Ok($"'12,5' typed as the heading: nothing made, the box holds '{box}' and the dialog says '{err}'");
        else op.S.Problem(Severity.Improvement, "LATLON-COMMA-SILENT", "a comma for the decimal point in the heading: nothing made and nothing said", FindingKind.Enhancement);
        if (await op.IsOpenAsync("dlg-latlonab")) await op.TapAsync("#ll-cancel", "Cancel");

        // Then the same comma in the latitude, the heading typed right.
        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenLatLonAsync(op, "latLonHeading", "Lat/Lon + Heading")) return;
        string latComma = FlowOp.F($"{pos.Lat:F6}").Replace('.', ',');
        if (!await op.TypeAsync("#ll-lat-a", latComma, "A latitude, with a comma")) return;
        if (!await op.TypeAsync("#ll-heading", "12.5", "Heading")) return;
        string latBox = await op.PageAsync<string>("() => document.getElementById('ll-lat-a').value");
        op.S.Step($"the latitude box holds '{latBox}'");
        await op.TapAsync("#ll-ok", "Create");
        await op.HostAsync(() => op.Rig.Vm.SavedTracks.Count > before, 1500);
        err = await op.PageAsync<string>("() => document.getElementById('ll-err').textContent");
        now = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        open = await op.IsOpenAsync("dlg-latlonab");
        string boxDid = latBox == latComma.Replace(",", "") ? "the box strips the comma, so the latitude is out of range"
            : latBox.Contains(',') ? "the box keeps the comma" : "the box holds something else (see the steps)";
        if (now > before)
        {
            var tr = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks[^1]);
            var ti = (await op.TracksAsync())[^1];
            var p = await op.PoseAsync();
            double d = TrackInfo.DistanceTo(tr, p.E, p.N);
            op.S.Step(FlowOp.F($"made: {ti}, {d:0.00} m from the tractor"));
            if (d < 1 && Apart(ti.HeadingDeg, 12.5) < 0.2) op.S.Ok("a comma in the latitude: read as a decimal point, the line runs through the tractor at 12.5°");
            else op.S.Problem(Severity.Bug, "LATLON-COMMA-MISREAD", "a comma in the latitude made a line that does not run through the typed point at 12.5° (see the steps)", FindingKind.AppDefect);
        }
        else if (open && err.Length > 0) op.S.Ok($"a comma in the latitude: {boxDid}; nothing made, and the dialog says '{err}'");
        else op.S.Problem(Severity.Improvement, "LATLON-COMMA-SILENT", $"a comma in the latitude: {boxDid}; nothing made and nothing said", FindingKind.Enhancement);
        if (await op.IsOpenAsync("dlg-latlonab")) await op.TapAsync("#ll-cancel", "Cancel");

        // 2. The heading left empty (A as filled).
        if (!await OpenLatLonAsync(op, "latLonHeading", "Lat/Lon + Heading")) return;
        if (!await op.TypeAsync("#ll-heading", "", "Heading (left empty)")) return;
        await op.TapAsync("#ll-ok", "Create");
        await Task.Delay(400);
        err = await op.PageAsync<string>("() => document.getElementById('ll-err').textContent");
        if (err.Length > 0 && await op.IsOpenAsync("dlg-latlonab")) op.S.Ok($"no heading: the dialog says '{err}'");
        else op.S.Problem(Severity.Improvement, "LATLON-EMPTY-SILENT", $"no heading: the dialog said '{err}'", FindingKind.Enhancement);
        if (await op.IsOpenAsync("dlg-latlonab")) await op.TapAsync("#ll-cancel", "Cancel");

        // 3. B the same as A.
        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenLatLonAsync(op, "latLonAB", "Lat/Lon A, B")) return;
        await op.TapAsync("[data-ll-fill=b]", "B: Use current position");
        await op.ToldAsync(); op.HostSaid();
        await op.TapAsync("#ll-ok", "Create");
        await Task.Delay(800);
        now = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        var told = await op.ToldAsync();
        var host = op.HostSaid();
        if (now == before && told.Count > 0) op.S.Ok($"B on A: no line, the operator is told {Said(told, host)}");
        else if (now == before) op.S.Problem(Severity.Improvement, "LATLON-SAME-POINT-SILENT", Masked($"B on A: no line and nothing on the screen (host: {string.Join(" / ", host.Take(2))})"), FindingKind.Enhancement);
        else
        {
            op.S.Step("made: " + (await op.TracksAsync())[^1]);
            op.S.Problem(Severity.Bug, "LATLON-SAME-POINT-LINE", "B on A made a line (see the steps)");
        }

        // 4. Latitude and longitude swapped (a point in the sea far away).
        before = now;
        if (!await OpenLatLonAsync(op, "latLonHeading", "Lat/Lon + Heading")) return;
        if (!await op.TypeAsync("#ll-lat-a", FlowOp.F($"{pos.Lon:F8}"), "A latitude (the longitude typed)")) return;
        if (!await op.TypeAsync("#ll-lon-a", FlowOp.F($"{pos.Lat:F8}"), "A longitude (the latitude typed)")) return;
        if (!await op.TypeAsync("#ll-heading", "0", "Heading")) return;
        await op.ToldAsync(); op.HostSaid();
        await op.TapAsync("#ll-ok", "Create");
        await Task.Delay(800);
        now = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        told = await op.ToldAsync(); host = op.HostSaid();
        if (now > before)
        {
            var t = (await op.TracksAsync())[^1];
            var p = await op.PoseAsync();
            var tr = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks[^1]);
            double km = TrackInfo.DistanceTo(tr, p.E, p.N) / 1000;
            if (km > 50) op.S.Problem(Severity.Improvement, "LATLON-FAR-AWAY-ACCEPTED", FlowOp.F($"latitude and longitude swapped: a line {km:0} km from the field was made ('{t.Name}'), and the operator was told {Said(told, host)}"), FindingKind.Enhancement);
            else op.S.Note(FlowOp.F($"latitude and longitude swapped made a line {km:0.0} km away"));
        }
        else op.S.Ok($"latitude and longitude swapped: no line (told {Said(told, host)})");
        await op.CloseDialogAsync();
    }

    // ------------------------------------------------------------------ T7

    private static async Task FromBoundaryAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        var ring = await op.Rig.ReadAsync(() => op.Rig.Vm.OuterBoundaryPoints()?.ToList());
        if (ring == null || ring.Count < 3) { op.S.Harness("the field has no outer boundary"); return; }
        // The field's sides as the operator sees them: runs of boundary pieces in one direction
        // (the field loader cuts a long side into pieces of at most 50 m, BoundaryResolution).
        var fieldSides = Sides(ring);
        var side = fieldSides.OrderByDescending(x => x.Len).First();
        double piece = 0;
        for (int i = 0; i < ring.Count; i++) piece = Math.Max(piece, Dist(ring[i], ring[(i + 1) % ring.Count]));
        op.S.Step(FlowOp.F($"the boundary has {ring.Count} points and {fieldSides.Count} sides; its longest side is {side.Len:0} m, its longest piece between two points {piece:0} m"));

        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=boundaryEdge]", "Longest Edge")) return;
        if (await NewTrackAsync(op, before, "Longest Edge") is var (info, t))
        {
            // Which side the line lies on: both its ends on that side's line, and along it.
            bool On(((double E, double N) A, (double E, double N) B, double Len) x)
            {
                double eh = (Math.Atan2(x.B.E - x.A.E, x.B.N - x.A.N) * 180 / Math.PI + 360) % 360;
                double dh = Math.Abs(((info.HeadingDeg - eh + 540) % 360) - 180);
                return PointToLine(info.AE, info.AN, x.A, x.B) < 0.5 && PointToLine(info.BE, info.BN, x.A, x.B) < 0.5 && Math.Min(dh, 180 - dh) < 1;
            }
            op.S.Step(FlowOp.F($"'{Masked(info.Name)}' runs at {info.HeadingDeg:0.0}°"));
            if (On(side)) op.S.Ok(FlowOp.F($"the line lies on the field's longest side ({side.Len:0} m)"));
            else if (fieldSides.FirstOrDefault(On) is { Len: > 0 } other)
                op.S.Problem(Severity.Bug, "LONGEST-EDGE-NOT-LONGEST", FlowOp.F($"Longest Edge: the line runs along the {other.Len:0} m side, not the field's longest ({side.Len:0} m): it takes the longest piece between two boundary points"), FindingKind.AppDefect);
            else op.S.Problem(Severity.Bug, "BOUNDARY-EDGE-LINE-OFF", "Longest Edge: the line lies on no side of the field (see the steps)", FindingKind.AppDefect);
            await ShownAsync(op, info);
        }

        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=allEdges]", "All Edges")) return;
        await op.HostAsync(() => op.Rig.Vm.SavedTracks.Count > before, 4000);
        await Task.Delay(500);
        int made = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count) - before;
        // A straight-sided field: one line per side (the host's ring has more points than corners).
        int sides = Corners(ring);
        if (made == sides) op.S.Ok($"All Edges made {made} lines, one per side");
        else if (made == 0) op.S.Problem(Severity.Bug, "TRACK-NOT-CREATED", $"All Edges made no line (said {Said(await op.ToldAsync(), op.HostSaid())})");
        else op.S.Problem(Severity.Bug, "ALL-EDGES-COUNT", $"All Edges made {made} lines for a field of {sides} straight sides ({ring.Count} boundary points)");
        var names = (await op.TracksAsync()).Select(x => x.Name).ToList();
        if (names.Distinct().Count() == names.Count) op.S.Ok($"all {names.Count} track names differ");
        else op.S.Problem(Severity.Bug, "TRACK-NAMES-REPEATED", Masked("two tracks share a name: " + string.Join(", ", names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key))));

        // Bnd. Curve: A on the south edge, B on the north edge, the shorter way round (the east side).
        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=boundaryCurve]", "Bnd. Curve")) return;
        // The field's south-west corner as the host has it (the KML's first point).
        var sw = ring.OrderBy(p => p.E + p.N).First();
        if (!await op.TapMapAsync(sw.E + 180, sw.N + 0.5, "A on the south edge")) return;
        if (!await op.TapMapAsync(sw.E + 180, sw.N + 389, "B on the north edge")) return;
        if (!await op.SeeAsync("document.getElementById('draw-finish').offsetParent !== null", 2000))
        { op.S.Problem(Severity.Bug, "BND-CURVE-NO-CREATE", "Bnd. Curve: no Create after A and B"); return; }
        if (!await op.TapAsync("#draw-finish", "Create")) return;
        if (await NewTrackAsync(op, before, "Bnd. Curve") is var (bc, bct))
        {
            if (bc.Type == TrackType.BoundaryCurve || bc.Points > 2) op.S.Ok($"a boundary curve of {bc.Points} points ({bc.Type})");
            else op.S.Problem(Severity.Bug, "BND-CURVE-WRONG", $"Bnd. Curve made a {bc.Type} of {bc.Points} points");
            // The curve follows the fence from A to B: every point between them within 0.5 m of the
            // boundary. Its ends run on past A and B along their tangents (ExtendCurvePastBoundary,
            // so a U-turn has a boundary crossing to anchor at): not part of the check.
            var pts = await op.Rig.ReadAsync(() => bct.Points.Select(p => (p.Easting, p.Northing)).ToList());
            int Nearest(double e, double n) => pts.Select((p, i) => (d: Math.Pow(p.Easting - e, 2) + Math.Pow(p.Northing - n, 2), i)).MinBy(x => x.d).i;
            int ia = Nearest(sw.E + 180, sw.N + 0.5), ib = Nearest(sw.E + 180, sw.N + 389);
            int lo = Math.Min(ia, ib), hi = Math.Max(ia, ib);
            var off = pts.Skip(lo).Take(hi - lo + 1).Select(p => DistanceToRing(p.Easting, p.Northing, ring)).Max();
            double runOn(int from, int to) { double l = 0; for (int i = from; i < to; i++) l += Math.Sqrt(Math.Pow(pts[i + 1].Easting - pts[i].Easting, 2) + Math.Pow(pts[i + 1].Northing - pts[i].Northing, 2)); return l; }
            op.S.Step(FlowOp.F($"the curve runs on {runOn(0, lo):0.0} m before A and {runOn(hi, pts.Count - 1):0.0} m after B (its ends extended past the fence)"));
            op.S.Step(FlowOp.F($"from A to B the farthest curve point is {off:0.00} m off the boundary"));
            if (off <= 0.5) op.S.Ok("from A to B it follows the boundary (within 0.5 m of it)");
            else op.S.Problem(Severity.Bug, "BND-CURVE-OFF-FENCE", "Bnd. Curve: the curve leaves the boundary between A and B (see the steps)");
            await ShownAsync(op, bc);
        }
        await op.Run.ShotAsync(op.S, op.Screen, "T7-boundary-lines.png");
    }

    /// <summary>The corners of a ring: points where its direction turns more than 5°.</summary>
    private static int Corners(List<(double E, double N)> ring)
    {
        int c = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[(i + ring.Count - 1) % ring.Count]; var b = ring[i]; var d = ring[(i + 1) % ring.Count];
            double h1 = Math.Atan2(b.E - a.E, b.N - a.N), h2 = Math.Atan2(d.E - b.E, d.N - b.N);
            double t = Math.Abs(((h2 - h1) * 180 / Math.PI + 540) % 360 - 180);
            if (t > 5) c++;
        }
        return c;
    }

    private static double Dist((double E, double N) a, (double E, double N) b) => Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));

    /// <summary>A boundary's sides: runs of pieces within 2° of one direction, from corner to corner.</summary>
    private static List<((double E, double N) A, (double E, double N) B, double Len)> Sides(List<(double E, double N)> ring)
    {
        var pts = new List<(double E, double N)>();
        foreach (var q in ring) if (pts.Count == 0 || Dist(pts[^1], q) > 0.01) pts.Add(q);
        if (pts.Count > 1 && Dist(pts[0], pts[^1]) <= 0.01) pts.RemoveAt(pts.Count - 1);
        int n = pts.Count;
        double Dir(int i) { var a = pts[i]; var b = pts[(i + 1) % n]; return Math.Atan2(b.E - a.E, b.N - a.N) * 180 / Math.PI; }
        bool Corner(int i) => Math.Abs(((Dir((i + n - 1) % n) - Dir(i)) % 360 + 540) % 360 - 180) >= 2;
        // Start at a corner, so no side is split across the ring's first point.
        int s0 = 0;
        for (int i = 0; i < n; i++) if (Corner(i)) { s0 = i; break; }
        var sides = new List<((double E, double N) A, (double E, double N) B, double Len)>();
        int start = s0;
        for (int k = 1; k <= n; k++)
        {
            int i = (s0 + k) % n;
            if (k < n && !Corner(i)) continue;
            sides.Add((pts[start], pts[i], Dist(pts[start], pts[i])));
            start = i;
        }
        return sides;
    }

    private static double PointToLine(double e, double n, (double E, double N) a, (double E, double N) b)
    {
        double dx = b.E - a.E, dn = b.N - a.N, len = Math.Sqrt(dx * dx + dn * dn);
        return Math.Abs((e - a.E) * dn - (n - a.N) * dx) / len;
    }

    private static double DistanceToRing(double e, double n, List<(double E, double N)> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double dx = b.E - a.E, dy = b.N - a.N, l2 = dx * dx + dy * dy;
            double u = l2 > 0 ? Math.Clamp(((e - a.E) * dx + (n - a.N) * dy) / l2, 0, 1) : 0;
            double qx = a.E + u * dx - e, qy = a.N + u * dy - n;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }

    // ------------------------------------------------------------------ T8

    private static async Task DrawOnMapAsync(FlowOp op)
    {
        await op.PlaceAsync(FlowFields.At(Slot, 120, 200), 0);
        var me = await op.PoseAsync();
        int before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=drawStraight]", "Draw: Straight")) return;
        if (!await op.TapMapAsync(me.E - 40, me.N - 30, "point A")) return;
        if (!await op.TapMapAsync(me.E - 40, me.N + 50, "point B")) return;
        if (await NewTrackAsync(op, before, "Draw: Straight") is var (info, t))
        {
            double da = TrackInfo.DistanceTo(t, me.E - 40, me.N - 30), db = TrackInfo.DistanceTo(t, me.E - 40, me.N + 50);
            op.S.Step(FlowOp.F($"the line is {da:0.00} and {db:0.00} m from the taps, at {info.HeadingDeg:0.00}°"));
            if (da < 0.5 && db < 0.5 && Math.Min(info.HeadingDeg, 360 - info.HeadingDeg) < 0.5)
                op.S.Ok("the line goes through the two taps, due north");
            else op.S.Problem(Severity.Bug, "DRAWN-LINE-OFF-TAPS", "Draw: Straight: the line does not go through the two taps (see the steps)");
            await ShownAsync(op, info);
        }

        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=drawCurve]", "Draw: Curve")) return;
        var taps = new[] { (-20.0, -30.0), (-10.0, 0.0), (5.0, 25.0), (25.0, 45.0) };
        foreach (var (de, dn) in taps)
            if (!await op.TapMapAsync(me.E + de, me.N + dn, "a curve point")) return;
        if (!await op.TapAsync("#draw-finish", "Finish")) return;
        if (await NewTrackAsync(op, before, "Draw: Curve") is var (ci, ct))
        {
            double worst = taps.Max(p => TrackInfo.DistanceTo(ct, me.E + p.Item1, me.N + p.Item2));
            op.S.Step(FlowOp.F($"the drawn curve has {ci.Points} points; the tap farthest from it is {worst:0.00} m off"));
            if (ci.Points >= taps.Length && worst < 0.5) op.S.Ok("a curve through the four taps");
            else op.S.Problem(Severity.Bug, "DRAWN-CURVE-OFF-TAPS", worst < 0.5 ? "Draw: Curve: the curve has fewer points than the four taps" : "Draw: Curve: the curve does not go through the four taps (see the steps)");
        }

        // Careless: in the middle of drawing, a tap on the bottom bar's Flags button (#399), then Cancel.
        before = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        int flags = await op.Rig.ReadAsync(() => op.Rig.Vm.Flags.Count);
        if (!await OpenQuickAbAsync(op)) return;
        if (!await op.TapAsync("[data-qab=drawStraight]", "Draw: Straight")) return;
        if (!await op.TapMapAsync(me.E + 30, me.N - 20, "point A")) return;
        int dots = await op.PageAsync<int>("() => drawPts.length");
        await op.TapAsync("#bn-flags", "the Flags button (by mistake)");
        await Task.Delay(300);
        int dots2 = await op.PageAsync<int>("() => drawPts.length");
        bool stillDrawing = await op.PageAsync<bool>("() => abFlow === 'straight'");
        op.S.Step($"after the stray tap: still drawing {stillDrawing}, the line's points {dots} → {dots2}");
        // A drawing the tap ended has cleared its points: only one that goes on can show a point
        // added here. One the tap ended by setting point B shows as a line made, below.
        if (stillDrawing)
        {
            if (dots2 <= dots) op.S.Ok("a tap on a button while drawing adds no point to the line");
            else op.S.Problem(Severity.Bug, "DRAW-TAP-THROUGH-BUTTON", "a tap on the Flags button while drawing went through to the map: it added a point to the line");
        }
        await op.Screen.EvalAsync<bool>("() => { bnFlags.classList.remove('open'); document.getElementById('bn-flags').classList.remove('menuopen'); return true; }");
        if (stillDrawing) await op.TapAsync("#draw-cancel", "Cancel");
        else op.S.Note("the stray tap ended the drawing by itself: Cancel was not needed");
        // Cancel goes from the page to the host loop: wait for both to end the drawing, then read them.
        await op.HostAsync(() => op.Rig.Vm.CurrentABCreationMode == AgOpenWeb.Models.ABCreationMode.None && op.Rig.Vm.PendingPointA == null, 2000);
        await op.SeeAsync("!abFlow && !document.getElementById('draw-toolbar').classList.contains('show')", 1000);
        int after = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Count);
        bool drawing = await op.PageAsync<bool>("() => !!abFlow || document.getElementById('draw-toolbar').classList.contains('show')");
        // The host too: a page that drops its drawing without telling the host leaves point A there,
        // and the next point sent would finish a line the operator never drew.
        var (mode, pendingA) = await op.Rig.ReadAsync(() => (op.Rig.Vm.CurrentABCreationMode, op.Rig.Vm.PendingPointA != null));
        bool hostDrawing = mode != AgOpenWeb.Models.ABCreationMode.None || pendingA;
        op.S.Step($"the screen still draws: {drawing}; the host's drawing mode {mode}, point A held {pendingA}");
        string how = stillDrawing ? "Cancel" : "the stray tap";
        // A tap that reaches the map is point B: the page ends its drawing and the host makes the line.
        if (!stillDrawing && after > before)
            op.S.Problem(Severity.Bug, "DRAW-TAP-THROUGH-BUTTON", "a tap on the Flags button while drawing went through to the map: it set point B and made a line");
        else if (after == before && !drawing && !hostDrawing)
            op.S.Ok($"after {how}: no line made, and the drawing ended on the screen and on the host");
        else if (after == before && !drawing)
            op.S.Problem(Severity.Inconsistency, "DRAW-ENDED-ON-SCREEN-ONLY", $"after {how}: the screen stopped drawing, but the host did not: it still waits for the line's next point", FindingKind.AppDefect);
        else op.S.Problem(Severity.Bug, "DRAW-CANCEL-LEAVES", $"after {how}: " + (after > before ? "a line was made" : "no line made") + (drawing ? ", and the screen still draws" : "") + (hostDrawing ? ", and the host still draws" : ""));
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.Flags.Count) != flags) op.S.Problem(Severity.Bug, "DRAW-STRAY-FLAG", "the stray tap placed a flag");
    }

    // ------------------------------------------------------------------ T9-T11: the Tracks manager

    /// <summary>Tap a track's row in the Tracks manager (the tap makes a visible track active, #148).</summary>
    internal static async Task<bool> PickRowAsync(FlowOp op, string name)
    {
        if (!await op.IsOpenAsync("dlg-tracks") && !await OpenTracksAsync(op)) return false;
        // A name just changed on the host reaches the list with the next Scene: give it a moment.
        string find = $"[...document.querySelectorAll('#trk-list .trk-row .trk-name')].findIndex(r => r.textContent === {JsonSerializer.Serialize(name)})";
        await op.SeeAsync(find + " >= 0", 2000);
        int i = await op.PageAsync<int>("() => " + find);
        if (i < 0) { op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"'{name}' is not in the Tracks manager: {string.Join(" | ", await op.RowsAsync("#trk-list .trk-row"))}")); return false; }
        return await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-name"), $"the row '{name}'");
    }

    internal static async Task<string?> RenameAsync(FlowOp op, string from, string to, string then)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (!await PickRowAsync(op, from)) return null;
            double p0 = await op.PageAsync<double>("() => { window.__sent = []; return performance.now(); }");
            long t0 = ModuleListener.NowMs;
            if (!await op.TapAsync("#trk-rename", "Rename")) return null;
            if (!await op.SeeAsync("document.activeElement && document.activeElement.classList.contains('flg-nameedit')", 1500))
            { op.S.Problem(Severity.Bug, "RENAME-BOX-MISSING", "Rename: no name box opened in the row"); return null; }
            await op.Screen.Page.Keyboard.PressAsync("Control+A");
            await op.Screen.Page.Keyboard.PressAsync("Delete");
            if (to.Length > 0) await op.Screen.Page.Keyboard.TypeAsync(to, new() { Delay = FlowOp.KeyDelayMs });
            // What the box holds as the operator finishes: a box that closed under the typing
            // (a list redrawn, focus taken) shows here, not as a mystery later.
            string box = await op.PageAsync<string>("() => { const a = document.activeElement; return a && a.classList.contains('flg-nameedit') ? 'the box holds [' + a.value + ']' : 'the box is gone, focus on ' + (a ? a.tagName.toLowerCase() + (a.id ? '#' + a.id : '') : 'nothing'); }");
            op.S.Step($"type '{to}' ({box})");
            if (then == "tap") await op.TapAsync("#dlg-tracks .dlg-title", "the dialog's title (leaving the box)");
            else await op.Screen.Page.Keyboard.PressAsync(then);
            await op.HostAsync(() => op.Rig.Vm.SavedTracks.Any(t => t.Name == to.Trim()), 2000);
            var got = await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.FirstOrDefault(t => t.Name == to.Trim())?.Name);
            if (got != null || to.Trim().Length == 0 || attempt == 2) return got;
            // Once more, as an operator would when the name didn't take; the first try stays in the steps.
            var sent = await op.PageAsync<string[]>("() => window.__sent || []");
            // Was the page the operator all along? A presence that came late drops its commands (the deadman).
            var control = op.Rig.ControlLog.Where(c => c.Ms >= t0).Select(c => FlowOp.F($"{c.Ms - t0} ms {(c.Held ? "taken" : "ended")} ({c.Reason})")).ToList();
            if (control.Count > 0)
            {
                // Did the page stop sending its presence, or did the host stop reading it?
                var pres = await op.PageAsync<double[]>($"() => (window.__presence || []).filter(t => t >= {FlowOp.F($"{p0 - 1000:0}")})");
                double gap = 0, at = 0;
                for (int k = 1; k < pres.Length; k++) if (pres[k] - pres[k - 1] > gap) { gap = pres[k] - pres[k - 1]; at = pres[k - 1] - p0; }
                var longs = await op.PageAsync<double[][]>($"() => (window.__long || []).filter(e => e[0] + e[1] >= {FlowOp.F($"{p0 - 1000:0}")})");
                op.S.Step(FlowOp.F($"the page sent its presence {pres.Length} times from 1 s before the tap, the longest gap {gap:0} ms (from {at:0} ms); long tasks: {(longs.Length == 0 ? "none" : string.Join(", ", longs.Select(e => $"{e[1]:0} ms at {e[0] - p0:0} ms")))}"));
            }
            op.S.Step($"the name didn't take (the page sent {(sent.Length == 0 ? "nothing" : string.Join(" / ", sent))}; control {(control.Count == 0 ? "held throughout" : string.Join(", ", control))}; the host has {string.Join(", ", (await op.TracksAsync()).Select(x => $"'{x.Name}'"))}; said {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())}); again");
            if (await op.Rig.ReadAsync(() => op.Rig.Vm.SavedTracks.Any(t => t.Name == from)) == false) return null;
        }
    }

    private static async Task RenameAsync(FlowOp op)
    {
        var tracks = await op.TracksAsync();
        if (tracks.Count < 3) { op.S.Note("skipped: fewer than three tracks to work on (T1-T8 make them)"); return; }
        string a = tracks[0].Name, b = tracks[1].Name;

        string n1 = "North pass";
        if (await RenameAsync(op, a, n1, "Enter") == n1) op.S.Ok(Masked($"'{a}' renamed '{n1}' with Enter"));
        else op.S.Problem(Severity.Bug, "RENAME-NOT-SAVED", Masked($"'{a}' → '{n1}' with Enter: the host has {string.Join(", ", (await op.TracksAsync()).Select(x => x.Name))}"));
        if (await op.SeeAsync($"[...document.querySelectorAll('#trk-list .trk-name')].some(r => r.textContent === '{n1}')", 2000)) op.S.Ok("the Tracks manager shows the new name");
        else op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"the Tracks manager doesn't show '{n1}': {string.Join(" | ", await op.RowsAsync("#trk-list .trk-row"))}"));

        string n2 = "Linha do Poço, à direita";
        if (await RenameAsync(op, n1, n2, "tap") == n2) op.S.Ok($"renamed '{n2}' (a comma and accents) by tapping away from the box");
        else op.S.Problem(Severity.Bug, "RENAME-NOT-SAVED", Masked($"'{n1}' → '{n2}' by tapping away: the host has {string.Join(", ", (await op.TracksAsync()).Select(x => x.Name))}"));

        // Careless: the name emptied, then Enter.
        await op.ToldAsync(); op.HostSaid();
        await RenameAsync(op, n2, "", "Enter");
        var now = await op.TracksAsync();
        if (now.Any(t => t.Name == n2)) op.S.Ok("an emptied name keeps the old one");
        else op.S.Problem(Severity.Bug, "RENAME-EMPTY-ACCEPTED", Masked($"an emptied name: the track is now called {string.Join(", ", now.Select(x => "'" + x.Name + "'"))}"));

        // Careless: another track's name.
        await op.ToldAsync(); op.HostSaid();
        await RenameAsync(op, n2, b, "Enter");
        // The name exists already, so the host has it at once: the refusal comes a moment later.
        var told = await op.ToldAsync();
        for (int k = 0; k < 20 && told.Count == 0; k++) { await Task.Delay(100); told.AddRange(await op.ToldAsync()); }
        now = await op.TracksAsync();
        int same = now.Count(t => t.Name == b);
        if (same == 1 && told.Count > 0) op.S.Ok($"another track's name is refused, and the operator is told {Said(told, op.HostSaid())}");
        else if (same == 1) op.S.Problem(Severity.Improvement, "RENAME-DUPLICATE-SILENT", $"another track's name is refused, but nothing is shown", FindingKind.Enhancement);
        else op.S.Problem(Severity.Bug, "TRACK-NAMES-REPEATED", Masked($"two tracks are now called '{b}'"));
        // The rename sticks across Field Builder too (the same list, another screen).
        if (!await op.OpenMenuAsync("ln-fieldtools", "fieldtools", "Field Tools")) return;
        if (!await op.TapAsync("#ft-fieldbuilder", "Field Builder")) return;
        if (await op.SeeAsync($"[...document.querySelectorAll('#fb-tracklist .fb-tname')].some(r => r.textContent === {JsonSerializer.Serialize(n2)})", 2000))
            op.S.Ok("Field Builder › Tracks shows the renamed track");
        else op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"Field Builder › Tracks doesn't show '{n2}': {string.Join(" | ", await op.RowsAsync("#fb-tracklist .fb-trkrow"))}"));
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
    }

    private static async Task HideAsync(FlowOp op)
    {
        var tracks = await op.TracksAsync();
        var hideMe = tracks.FirstOrDefault(t => !t.Active && t.Type == TrackType.ABLine);
        if (hideMe == null) { op.S.Note("skipped: no inactive AB line to hide (T1-T8 make them)"); return; }
        if (!await OpenTracksAsync(op)) return;
        int i = await op.PageAsync<int>($"() => [...document.querySelectorAll('#trk-list .trk-row .trk-name')].findIndex(r => r.textContent === {JsonSerializer.Serialize(hideMe.Name)})");
        if (i < 0) { op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"'{hideMe.Name}' is not in the Tracks manager")); return; }
        if (!await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-vis"), $"the tick of '{hideMe.Name}'")) return;
        if (await op.HostAsync(() => !op.Rig.Vm.SavedTracks[hideMe.Index].IsVisible, 2000)) op.S.Ok(Masked($"'{hideMe.Name}' hidden"));
        else { op.S.Problem(Severity.Bug, "HIDE-NOT-APPLIED", Masked($"the tick of '{hideMe.Name}' taken off: the host still shows it")); return; }
        if (await op.SeeAsync($"(scene.trackList || []).some(t => t.name === {JsonSerializer.Serialize(hideMe.Name)} && !t.visible)", 2000)) op.S.Ok("the screen's list has it hidden");
        else op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", "the screen's list still has it shown");
        // Its row tapped: not made active, and the operator is told why (#695).
        await op.ToldAsync(); op.HostSaid();
        await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-name"), $"the row of hidden '{hideMe.Name}'");
        await Task.Delay(500);
        bool active = await op.Rig.ReadAsync(() => ReferenceEquals(op.Rig.Vm.SelectedTrack, op.Rig.Vm.SavedTracks[hideMe.Index]));
        var told = await op.ToldAsync();
        if (!active && told.Count > 0) op.S.Ok($"a hidden track's row doesn't make it active; the operator is told {Said(told, op.HostSaid())}");
        else if (active) op.S.Problem(Severity.Bug, "HIDDEN-TRACK-ACTIVATED", Masked($"tapping hidden '{hideMe.Name}' made it the active line"));
        else op.S.Problem(Severity.Improvement, "HIDDEN-TRACK-TAP-SILENT", "tapping a hidden track does nothing, and says nothing", FindingKind.Enhancement);
        // Shown again.
        await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-vis"), $"the tick of '{hideMe.Name}' again");
        if (await op.HostAsync(() => op.Rig.Vm.SavedTracks[hideMe.Index].IsVisible, 2000)) op.S.Ok("ticked again, it is shown");
        else op.S.Problem(Severity.Bug, "HIDE-NOT-APPLIED", "ticked again, the host keeps it hidden");

        // Careless: the line AutoSteer is steering on, hidden at 8 km/h.
        await op.CloseDialogAsync();
        if (!await SteerOnActiveAsync(op)) return;
        var act = (await op.TracksAsync()).First(t => t.Active);
        if (!await OpenTracksAsync(op)) return;
        i = await op.PageAsync<int>($"() => [...document.querySelectorAll('#trk-list .trk-row .trk-name')].findIndex(r => r.textContent === {JsonSerializer.Serialize(act.Name)})");
        await op.ToldAsync(); op.HostSaid();
        long t0 = ModuleListener.NowMs;
        if (!await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-vis"), $"the tick of the active line '{act.Name}'")) return;
        var off = await op.Run.WaitModuleAsync(m => !m.Steering, t0, 3000);
        var told2 = await op.ToldAsync();
        if (off != null)
        {
            op.S.Step($"the module gets AutoSteer off {off - t0} ms after the tick is taken off");
            op.S.Ok($"the line AutoSteer was on, hidden: the module gets AutoSteer off; the operator is told {Said(told2, op.HostSaid())}");
        }
        else
        {
            op.S.Step($"3 s later the module gets {op.Rig.Module.Last}");
            op.S.Problem(Severity.Safety, "HIDDEN-LINE-STILL-STEERED", $"the active line hidden at 8 km/h: the module still gets AutoSteer on 3 s later (status {op.Rig.Module.Last?.Status})");
        }
        await op.StopAsync();
        await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-vis"), $"the tick of '{act.Name}' again");
        await op.CloseDialogAsync();
    }

    /// <summary>The tractor on the active line at 8 km/h, AutoSteer engaged from the right nav.</summary>
    private static async Task<bool> SteerOnActiveAsync(FlowOp op)
    {
        var tr = await op.Rig.ReadAsync(() => op.Rig.Vm.SelectedTrack);
        if (tr == null || !tr.IsVisible || tr.Type != TrackType.ABLine)
        {
            // Make the first visible AB line the active one from its row.
            var ab = (await op.TracksAsync()).FirstOrDefault(t => t.Visible && t.Type == TrackType.ABLine);
            if (ab == null) { op.S.Note("skipped: no AB line to steer on (T1-T8 make them)"); return false; }
            if (!await PickRowAsync(op, ab.Name)) return false;
            await op.CloseDialogAsync();
            tr = await op.Rig.ReadAsync(() => op.Rig.Vm.SelectedTrack);
            if (tr == null) { op.S.Harness("no active line after tapping its row"); return false; }
        }
        // Stand on the line, facing along it, inside the field: first at the field's middle, where
        // the tractor's place in the plane is read, then square across from there onto the line.
        var a = tr.Points[0]; var b = tr.Points[^1];
        double h = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        var here = FlowFields.At(Slot, 120, 200);
        await op.PlaceAsync(here, h * 180 / Math.PI);
        var me = await op.PoseAsync();
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
        double u = ((me.E - a.Easting) * dx + (me.N - a.Northing) * dy) / l2;
        double pe = a.Easting + u * dx, pn = a.Northing + u * dy;
        const double R = 6371000.0;
        var to = (here.Lat + (pn - me.N) / R * 180 / Math.PI, here.Lon + (pe - me.E) / (R * Math.Cos(here.Lat * Math.PI / 180)) * 180 / Math.PI);
        await op.PlaceAsync(to, h * 180 / Math.PI);
        var on0 = await op.PoseAsync();
        op.S.Step(FlowOp.F($"the tractor put on '{tr.Name}': {TrackInfo.DistanceTo(tr, on0.E, on0.N):0.00} m off it, facing {on0.HeadingDeg:0.0}° (the line {(h * 180 / Math.PI + 360) % 360:0.0}°)"));
        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(800);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-steer", () => op.Rig.Vm.IsAutoSteerEngaged))
        { op.S.Harness(Masked($"AutoSteer did not engage on '{tr.Name}'")); return false; }
        var on = await op.Run.WaitModuleAsync(m => m.Steering, ModuleListener.NowMs, 3000);
        if (on == null) { op.S.Harness("the module never got AutoSteer on"); return false; }
        op.S.Step($"steering on '{tr.Name}' at 8 km/h; the module gets {op.Rig.Module.Last}");
        return true;
    }

    private static async Task DeleteSwapAsync(FlowOp op)
    {
        var tracks = await op.TracksAsync();
        var ab = tracks.FirstOrDefault(t => t.Type == TrackType.ABLine && t.Visible && !t.Active);
        if (ab == null) { op.S.Note("skipped: no visible AB line that isn't active, to swap (T1-T8 make them)"); return; }
        if (!await PickRowAsync(op, ab.Name)) return;
        double h0 = (await op.TracksAsync()).First(t => t.Name == ab.Name).HeadingDeg;
        if (!await op.TapAsync("#trk-swap", "Swap A/B")) return;
        // The swap goes from the page to the host loop: wait for the host to have it turned round.
        await op.HostAsync(() => op.Rig.Vm.SavedTracks.FirstOrDefault(x => x.Name == ab.Name) is { } line && line.Points.Count >= 2
            && Apart(TrackInfo.Of(0, line, null).HeadingDeg, h0) > 179.5, 2000);
        double h1 = (await op.TracksAsync()).First(t => t.Name == ab.Name).HeadingDeg;
        op.S.Step(FlowOp.F($"'{ab.Name}' ran at {h0:0.0}°, now at {h1:0.0}°"));
        if (Math.Abs(Apart(h1, h0) - 180) < 0.5) op.S.Ok(Masked($"Swap A/B turned '{ab.Name}' round"));
        else op.S.Problem(Severity.Bug, "SWAP-NOT-APPLIED", Masked($"Swap A/B: '{ab.Name}' was not turned round (see the steps)"));

        // Delete, Cancel: kept. Delete, Confirm: gone.
        var victim = (await op.TracksAsync()).Last(t => !t.Active).Name;
        int n0 = (await op.TracksAsync()).Count;
        if (!await PickRowAsync(op, victim)) return;
        if (!await op.TapAsync("#trk-delete", "Delete")) return;
        if (!await op.ConfirmAsync(false, "Delete")) return;
        await Task.Delay(400);
        if ((await op.TracksAsync()).Count == n0) op.S.Ok("Delete, Cancel: the track is kept");
        else op.S.Problem(Severity.Bug, "DELETE-CANCEL-DELETED", "Delete then Cancel deleted the track");
        bool back = await op.IsOpenAsync("dlg-tracks");
        op.S.Step($"after Cancel the Tracks manager is {(back ? "open again" : "closed")}");
        if (!await PickRowAsync(op, victim)) return;
        if (!await op.TapAsync("#trk-delete", "Delete")) return;
        if (!await op.ConfirmAsync(true, "Delete")) return;
        if (await op.HostAsync(() => !op.Rig.Vm.SavedTracks.Any(t => t.Name == victim), 2000)) op.S.Ok(Masked($"Delete, Confirm: '{victim}' is gone"));
        else op.S.Problem(Severity.Bug, "DELETE-NOT-APPLIED", Masked($"Delete, Confirm: '{victim}' is still there"));
        if (await op.SeeAsync($"!(scene.trackList || []).some(t => t.name === {JsonSerializer.Serialize(victim)})", 2000)) op.S.Ok("and gone from the screen's list");
        else op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"'{victim}' is still on the screen's list"));

        // Careless: the line AutoSteer is on, deleted at 8 km/h.
        await op.CloseDialogAsync();
        if (!await SteerOnActiveAsync(op)) return;
        var act = (await op.TracksAsync()).First(t => t.Active);
        if (!await OpenTracksAsync(op)) return;
        // The row tap would make it active anyway; it is.
        if (!await PickRowAsync(op, act.Name)) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await op.TapAsync("#trk-delete", "Delete")) return;
        long t0 = ModuleListener.NowMs;
        if (!await op.ConfirmAsync(true, "Delete the active line")) return;
        var off = await op.Run.WaitModuleAsync(m => !m.Steering, t0, 3000);
        var told = await op.ToldAsync();
        if (off != null)
        {
            op.S.Step($"the module gets AutoSteer off {off - t0} ms after Confirm");
            op.S.Ok($"the active line deleted at 8 km/h: the module gets AutoSteer off; the operator is told {Said(told, op.HostSaid())}");
        }
        else
        {
            op.S.Step($"3 s later the module gets {op.Rig.Module.Last}");
            op.S.Problem(Severity.Safety, "DELETED-LINE-STILL-STEERED", $"the active line deleted at 8 km/h: the module still gets AutoSteer on 3 s later (status {op.Rig.Module.Last?.Status})");
        }
        await op.StopAsync();
        await op.CloseDialogAsync();
    }

    // ------------------------------------------------------------------ T12

    private static async Task ReopenAsync(FlowOp op)
    {
        // One track hidden and one active, so both are read back.
        var before = await op.TracksAsync();
        if (before.Count < 2) { op.S.Note("skipped: fewer than two tracks to compare (T1-T8 make them)"); return; }
        var hide = before.First(t => !t.Active);
        if (!await OpenTracksAsync(op)) return;
        int i = await op.PageAsync<int>($"() => [...document.querySelectorAll('#trk-list .trk-row .trk-name')].findIndex(r => r.textContent === {JsonSerializer.Serialize(hide.Name)})");
        await op.TapAsync(FlowOp.F($"#trk-list .trk-row:nth-of-type({i + 1}) .trk-vis"), $"the tick of '{hide.Name}'");
        var keepActive = before.FirstOrDefault(t => t.Visible && t.Name != hide.Name && t.Type == TrackType.ABLine);
        if (keepActive != null) await PickRowAsync(op, keepActive.Name);
        await op.CloseDialogAsync();
        await Task.Delay(500);
        before = await op.TracksAsync();
        op.S.Step("before: " + string.Join("; ", before.Select(t => t.ToString())));

        if (!await FlowFields.CloseFieldAsync(op, "discard")) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close did not close the field"); return; }
        if (await op.SeeAsync("scene && !scene.hasField", 3000)) op.S.Ok("the screen shows no field");
        else op.S.Problem(Severity.Inconsistency, "FIELD-STILL-SHOWN", "the field is closed but the screen still shows it");
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        await Task.Delay(800);
        var after = await op.TracksAsync();
        op.S.Step("after: " + string.Join("; ", after.Select(t => t.ToString())));
        string Key(TrackInfo t) => FlowOp.F($"{t.Name}|{t.Type}|{t.Visible}|{t.Points}|{Math.Round(t.HeadingDeg, 1) % 360:0.0}");
        var lost = before.Select(Key).Except(after.Select(Key)).ToList();
        var gained = after.Select(Key).Except(before.Select(Key)).ToList();
        if (lost.Count == 0 && gained.Count == 0) op.S.Ok($"all {after.Count} tracks came back the same (names, types, ticks, headings)");
        else op.S.Problem(Severity.Bug, "TRACKS-CHANGED-ON-REOPEN", Masked($"closed and opened again: lost {string.Join(", ", lost)}; new or changed {string.Join(", ", gained)}"));
        string? wasActive = before.FirstOrDefault(t => t.Active)?.Name, isActive = after.FirstOrDefault(t => t.Active)?.Name;
        if (wasActive == isActive) op.S.Ok(Masked($"the active line is the same: '{isActive ?? "none"}'"));
        else op.S.Problem(Severity.Bug, "ACTIVE-TRACK-CHANGED-ON-REOPEN", Masked($"active line was '{wasActive ?? "none"}', after opening again '{isActive ?? "none"}'"));
        var screen = await op.ScreenTracksAsync();
        if (screen.Count == after.Count && screen.Zip(after).All(z => z.First.Name == z.Second.Name && z.First.Visible == z.Second.Visible))
            op.S.Ok("the screen's list matches the host's");
        else op.S.Problem(Severity.Inconsistency, "TRACK-LIST-STALE", Masked($"the screen lists {string.Join(", ", screen.Select(x => x.Name))}"));
        await op.Run.ShotAsync(op.S, op.Screen, "T12-reopened.png");
    }
}
