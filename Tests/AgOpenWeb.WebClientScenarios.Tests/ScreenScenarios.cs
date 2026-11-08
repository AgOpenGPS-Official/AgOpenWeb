// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// S: one screen at a time, holding control, with a job running. Does everything the operator
/// needs fit, can every button be tapped, do the menus open inside the screen, and do the
/// AutoSteer and section buttons work by touch? (Checklist 12.4, 12.5, 12.8.)
/// </summary>
public static class ScreenScenarios
{
    public const string Group = "S: screens";

    /// <summary>The menus an operator opens in the cab, by the taps that open them.</summary>
    private static readonly (string Name, string[] Taps)[] Menus =
    {
        ("File menu", new[] { "#ln-filemenu" }),
        ("Screen & Alerts", new[] { "#ln-screenalerts" }),
        ("Tools", new[] { "#ln-tools" }),
        ("Vehicle & Tool", new[] { "#ln-vehicle" }),
        ("Vehicle settings", new[] { "#ln-vehicle", "#vth-vehconfig" }),
        ("Tool settings", new[] { "#ln-vehicle", "#vth-toolconfig" }),
        ("AutoSteer", new[] { "#ln-autosteer" }),
        ("AutoSteer, full", new[] { "#ln-autosteer", "#as-expand" }),
        ("Field Operations", new[] { "#ln-fieldops" }),
        ("Field Tools", new[] { "#ln-fieldtools" }),
        ("Network", new[] { "#ln-network" }),
    };

    public static async Task RunAsync(Run run)
    {
        var devices = new[]
        {
            ("S1", Device.Tablet10), ("S2", Device.Pc720), ("S3", Device.Tablet7), ("S4", Device.Screen5),
            ("S5", Device.PhoneLandscape), ("S6", Device.PhonePortrait), ("S7", Device.TabletPortrait),
        };
        foreach (var (code, d) in devices)
            await run.ScenarioAsync(Group, code, $"{d.Name} ({d.Size})",
                $"The only screen, holding control, {d.Size} CSS px{(d.Touch ? ", touch" : ", mouse")}{(d.Phone ? ", phone browser" : "")}; a job running on the AB line.",
                s => OneScreenAsync(run, s, code, d, "en-US"));

        await run.ScenarioAsync(Group, "S8", "5\" cab screen in Portuguese",
            "As S4 with the app in Portuguese (pt-PT), the longest labels on the smallest screen.",
            async s =>
            {
                await run.Rig.SendAsync("app.setLanguage", "pt");
                try { await OneScreenAsync(run, s, "S8", Device.Screen5, "pt-PT"); }
                finally { await run.Rig.SendAsync("app.setLanguage", "en"); }
            });
    }

