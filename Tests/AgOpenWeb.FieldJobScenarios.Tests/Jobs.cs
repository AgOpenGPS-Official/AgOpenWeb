// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>Runs one job and judges what the real implement did against the field, the
/// headland, the obstacles, the neighbouring passes and AgOpenWeb's own map.</summary>
public static class Jobs
{
    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static string NewDataRoot(string code)
    {
        string root = Path.Combine(Path.GetTempPath(), "agopenweb-field-job", code + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        return root;
    }

    public static bool Want(string? only, string code) =>
        string.IsNullOrEmpty(only) || only.Split(',').Any(o => code.Contains(o.Trim(), StringComparison.OrdinalIgnoreCase));

    public static async Task<JobAreas?> RunAsync(Journal journal, string op, JobSpec spec)
    {
        var sc = new Scenario(op, spec.Code, spec.Title, spec.Story);
        JobAreas? areas = null;
        await using var run = new JobRun(spec, sc);
        try
        {
            if (await run.SetUpAsync(NewDataRoot(spec.Code)) && await run.DriveAsync())
            {
                areas = run.Measure();
                Judge(sc, run, areas);
                if (Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_DUMP") is { Length: > 0 } dumpDir)
                {
                    Directory.CreateDirectory(dumpDir);
                    run.DumpImage(Path.Combine(dumpDir, spec.Code + ".ppm"));
                }
            }
        }
        catch (Exception ex)
        {
            sc.Harness("the harness threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }
        foreach (var p in run.Problems) if (!p.StartsWith("HARNESS", StringComparison.Ordinal)) sc.Note(p);
        sc.Row.Steps.AddRange(run.Said.Select(s => "  app: " + s));
        sc.ModuleSnapshot(run.Module, run.Seconds);
        journal.Add(sc.Row);
        return areas;
    }

    /// <summary>The checks a job must pass. Thresholds in metres and square metres, with the
    /// 0.2 m grid's own rounding allowed for.</summary>
    public static void Judge(Scenario sc, JobRun r, JobAreas a)
    {
        var spec = r.Spec;
        void flag(Severity sev, string code, string text)
        {
            if (spec.Expected.Contains(code)) sc.Note($"expected from what the operator did (`{code}`): {text}");
            else sc.Problem(sev, code, text);
        }
        double ha = a.Work / 1e4;
        sc.Row.Metrics.Add(("Cultivated area (inside the headland line, outside obstacles)", F($"{ha:0.00} ha")));
        sc.Row.Metrics.Add(("Sprayed at least once (valves open, real implement)", F($"{a.Sprayed / 1e4:0.00} ha = {Pct(a.Sprayed, a.Work)}")));
        sc.Row.Metrics.Add(("Never sprayed, of which between passes / at the pass ends (within 3 m of the headland line) / next to the obstacles (within 2 m)", F($"{a.Missed:0} m² = {Pct(a.Missed, a.Work)}, {a.MissedBetweenPasses:0} / {a.MissedAtEnds:0} / {a.MissedAtObstacles:0} m²")));
        sc.Row.Metrics.Add(("Widest missed strip / overlap between neighbouring passes", F($"{a.LargestMissedStripM:0.00} m / {a.LargestOverlapM:0.00} m")));
        sc.Row.Metrics.Add(("Sprayed twice or more", F($"{a.Twice:0} m² = {Pct(a.Twice, a.Work)}")));
        sc.Row.Metrics.Add(("Sprayed outside the boundary / in obstacles / in the headland", F($"{a.OutsideBoundary:0} / {a.InObstacles:0} / {a.InHeadland:0} m²")));
        sc.Row.Metrics.Add(("AgOpenWeb's map: painted where no valve was open / open but not painted", F($"{a.AppPaintedNotSprayed:0} / {a.SprayedNotAppPainted:0} m²")));
        sc.Row.Metrics.Add(("AgOpenWeb's own sections on screen: swept / swept but the map blank / the map painted beyond them", F($"{a.ScreenSwept / 1e4:0.00} ha / {a.ScreenSweptNotPainted:0} m² / {a.AppPaintedNotSprayed - a.AppPaintedScreenSweptNotSprayed:0} m²")));
        sc.Row.Metrics.Add(("AgOpenWeb's mapping at 100 Hz (IsMappingOn at its tool position): swept / the map painted beyond it / it swept beyond the screen's sections", F($"{a.MapSwept / 1e4:0.00} ha / {a.AppPaintedNotMapped:0} m² / {a.MappedNotScreen:0} m²")));
        sc.Row.Metrics.Add(("AgOpenWeb's worked area / area covered once vs the truth", F($"{a.AppTotalWorked / 1e4:0.00} / {a.AppActualCovered / 1e4:0.00} ha vs {(a.Sprayed + a.OutsideBoundary + a.InObstacles + a.InDriveThrough + a.InHeadland) / 1e4:0.00} ha")));
        sc.Row.Metrics.Add(("U-turns / passes / simulated time", F($"{r.Turns.Count} / {(r.PathOp != null ? r.PassesDriven : r.Turns.Count + 1)} / {r.Seconds / 60:0.0} min")));
        if (r.ToolSamples > 0)
            sc.Row.Metrics.Add(("AgOpenWeb's tool position vs the real implement on the passes, along (+ ahead) / across (+ right), mean and worst", F($"{r.ToolAlongSum / r.ToolSamples:+0.00;-0.00} m ({r.ToolAlongMax:+0.00;-0.00}) / {r.ToolAcrossSum / r.ToolSamples:+0.00;-0.00} m ({r.ToolAcrossMax:+0.00;-0.00})")));
        sc.Row.Metrics.Add(("AgOpenWeb's position behind the receiver's (harness pace check): median lag in fixes of 0.1 s / GPS cycles run of fixes sent", F($"{r.MedianLagFixes} / {r.CyclesAtEnd} of {r.FixesAtEnd}")));
        if (r.SecondsGeoStop > 0)
            sc.Row.Metrics.Add(("Geo-stop sent to the module (tractor out of bounds)", F($"{r.SecondsGeoStop:0.0} s")));
        r.PathOp?.Metrics(sc, r);
        if (r.PathOp is { JudgesCoverage: false } only)
        {
            only.Judge(sc, r, a, flag);
            JudgeAutoSteer(sc, r, flag);
            return;
        }
        if (r.SliverPasses.Count > 0)
            sc.Note(F($"pass(es) AgOpenWeb counts as inside the field but with less than 20 m of line in the cultivated area: {string.Join(", ", r.SliverPasses.Select(p => F($"{p.Pass} ({p.ChordM:0} m)")))}"));
        if (r.Turns.Count > 0)
            sc.Row.Metrics.Add(("U-turn time, mean / longest; furthest past the boundary", F($"{r.Turns.Average(t => t.Seconds):0.0} / {r.Turns.Max(t => t.Seconds):0.0} s; {r.Turns.Max(t => t.MaxOutsideM):0.0} m")));
            if (Journal.Verbose)
                foreach (var t in r.Turns)
                    sc.Note(F($"turn {t.Index}: pass {t.PassBefore} -> {t.PassAfter}, {(t.Left ? "left" : "right")}, {t.Seconds:0.0} s, past the boundary: tractor {t.MaxPivotOutsideM:0.0} m, implement end {t.MaxOutsideM:0.0} m{(t.Fallback ? ", fallback path" : "")}, slowest {t.MinSpeedKmh:0.0} km/h{(t.BackOnPassSeconds >= 0 && t.Seconds - t.BackOnPassSeconds > 20 ? F($", back on the next pass after {t.BackOnPassSeconds:0} s but AgOpenWeb kept the turn going for {t.Seconds - t.BackOnPassSeconds:0} s more") : "")}"));

        // ---- the field done
        if (r.EndedEarly)
            sc.Note(F($"the job ended early ({r.EndedBy}): {Pct(a.Sprayed, a.Work)} of the cultivated area sprayed by then"));
        else if (!r.FieldDone)
            flag(Severity.Bug, "FIELD-NOT-FINISHED", F($"AgOpenWeb never reported the end of the field; the job stopped after {r.Seconds / 60:0} min with {Pct(a.Missed, a.Work)} of the cultivated area unsprayed. {string.Join(" | ", r.Problems)}"));
        else if (a.Missed - a.MissedBetweenPasses - a.MissedLeftInMiddle - a.MissedAtCorners > 0.05 * a.Work)
            flag(Severity.Bug, "FIELD-LEFT-UNDONE", F($"{(r.EndedBy.Length > 0 ? "The job ended (" + r.EndedBy + ")" : "AgOpenWeb reported the end of the field")} with {Pct(a.Missed - a.MissedBetweenPasses - a.MissedAtEnds - a.MissedAtObstacles - a.MissedAlongBoundary - a.MissedLeftInMiddle - a.MissedAtCorners, a.Work)} of the cultivated area never sprayed outside the strips between passes, the pass ends and the obstacles (a side of the field, or passes it skipped)."));
        else if (a.MissedLeftInMiddle > 0.003 * a.Work)
            sc.Ok(F($"the laps were worked: {Pct(a.Sprayed, a.Work)} of the cultivated area sprayed, {Pct(a.MissedLeftInMiddle, a.Work)} left in the middle where no lap reaches (for an AB line or by hand); {r.EndedBy}"));
        else
            sc.Ok(F($"the whole field was worked: {Pct(a.Sprayed, a.Work)} of the cultivated area sprayed, {(r.EndedBy.Length > 0 ? r.EndedBy : F($"AgOpenWeb reported the end of the field after {r.Turns.Count} U-turns"))}"));
        if (a.MissedAtCorners > 0)
            sc.Note(F($"{a.MissedAtCorners:0} m² ({Pct(a.MissedAtCorners, a.Work)}) at the laps' corners were never sprayed: a lap is rounded where it turns tighter than the rig can (the U-turn radius, {r.Rig.Store.Guidance.UTurnRadius:0} m), so at a corner the next lap in pulls away from it"));

        // ---- strips between passes
        if (a.MissedAtObstacles > 0)
            sc.Note(F($"{a.MissedAtObstacles:0} m² of the cultivated area within 2 m of the obstacles were never sprayed: the sections closing over an obstacle and opening again after it (the section look-ahead and the valves' latency)"));
        if (a.LargestMissedStripM > 0.15 || a.MissedBetweenPasses > 0.005 * a.Work)
            flag(Severity.Bug, "MISSED-STRIPS", F($"strips of ground between neighbouring passes were never sprayed: {a.MissedBetweenPasses:0} m² ({Pct(a.MissedBetweenPasses, a.Work)}), the widest {a.LargestMissedStripM:0.00} m{(a.LargestStripWhere.Length > 0 ? " " + a.LargestStripWhere : "")}; the strips centre on ({a.MissedBetweenE / Math.Max(a.MissedBetweenPasses, 1e-9):0},{a.MissedBetweenN / Math.Max(a.MissedBetweenPasses, 1e-9):0}). AgOpenWeb's passes are {r.Spacing:0.00} m apart for a {spec.T.Width:0.00} m implement with {spec.T.Overlap:0.00} m overlap."));
        else
            sc.Ok(F($"no strip was left between passes (widest {a.LargestMissedStripM:0.00} m, {a.MissedBetweenPasses:0} m² in all)"));
        // ---- the edge of a field worked with no headland (laps along the boundary)
        if (r.Work == null)
        {
            double margin = r.Rig.Store.Tool.CoverageMarginMeters;
            sc.Row.Metrics.Add(("Never sprayed along the boundary (no headland: within 1.5 m of it) / at the laps' corners / in the middle no lap reaches", F($"{a.MissedAlongBoundary:0} / {a.MissedAtCorners:0} / {a.MissedLeftInMiddle:0} m²")));
            if (a.MissedAlongBoundary > 0.003 * a.Work)
                flag(Severity.Improvement, "EDGE-NOT-SPRAYED", F($"{a.MissedAlongBoundary:0} m² ({Pct(a.MissedAlongBoundary, a.Work)}) along the boundary were never sprayed with the boom's end on the boundary (the first lap half a width inside it): a strip about {a.MissedAlongBoundary / Math.Max(Ring.Perimeter(r.Outer), 1):0.00} m wide round the field. With 'Off outside boundary' on, AgOpenWeb widens each section by the coverage margin ({margin * 100:0} cm) and closes it when that edge is outside the boundary, so the outermost section stays closed along the fence."));
            else
                sc.Ok(F($"the edge along the boundary was sprayed ({a.MissedAlongBoundary:0} m² missed within 1.5 m of it)"));
        }
        double overlapExpected = spec.T.Overlap + 0.25;
        if (a.LargestOverlapM > overlapExpected + 0.5 || a.Twice > 0.05 * a.Work)
            flag(Severity.Improvement, "DOUBLE-SPRAYED", F($"{Pct(a.Twice, a.Work)} of the cultivated area was sprayed twice (widest overlap between passes {a.LargestOverlapM:0.00} m, {spec.T.Overlap:0.00} m asked for)."));
        else
            sc.Ok(F($"overlap as set: {Pct(a.Twice, a.Work)} sprayed twice, widest {a.LargestOverlapM:0.00} m"));

        // ---- where it must not spray
        if (a.OutsideBoundary > 10)
            flag(spec.OffWhenOut ? Severity.Safety : Severity.Improvement, "SPRAYED-OUTSIDE-BOUNDARY",
                F($"{a.OutsideBoundary:0} m² outside the field boundary were sprayed (valves open with the implement past the edge){(spec.OffWhenOut ? " with 'Off outside boundary' on." : " with 'Off outside boundary' off, AgOpenWeb's default (AgOpenGPS's default is on).")}"));
        else
            sc.Ok(F($"nothing sprayed outside the boundary ({a.OutsideBoundary:0} m²)"));
        if (spec.Obstacles)
        {
            if (a.InObstacles > 2)
                flag(Severity.Safety, "SPRAYED-IN-OBSTACLE", F($"{a.InObstacles:0} m² inside obstacles (inner boundaries that are not drive-through) were sprayed."));
            else
                sc.Ok(F($"nothing sprayed inside the obstacles ({a.InObstacles:0} m²; {a.InDriveThrough:0} m² in drive-through ones, allowed)"));
        }
        if (spec.HeadlandM > 0 && spec.HeadlandSections)
        {
            if (a.InHeadland > 20)
                flag(Severity.Bug, "SPRAYED-IN-HEADLAND", F($"{a.InHeadland:0} m² of the headland were sprayed with section control in the headland on (the sections should be off there)."));
            else
                sc.Ok(F($"the headland was left alone ({a.InHeadland:0} m² sprayed in it)"));
        }

        // ---- the map against the valves
        if (a.AppPaintedNotSprayed > 20 && a.AppPaintedNotSprayed > 0.005 * a.Sprayed)
            flag(Severity.Bug, "MAP-SHOWS-UNSPRAYED", F($"AgOpenWeb's map shows {a.AppPaintedNotSprayed:0} m² as worked where no valve was open within 0.4 m: {a.AppPaintedScreenSweptNotSprayed:0} m² of it its own sections swept with the screen on and the valves shut ({r.ScreenOnValveShut.Take(spec.T.Sections).Sum():0.0} section-seconds; module link down {r.SecondsLinkDown:0.0} s), {a.AppPaintedNotSprayed - a.AppPaintedScreenSweptNotSprayed:0} m² the map painted where its sections never were."));
        else
            sc.Ok(F($"the map shows nothing that was not sprayed ({a.AppPaintedNotSprayed:0} m²)"));
        if (a.SprayedNotAppPainted > 20 && a.SprayedNotAppPainted > 0.005 * a.Sprayed)
            flag(Severity.Bug, "SPRAYED-NOT-ON-MAP", F($"{a.SprayedNotAppPainted:0} m² were sprayed (valves open) but AgOpenWeb's map shows nothing within 0.4 m there."));
        else
            sc.Ok(F($"everything sprayed is on the map ({a.SprayedNotAppPainted:0} m² not)"));
        double truthTotal = a.Sprayed + a.OutsideBoundary + a.InObstacles + a.InDriveThrough + a.InHeadland;
        if (truthTotal > 0 && Math.Abs(a.AppActualCovered - truthTotal) > 0.05 * truthTotal)
            flag(Severity.Inconsistency, "APP-AREA-OFF", F($"AgOpenWeb's 'area covered once' is {a.AppActualCovered / 1e4:0.00} ha, the real implement sprayed {truthTotal / 1e4:0.00} ha ({(a.AppActualCovered / truthTotal - 1) * 100:+0.0;-0.0} %)."));
        else
            sc.Ok(F($"AgOpenWeb's area covered once ({a.AppActualCovered / 1e4:0.00} ha) matches the truth ({truthTotal / 1e4:0.00} ha)"));

        // ---- valves vs screen
        var lag = new List<string>();
        for (int s = 0; s < Math.Min(64, spec.T.Sections); s++)
            if (r.PersistentDisagreement[s] > 1)
                lag.Add(F($"{s + 1} (on screen, valve shut {r.ScreenOnValveShut[s]:0.0} s; off, valve open {r.ScreenOffValveOpen[s]:0.0} s; {r.PersistentDisagreement[s]:0.0} s of it in stretches over 1 s)"));
        if (lag.Count > 0)
            flag(spec.DefaultRelays ? Severity.Robustness : Severity.Bug, spec.DefaultRelays ? "SECTION-WITHOUT-RELAY" : "VALVES-NOT-FOLLOWING",
                "the valves did not follow the screen for section(s) " + string.Join(", ", lag));
        else
            sc.Ok("every valve followed the screen within 2 s over the whole job");

        // ---- where AgOpenWeb thinks the implement is
        if (r.ToolSamples > 0)
        {
            double along = r.ToolAlongSum / r.ToolSamples, across = r.ToolAcrossSum / r.ToolSamples;
            if (Math.Abs(along) > 0.5 || Math.Abs(across) > 0.3)
                flag(Severity.Bug, "TOOL-POSITION-OFF", F($"on the passes AgOpenWeb places the implement {along:+0.00;-0.00} m along the track and {across:+0.00;-0.00} m across from where the real {spec.T.Name} is (hitch {spec.T.HitchLength:0.0} m, pivot to tool {spec.T.ToolToPivot:0.0} m, offset {spec.T.Offset:0.0} m); its map and section timing are shifted by that much."));
            else
                sc.Ok(F($"AgOpenWeb's implement position matches the real one on the passes ({along:+0.00;-0.00} m along, {across:+0.00;-0.00} m across)"));
        }

        // ---- the turns
        var outTurns = r.Turns.Where(t => t.MaxPivotOutsideM > 0.5).ToList();
        var outTool = r.Turns.Where(t => t.MaxOutsideM > 0.5).ToList();
        int fallbacks = r.Turns.Count(t => t.Fallback);
        var late = r.Turns.Where(t => t.BackOnPassSeconds >= 0 && t.Seconds - t.BackOnPassSeconds > 30).ToList();
        if (late.Count > 0)
        {
            var worst = late.OrderByDescending(t => t.Seconds - t.BackOnPassSeconds).First();
            flag(Severity.Improvement, "TURN-ENDS-LATE", F($"in {late.Count} of {r.Turns.Count} U-turns AgOpenWeb kept the turn 'executing' long after the tractor was back on the next pass inside the cultivated area: turn {worst.Index} ended {worst.Seconds - worst.BackOnPassSeconds:0} s ({(worst.Seconds - worst.BackOnPassSeconds) * 2.5:0} m at 9 km/h) after the tractor was on the pass{(worst.Fallback ? ", a simple fallback path" : "")}; until then no next turn can be planned and the screen says the turn is still on."));
        }
        if (r.ArrowAtEdge is { } edge)
            flag(Severity.Bug, "FIRST-TURN-TOWARD-EDGE", F($"before the first U-turn AgOpenWeb's arrow pointed at the edge the operator started from: pass {edge.Pass}, with only {edge.ChordM:0} m of line inside the cultivated area (any 5 m sample inside counts as a pass); left alone, AgOpenWeb turns onto it and then reports the end of the field with the whole field undone. The operator tapped the arrow the other way."));
        if (outTurns.Count > 0)
            flag(Severity.Safety, "TURN-OUTSIDE-BOUNDARY", F($"{outTurns.Count} of {r.Turns.Count} U-turns took the tractor itself past the field boundary, the furthest by {outTurns.Max(t => t.MaxPivotOutsideM):0.0} m (turn {outTurns.OrderByDescending(t => t.MaxPivotOutsideM).First().Index}{(outTurns.Any(t => t.Fallback) ? ", AgOpenWeb's simple fallback path" : "")}); 'distance from boundary' was {spec.UTurnDistanceFromBoundary:0.0} m."));
        else if (outTool.Count > 0)
            flag(Severity.Improvement, "TURN-IMPLEMENT-OUTSIDE", F($"the tractor stayed inside the boundary in every U-turn, but the {spec.T.Width:0} m implement's end swung past it in {outTool.Count} of {r.Turns.Count} turns, the furthest by {outTool.Max(t => t.MaxOutsideM):0.0} m: 'distance from boundary' ({spec.UTurnDistanceFromBoundary:0.0} m) keeps the tractor inside, not the implement."));
        else if (r.Turns.Count > 0)
            sc.Ok(F($"all {r.Turns.Count} U-turns kept the tractor and the implement inside the boundary"));
        if (fallbacks > 0)
            sc.Note(F($"AgOpenWeb's turn service failed ('path invalid or out of bounds') and the simple fallback path was used in {fallbacks} of {r.Turns.Count} turns"));
        var missed = r.Said.Where(s => s.Contains("U-turn missed", StringComparison.Ordinal) || s.Contains("cancelled", StringComparison.Ordinal)).ToList();
        if (missed.Count > 0)
            flag(Severity.Bug, "TURN-MISSED", F($"{missed.Count} U-turn(s) were missed or cancelled while the operator did nothing wrong: {missed[0]}"));
        r.PathOp?.Judge(sc, r, a, flag);
        JudgeAutoSteer(sc, r, flag);
    }

    /// <summary>AutoSteer dropping out on its own, and the tractor running off the field.</summary>
    private static void JudgeAutoSteer(Scenario sc, JobRun r, Action<Severity, string, string> flag)
    {
        var offs = r.Said.Where(s => s.Contains("AutoSteer off", StringComparison.Ordinal)).ToList();
        if (offs.Count > 0 && r.Spec.OnFix == null)
            flag(Severity.Bug, "AUTOSTEER-DROPPED", F($"AutoSteer switched itself off {offs.Count} time(s) during the job, re-engaged by the operator {r.Reengaged} time(s): {offs[0]}"));
        if (r.Problems.Any(p => p.StartsWith("HARNESS", StringComparison.Ordinal))) return;
        foreach (var p in r.Problems)
        {
            if (p.Contains("asked the operator to take over", StringComparison.Ordinal)) flag(Severity.Bug, "TURN-REFUSED", p);
            else if (p.Contains("ran", StringComparison.Ordinal)) flag(Severity.Safety, "RAN-OFF", p);
        }
    }

    private static string Pct(double part, double whole) => whole <= 0 ? "n/a" : F($"{part / whole * 100:0.0} %");
}
