// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// The update and settings-survival scenarios: the upgrader (older releases, then this build),
/// then the restarter (this build only), into one report. The test fails only when the harness
/// breaks; the findings are the product.
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class UpdateScenarioTests
{
    [Test]
    [CancelAfter(2_400_000)]
    public async Task Upgrader_and_restarter()
    {
        var journal = new Journal();
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        if (string.IsNullOrWhiteSpace(only)) only = null;
        string? who = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_OPERATOR");
        journal.Environment_.Add("This build: " + ThisVersion());

        if (who is null or "restarter") await RestarterOperator.RunAsync(journal, only);
        if (who is null or "upgrader") await UpgraderOperator.RunAsync(journal, only);

        string report = ReportPath.For("update-scenarios-report.md");
        File.WriteAllText(report, journal.ToMarkdown());
        TestContext.Out.WriteLine("Report: " + report);
        foreach (var g in journal.Findings.Values.OrderBy(v => v.First.Severity))
            TestContext.Out.WriteLine($"{g.First.Severity} {g.First.Code} x{g.Count}: {g.First.Message}");

        var harness = journal.Findings.Values.Where(v => v.First.Code == "HARNESS").Select(v => v.First.Message.Split('\n')[0] + " (" + string.Join(", ", v.Places) + ")").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed");
        Assert.That(journal.Rows.Count, Is.GreaterThan(0), "nothing was run (AGOPENWEB_SCENARIO_ONLY matched nothing?)");
    }

    private static string ThisVersion()
    {
        var asm = typeof(AgOpenWeb.RemoteWiring.WebBackend).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return string.IsNullOrEmpty(info) ? "unknown" : info;
    }
}