    private static async Task OneScreenAsync(Run run, Scenario s, string code, Device d, string locale)
    {
        await using var screen = await run.OpenAsync(d, d.Name, locale: locale);
        if (!await screen.WaitReadyAsync()) { s.Harness("the page did not connect within 20 s"); return; }
        if (!await screen.WaitForAsync("iHoldControl", 5000))
            s.Problem(Severity.Bug, "SOLE-SCREEN-NO-CONTROL", $"the only screen did not get control within 5 s: {await screen.ViewAsync()}");
        await run.Rig.SetSpeedAsync(8);
        await Task.Delay(1500);
        var lay = await Layout.MeasureAsync(screen);
        s.Step($"page {lay.PageWidth}×{lay.PageHeight} px on a {d.Size} screen; renderer '{lay.Renderer}', {lay.Frames} frames/s; {lay.Controls.Count} buttons in the bars");

        if (lay.PageWidth > d.Width + 1 || lay.PageHeight > d.Height + 1)
            s.Problem(Severity.Layout, "PAGE-SCROLLS", $"the page is {lay.PageWidth}×{lay.PageHeight} px on a {d.Size} screen: it scrolls, and the map with it");
        else s.Ok("the page fits the screen (no page scrolling)");

        // The bars: every button reachable, by a tap or after scrolling its bar.
        var lost = lay.Controls.Where(c => !c.Reachable).ToList();
        foreach (var g in lost.GroupBy(c => c.Group))
            s.Problem(Severity.Layout, "BUTTON-UNREACHABLE",
                $"{g.Key}: {g.Count()} of {lay.Controls.Count(c => c.Group == g.Key)} buttons can't be tapped: "
                + string.Join("; ", g.Take(6).Select(c => $"{Label(c)} {c.How}")) + (g.Count() > 6 ? "; …" : ""));
        var scroll = lay.Controls.Where(c => c.How == "after scrolling its bar").ToList();
        if (scroll.Count > 0)
            s.Note($"{scroll.Count} buttons need their bar scrolled first: " + string.Join(", ", scroll.GroupBy(c => c.Group).Select(g => $"{g.Key} {g.Count()}")));
        if (lost.Count == 0) s.Ok($"all {lay.Controls.Count} buttons of the bars can be tapped" + (scroll.Count > 0 ? " (some after scrolling)" : ""));

        // A finger in a moving cab.
        if (d.Touch)
        {
            var small = lay.Controls.Where(c => c.Reachable && c.MinSide < Layout.MinTouchPx).ToList();
            if (small.Count > 0)
                s.Problem(Severity.Improvement, "SMALL-TOUCH-TARGET",
                    $"{small.Count} buttons are under {Layout.MinTouchPx:0} px on their short side: "
                    + string.Join(", ", small.GroupBy(c => c.Group).Select(g => $"{g.Key}: " + string.Join(" ", g.Take(5).Select(c => $"{Label(c)} {c.Box}")))));
            else s.Ok($"every button is at least {Layout.MinTouchPx:0} px");
        }

        // The readouts the operator glances at.
        foreach (var r in lay.Readouts)
        {
            if (!r.OnScreen) s.Problem(Severity.Layout, "READOUT-OFF-SCREEN", $"the {r.Name} ('{r.Text}') is not on the screen");
            else if (r.Cut) s.Problem(r.Name == "field / job line" ? Severity.Improvement : Severity.Layout, "READOUT-CUT", $"the {r.Name} is cut: '{r.Text}'");
        }
        if (lay.Readouts.All(r => r.OnScreen && !r.Cut)) s.Ok("role, speed, heading, GPS fix, field line and cross-track are on the screen, uncut");

        // The two buttons that move the machine, by touch.
        if (await run.TapUntilAsync(screen, "#rn-steer", () => run.Rig.Vm.IsAutoSteerEngaged))
        {
            s.Ok("tapping AutoSteer engages it");
            await run.ShotAsync(s, screen, $"{code}-{d.Code}.png");
            if (await run.TapUntilAsync(screen, "#rn-steer", () => !run.Rig.Vm.IsAutoSteerEngaged)) s.Ok("tapping it again disengages it");
            else s.Problem(Severity.Bug, "AUTOSTEER-TAP-FAILED", "a second tap on AutoSteer did not disengage it");
        }
        else
        {
            await run.ShotAsync(s, screen, $"{code}-{d.Code}.png");
            s.Problem(Severity.Layout, "AUTOSTEER-TAP-FAILED", $"tapping the AutoSteer button did not engage AutoSteer ({(await screen.TapAsync("#rn-steer")).Why})");
        }
        if (await run.TapUntilAsync(screen, "#rn-auto", () => run.Rig.Vm.IsSectionMasterOn)
            && await run.TapUntilAsync(screen, "#rn-auto", () => !run.Rig.Vm.IsSectionMasterOn))
            s.Ok("tapping the section master turns the sections to Auto and back off");
        else s.Problem(Severity.Layout, "SECTION-MASTER-TAP-FAILED", "the section master button did not switch Auto on and off by touch");
        await run.Rig.SetSpeedAsync(0);

        // The menus.
        foreach (var (name, taps) in Menus)
            await MenuAsync(run, s, screen, d, name, taps);

        // A phone turns around.
        if (d.Phone)
        {
            await screen.Page.SetViewportSizeAsync(d.Height, d.Width);
            await Task.Delay(800);
            var turned = await Layout.MeasureAsync(screen);
            var view = await screen.ViewAsync();
            s.Step($"turned to {d.Height}×{d.Width}: {view}, {turned.Controls.Count(c => !c.Reachable)} buttons unreachable");
            if (!view.HoldsControl) s.Problem(Severity.Bug, "ROTATE-LOST-CONTROL", "turning the phone lost control: " + view);
            else s.Ok("turning the phone keeps control and redraws");
            await screen.Page.SetViewportSizeAsync(d.Width, d.Height);
        }

        Run.PageErrors(s, screen);
    }

    private static async Task MenuAsync(Run run, Scenario s, Screen screen, Device d, string name, string[] taps)
    {
        await screen.EvalAsync<object?>("() => { lnCloseAll(); return null; }");
        foreach (var sel in taps)
        {
            await screen.ScrollToAsync(sel);
            var tap = await screen.TapAsync(sel);
            if (!tap.Hit)
            {
                s.Problem(Severity.Layout, "MENU-WONT-OPEN", $"{name}: its button {sel} can't be tapped ({tap.Why})");
                return;
            }
            await Task.Delay(350);
        }
        var panel = await Layout.OpenPanelAsync(screen);
        if (panel is not { } p) { s.Problem(Severity.Bug, "MENU-WONT-OPEN", $"{name}: tapping {string.Join(" then ", taps)} opened no panel"); return; }
        var lost = p.Controls.Where(c => !c.Reachable).ToList();
        string where = Run.F($"#{p.Id} {p.W:0}×{p.H:0} px at {p.X:0},{p.Y:0}");
        if (p.X < -1 || p.Y < -1 || p.X + p.W > d.Width + 1 || p.Y + p.H > d.Height + 1)
            s.Note($"{name}: the panel ({where}) runs past the screen's edge");
        if (lost.Count > 0)
        {
            bool close = lost.Any(c => c.Name.Contains("close", StringComparison.OrdinalIgnoreCase) || c.Title is "✕" or "×" || c.Title.StartsWith("‹", StringComparison.Ordinal));
            s.Problem(Severity.Layout, "MENU-CONTROL-UNREACHABLE",
                $"{name} ({where}): {lost.Count} of {p.Controls.Count} controls can't be reached{(close ? ", including its close/back button" : "")}: "
                + string.Join("; ", lost.Take(5).Select(c => $"{Label(c)} {c.How}")) + (lost.Count > 5 ? "; …" : ""));
            if (d.Phone || d.Height <= 600) await run.ShotAsync(s, screen, $"{s.Row.Code}-{d.Code}-{Slug(name)}.png");
        }
        else s.Ok($"{name}: all {p.Controls.Count} controls reachable ({where})");
        await screen.EvalAsync<object?>("() => { lnCloseAll(); return null; }");
    }

    private static string Label(Control c) => c.Name.StartsWith('#') || c.Name.StartsWith('[') ? c.Name + (c.Title.Length > 0 && c.Title.Length <= 24 ? $" ('{c.Title}')" : "") : $"'{c.Name}'";

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Replace("--", "-").Trim('-');
}
