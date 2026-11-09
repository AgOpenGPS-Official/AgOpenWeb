// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

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
        Rows.Add(s.Row);
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
