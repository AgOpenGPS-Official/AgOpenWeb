// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

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
    // 100 minutes for one operator's jobs (the workflow runs the careful and the careless operator
    // as two jobs, each on its own runner): the careful set alone took 57 min on a GitHub runner
    // on 2026-11-09, so both in one hour lost the whole run, report and all.
    [Test]
    [CancelAfter(6_000_000)]
    public async Task Field_jobs(CancellationToken ct)
    {
        // One run at a time on a machine: the emulated machine module binds UDP 8888 (with address
        // reuse, as the firmware's port), so a second run's module hears the first run's section
        // commands and both runs' valves are wrong without any error.
        using var exclusive = OneRunAtATime();

        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        string op = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_OPERATOR") ?? "";
        var journal = new Journal();

        var wanted = new List<(string Op, JobSpec Job)>();
        if (op is "" or "careful") wanted.AddRange(CarefulOperator.Jobs().Where(j => Jobs.Want(only, j.Code)).Select(j => ("Careful", j)));
        if (op is "" or "careless") wanted.AddRange(CarelessOperator.Jobs().Where(j => Jobs.Want(only, j.Code)).Select(j => ("Careless", j)));
        await RunAllAsync(journal, wanted, ct);

        await WriteReportAsync(journal, journal.ToMarkdown(), "field-job-scenarios-report.md");
    }

    /// <summary>
    /// Runs the jobs in order. When the test's time is up, the jobs not run are a HARNESS finding
    /// (a run that did not reach its situations is the harness's fault) and the report is still
    /// written: a time limit used to lose the whole run, with nothing to read the next morning.
    /// </summary>
    internal static async Task RunAllAsync(Journal journal, IReadOnlyList<(string Op, JobSpec Job)> wanted, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < wanted.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                var left = wanted.Skip(i).Select(w => w.Op + " " + w.Job.Code).ToList();
                var sc = new Scenario(wanted[i].Op, "TIME-UP", "The run's time limit was reached",
                    "The test's CancelAfter limit was reached before every job had run.");
                sc.Harness(string.Create(CultureInfo.InvariantCulture,
                    $"the time limit was reached after {started.Elapsed.TotalMinutes:0} min and {i} job(s): {left.Count} not run ({string.Join(", ", left)})"));
                journal.Add(sc.Row);
                return;
            }
            await Jobs.RunAsync(journal, wanted[i].Op, wanted[i].Job);
        }
    }

    /// <summary>Writes the report (whatever the token says: the report is the point of the run) and
    /// fails the test on a HARNESS finding.</summary>
    internal static async Task WriteReportAsync(Journal journal, string report, string defaultName)
    {
        string path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, defaultName);
        await File.WriteAllTextAsync(path, report, CancellationToken.None);
        TestContext.Out.WriteLine(report);
        TestContext.Out.WriteLine("Report: " + path);

        var harness = journal.Findings.Where(f => f.Key == "HARNESS").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed: " + string.Join("; ", harness.Select(h => h.Value.First.Message)));
    }

    internal static FileStream OneRunAtATime()
    {
        string path = Path.Combine(Path.GetTempPath(), "agopenweb-field-job-scenarios.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            Assert.Fail("Another field or path job run is going on this machine (" + path + " is held): the two would share the "
                + "emulated module's UDP port 8888 and each other's section commands. Run them one after the other.");
            throw;
        }
    }
}
