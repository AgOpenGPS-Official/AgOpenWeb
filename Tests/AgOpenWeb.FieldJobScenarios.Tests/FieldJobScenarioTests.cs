// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Whole jobs on real field shapes with AutoSteer, automatic U-turns and automatic sections.
/// Long (each job is a field driven at about 12x real time), so [Category("Scenario")]. Switches:
/// AGOPENWEB_SCENARIO_ONLY=J1,J3 picks jobs; AGOPENWEB_SCENARIO_OPERATOR=careful|careless;
/// AGOPENWEB_SCENARIO_REPORT=path.md; AGOPENWEB_SCENARIO_PACE=ms per fix (8).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class FieldJobScenarioTests
{
    [Test]
    [CancelAfter(3_600_000)]
    public async Task Field_jobs(CancellationToken ct)
    {
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        string op = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_OPERATOR") ?? "";
        var journal = new Journal();

        if (op is "" or "careful")
            foreach (var job in CarefulOperator.Jobs())
            {
                if (!Jobs.Want(only, job.Code)) continue;
                ct.ThrowIfCancellationRequested();
                await Jobs.RunAsync(journal, "Careful", job);
            }
        if (op is "" or "careless")
            foreach (var job in CarelessOperator.Jobs())
            {
                if (!Jobs.Want(only, job.Code)) continue;
                ct.ThrowIfCancellationRequested();
                await Jobs.RunAsync(journal, "Careless", job);
            }

        string report = journal.ToMarkdown();
        string path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "field-job-scenarios-report.md");
        await File.WriteAllTextAsync(path, report, ct);
        TestContext.Out.WriteLine(report);
        TestContext.Out.WriteLine("Report: " + path);

        var harness = journal.Findings.Where(f => f.Key == "HARNESS").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed: " + string.Join("; ", harness.Select(h => h.Value.First.Message)));
    }
}
