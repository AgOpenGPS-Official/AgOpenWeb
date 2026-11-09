// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Whole jobs with the testers' rigs and tracks (<see cref="RigJobs"/>): implements shifted to
/// one side, a curve recorded by driving, a wide implement skipping rows. Long, so
/// [Category("Scenario")]. Switches: AGOPENWEB_SCENARIO_ONLY=J8,J10 picks jobs;
/// AGOPENWEB_SCENARIO_REPORT=path.md; AGOPENWEB_SCENARIO_PACE=ms per fix (8).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class RigJobScenarioTests
{
    public const string Title = "Rig job scenarios: offset implements, a recorded curve, skip rows, a tester's own rigs, a combine, TBT, a spreader, a harrow, a plough, a roller";
    public const string Intro =
        "The field job harness with the rigs and tracks testers use: a mower shifted 2.5 m to one side of the tractor (mounted, and " +
        "trailed), a curve recorded by driving it, a 21 m implement skipping a row at every U-turn, a tester's own tractor with the front " +
        "sprayer of unequal sections and the semi-mounted drill (with the settings the suggested-settings page gives them), a combine, a seed drill behind its tank (TBT), a manure spreader, a disc harrow, a reversible plough and a seeding roller on a bar on the lift arms. The careful operator, the same " +
        "simulated machine, emulated machine module and truth coverage as the field jobs; on these jobs the implement is also measured " +
        "against the pass it should be on. Codes and job names stay the same from run to run, so two reports can be diffed.";

    [Test]
    [CancelAfter(9_000_000)]
    public async Task Rig_jobs(CancellationToken ct)
    {
        // The field jobs' lock: every fixture's emulated module binds UDP 8888.
        using var exclusive = FieldJobScenarioTests.OneRunAtATime();
        await Jobs.RunFixtureAsync(RigJobs.Jobs().Select(j => ("Careful", j)), "rig-job-scenarios-report.md", j => j.ToMarkdown(Title, Intro), ct);
    }
}
