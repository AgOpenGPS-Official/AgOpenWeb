// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Whole jobs on lines that are not straight (<see cref="PathJobs"/>): curves, the boundary as
/// a loop, a water pivot, contour, a recorded path. Long, so [Category("Scenario")]. Switches:
/// AGOPENWEB_SCENARIO_ONLY=P1,P3 picks jobs; AGOPENWEB_SCENARIO_REPORT=path.md;
/// AGOPENWEB_SCENARIO_PACE=ms per fix (8).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class PathJobScenarioTests
{
    public const string Title = "Path job scenarios: curves, laps, a water pivot, contour and a recorded path";
    public const string Intro =
        "The field job harness on lines that are not straight. The careful operator works Olival lap by lap along its outer boundary " +
        "(Boundary Curve) and in curves along its west boundary (Bnd. Curve), a field under a water pivot in circles (the pivot typed in, " +
        "and recorded by driving), the same field and Olival with contour, and plays back a route recorded by hand. Where AgOpenWeb makes no " +
        "U-turn (a closed loop, a pivot, contour) the operator moves one pass in at the end of each lap and stops when there is no lap left. " +
        "AgOpenWeb's clock runs on the job's simulated time. Codes and job names stay the same from run to run, so two reports can be diffed.";

    [Test]
    [CancelAfter(5_400_000)]
    public async Task Path_jobs(CancellationToken ct)
    {
        // The field jobs' lock: both fixtures' emulated module binds UDP 8888.
        using var exclusive = FieldJobScenarioTests.OneRunAtATime();
        string? only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        var journal = new Journal();
        foreach (var job in PathJobs.Jobs())
        {
            if (!Jobs.Want(only, job.Code)) continue;
            ct.ThrowIfCancellationRequested();
            await Jobs.RunAsync(journal, "Careful", job);
        }

        string report = journal.ToMarkdown(Title, Intro);
        string path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "path-job-scenarios-report.md");
        await File.WriteAllTextAsync(path, report, ct);
        TestContext.Out.WriteLine(report);
        TestContext.Out.WriteLine("Report: " + path);

        var harness = journal.Findings.Where(f => f.Key == "HARNESS").ToList();
        Assert.That(harness, Is.Empty, "the harness itself failed: " + string.Join("; ", harness.Select(h => h.Value.First.Message)));
    }
}
