// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The units pass (<see cref="ImperialJobs"/>): the careful job in metric, the same typed in feet
/// and inches through the real web client, and centimetres typed into the inch boxes. Needs
/// Chromium for Playwright (`pwsh bin/Debug/net10.0/playwright.ps1 install chromium`, or
/// AGOPENWEB_CHROMIUM=path). Long, so [Category("Scenario")]. Switches: AGOPENWEB_SCENARIO_ONLY=U0,U1;
/// AGOPENWEB_SCENARIO_REPORT=path.md; AGOPENWEB_SCENARIO_PACE=ms per fix (8).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class ImperialJobScenarioTests
{
    public const string Title = "Imperial job scenarios: the same job typed in feet and inches";
    public const string Intro =
        "AgOpenWeb keeps and sends everything in metric; imperial is a conversion in the web client, made when a value is typed and " +
        "when a box is filled. So the operator here types every setting with a unit into the web client's own box (headless " +
        "Chromium, App Settings > Imperial), and the job that follows must come out as the metric one (U0). A careless operator " +
        "types centimetres into the inch boxes: what AgOpenWeb keeps and what the machine module gets. The careful operator, the " +
        "same simulated machine, emulated machine module and truth coverage as the field jobs. Codes and job names stay the same " +
        "from run to run, so two reports can be diffed.";

    [Test]
    [CancelAfter(3_600_000)]
    public async Task Imperial_jobs(CancellationToken ct)
    {
        // The field jobs' lock: every fixture's emulated module binds UDP 8888.
        using var exclusive = FieldJobScenarioTests.OneRunAtATime();
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        var journal = new Journal();
        foreach (var job in ImperialJobs.Jobs())
        {
            if (!Jobs.Want(only, job.Code)) continue;
            ct.ThrowIfCancellationRequested();
            await Jobs.RunAsync(journal, job.Code == "U2" ? "Careless" : "Careful", job);
        }

        string report = journal.ToMarkdown(Title, Intro);
        string path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "imperial-job-scenarios-report.md");
        await File.WriteAllTextAsync(path, report, ct);
        TestContext.Out.WriteLine(report);
        TestContext.Out.WriteLine("Report: " + path);

        var harness = journal.Findings.Where(f => f.Key == "HARNESS").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed: " + string.Join("; ", harness.Select(h => h.Value.First.Message)));
    }
}
