// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.MachineModuleScenarios.Tests;

/// <summary>
/// The machine module scenarios: the careful operator, then the careless one, into one
/// report. The test fails only when the harness breaks; the findings are the product.
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class MachineModuleScenarioTests
{
    [Test]
    [CancelAfter(1_200_000)]
    public async Task Careful_and_careless_operator()
    {
        var journal = new Journal();
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        if (string.IsNullOrWhiteSpace(only)) only = null;
        string? who = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_OPERATOR");

        if (who is null or "careful") await CarefulOperator.RunAsync(journal, only);
        if (who is null or "careless") await CarelessOperator.RunAsync(journal, only);

        string report = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "machine-module-scenarios-report.md");
        File.WriteAllText(report, journal.ToMarkdown());
        TestContext.Out.WriteLine("Report: " + report);
        foreach (var g in journal.Findings.Values.OrderBy(v => v.First.Severity))
            TestContext.Out.WriteLine($"{g.First.Severity} {g.First.Code} x{g.Count}: {g.First.Message}");

        var harness = journal.Findings.Values.Where(v => v.First.Code == "HARNESS").Select(v => v.First.Message + " (" + string.Join(", ", v.Places) + ")").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed");
        Assert.That(journal.Rows.Count, Is.GreaterThan(0), "nothing was run (AGOPENWEB_SCENARIO_ONLY matched nothing?)");
    }
}
