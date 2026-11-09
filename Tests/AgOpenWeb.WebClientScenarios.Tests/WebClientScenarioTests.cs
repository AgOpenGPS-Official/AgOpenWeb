// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// The web client scenarios (README.md): S screens, O a second screen, C the operator's screen
/// goes. One backend for the whole run, a fresh browser context per screen. Writes the findings
/// report to AGOPENWEB_SCENARIO_REPORT (default: the test output folder) and the screenshots
/// beside it. The run fails only when the harness itself breaks; findings are in the report.
/// </summary>
[TestFixture, Category("Scenario"), NonParallelizable]
public class WebClientScenarioTests
{
    [Test]
    [CancelAfter(3_300_000)]
    public async Task Web_client_scenarios()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT");
        if (string.IsNullOrEmpty(report)) report = Path.Combine(TestContext.CurrentContext.WorkDirectory, "web-client-scenarios-report.md");
        var shots = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "web-client-shots");
        if (Directory.Exists(shots)) Directory.Delete(shots, true);
        var dataRoot = Path.Combine(Path.GetTempPath(), "aow-web-scenarios-" + Guid.NewGuid().ToString("N")[..8]);

        await using var browsers = await Browsers.StartAsync();
        await using var rig = await WebClientRig.StartAsync(dataRoot);
        var run = new Run(rig, browsers, shots);
        var setUp = await rig.SetUpJobAsync();
        Assert.That(setUp, Is.Null, "set-up: " + setUp);

        await ScreenScenarios.RunAsync(run);
        await ObserverScenarios.RunAsync(run);
        await ControlLostScenarios.RunAsync(run);
        // The flows (Plans/HARNESS_IMPROVEMENT_PLAN.md 3.5), each group in a field of its own.
        await TrackFlows.RunAsync(run);
        await BoundaryFlows.RunAsync(run);
        await TramFlows.RunAsync(run);
        await FieldFlows.RunAsync(run);
        await SectionBarFlows.RunAsync(run);
        await UTurnFlows.RunAsync(run);

        string commit = Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: >= 7 } sha ? sha[..7] : "local";
        string header = "Commit " + commit + ", Chromium " + browsers.Version + " (headless). The backend runs in the test process with every service real; "
            + "the screens are real browser pages on its web server; the tractor is AgOpenWeb's simulator at 8 km/h on an AB line with an "
            + "8 m boom in 8 sections; what the machine gets is read from PGN 254 on the module port (127.0.0.1:8888). "
            + "A button counts as tappable when its centre is on the screen and not under another element (after scrolling its bar, if that bar scrolls); "
            + Run.F($"touch targets under {Layout.MinTouchPx:0} px are listed as an improvement. ")
            + Run.F($"The flows (T, B, R, F, X, U) each work in a field of their own (T, R, F, X and U make it from a KML file; B makes its own and imports the KML's outline in B4), on the 10\" tablet by taps and typing ({FlowOp.KeyDelayMs} ms between keys); ")
            + "each finding says its kind: app defect, harness, parity or enhancement. Screenshots are in `web-client-shots/`.\n";
        var text = Journal.Write(run.Rows, header, clock.Elapsed);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, text);
        TestContext.Out.WriteLine($"Report: {report}");

        try { Directory.Delete(dataRoot, true); } catch { }
        var broken = run.Rows.SelectMany(r => r.Findings.Where(f => f.Code == "HARNESS").Select(f => r.Code + ": " + f.Message)).ToList();
        Assert.That(broken, Is.Empty, "the harness could not run some scenarios:\n" + string.Join("\n", broken));
    }
}
