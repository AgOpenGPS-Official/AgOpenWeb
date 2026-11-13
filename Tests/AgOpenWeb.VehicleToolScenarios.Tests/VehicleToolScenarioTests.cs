// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The Vehicle &amp; Tool scenarios: the careful operator, the careless one and the settings sweep, into one
/// report. The test fails only when the harness breaks; the findings are the product.
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class VehicleToolScenarioTests
{
    [Test]
    public async Task Careful_and_careless_operator()
    {
        var journal = new Journal();
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        if (string.IsNullOrWhiteSpace(only)) only = null;
        string? who = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_OPERATOR");

        // One operator, or several comma-separated ("careful,careless"): the nightly runs the two
        // operators and the sweep as two jobs side by side.
        bool Wants(string w) => who is null || who.Split(',', StringSplitOptions.TrimEntries).Contains(w);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Progress(string what) => TestContext.Progress.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {what}");
        if (Wants("careful")) { Progress("careful operator"); await CarefulOperator.RunAsync(journal, only); }
        if (Wants("careless")) { Progress("careless operator"); await CarelessOperator.RunAsync(journal, only); }
        if (Wants("sweep")) { Progress("settings sweep"); await SettingsSweep.RunAsync(journal, only, Progress); }
        Progress("done");

        string report = ReportPath.For("vehicle-tool-scenarios-report.md");
        File.WriteAllText(report, journal.ToMarkdown());
        TestContext.Out.WriteLine("Report: " + report);
        foreach (var g in journal.Findings.Values.OrderBy(v => v.First.Severity))
            TestContext.Out.WriteLine($"{g.First.Severity} {g.First.Code} x{g.Count}: {g.First.Message}");

        var harness = journal.Findings.Values.Where(v => v.First.Code == "HARNESS").Select(v => v.First.Message + " (" + string.Join("; ", v.Places) + ")").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed");
        Assert.That(journal.Drives.Count + journal.Sweep.Count, Is.GreaterThan(0), "nothing was driven (AGOPENWEB_SCENARIO_ONLY matched nothing?)");
    }
}
