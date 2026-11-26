// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// U: the U-turn and lateral buttons the operator's way, with AutoSteer on an A+ line in a field
/// with a 16 m headland (made in Field Builder › Headland): the automatic U-turn switched on from
/// the right-hand bar and taken at the headland; the on-screen manual U-turn and lateral buttons
/// (hidden without AutoSteer); the skip-rows button. Where the tractor ends up is read on the host
/// (its pose and cross-track error) and what steers is read at the module (PGN 254 status).
/// The careless operator taps the lateral and U-turn buttons again in the middle of a turn.
/// </summary>
public static class UTurnFlows
{
    public const string Group = "U: U-turn and lateral buttons";
    private const int Slot = 7;
    private const string Field = "Flows U-turns";
    private const double Pass = 8.0; // the 8 m boom, no overlap

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "U1", "The automatic U-turn at the headland", "Careful operator on the A+ line at 10 km/h with AutoSteer: the right-hand bar's U-Turn button, then on to the north headland.", AutoTurnAsync);
        await Flow(run, "U2", "The on-screen manual U-turn", "Without AutoSteer the on-screen U-turn buttons are hidden; with AutoSteer at 8 km/h, U-turn right. Careless: the lateral and the U-turn buttons tapped again during the turn.", ManualTurnAsync);
        await Flow(run, "U3", "The lateral buttons", "AutoSteer at 8 km/h: Lateral right, then Lateral left.", LateralAsync);
        await Flow(run, "U4", "Skip rows", "Bottom bar: the skip-rows button tapped once (1); with AutoSteer at 8 km/h, U-turn right; the skip-rows button tapped round back to 0.", SkipAsync);
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            if (!await FlowFields.EnsureOpenAsync(op, Slot, Field)) { s.Harness("the U-turn field could not be opened"); return; }
            if (!await HeadlandAsync(op)) return;
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
            await op.Rig.RunAsync(() =>
            {
                var vm = op.Rig.Vm;
                if (vm.IsAutoSteerEngaged) vm.ToggleAutoSteerCommand?.Execute("scenario end");
                if (vm.IsYouTurnEnabled) vm.ToggleYouTurnCommand?.Execute(null);
                vm.SimulatorSpeedKph = 0;
            });
        }, onTheRunsLine: false);

    /// <summary>A 16 m headland round the whole boundary, made in Field Builder the first time.</summary>
    private static async Task<bool> HeadlandAsync(FlowOp op)
    {
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.HasHeadland)) return true;
        if (!await op.OpenMenuAsync("ln-fieldtools", "fieldtools", "Field Tools")) return false;
        if (!await op.TapAsync("#ft-fieldbuilder", "Field Builder")) return false;
        if (!await op.WaitOpenAsync("fieldbuilder")) { op.S.Harness("Field Builder did not open"); return false; }
        if (!await op.TapAsync("#fieldbuilder .fb-tab[data-fbtab=headland]", "the Headland tab")) return false;
        if (!await op.TapAsync("#fb-hl-add", "+ Add")) return false;
        if (!await op.SelectAsync("#fb-hl-tw", "2 tool widths", "Inset")) return false;
        if (!await op.TapAsync("#fb-addhl [data-fbhl=whole]", "Whole Boundary")) return false;
        if (!await op.HostAsync(() => op.Rig.Vm.HasHeadland && op.Rig.Vm.IsHeadlandOn, 4000))
        {
            if (await op.Rig.ReadAsync(() => op.Rig.Vm.HasHeadland)) await op.TapAsync("#bn-headland", "Headland on");
            if (!await op.HostAsync(() => op.Rig.Vm.HasHeadland && op.Rig.Vm.IsHeadlandOn, 2000)) { op.S.Harness("no headland"); return false; }
        }
        await op.CloseAllAsync();
        op.S.Step("a 16 m headland round the boundary");
        return true;
    }

    private static Task<bool> ExecutingAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.State.YouTurn.IsExecuting);

    /// <summary>What the host showed while a turn was awaited, read every 100 ms: the zones the U-turn
    /// reported, in order; whether it planned a turn; whether AutoSteer went off. A zone counts only
    /// when the U-turn ran since the last reading (its counter moved): without AutoSteer or a headland
    /// line it does not run, and the zone it holds is an old one.</summary>
    private sealed class Approach
    {
        public bool Live, InField, InHeadland, LeftField, Planned, SteerOff;
        public long Ms; // how long it was watched
        public readonly List<string> Zones = new();
    }

    /// <summary>Waits up to <paramref name="ms"/> for a turn to start, reading the host every 100 ms
    /// into <paramref name="way"/>. Gives up once AutoSteer is off or the tractor has left the field,
    /// as no turn starts after that, and stops the tractor there.</summary>
    private static async Task<bool> StartAsync(FlowOp op, int ms, Approach way)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int? counter = null;
        TractorZone? last = null;
        while (true)
        {
            var (executing, zone, n, steering, planned) = await op.Rig.ReadAsync(() =>
            {
                var y = op.Rig.State.YouTurn;
                return (y.IsExecuting, y.CurrentZone, y.YouTurnCounter, op.Rig.Vm.IsAutoSteerEngaged, y.TurnPath != null);
            });
            way.Ms = sw.ElapsedMilliseconds;
            if (executing) return true;
            // Without AutoSteer the U-turn does not run (and a planned turn is dropped): none starts.
            if (!steering) { way.SteerOff = true; break; }
            way.Planned |= planned;
            if (counter is int c && c != n)
            {
                way.Live = true;
                if (zone != last) way.Zones.Add(FlowOp.F($"{zone} at {way.Ms / 1000.0:0.0} s"));
                last = zone;
                if (zone == TractorZone.InCultivatedArea) way.InField = true;
                else if (zone == TractorZone.InHeadland) way.InHeadland = true;
                else if (way.InField || way.InHeadland) { way.LeftField = true; break; }
            }
            counter = n;
            if (way.Ms >= ms) return false;
            await Task.Delay(100);
        }
        await op.StopAsync();
        return false;
    }

    /// <summary>Why no automatic turn started, from what the host showed on the way: one wording per cause.</summary>
    private static string NoTurnWhy(Approach way) =>
        way.SteerOff ? (way.InHeadland ? "AutoSteer went off in the headland, before the turn" : "AutoSteer went off before the headland")
        : !way.Live ? "the U-turn's headland check never ran"
        : !way.InField && !way.InHeadland ? "the U-turn never had the tractor in the field"
        : way.LeftField && !way.InHeadland ? "the tractor drove out of the field without the U-turn seeing the headland"
        : way.LeftField ? (way.Planned ? "a turn was planned, but the tractor drove through the headland and out of the field"
                                       : "no turn was planned, and the tractor drove through the headland and out of the field")
        : way.InHeadland ? (way.Planned ? "a turn was planned, but it did not start in the headland"
                                        : "no turn was planned, and the tractor drove into the headland")
        : "the tractor never reached the headland";

    /// <summary>Waits for a turn to start and end; what the host showed until it started goes in
    /// <paramref name="way"/>. Gives when it started and ended (harness ms), so the module's AutoSteer
    /// status can be read over the turn and the settle after it, and the side the screen's U-turn
    /// arrow showed when it started (true: left).</summary>
    private static async Task<(bool Started, bool Ended, long StartMs, long EndMs, bool Left)> TurnAsync(FlowOp op, int startMs, int endMs, Approach? way = null)
    {
        bool started = await StartAsync(op, startMs, way ?? new Approach());
        long ts = ModuleListener.NowMs;
        if (!started) return (false, false, ts, ts, false);
        // The arrow's side as SceneProjector sends it to the screen.
        bool left = await op.Rig.ReadAsync(() => op.Rig.State.YouTurn.NextUTurnDirectionLeftOverride ?? op.Rig.State.YouTurn.IsTurnLeft);
        bool ended = await op.HostAsync(() => !op.Rig.State.YouTurn.IsExecuting, endMs);
        return (true, ended, ts, ModuleListener.NowMs, left);
    }

    /// <summary>AutoSteer at the module (the PGN 254 status) from the turn's start to now (the settle
    /// on the next pass done), and on the host now. A drop anywhere in that time is one finding: the
    /// harness sees the turn's end a poll or so after the cycle that ended it, so a drop at the
    /// hand-back falls either side of it. When it came goes in a step.</summary>
    private static async Task SteerKeptAsync(FlowOp op, long startMs, long endMs, string what)
    {
        var off = op.Rig.Module.First(startMs, m => !m.Steering);
        bool engaged = await op.Rig.ReadAsync(() => op.Rig.Vm.IsAutoSteerEngaged);
        if (off == null && engaged) { op.S.Ok($"AutoSteer stayed on at the module through {what} and on the next pass"); return; }
        if (off is { } o)
        {
            string phase = Math.Abs(o.Ms - endMs) <= 500 ? "at the hand-back" : o.Ms < endMs ? "during the turn" : "on the next pass";
            op.S.Step(FlowOp.F($"AutoSteer off at the module {(o.Ms - startMs) / 1000.0:0.0} s after the start of {what}, {phase} (the turn ended at {(endMs - startMs) / 1000.0:0.0} s)"));
        }
        else op.S.Step("AutoSteer on at the module, but off on the host");
        op.S.Problem(Severity.Bug, "TURN-STEER-DROPPED", $"AutoSteer went off between the start of {what} and the settle on the next pass");
    }

    /// <summary>How far right of the line a point is, square to it (the line's own heading, which
    /// Quick AB takes from the tractor and so is a degree or two off north).</summary>
    private static double Across(Track line, Pose p)
    {
        double h = line.Heading;
        return (p.E - line.Points[0].Easting) * Math.Cos(h) - (p.N - line.Points[0].Northing) * Math.Sin(h);
    }

    private static double LineDeg(Track line) => (line.Heading * 180 / Math.PI + 360) % 360;

    /// <summary>After <paramref name="ms"/>: how far the tractor is from the pass it was on (square to
    /// the line, + right of it facing A→B), its heading and the cross-track error the guidance reads.</summary>
    private static async Task<(double Across, double Heading, double Xte)> SettleAsync(FlowOp op, Track line, Pose before, int ms)
    {
        await Task.Delay(ms);
        var p = await op.PoseAsync();
        double xte = await op.Rig.ReadAsync(() => op.Rig.State.Guidance.CrossTrackError);
        return (Across(line, p) - Across(line, before), p.HeadingDeg, xte);
    }

    /// <summary>Where the tractor is once AutoSteer holds it on the pass (within 0.1 m), so a turn or
    /// a lateral is measured from the pass, not from where the tractor was put.</summary>
    private static async Task<Pose> OnPassAsync(FlowOp op)
    {
        await Task.Delay(1500);
        if (!await op.HostAsync(() => Math.Abs(op.Rig.State.Guidance.CrossTrackError) <= 0.1, 8000))
            op.S.Step(FlowOp.F($"AutoSteer has the tractor {await op.Rig.ReadAsync(() => op.Rig.State.Guidance.CrossTrackError):0.00} m off the pass after 9.5 s"));
        return await op.PoseAsync();
    }

    private static double HeadingOff(double h, double want) => Math.Abs(((h - want + 540) % 360) - 180);

    /// <summary>What is wrong with where the tractor is, in words that stay the same from night to
    /// night (the measured numbers go in a step); empty when it is within 0.5 m of the pass
    /// <paramref name="wantAcross"/> m over (+ = right of A→B) and within 0.3 m of the line.</summary>
    private static List<string> Misses(double across, double wantAcross, double xte, string from)
    {
        var wrong = new List<string>();
        if (Math.Abs(across - wantAcross) > 0.5)
        {
            string side = wantAcross >= 0 ? "right" : "left", other = wantAcross >= 0 ? "left" : "right";
            if (wantAcross == 0) wrong.Add($"not back on {from} (more than 0.5 m from it)");
            else if (Math.Abs(across) <= 0.5) wrong.Add($"still on {from}");
            else if (Math.Abs(across + wantAcross) <= 0.5) wrong.Add(FlowOp.F($"on the pass {Math.Abs(wantAcross):0} m {other} of {from}, the wrong side"));
            else wrong.Add(FlowOp.F($"not on the pass {Math.Abs(wantAcross):0} m {side} of {from} (more than 0.5 m from it)"));
        }
        if (Math.Abs(xte) > 0.3) wrong.Add("more than 0.3 m off the line");
        return wrong;
    }

    private static void Landed(FlowOp op, Track line, (double Across, double Heading, double Xte) r, double wantAcross, string what)
    {
        double wantHeading = (LineDeg(line) + 180) % 360; // back the other way
        string where = FlowOp.F($"{r.Across:+0.0;-0.0} m from the pass it left (+ = right of A→B), heading {r.Heading:0.0}°, {r.Xte:0.00} m off the line");
        var wrong = Misses(r.Across, wantAcross, r.Xte, "the pass it left");
        if (HeadingOff(r.Heading, wantHeading) > 3) wrong.Add("heading more than 3° off the way back along the line");
        op.S.Step(FlowOp.F($"{what}: {where}; the next pass is {wantAcross:+0.0;-0.0} m, heading {wantHeading:0.0}°"));
        if (wrong.Count == 0) op.S.Ok(FlowOp.F($"{what}: on the pass {wantAcross:+0;-0} m from the one it left, heading back along the line"));
        else op.S.Problem(Severity.Bug, "TURN-LANDED-WRONG", $"{what}: {string.Join("; ", wrong)}");
    }

    // ------------------------------------------------------------------ U1

    private static async Task AutoTurnAsync(FlowOp op)
    {
        var line = await FlowFields.NorthLineAsync(op, Slot, 120);
        if (line == null) return;
        if (!await FlowFields.SteerOnPassAsync(op, Slot, line, 300, 0, 10)) return;
        var before = await OnPassAsync(op);
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-youturn", () => op.Rig.Vm.IsYouTurnEnabled))
        { op.S.Problem(Severity.Bug, "YOUTURN-BUTTON-DEAD", "the right-hand bar's U-Turn button did not switch the automatic U-turn on"); return; }
        op.S.Ok("automatic U-turn on");
        var way = new Approach();
        var (started, ended, ts, te, left) = await TurnAsync(op, 45000, 45000, way);
        if (!started)
        {
            var p = await op.PoseAsync();
            string zones = way.Zones.Count > 0 ? string.Join(" → ", way.Zones) : "none";
            op.S.Step(FlowOp.F($"no turn after {way.Ms / 1000.0:0.0} s: the tractor at {p}, AutoSteer {(way.SteerOff ? "off" : "on")}; the U-turn's zones: {zones}; a turn planned: {(way.Planned ? "yes" : "no")}")
                + $"; told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())}");
            op.S.Problem(Severity.Bug, "AUTO-TURN-NOT-TAKEN", $"no U-turn at the north headland: {NoTurnWhy(way)}");
            return;
        }
        if (!ended) { op.S.Problem(Severity.Bug, "TURN-NEVER-ENDED", "the U-turn did not end within 45 s"); return; }
        var r = await SettleAsync(op, line, before, 6000);
        await SteerKeptAsync(op, ts, te, "the automatic U-turn");
        // The tractor drives along A→B, so a right turn lands one pass right of A→B, a left one left.
        string side = left ? "left" : "right";
        op.S.Step($"the screen's U-turn arrow showed a {side} turn when it started");
        Landed(op, line, r, left ? -Pass : Pass, $"after the automatic U-turn ({side})");
        await op.Run.ShotAsync(op.S, op.Screen, "U1-auto-turn.png");
    }

    // ------------------------------------------------------------------ U2, U3, U4

    private static async Task ManualTurnAsync(FlowOp op)
    {
        var line = await FlowFields.NorthLineAsync(op, Slot, 120);
        if (line == null) return;
        await op.PlaceAsync(FlowFields.At(Slot, 116, 150), 0);
        await Task.Delay(600);
        bool hidden = await op.PageAsync<bool>("() => document.getElementById('osb-uturn').hidden && document.getElementById('osb-lateral').hidden");
        if (hidden) op.S.Ok("without AutoSteer the on-screen U-turn and lateral buttons are hidden");
        else op.S.Problem(Severity.Bug, "ONSCREEN-BUTTONS-WITHOUT-STEER", "the on-screen U-turn/lateral buttons show with AutoSteer off");
        if (!await FlowFields.SteerOnPassAsync(op, Slot, line, 150, 0, 8)) return;
        if (!await op.SeeAsync("!document.getElementById('osb-uturn').hidden", 2000)) { op.S.Problem(Severity.Bug, "ONSCREEN-BUTTONS-MISSING", "AutoSteer on: no on-screen U-turn buttons"); return; }
        op.S.Ok("AutoSteer on: the on-screen U-turn buttons show");
        var before = await OnPassAsync(op);
        await op.ToldAsync(); op.HostSaid();
        int skip = await op.Rig.ReadAsync(() => op.Rig.Vm.UTurnSkipRows);
        long tTurn = ModuleListener.NowMs;
        if (!await op.TapAsync("#osb-uturn [data-cmd='youturn.manualRight']", "U-turn right")) return;
        if (!await op.HostAsync(() => op.Rig.State.YouTurn.IsExecuting, 3000))
        {
            op.S.Step($"U-turn right: no turn; told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())}");
            op.S.Problem(Severity.Bug, "MANUAL-TURN-NOT-STARTED", "U-turn right: no turn");
            return;
        }
        op.S.Ok("U-turn right: the turn starts");
        // Careless: the lateral and the U-turn buttons again in the middle of it. A lateral is a
        // pass (the guidance's pass number), not a nudge; the turn itself changes the pass only
        // when it ends, so the number holds while the turn is driven. The screen refuses the taps
        // with a hint; a lateral that reaches the host mid-turn is ignored there too (#50).
        await Task.Delay(1500);
        Task<(bool Executing, int Pass)> PassAsync() => op.Rig.ReadAsync(() => (op.Rig.State.YouTurn.IsExecuting, op.Rig.State.Guidance.HowManyPathsAway));
        var p0 = await PassAsync();
        // The taps moved the turn's pass or cut it short: what the turn does after is that same fault.
        bool disturbed = false;
        if (!p0.Executing) op.S.Note("the turn was over before the taps, so they are not made");
        else
        {
            await op.TapAsync("#osb-lateral [data-cmd='track.snapRight']", "Lateral right (during the turn)");
            await op.TapAsync("#osb-uturn [data-cmd='youturn.manualLeft']", "U-turn left (during the turn)");
            await Task.Delay(400);
            var p1 = await PassAsync();
            var told = await op.ToldAsync();
            var p2 = p1;
            // Only while the turn is driven: once it is over, a lateral is the operator's to make.
            if (p1.Executing)
            {
                await op.Screen.SendAsync("track.snapRight");
                op.S.Step("a Lateral right sent straight to the host during the turn (as a tap does when the screen has not yet heard that the turn started)");
                await Task.Delay(400);
                p2 = await PassAsync();
            }
            static string Show((bool Executing, int Pass) p) => FlowOp.F($"{p.Pass}") + (p.Executing ? "" : " (the turn over)");
            op.S.Step($"during the turn: pass {Show(p0)}, after the taps {Show(p1)}, "
                + (p1.Executing ? $"after the host's lateral {Show(p2)}" : "no lateral sent to the host")
                + $"; told {TrackFlows.Said(told, op.HostSaid())}");
            var wrong = new List<string>();
            if (!told.Any(t => t.Contains("Finish the U-turn first", StringComparison.Ordinal))) wrong.Add("no 'Finish the U-turn first' hint");
            // While the turn is driven its pass holds; once it is over, the guidance is on the pass
            // the turn lands on (a right turn along A→B: skip + 1 passes right).
            int landing = p0.Pass + skip + 1;
            bool Moved((bool Executing, int Pass) p) => p.Pass != (p.Executing ? p0.Pass : landing);
            // Once the turn is over the guidance may be on the old pass (a cancelled turn): not moved, but not landed.
            static string Away((bool Executing, int Pass) p, string after) => p.Executing
                ? $"{after} moved the guidance to another pass"
                : $"after {after}, the guidance is not on the pass the turn lands on";
            if (Moved(p1)) wrong.Add(Away(p1, "the taps"));
            else if (Moved(p2)) wrong.Add(Away(p2, "a lateral that reached the host"));
            // The 8 m radius over an 8 m pass is an omega of 40 m or more, well over ten seconds at
            // 8 km/h: a turn over a few seconds in was cut short.
            if (!p2.Executing) wrong.Add("the turn ended within seconds of the taps or the lateral made during it");
            disturbed = Moved(p1) || Moved(p2) || !p2.Executing;
            if (wrong.Count == 0) op.S.Ok("taps during the turn are refused, and the guidance stays on its pass");
            else op.S.Problem(Severity.Bug, "TAP-DURING-TURN", $"lateral/U-turn tapped during the turn: {string.Join("; ", wrong)}");
        }
        // The turn's end; at once when it is already over.
        bool ended = await op.HostAsync(() => !op.Rig.State.YouTurn.IsExecuting, 45000);
        long te = ModuleListener.NowMs;
        if (disturbed) op.S.Note("how the turn ended, steered and landed is not judged: the taps during it changed it");
        else
        {
            if (!ended) { op.S.Problem(Severity.Bug, "TURN-NEVER-ENDED", "the manual U-turn did not end within 45 s"); return; }
            var r = await SettleAsync(op, line, before, 6000);
            await SteerKeptAsync(op, tTurn, te, "the manual U-turn");
            Landed(op, line, r, Pass, "after U-turn right");
        }
        await op.Run.ShotAsync(op.S, op.Screen, "U2-manual-turn.png");
    }

    private static async Task LateralAsync(FlowOp op)
    {
        var line = await FlowFields.NorthLineAsync(op, Slot, 120);
        if (line == null) return;
        if (!await FlowFields.SteerOnPassAsync(op, Slot, line, 100, 0, 8)) return;
        var before = await OnPassAsync(op);
        // A lateral moves the guidance one pass (its pass number); the nudge stays as it was.
        Task<int> PassAsync() => op.Rig.ReadAsync(() => op.Rig.State.Guidance.HowManyPathsAway);
        int n0 = await PassAsync();
        if (!await op.TapAsync("#osb-lateral [data-cmd='track.snapRight']", "Lateral right")) return;
        await op.HostAsync(() => op.Rig.State.Guidance.HowManyPathsAway != n0, 1500);
        int n1 = await PassAsync();
        op.S.Step(FlowOp.F($"the guidance's pass {n0} → {n1}"));
        var r = await SettleAsync(op, line, before, 12000);
        op.S.Step(FlowOp.F($"Lateral right: the tractor {r.Across:+0.0;-0.0} m from the first pass (+ = right of A→B), {r.Xte:0.00} m off the line"));
        var wrong = Misses(r.Across, Pass, r.Xte, "the first pass");
        if (wrong.Count == 0) op.S.Ok("Lateral right: the tractor follows the line one width over");
        else op.S.Problem(Severity.Bug, "LATERAL-WRONG", $"Lateral right: the tractor is {string.Join("; ", wrong)}");
        if (!await op.TapAsync("#osb-lateral [data-cmd='track.snapLeft']", "Lateral left")) return;
        var back = await SettleAsync(op, line, before, 12000);
        op.S.Step(FlowOp.F($"Lateral left: the guidance's pass {await PassAsync()}, the tractor {back.Across:+0.0;-0.0} m from the first pass, {back.Xte:0.00} m off the line"));
        wrong = Misses(back.Across, 0, back.Xte, "the first pass");
        if (wrong.Count == 0) op.S.Ok("Lateral left: back on the first pass");
        else op.S.Problem(Severity.Bug, "LATERAL-WRONG", $"Lateral left: the tractor is {string.Join("; ", wrong)}");
    }

    private static async Task SkipAsync(FlowOp op)
    {
        var line = await FlowFields.NorthLineAsync(op, Slot, 120);
        if (line == null) return;
        await op.CloseAllAsync();
        int s0 = await op.Rig.ReadAsync(() => op.Rig.Vm.UTurnSkipRows);
        if (!await op.TapAsync("#bn-skipcycle", "the skip-rows button")) return;
        await op.HostAsync(() => op.Rig.Vm.UTurnSkipRows != s0, 1500);
        int s1 = await op.Rig.ReadAsync(() => op.Rig.Vm.UTurnSkipRows);
        // The button's number comes with the next Tick (10 Hz) and the frame after it.
        string want = s1.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool seen = await op.SeeAsync($"document.getElementById('bn-skipnum').textContent === '{want}'", 1500);
        string shown = await op.PageAsync<string>("() => document.getElementById('bn-skipnum').textContent");
        if (s1 == s0 + 1 && seen) op.S.Ok($"skip rows {s0} → {s1}, the button shows {want}");
        else { op.S.Problem(Severity.Bug, "SKIP-BUTTON-WRONG", $"skip rows {s0} → {s1}, the button shows '{shown}'"); if (s1 != 1) return; }

        if (!await FlowFields.SteerOnPassAsync(op, Slot, line, 120, 0, 8)) return;
        var before = await OnPassAsync(op);
        long tTurn = ModuleListener.NowMs;
        if (!await op.TapAsync("#osb-uturn [data-cmd='youturn.manualRight']", "U-turn right")) return;
        var (started, ended, _, te, _) = await TurnAsync(op, 3000, 45000);
        if (!started || !ended) { op.S.Problem(Severity.Bug, "MANUAL-TURN-NOT-STARTED", $"U-turn right with skip 1: started {started}, ended {ended}"); return; }
        var r = await SettleAsync(op, line, before, 6000);
        await SteerKeptAsync(op, tTurn, te, "the U-turn with skip rows 1");
        Landed(op, line, r, Pass * 2, "U-turn right with skip rows 1 (one pass left between)");

        // Round back to 0 (the buttons cycle 0-9).
        await op.Rig.RunAsync(() => { if (op.Rig.Vm.IsAutoSteerEngaged) op.Rig.Vm.ToggleAutoSteerCommand?.Execute("scenario"); op.Rig.Vm.SimulatorSpeedKph = 0; });
        for (int k = 0; k < 10 && await op.Rig.ReadAsync(() => op.Rig.Vm.UTurnSkipRows) != 0; k++)
        {
            await op.TapAsync("#bn-skipcycle", "the skip-rows button");
            await Task.Delay(250);
        }
        int s2 = await op.Rig.ReadAsync(() => op.Rig.Vm.UTurnSkipRows);
        if (s2 == 0) op.S.Ok("tapped round, skip rows back to 0"); else op.S.Problem(Severity.Bug, "SKIP-BUTTON-WRONG", $"after ten taps skip rows is {s2}");
    }
}
