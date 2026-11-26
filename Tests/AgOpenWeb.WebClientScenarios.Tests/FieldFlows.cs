// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// F: fields and jobs the operator's way, in Field Operations: a job started and worked, the
/// field closed and the job resumed (Resume Last Job, Resume Job), a field worked without a job
/// and closed (Unsaved Coverage: Cancel, Save, Discard), Drive In, deleting a job and a field.
/// The careless operator closes the field while AutoSteer and the sections are working at
/// 10 km/h, opens another field in the middle of a job, types field names with a slash, accents,
/// another field's name or nothing, and deletes the field and the job that are open.
/// The coverage is read where the operator reads it (the status frame's worked area) and on the host;
/// what a resume brings back is the ground covered, counted from the coverage loaded.
/// </summary>
public static class FieldFlows
{
    public const string Group = "F: fields and jobs";
    private const int Slot = 3, SlotB = 4;
    private const string Field = "Flows Field", FieldB = "Flows Field B";
    private const string Job = "Spraying 1";

    /// <summary>The ground the job had covered when F2 closed it (what Resume Last Job in F3 must
    /// bring back); 0 when F2 did not close it.</summary>
    private static double s_closedArea;

    public static async Task RunAsync(Run run)
    {
        await Flow(run, "F1", "A job started and worked", "Careful operator: Fields and Jobs › the field › task name 'Spraying 1', work type 'Spraying', Start Job; sections on by hand (Manual), 100 m at 10 km/h.", StartJobAsync);
        await Flow(run, "F2", "The field closed with its job", "Field Operations › Close: no question; the job's coverage is on disk and the job is listed In progress.", CloseWithJobAsync);
        await Flow(run, "F3", "Resume Last Job", "Field Operations › Resume Last Job: the same field, job and worked area.", ResumeLastAsync);
        await Flow(run, "F4", "Resume Job from the list", "Field Operations › Close; Resume Job › the job's row: the same field, job and worked area.", ResumeFromListAsync);
        await Flow(run, "F5", "A field worked without a job", "Open Field Only, sections on by hand, 50 m; Close › Unsaved Coverage: Cancel, then Close › Save; again 50 m without a job, Close › Discard.", NoJobAsync);
        await Flow(run, "F6", "The field closed while working", "Careless operator: a job, an A+ line, AutoSteer and every section on by hand (Manual) at 10 km/h; Field Operations › Close.", CloseWhileWorkingAsync);
        await Flow(run, "F7", "Another field opened in the middle of a job", "Careless operator: the job resumed and worked at 8 km/h with every section on by hand (Manual); Fields and Jobs › another field › Open Field Only; the job resumed again later.", SwitchFieldAsync);
        await Flow(run, "F8", "Field names typed carelessly", "New Field: 'North/South, 2'; 'Campo da Ribeira, Açores'; the name of a field that exists; nothing.", NamesAsync);
        await Flow(run, "F9", "Drive In", "The tractor in the second field with no field open: Field Operations › Drive In.", DriveInAsync);
        await Flow(run, "F11", "Another field opened after working without a job", "Careless operator: Open Field Only, sections on by hand, 30 m; Fields and Jobs › another field › Open Field Only: Cancel; again: Discard.", LeaveWorkedAsync);
        await Flow(run, "F10", "Deleting a job and a field", "Careless: Delete Field on the open field, Delete Job on the open job; then closed: Delete Job, Cancel; Delete Job, Confirm; Delete Field, Confirm.", DeleteAsync);
    }

    private static Task Flow(Run run, string code, string title, string story, Func<FlowOp, Task> body)
        => run.ScenarioAsync(Group, code, title, story, async s =>
        {
            await using var op = await FlowOp.OpenAsync(run, s);
            if (op == null) return;
            await op.ToldAsync(); op.HostSaid();
            await body(op);
            await op.StopAsync();
            await StopWorkAsync(op);
        }, onTheRunsLine: false);

    // ------------------------------------------------------------------ helpers

