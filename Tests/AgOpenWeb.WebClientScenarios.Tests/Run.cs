// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>What every scenario shares: the backend, the browser, where the screenshots go, the
/// report rows; and the operator actions several scenarios do the same way.</summary>
public sealed class Run
{
    public WebClientRig Rig { get; }
    public Browsers Browsers { get; }
    public string ShotDir { get; }
    public List<ScenarioRow> Rows { get; } = new();
    private readonly string? _only;
    private int _started;

    public Run(WebClientRig rig, Browsers browsers, string shotDir)
    {
        Rig = rig; Browsers = browsers; ShotDir = shotDir;
        _only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
    }

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>False when AGOPENWEB_SCENARIO_ONLY names other scenarios (comma-separated codes or a prefix: "C", "S1,O2").</summary>
    public bool Wanted(string code) => string.IsNullOrEmpty(_only)
        || _only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(o => code.Equals(o, StringComparison.OrdinalIgnoreCase)
                         || (o.Length == 1 && code.StartsWith(o, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Run one scenario with its own clean start; a harness fault is recorded, not thrown.</summary>
    public async Task ScenarioAsync(string group, string code, string title, string story, Func<Scenario, Task> body,
        bool onTheRunsLine = true)
    {
        if (!Wanted(code)) return;
        var s = new Scenario(group, code, title, story);
        // Before anything of the scenario: the app's log and GPS log are cleared for it.
        var evidence = new Evidence(EvidenceFolder, code);
        long startMs = ModuleListener.NowMs;
        try
        {
            // Each scenario on ground not yet worked, so a section in Auto is open because the
            // boom is over unworked ground, and closes only when something closes it: four starts
            // 130 m apart along the line, then two passes (16 m) over, and so on. The flows work
            // in fields of their own and place the tractor themselves.
            if (onTheRunsLine)
            {
                int n = _started++;
                await Rig.PlaceTractorAsync(40 + n % 4 * 130.0, WebClientRig.LineE + n / 4 * 16.0);
            }
            Rig.TakeEvents();
            await body(s);
        }
        catch (Exception ex)
        {
            s.Harness("the scenario stopped: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
        }
        foreach (var e in Rig.TakeEvents().Where(e => e.Kind is RigEventKind.Exception or RigEventKind.Hang).Take(3))
            s.Problem(Severity.Bug, "HOST-EXCEPTION", e.Text.Split('\n')[0]);
        await KeepEvidenceAsync(s, evidence, startMs);
        Rows.Add(s.Row);
    }

    /// <summary>The evidence folder beside the report.</summary>
    public static string EvidenceFolder => Evidence.FolderFor(ReportPath.For("web-client-scenarios-report.md"));

    /// <summary>
    /// A scenario with a finding (every scenario with AGOPENWEB_SCENARIO_EVIDENCE=all) leaves the
    /// app's bug report dump at its end, as a tester's report does (Tests/ScenarioShared/Evidence.cs):
    /// the app's log with the browsers' lines (a deadman says when it last heard the page and last
    /// sent it a frame), plus what only the harness saw: the control changes, every change of the
    /// module's PGN 254, and the timers that ran late in the pages, the test process and the host
    /// loop (<see cref="StallWatch"/>). C12's lost control (21 Nov 2026) left nothing to tell the
    /// page from the runner.
    /// </summary>
    private async Task KeepEvidenceAsync(Scenario s, Evidence evidence, long startMs)
    {
        try
        {
            if (Evidence.Off || (s.Row.Findings.Count == 0 && !Evidence.All)) return;
            var inv = CultureInfo.InvariantCulture;
            string work = Path.Combine(Path.GetTempPath(), "aow-web-evidence-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                string control = Path.Combine(work, "control_changes.txt");
                File.WriteAllLines(control, new[] { "# Control changes seen by the host, from the scenario's start (harness ms): held or ended, and by whom or why." }
                    .Concat(Rig.ControlLog.Where(c => c.Ms >= startMs)
                        .Select(c => string.Create(inv, $"{c.Ms - startMs,8} ms  {(c.Held ? "taken" : "ended")}  {c.Reason}"))));
                string module = Path.Combine(work, "module_pgn254.txt");
                File.WriteAllLines(module, new[] { "# Every change of what the steer module got in PGN 254, from the scenario's start (harness ms)." }
                    .Concat(Rig.Module.Since(startMs).Select(m => string.Create(inv, $"{m.Ms - startMs,8} ms  {m}"))));
                string stalls = Path.Combine(work, "timers_late.txt");
                File.WriteAllText(stalls, StallWatch.Text(startMs));

                var notes = new StringBuilder(Evidence.NotesHeader("Web client scenarios", s.Row.Code, s.Row.Title, turns: false));
                notes.Append("## Findings\n\n");
                foreach (var f in s.Row.Findings) notes.Append("- ").Append(f.Severity).Append(" `").Append(f.Code).Append("`: ").Append(f.Message).Append('\n');
                if (s.Row.Findings.Count == 0) notes.Append("None (kept for every scenario).\n");
                notes.Append("\n## Steps\n\n```\n");
                foreach (var st in s.Row.Steps) notes.Append(st).Append('\n');
                notes.Append("```\n\nThe harness's own files in `attachments/`: `control_changes.txt`, `module_pgn254.txt`, `timers_late.txt` (")
                     .Append(StallWatch.GapMs.ToString(inv)).Append(" ms or more: the pages, the test process, the host loop).\n");

                string? zip = await evidence.SaveAsync(null, notes.ToString(), Rig.Services, Rig.State, Rig.Store,
                    a => Rig.RunAsync(a), new[] { control, module, stalls });
                if (zip != null) s.Note("evidence: `evidence/" + Path.GetFileName(zip) + "`");
                foreach (var e in evidence.Errors) s.Note("evidence not kept: " + e);
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        catch (Exception ex) { s.Note("evidence not kept: " + ex.Message); }
        finally { evidence.Close(); }
    }

    public Task<Screen> OpenAsync(Device d, string name, bool ownWifi = false, string locale = "en-US")
        => Screen.OpenAsync(Browsers, Rig, d, name, ownWifi, locale);

    public string ShotPath(string file)
    {
        Directory.CreateDirectory(ShotDir);
        return Path.Combine(ShotDir, file);
    }

    public async Task ShotAsync(Scenario s, Screen screen, string file)
    {
        await screen.ShotAsync(ShotPath(file));
        s.Shot(file);
    }

    /// <summary>Script errors the page logged: each is a finding (the web client must not throw).</summary>
    public static void PageErrors(Scenario s, Screen screen)
    {
        foreach (var e in screen.TakeErrors().Distinct().Take(4))
            s.Problem(Severity.Bug, "PAGE-SCRIPT-ERROR", $"{screen.Name}: {e.Split('\n')[0]}");
        // Aerial tiles and a field's background picture are fetched on demand and may not exist
        // (no internet on a tractor, no picture for a field); anything else the page asks for must.
        var missing = screen.TakeMissing().Distinct().ToList();
        var expected = missing.Where(m => m.Contains("/tile", StringComparison.OrdinalIgnoreCase) || m.Contains("/sat", StringComparison.OrdinalIgnoreCase)
                                          || m.Contains("backpic", StringComparison.OrdinalIgnoreCase)).ToList();
        if (expected.Count > 0) s.Note($"{screen.Name}: {expected.Count} on-demand picture(s) not there (expected without internet or a background picture): {string.Join(", ", expected.Take(3))}");
        foreach (var m in missing.Except(expected).Take(4))
            s.Problem(Severity.Bug, "PAGE-RESOURCE-MISSING", $"{screen.Name}: the server answered {m}");
    }

    /// <summary>Wait until the module's PGN 254 matches; the time it first did (harness ms), or null.</summary>
    public async Task<long?> WaitModuleAsync(Func<ModuleListener.Steer, bool> match, long fromMs, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Rig.Module.First(fromMs, match) is { } hit) return hit.Ms;
            await Task.Delay(20);
        }
        return Rig.Module.First(fromMs, match)?.Ms;
    }

    /// <summary>The tractor moving on the line, the operator's screen holding control: tap AutoSteer
    /// and the section master (Auto) on that screen. Returns why it could not, or null.</summary>
    public async Task<string?> StartWorkAsync(Scenario s, Screen op, bool sections = true)
    {
        await Rig.SetSpeedAsync(8);
        await Task.Delay(600);
        if (!await TapUntilAsync(op, "#rn-steer", () => Rig.Vm.IsAutoSteerEngaged))
            return "AutoSteer did not engage from the operator's AutoSteer button";
        if (sections && !await TapUntilAsync(op, "#rn-auto", () => Rig.Vm.IsSectionMasterOn))
            return "the section master did not go to Auto from the operator's button";
        long t = ModuleListener.NowMs;
        var steering = await WaitModuleAsync(m => m.Steering && (!sections || m.Sections != 0), t, 3000);
        if (steering == null) return "the module never got AutoSteer on" + (sections ? " with sections on" : "") + $" (last PGN 254: {Rig.Module.Last})";
        s.Step($"working: 8 km/h on the AB line, AutoSteer on, sections Auto; the module gets {Rig.Module.Last}");
        return null;
    }

    /// <summary>Tap a control (scrolling its bar first if it has to) until the host shows the result.</summary>
    public async Task<bool> TapUntilAsync(Screen screen, string selector, Func<bool> done, int timeoutMs = 2000)
    {
        await screen.ScrollToAsync(selector);
        var tap = await screen.TapAsync(selector);
        if (!tap.Hit) return false;
        return await Rig.WaitUntilAsync(done, timeoutMs);
    }

    /// <summary>The steering and the sections at the module after a screen went away: how long until
    /// AutoSteer was off (status 0) and every section closed, measured from <paramref name="t0"/>.</summary>
    public async Task<(long? SteerOffMs, long? SectionsOffMs)> MachineStopsAsync(long t0, int timeoutMs)
    {
        var steer = await WaitModuleAsync(m => !m.Steering, t0, timeoutMs);
        var secs = await WaitModuleAsync(m => m.Sections == 0, t0, Math.Max(200, timeoutMs - (int)(ModuleListener.NowMs - t0)));
        return (steer - t0, secs - t0);
    }

    public static string Ms(long? ms) => ms is { } v ? F($"{v / 1000.0:0.00} s") : "never";
}