    private static Task<double> HostAreaAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.Services.GetRequiredService<ICoverageMapService>().TotalWorkedArea);
    /// <summary>The ground covered at least once, counted from the coverage map itself. A resume
    /// recounts it from the tiles it loaded; the worked area (HostAreaAsync) is the number the job saved.</summary>
    private static Task<double> CoveredAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.Services.GetRequiredService<ICoverageMapService>().ActualAreaCovered);
    private static Task<double> ScreenAreaAsync(FlowOp op) => op.PageAsync<double>("() => (statusBar && statusBar.workedAreaSqM) || 0");
    private static Task<string> JobAsync(FlowOp op) => op.Rig.ReadAsync(() => op.Rig.Vm.CurrentJobTaskName);
    private static bool Near(double a, double b) => Math.Abs(a - b) <= Math.Max(2, b * 0.01);

    /// <summary>The sections that closed at the module at <paramref name="closedAt"/> (harness ms) did
    /// not open again, and the module's last PGN 254 still has them closed.</summary>
    private static bool StayedClosed(FlowOp op, long closedAt)
        => !op.Rig.Module.Since(closedAt + 1).Any(m => m.Sections != 0) && op.Rig.Module.Last is { Sections: 0 };

    /// <summary>The open field closed (Field Operations › Close), an Unsaved Coverage question
    /// answered <paramref name="unsaved"/>; true when none was open. False when it stays open, with
    /// one finding: FIELD-NOT-CLOSED, unless a menu or control on the way was already the finding.</summary>
    private static async Task<bool> CloseOpenFieldAsync(FlowOp op, string unsaved, string what = "the field")
    {
        if (!await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) return true;
        int n0 = op.S.Row.Findings.Count;
        if (await FlowFields.CloseFieldAsync(op, unsaved) || !await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) return true;
        if (op.S.Row.Findings.Count == n0) op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", $"Close did not close {what}");
        return false;
    }

    private static async Task StopWorkAsync(FlowOp op) => await op.Rig.RunAsync(() =>
    {
        var vm = op.Rig.Vm;
        if (vm.IsAutoSteerEngaged) vm.ToggleAutoSteerCommand?.Execute("scenario end");
        if (vm.IsSectionMasterOn) vm.ToggleSectionMasterCommand?.Execute(null);
        if (vm.IsManualSectionMode) vm.ToggleManualModeCommand?.Execute(null);
        vm.SimulatorSpeedKph = 0;
    });

    /// <summary>Sections on by hand (the Manual button), <paramref name="metres"/> north at <paramref name="kmh"/>, sections off.</summary>
    private static async Task<bool> PaintAsync(FlowOp op, double metres, double kmh = 10)
    {
        await op.CloseAllAsync();
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => op.Rig.Vm.IsManualSectionMode))
        { op.S.Problem(Severity.Bug, "SECTIONS-DID-NOT-START", "the Manual sections button did not switch the sections on"); return false; }
        await op.DriveAsync(metres, kmh);
        await op.StopAsync();
        await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => !op.Rig.Vm.IsManualSectionMode);
        await Task.Delay(600);
        return true;
    }

    private static async Task<bool> FieldsAndJobsAsync(FlowOp op, string? field)
    {
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return false;
        if (!await op.TapAsync("#fo-fields", "Fields and Jobs")) return false;
        if (!await op.WaitOpenAsync("fieldsandjobs")) { op.S.Harness("Fields and Jobs did not open"); return false; }
        if (field == null) return true;
        if (!await op.SeeAsync($"[...document.querySelectorAll('#fj-fieldlist .fj-frow .fj-fname')].some(r => r.textContent === {JsonSerializer.Serialize(field)})", 5000))
        { op.S.Problem(Severity.Bug, "FLOW-FIELD-NOT-LISTED", $"'{field}' is not in the field list"); return false; }
        return await op.TapRowAsync("#fj-fieldlist .fj-frow", field, $"the field '{field}'");
    }

    private static async Task<bool> StartJobOnAsync(FlowOp op, string field, string task, string workType)
    {
        if (!await FieldsAndJobsAsync(op, field)) return false;
        if (!await op.TypeAsync("#fj-taskname", task, "Task name")) return false;
        if (!await op.TypeAsync("#fj-worktype", workType, "Work type")) return false;
        if (!await op.TapAsync("#fj-startjob", "Start Job")) return false;
        if (await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && op.Rig.Vm.CurrentFieldName == field && op.Rig.Vm.CurrentJobTaskName == task && !op.Rig.State.UI.IsBusy, 15000)) return true;
        op.S.Problem(Severity.Bug, "JOB-NOT-STARTED", $"Start Job '{task}' on '{field}': open field '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}', job '{await JobAsync(op)}' (told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())})");
        return false;
    }

    /// <summary>The field open with its job, resumed from Resume Job when it is not. False when it
    /// could not be: no job on disk (a skip note, see F1) or the resume failed (a finding).</summary>
    private static async Task<bool> EnsureJobAsync(FlowOp op)
    {
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName == Field && op.Rig.Vm.CurrentJobTaskName == Job)) return true;
        if (!await CloseOpenFieldAsync(op, "discard")) return false;
        if (!Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job))) { op.S.Note($"skipped: no job '{Job}' to work with (see F1)"); return false; }
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return false;
        if (!await op.TapAsync("#fo-resumejob", "Resume Job")) return false;
        if (!await op.WaitOpenAsync("resumejob")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Resume Job did not open"); return false; }
        if (!await op.TapRowAsync("#rj-list .fj-jrow", Job, $"the job '{Job}'")) return false;
        if (await op.HostAsync(() => op.Rig.Vm.CurrentJobTaskName == Job && !op.Rig.State.UI.IsBusy, 15000)) return true;
        op.S.Problem(Severity.Bug, "RESUME-JOB-FAILED", $"Resume Job › '{Job}': open '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}' / '{await JobAsync(op)}'");
        return false;
    }

    /// <summary>After a resume: the ground the job had covered is covered again (counted from the
    /// coverage loaded, not the worked area the job saved), and the screen shows the host's worked area.</summary>
    private static async Task SameAreaAsync(FlowOp op, double coveredBefore, string what)
    {
        double covered = 0, host = 0, screen = 0;
        for (int k = 0; k < 15; k++)
        {
            covered = await CoveredAsync(op); host = await HostAreaAsync(op); screen = await ScreenAreaAsync(op);
            if (Near(covered, coveredBefore) && Near(screen, host)) break;
            await Task.Delay(200);
        }
        op.S.Step(FlowOp.F($"{what}: {covered:0} m² covered (was {coveredBefore:0} m²); worked area {host:0} m² on the host, {screen:0} m² on the screen"));
        if (Near(covered, coveredBefore)) op.S.Ok($"{what}: the ground the job had covered is covered again");
        else op.S.Problem(Severity.Bug, "COVERAGE-LOST", $"{what}: {(covered < coveredBefore ? "less" : "more")} ground is covered than when the job was closed");
        if (Near(screen, host)) op.S.Ok("the screen shows the host's worked area");
        else op.S.Problem(Severity.Inconsistency, "WORKED-AREA-STALE", $"{what}: the screen's worked area is not the host's");
    }

    // ------------------------------------------------------------------ F1-F4

    private static async Task StartJobAsync(FlowOp op)
    {
        if (!await FlowFields.EnsureOpenAsync(op, Slot, Field)) { op.S.Harness("the field could not be made"); return; }
        if (!await StartJobOnAsync(op, Field, Job, "Spraying")) return;
        op.S.Ok($"job '{Job}' started on '{Field}'");
        if (await op.SeeAsync($"statusBar && statusBar.jobName === {JsonSerializer.Serialize(Job)}", 3000)) op.S.Ok("the screen shows the job");
        else op.S.Problem(Severity.Inconsistency, "JOB-NOT-SHOWN", $"the screen's job is '{await op.PageAsync<string>("() => (statusBar && statusBar.jobName) || ''")}'");
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        string pill = await op.PageAsync<string>("() => document.getElementById('fo-current').textContent");
        if (pill.Contains(Field, StringComparison.Ordinal) && pill.Contains(Job, StringComparison.Ordinal)) op.S.Ok($"Field Operations says '{pill}'");
        else op.S.Problem(Severity.Inconsistency, "FIELD-PILL-STALE", $"Field Operations says '{pill}'");

        await op.PlaceAsync(FlowFields.At(Slot, 120, 60), 0);
        double a0 = await HostAreaAsync(op);
        if (!await PaintAsync(op, 100)) return;
        double a1 = await HostAreaAsync(op) - a0;
        double sc = await ScreenAreaAsync(op);
        op.S.Step(FlowOp.F($"100 m with the 8 m boom on: {a1:0} m² worked; the screen says {sc:0} m²"));
        // An 8 m boom over 100 m: about 800 m², less the start and end of the drive.
        if (a1 > 650 && a1 < 900) op.S.Ok("100 m with the 8 m boom on: about 800 m² worked");
        else op.S.Problem(Severity.Bug, "COVERAGE-WRONG", $"100 m with the 8 m boom on: {(a1 <= 650 ? "much less" : "much more")} than the 800 m² expected was worked");
        await op.Run.ShotAsync(op.S, op.Screen, "F1-job-worked.png");
    }

    private static async Task CloseWithJobAsync(FlowOp op)
    {
        s_closedArea = 0;
        if (!await EnsureJobAsync(op)) return;
        double before = await HostAreaAsync(op), covered = await CoveredAsync(op);
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-close", "Close")) return;
        // A question would be on the host at once and reach the screen with a status frame (about
        // twice a second): wait for the field to close or the question to come, whichever is first.
        await op.HostAsync(() => (!op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy) || op.Rig.State.UI.IsUnsavedCoverageDialogVisible, 10000);
        bool asked = await op.Rig.ReadAsync(() => op.Rig.State.UI.IsUnsavedCoverageDialogVisible);
        if (asked)
        {
            op.S.Problem(Severity.Bug, "UNSAVED-ASKED-WITH-JOB", "Close with a job open asked about unsaved coverage");
            // Unanswered, the field stays open on that question: no second finding for it.
            if (!await op.WaitOpenAsync("dlg-unsavedcov", 3000)) { op.S.Step("the question did not come to the screen: not answered"); return; }
            if (!await op.TapAsync("#ucov-save", "Save")) return;
        }
        if (!await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 10000)) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close did not close the field"); return; }
        op.S.Step(FlowOp.F($"the field closed with {before:0} m² worked in the job"));
        // Saved from the question, the coverage went to a new job: F3 has no area of this job to compare.
        if (!asked) { op.S.Ok("Close: the field closed without a question"); s_closedArea = covered; }
        if (await op.SeeAsync("scene && !scene.hasField", 3000)) op.S.Ok("the screen shows no field");
        else op.S.Problem(Severity.Inconsistency, "FIELD-STILL-SHOWN", "the field is closed but the screen still shows it");
        string cov = Path.Combine(op.FieldDir(Field), "jobs", Job, "coverage");
        int tiles = Directory.Exists(cov) ? Directory.GetFiles(cov, "*", SearchOption.AllDirectories).Length : 0;
        op.S.Step($"{tiles} file(s) under jobs/{Job}/coverage");
        if (asked) op.S.Note("the job's coverage on disk not judged: Save put it in a new job");
        else if (tiles > 0) op.S.Ok($"the job's coverage is on disk (under jobs/{Job}/coverage)");
        else op.S.Problem(Severity.Bug, "COVERAGE-NOT-SAVED", $"no coverage files under jobs/{Job}/coverage after Close");
        if (!await FieldsAndJobsAsync(op, Field)) return;
        var rows = await op.RowsAsync("#fj-joblist .fj-jrow");
        var row = rows.FirstOrDefault(r => r.Contains(Job, StringComparison.Ordinal));
        // The rows carry their Last opened dates: in the steps, not the checks.
        op.S.Step($"the field's jobs: {string.Join(" | ", rows)}");
        if (row != null && row.Contains("In progress", StringComparison.Ordinal)) op.S.Ok("the job is listed In progress");
        else op.S.Problem(Severity.Inconsistency, "JOB-LIST-STALE", $"'{Job}' is not listed In progress after Close");
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
    }

    private static async Task ResumeLastAsync(FlowOp op)
    {
        // What the job had covered when it was closed: F2's close, or read here when the job is still open.
        double before = s_closedArea;
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen))
        {
            if (await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName == Field && op.Rig.Vm.CurrentJobTaskName == Job)) before = await CoveredAsync(op);
            if (!await CloseOpenFieldAsync(op, "discard")) return;
        }
        if (!Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job))) { op.S.Note($"skipped: no job '{Job}' to resume (see F1)"); return; }
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-resumelast", "Resume Last Job")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 15000))
        { op.S.Problem(Severity.Bug, "RESUME-LAST-FAILED", $"Resume Last Job: no field opened (told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())})"); return; }
        var (f, j) = await op.Rig.ReadAsync(() => (op.Rig.Vm.CurrentFieldName, op.Rig.Vm.CurrentJobTaskName));
        if (f == Field && j == Job) op.S.Ok($"Resume Last Job: '{f}' / '{j}'");
        else op.S.Problem(Severity.Bug, "RESUME-LAST-WRONG", $"Resume Last Job opened '{f}' / '{j}', the last was '{Field}' / '{Job}'");
        if (before > 0) await SameAreaAsync(op, before, "Resume Last Job");
        else op.S.Note("worked area not compared: F2 did not record it");
        await op.Run.ShotAsync(op.S, op.Screen, "F3-resumed.png");
    }

    private static async Task ResumeFromListAsync(FlowOp op)
    {
        if (!await EnsureJobAsync(op)) return;
        double before = await CoveredAsync(op);
        if (!await FlowFields.CloseFieldAsync(op, "save")) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close did not close the field"); return; }
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-resumejob", "Resume Job")) return;
        if (!await op.WaitOpenAsync("resumejob")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "Resume Job did not open"); return; }
        var rows = await op.RowsAsync("#rj-list .fj-jrow");
        op.S.Step($"Resume Job lists: {string.Join(" | ", rows)}");
        if (!await op.TapRowAsync("#rj-list .fj-jrow", Job, $"the job '{Job}'")) return;
        if (!await op.HostAsync(() => op.Rig.Vm.CurrentJobTaskName == Job && !op.Rig.State.UI.IsBusy, 15000))
        { op.S.Problem(Severity.Bug, "RESUME-JOB-FAILED", $"Resume Job › '{Job}': open '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}' / '{await JobAsync(op)}'"); return; }
        op.S.Ok($"Resume Job: '{Field}' / '{Job}'");
        await SameAreaAsync(op, before, "Resume Job");
    }

    // ------------------------------------------------------------------ F5

    private static async Task NoJobAsync(FlowOp op)
    {
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) await FlowFields.CloseFieldAsync(op, "save");
        string jobsDir = Path.Combine(op.FieldDir(Field), "jobs");
        var jobs0 = Directory.Exists(jobsDir) ? Directory.GetDirectories(jobsDir).Select(d => Path.GetFileName(d)).ToList() : new List<string>();
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        if (await JobAsync(op) is { Length: > 0 } j) op.S.Problem(Severity.Bug, "OPEN-ONLY-HAS-JOB", $"Open Field Only opened the job '{j}'");
        await op.PlaceAsync(FlowFields.At(Slot, 60, 200), 0);
        if (!await PaintAsync(op, 50)) return;
        double painted = await HostAreaAsync(op);
        op.S.Step(FlowOp.F($"{painted:0} m² worked with no job"));

        // Close › Cancel: still open, coverage still there.
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-close", "Close")) return;
        if (!await op.WaitOpenAsync("dlg-unsavedcov", 2000))
        {
            bool closed = await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen, 3000);
            op.S.Problem(Severity.Bug, "UNSAVED-NOT-ASKED", $"Close after working with no job: no Unsaved Coverage question (field {(closed ? "closed, the coverage dropped" : "still open")})");
            return;
        }
        string msg = await op.PageAsync<string>("() => document.getElementById('dlg-unsavedcov').textContent.replace(/\\s+/g, ' ').trim()");
        op.S.Ok($"asked: '{msg}'");
        if (!await op.TapAsync("#ucov-cancel", "Cancel")) return;
        await Task.Delay(600);
        bool open = await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen);
        double kept = await HostAreaAsync(op);
        op.S.Step(FlowOp.F($"after Cancel: field open {open}, {kept:0} m² worked ({painted:0} m² before)"));
        if (open && Math.Abs(kept - painted) < 1) op.S.Ok("Cancel: the field stays open with its coverage");
        else op.S.Problem(Severity.Bug, "UNSAVED-CANCEL-CLOSED", open ? "Cancel: the field stays open, but its worked area changed" : "Cancel: the field closed");
        if (!await op.IsOpenAsync("dlg-unsavedcov")) op.S.Ok("and the question is gone");
        else op.S.Problem(Severity.Bug, "UNSAVED-STAYS", "Cancel: the Unsaved Coverage question stays on the screen");

        // Close › Save: a job made for it.
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-close", "Close")) return;
        if (!await op.WaitOpenAsync("dlg-unsavedcov", 2000)) { op.S.Problem(Severity.Bug, "UNSAVED-NOT-ASKED", "the second Close did not ask"); return; }
        if (!await op.TapAsync("#ucov-save", "Save")) return;
        if (!await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 10000)) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Save: the field did not close"); return; }
        var dirs = Directory.Exists(jobsDir) ? Directory.GetDirectories(jobsDir) : Array.Empty<string>();
        var made = dirs.Select(d => Path.GetFileName(d)).Except(jobs0).ToList();
        if (made.Count == 1)
        {
            // Its name is made from the date: in the steps, not the check.
            op.S.Step($"the new job: '{made[0]}'");
            op.S.Ok("Save: a job was made for the coverage");
            // The point of Save is the coverage: it must be in that job (tiles first, manifest.json last).
            string cov = Path.Combine(jobsDir, made[0], "coverage");
            bool manifest = File.Exists(Path.Combine(cov, "manifest.json"));
            int tiles = Directory.Exists(Path.Combine(cov, "d")) ? Directory.GetFiles(Path.Combine(cov, "d"), "*.tile").Length : 0;
            op.S.Step($"the new job's coverage: manifest.json {(manifest ? "there" : "missing")}, {tiles} detection tile(s)");
            if (manifest && tiles > 0) op.S.Ok("Save: the coverage is saved in the new job");
            else op.S.Problem(Severity.Bug, "UNSAVED-SAVE-NO-COVERAGE", $"Save: a job was made, but the coverage is not in it (manifest.json {(manifest ? "there" : "missing")}, {(tiles > 0 ? "tiles there" : "no tiles")})");
        }
        else op.S.Problem(Severity.Bug, "UNSAVED-SAVE-NO-JOB", $"Save: {made.Count} new job folder(s), one expected");

        // Again with no job, Close › Discard.
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        await op.PlaceAsync(FlowFields.At(Slot, 180, 200), 0);
        if (!await PaintAsync(op, 50)) return;
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-close", "Close")) return;
        if (!await op.WaitOpenAsync("dlg-unsavedcov", 2000)) { op.S.Problem(Severity.Bug, "UNSAVED-NOT-ASKED", "Close after working with no job (again): no question"); return; }
        if (!await op.TapAsync("#ucov-discard", "Discard")) return;
        if (!await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 10000)) { op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Discard: the field did not close"); return; }
        int jobs2 = Directory.Exists(jobsDir) ? Directory.GetDirectories(jobsDir).Length : 0;
        if (jobs2 == dirs.Length) op.S.Ok("Discard: closed, no job made");
        else op.S.Problem(Severity.Bug, "UNSAVED-DISCARD-SAVED", $"Discard made a job ({jobs2} job folders, {dirs.Length} before)");
    }

    // ------------------------------------------------------------------ F6, F7

    private static async Task<bool> SteerAsync(FlowOp op)
    {
        // The A+ line due north, AutoSteer from its button at 10 km/h on the first pass, the sections on by hand.
        var line = await FlowFields.NorthLineAsync(op, Slot, 60);
        if (line == null) return false;
        if (!await FlowFields.SteerOnPassAsync(op, Slot, line, 40, 0, 10)) return false;
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => op.Rig.Vm.IsManualSectionMode))
        { op.S.Harness("the sections did not go on"); return false; }
        long t0 = ModuleListener.NowMs;
        if (await op.Run.WaitModuleAsync(m => m.Steering && m.Sections != 0, t0, 3000) == null)
        { op.S.Step($"the module gets {op.Rig.Module.Last}"); op.S.Harness("the module never got AutoSteer and sections on"); return false; }
        await Task.Delay(1500);
        op.S.Step($"working at 10 km/h: the module gets {op.Rig.Module.Last}");
        return true;
    }

    private static async Task CloseWhileWorkingAsync(FlowOp op)
    {
        if (!await EnsureJobAsync(op)) return;
        if (!await SteerAsync(op)) return;
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        await op.ToldAsync(); op.HostSaid();
        var pre = op.Rig.Module.Last;
        op.S.Step($"just before Close the module gets {pre}");
        if (pre is not { Steering: true } || pre.Value.Sections == 0) { op.S.Harness("AutoSteer and the sections were not both on before Close"); return; }
        if (!await op.TapAsync("#fo-close", "Close")) return;
        long t0 = ModuleListener.NowMs;
        var (steerOff, secOff) = await op.Run.MachineStopsAsync(t0, 3000);
        bool closed = await op.HostAsync(() => !op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 10000);
        var told = TrackFlows.Said(await op.ToldAsync(), op.HostSaid());
        // Closed once is not enough: they must still be closed 3 s after Close.
        await Task.Delay((int)Math.Max(0, 3000 - (ModuleListener.NowMs - t0)));
        bool stayedClosed = secOff is { } sc0 && StayedClosed(op, t0 + sc0);
        long heard = ModuleListener.NowMs - op.Rig.Module.LastHeardMs;
        bool manualShown = await op.PageAsync<bool>("() => { const b = document.getElementById('rn-manual'); return !!b && b.offsetParent !== null; }");
        bool barShown = await op.PageAsync<bool>("() => document.getElementById('sectionbar').classList.contains('open')");
        op.S.Step($"Close at 10 km/h: AutoSteer off at the module after {Run.Ms(steerOff)}, sections closed after {Run.Ms(secOff)}{(secOff != null && !stayedClosed ? " and opened again" : "")}; told {told}");
        op.S.Step($"3 s or more after Close, the module last heard PGN 254 {heard} ms ago: {op.Rig.Module.Last}; host manual mode {await op.Rig.ReadAsync(() => op.Rig.Vm.IsManualSectionMode)}, the screen shows the Manual button {manualShown}, the section bar {barShown}");
        if (steerOff is { } so && so < 1500) op.S.Ok("AutoSteer off at the module within 1.5 s of Close");
        else op.S.Problem(Severity.Safety, "CLOSED-FIELD-STILL-STEERED", "the field closed at 10 km/h: the module still got AutoSteer on 1.5 s after Close" + (steerOff == null ? ", and 3 s after" : ""));
        string now = $"now it gets sections {Convert.ToString((op.Rig.Module.Last?.Sections ?? 0) & 0xFF, 2).PadLeft(8, '0')} (PGN 254 {(heard < 200 ? "still arriving every cycle" : "no longer arriving")}; the section bar is {(barShown ? "shown" : "gone")}, the Manual button {(manualShown ? "shown" : "hidden")})";
        if (secOff is { } sc && sc < 1500 && stayedClosed) op.S.Ok("every section closed at the module within 1.5 s of Close, and stayed closed");
        else if (secOff is { } sc2 && sc2 < 1500) op.S.Problem(Severity.Safety, "CLOSED-FIELD-STILL-SPRAYING", $"the field closed at 10 km/h with the sections on by hand: the sections closed at the module, then opened again; {now}");
        else op.S.Problem(Severity.Safety, "CLOSED-FIELD-STILL-SPRAYING", $"the field closed at 10 km/h with the sections on by hand: the module still got sections on 1.5 s after Close; {now}");
        if (closed) op.S.Ok("the field closed"); else op.S.Problem(Severity.Bug, "FIELD-NOT-CLOSED", "Close while working did not close the field");
        await StopWorkAsync(op);
    }

    private static async Task SwitchFieldAsync(FlowOp op)
    {
        // The second field exists (made once from its KML).
        if (!Directory.Exists(op.FieldDir(FieldB)))
        {
            if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) await FlowFields.CloseFieldAsync(op, "discard");
            if (!await FlowFields.MakeFromKmlAsync(op, SlotB, FieldB)) { op.S.Harness("the second field could not be made"); return; }
        }
        if (!await EnsureJobAsync(op)) return;
        await op.PlaceAsync(FlowFields.At(Slot, 200, 60), 0);
        await op.CloseAllAsync();
        if (!await op.Run.TapUntilAsync(op.Screen, "#rn-manual", () => op.Rig.Vm.IsManualSectionMode)) { op.S.Harness("sections did not go on"); return; }
        await op.Rig.SetSpeedAsync(8);
        await Task.Delay(4000);
        double worked = await CoveredAsync(op);
        op.S.Step(FlowOp.F($"working at 8 km/h with the sections on: {worked:0} m² covered in the job, {await HostAreaAsync(op):0} m² worked"));
        if (!await FieldsAndJobsAsync(op, FieldB)) return;
        var pre = op.Rig.Module.Last;
        op.S.Step($"just before Open Field Only the module gets {pre}");
        if (pre is not { } p0 || p0.Sections == 0) { op.S.Harness("the sections were not on before Open Field Only"); await op.StopAsync(); return; }
        if (!await op.TapAsync("#fj-openonly", "Open Field Only")) return;
        long t0 = ModuleListener.NowMs;
        var secOffTask = op.Run.WaitModuleAsync(m => m.Sections == 0, t0, 3000);
        // The new field's coverage, read on the host the moment it is open. The open stops the
        // simulator at the field's origin before it clears the coverage (SetSimulatorCoordinates),
        // so sections left on can't paint it before this read; a tractor still moving could.
        var cov = op.Rig.Services.GetRequiredService<ICoverageMapService>();
        var sim = op.Rig.Services.GetRequiredService<IGpsSimulationService>();
        double areaB = 0, coveredB = 0;
        bool movingB = false;
        bool switched = await op.HostAsync(() =>
        {
            if (op.Rig.Vm.CurrentFieldName != FieldB || op.Rig.State.UI.IsBusy) return false;
            areaB = cov.TotalWorkedArea; coveredB = cov.ActualAreaCovered; movingB = sim.StepDistance != 0;
            return true;
        }, 15000);
        var secOff = await secOffTask;
        // Closed once is not enough: they must still be closed 3 s after Open Field Only.
        await Task.Delay((int)Math.Max(0, 3000 - (ModuleListener.NowMs - t0)));
        bool stayedClosed = secOff is { } sc0 && StayedClosed(op, sc0);
        op.S.Step($"after Open Field Only the module gets {op.Rig.Module.Last}; " + (secOff == null ? "the sections never closed in 3 s"
            : $"the sections closed after {Run.Ms(secOff - t0)}{(stayedClosed ? "" : " and opened again")}"));
        await op.StopAsync();
        await StopWorkAsync(op);
        if (stayedClosed) op.S.Ok("the sections closed at the module after Open Field Only, and stayed closed");
        else op.S.Problem(Severity.Safety, "SWITCHED-FIELD-STILL-SPRAYING", "another field opened at 8 km/h with the sections on by hand: "
            + (secOff == null ? "the module still got sections on 3 s later" : "the sections closed at the module, then opened again"));
        if (switched) op.S.Ok($"'{FieldB}' open"); else { op.S.Problem(Severity.Bug, "FIELD-NOT-SWITCHED", $"Open Field Only '{FieldB}' while working: open is '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}'"); return; }
        op.S.Step(FlowOp.F($"'{FieldB}' opened with {areaB:0} m² worked, {coveredB:0} m² covered{(movingB ? ", the tractor moving" : "")}"));
        // A tractor still moving with sections left on paints a cycle or two before the read: far less than the job had.
        double stray = movingB ? Math.Max(5, worked * 0.05) : 1;
        if (areaB < 1 && coveredB < 1) op.S.Ok("the new field starts with no coverage");
        else if (areaB < stray && coveredB < stray) op.S.Note("the new field starts with a little coverage, painted after it opened: the tractor was still moving");
        else op.S.Problem(Severity.Bug, "COVERAGE-CARRIED-OVER", $"'{FieldB}' opened with coverage from the job");
        // The job kept what was worked before the switch.
        if (!await CloseOpenFieldAsync(op, "discard", $"'{FieldB}'")) return;
        if (!await EnsureJobAsync(op)) return;
        double after = await CoveredAsync(op);
        op.S.Step(FlowOp.F($"resumed: the job has {after:0} m² covered ({worked:0} m² before the switch)"));
        if (after >= worked - Math.Max(5, worked * 0.02)) op.S.Ok("resumed: the job has all the ground covered before the switch");
        else op.S.Problem(Severity.Bug, "COVERAGE-LOST-ON-SWITCH", "the job was worked, then another field was opened: resumed, it has less ground covered than before the switch");
    }

    // ------------------------------------------------------------------ F8, F9, F10

    private static async Task<(string? Opened, string Said)> NewFieldAsync(FlowOp op, string name)
    {
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) await FlowFields.CloseFieldAsync(op, "discard");
        await op.PlaceAsync(FlowFields.At(5, 30, 30), 0);
        await op.ToldAsync(); op.HostSaid();
        if (!await FieldsAndJobsAsync(op, null)) return (null, "");
        if (!await op.TapAsync("#fj-newfield", "New Field")) return (null, "");
        if (!await op.WaitOpenAsync("newfield")) { op.S.Problem(Severity.Bug, "FLOW-MENU-DID-NOT-OPEN", "New Field did not open"); return (null, ""); }
        if (name.Length > 0 && !await op.TypeAsync("#nf-name", name, "the field's name")) return (null, "");
        if (!await op.TapAsync("#nf-create", "Create Field")) return (null, "");
        await op.HostAsync(() => op.Rig.Vm.IsFieldOpen && !op.Rig.State.UI.IsBusy, 4000);
        await Task.Delay(500);
        string? opened = await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen ? op.Rig.Vm.CurrentFieldName : null);
        return (opened, TrackFlows.Said(await op.ToldAsync(), op.HostSaid()));
    }

    private static async Task NamesAsync(FlowOp op)
    {
        // A slash and a comma.
        var (o1, s1) = await NewFieldAsync(op, "North/South, 2");
        if (o1 == null && s1 != "nothing") op.S.Ok($"'North/South, 2': refused, told {s1}");
        else if (o1 == null) op.S.Problem(Severity.Improvement, "FIELD-NAME-SLASH-SILENT", "'North/South, 2': no field made and nothing said", FindingKind.Enhancement);
        else op.S.Note($"'North/South, 2': field '{o1}' made (told {s1})");
        bool panel = await op.IsOpenAsync("newfield");
        op.S.Step($"after it the New Field panel is {(panel ? "still open" : "closed")}");

        // Accents and a comma.
        const string acc = "Campo da Ribeira, Açores";
        var (o2, s2) = await NewFieldAsync(op, acc);
        if (o2 == acc) op.S.Ok($"'{acc}': made and open");
        else op.S.Problem(Severity.Bug, "FIELD-NAME-ACCENTS", $"'{acc}': open is '{o2}' (told {s2})");
        if (o2 != null && await FieldsAndJobsAsync(op, null))
        {
            bool listed = await op.SeeAsync($"[...document.querySelectorAll('#fj-fieldlist .fj-frow .fj-fname')].some(r => r.textContent === {JsonSerializer.Serialize(acc)})", 4000);
            if (listed) op.S.Ok("listed under its name"); else op.S.Problem(Severity.Inconsistency, "FIELD-LIST-STALE", $"'{acc}' is not in the field list");
        }

        // The name of a field that exists.
        var (o3, s3) = await NewFieldAsync(op, Field);
        bool ownJob = Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job));
        if (o3 == null && s3 != "nothing" && ownJob) op.S.Ok($"'{Field}' again: refused, told {s3}; the field and its job are untouched");
        else if (!ownJob) op.S.Problem(Severity.Bug, "FIELD-OVERWRITTEN", $"New Field '{Field}' (a field that exists): its job folder is gone (open '{o3}', told {s3})");
        else op.S.Problem(Severity.Bug, "FIELD-NAME-DUPLICATE", $"New Field '{Field}' (a field that exists): open '{o3}', told {s3}");

        // Nothing.
        var (o4, s4) = await NewFieldAsync(op, "");
        bool still = await op.IsOpenAsync("newfield");
        // AgOpenGPS greys Save out while the name is empty (FormFieldDir): a greyed button says it too.
        bool greyed = await op.PageAsync<bool>("() => { const b = document.getElementById('nf-create'); return !!b && (b.disabled || b.classList.contains('disabled')); }");
        if (o4 == null && s4 != "nothing") op.S.Ok($"no name: told {s4}");
        else if (o4 == null && greyed) op.S.Ok("no name: Create Field is greyed out");
        else if (o4 == null) op.S.Problem(Severity.Improvement, "FIELD-NAME-EMPTY-SILENT", $"Create Field with no name: nothing happens and nothing is said (the panel {(still ? "stays" : "closes")})", FindingKind.Enhancement);
        else op.S.Problem(Severity.Bug, "FIELD-NAME-EMPTY", $"Create Field with no name opened '{o4}'");
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
    }

    private static async Task DriveInAsync(FlowOp op)
    {
        if (!Directory.Exists(op.FieldDir(FieldB))) { op.S.Note($"skipped: no field '{FieldB}' (see F7)"); return; }
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) await FlowFields.CloseFieldAsync(op, "discard");
        await op.PlaceAsync(FlowFields.At(SlotB, 100, 150), 0);
        await op.ToldAsync(); op.HostSaid();
        if (!await op.OpenMenuAsync("ln-fieldops", "fieldops", "Field Operations")) return;
        if (!await op.TapAsync("#fo-drivein", "Drive In")) return;
        bool pick = await op.WaitOpenAsync("dlg-drivepick", 1500);
        if (pick)
        {
            var rows = await op.RowsAsync("#dp-list .dp-row");
            op.S.Step($"Drive In offers: {string.Join(" | ", rows)}");
            if (!await op.TapRowAsync("#dp-list .dp-row", FieldB, $"'{FieldB}'")) return;
        }
        if (await op.HostAsync(() => op.Rig.Vm.CurrentFieldName == FieldB && !op.Rig.State.UI.IsBusy, 10000)) op.S.Ok($"Drive In opened '{FieldB}', the field the tractor is in{(pick ? " (picked from a list)" : "")}");
        else op.S.Problem(Severity.Bug, "DRIVE-IN-FAILED", $"Drive In in '{FieldB}': open is '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}' (told {TrackFlows.Said(await op.ToldAsync(), op.HostSaid())})");
    }

    private static async Task LeaveWorkedAsync(FlowOp op)
    {
        if (!Directory.Exists(op.FieldDir(FieldB))) { op.S.Note($"skipped: no field '{FieldB}' (see F7)"); return; }
        if (await op.Rig.ReadAsync(() => op.Rig.Vm.IsFieldOpen)) await FlowFields.CloseFieldAsync(op, "save");
        if (!await FlowFields.OpenFieldOnlyAsync(op, Field)) return;
        await op.PlaceAsync(FlowFields.At(Slot, 30, 300), 0);
        if (!await PaintAsync(op, 30)) return;
        double painted = await HostAreaAsync(op);
        op.S.Step(FlowOp.F($"{painted:0} m² worked with no job"));
        if (!await FieldsAndJobsAsync(op, FieldB)) return;
        if (!await op.TapAsync("#fj-openonly", "Open Field Only")) return;
        if (!await op.WaitOpenAsync("dlg-unsavedcov", 2000))
        {
            bool switched = await op.HostAsync(() => op.Rig.Vm.CurrentFieldName == FieldB, 5000);
            op.S.Problem(Severity.Bug, "UNSAVED-DROPPED-ON-OPEN", $"another field opened after working with no job: no Unsaved Coverage question ({(switched ? "the coverage was dropped" : "nothing happened")})");
            return;
        }
        op.S.Ok("asked about the unsaved coverage before leaving the field");
        if (!await op.TapAsync("#ucov-cancel", "Cancel")) return;
        await Task.Delay(800);
        var (f, a) = await op.Rig.ReadAsync(() => (op.Rig.Vm.CurrentFieldName, op.Rig.Services.GetRequiredService<ICoverageMapService>().TotalWorkedArea));
        op.S.Step(FlowOp.F($"after Cancel: '{f}' open, {a:0} m² worked ({painted:0} m² before)"));
        if (f == Field && Math.Abs(a - painted) < 1) op.S.Ok("Cancel: still in the field, the coverage kept");
        else op.S.Problem(Severity.Bug, "UNSAVED-CANCEL-LEFT", f == Field ? "Cancel: still in the field, but its worked area changed" : $"Cancel: '{f}' is open, not '{Field}'");
        if (!await FieldsAndJobsAsync(op, FieldB)) return;
        if (!await op.TapAsync("#fj-openonly", "Open Field Only")) return;
        if (!await op.WaitOpenAsync("dlg-unsavedcov", 2000)) { op.S.Problem(Severity.Bug, "UNSAVED-NOT-ASKED", "the second Open Field Only did not ask"); return; }
        if (!await op.TapAsync("#ucov-discard", "Discard")) return;
        if (await op.HostAsync(() => op.Rig.Vm.CurrentFieldName == FieldB && !op.Rig.State.UI.IsBusy, 10000)) op.S.Ok($"Discard: '{FieldB}' opens");
        else op.S.Problem(Severity.Bug, "FIELD-NOT-SWITCHED", $"Discard: open is '{await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName)}'");
    }

    private static async Task DeleteAsync(FlowOp op)
    {
        if (!await EnsureJobAsync(op)) return;
        // Careless: the open field.
        if (!await FieldsAndJobsAsync(op, Field)) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await op.TapAsync("#fj-deletefield", "Delete Field")) return;
        if (!await op.ConfirmAsync(true, "Delete Field")) return;
        await Task.Delay(600);
        var said = TrackFlows.Said(await op.ToldAsync(), op.HostSaid());
        if (Directory.Exists(op.FieldDir(Field)) && await op.Rig.ReadAsync(() => op.Rig.Vm.CurrentFieldName) == Field && said != "nothing") op.S.Ok($"Delete Field on the open field: refused, told {said}");
        else if (Directory.Exists(op.FieldDir(Field))) op.S.Problem(Severity.Improvement, "DELETE-OPEN-FIELD-SILENT", "Delete Field on the open field: nothing deleted and nothing said", FindingKind.Enhancement);
        else { op.S.Problem(Severity.Bug, "OPEN-FIELD-DELETED", "Delete Field deleted the field that is open"); return; }

        // Careless: the open job.
        if (!await FieldsAndJobsAsync(op, Field)) return;
        if (!await op.TapRowAsync("#fj-joblist .fj-jrow", Job, $"the job '{Job}'")) return;
        await op.ToldAsync(); op.HostSaid();
        if (!await op.TapAsync("#fj-deletejob", "Delete Job")) return;
        if (!await op.ConfirmAsync(true, "Delete Job")) return;
        await Task.Delay(600);
        said = TrackFlows.Said(await op.ToldAsync(), op.HostSaid());
        bool jobThere = Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job));
        if (jobThere && said != "nothing") op.S.Ok($"Delete Job on the open job: refused, told {said}");
        else if (jobThere) op.S.Problem(Severity.Improvement, "DELETE-OPEN-JOB-SILENT", "Delete Job on the open job: nothing deleted and nothing said", FindingKind.Enhancement);
        else op.S.Problem(Severity.Bug, "OPEN-JOB-DELETED", "Delete Job deleted the job that is open");

        // Closed: Cancel keeps, Confirm deletes.
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
        if (!await FlowFields.CloseFieldAsync(op, "save")) return;
        if (!await FieldsAndJobsAsync(op, Field)) return;
        if (!await op.TapRowAsync("#fj-joblist .fj-jrow", Job, $"the job '{Job}'")) return;
        if (!await op.TapAsync("#fj-deletejob", "Delete Job")) return;
        if (!await op.ConfirmAsync(false, "Delete Job")) return;
        await Task.Delay(400);
        if (Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job))) op.S.Ok("Delete Job, Cancel: kept");
        else { op.S.Problem(Severity.Bug, "DELETE-CANCEL-DELETED", "Delete Job then Cancel deleted the job"); return; }
        if (!await FieldsAndJobsAsync(op, Field)) return;
        if (!await op.TapRowAsync("#fj-joblist .fj-jrow", Job, $"the job '{Job}'")) return;
        if (!await op.TapAsync("#fj-deletejob", "Delete Job")) return;
        if (!await op.ConfirmAsync(true, "Delete Job")) return;
        bool gone = false;
        for (int k = 0; k < 15 && !gone; k++) { gone = !Directory.Exists(Path.Combine(op.FieldDir(Field), "jobs", Job)); if (!gone) await Task.Delay(200); }
        if (gone) op.S.Ok("Delete Job, Confirm: the job's folder is gone");
        else op.S.Problem(Severity.Bug, "JOB-NOT-DELETED", "Delete Job, Confirm: the job is still on disk");
        if (await op.SeeAsync($"!fieldOps.jobs.some(j => j.taskName === {JsonSerializer.Serialize(Job)})", 3000)) op.S.Ok("and gone from the list");
        else op.S.Problem(Severity.Inconsistency, "JOB-LIST-STALE", $"'{Job}' is still listed after Delete");

        // A field that isn't open: deleted.
        const string acc = "Campo da Ribeira, Açores";
        if (!Directory.Exists(op.FieldDir(acc))) { op.S.Note($"'{acc}' was not made (F8), no field to delete"); return; }
        if (!await FieldsAndJobsAsync(op, acc)) return;
        if (!await op.TapAsync("#fj-deletefield", "Delete Field")) return;
        if (!await op.ConfirmAsync(true, "Delete Field")) return;
        gone = false;
        for (int k = 0; k < 15 && !gone; k++) { gone = !Directory.Exists(op.FieldDir(acc)); if (!gone) await Task.Delay(200); }
        if (gone) op.S.Ok($"Delete Field, Confirm: '{acc}' is gone");
        else op.S.Problem(Severity.Bug, "FIELD-NOT-DELETED", $"Delete Field, Confirm: '{acc}' is still on disk");
        if (await op.SeeAsync($"!fieldOps.fields.some(f => f.name === {JsonSerializer.Serialize(acc)})", 3000)) op.S.Ok("and gone from the list");
        else op.S.Problem(Severity.Inconsistency, "FIELD-LIST-STALE", $"'{acc}' is still listed after Delete");
        await op.Screen.EvalAsync<bool>("() => { lnCloseAll(); return true; }");
    }
}
