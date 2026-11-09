// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careless operator during a U-turn (<see cref="TurnInterventions"/>): settings changed, skip
/// rows, the arrow, a nudge, reversing, AutoSteer off and on, and U-turns by hand. Long, so
/// [Category("Scenario")]. Switches: AGOPENWEB_SCENARIO_ONLY=T1,T11 picks jobs;
/// AGOPENWEB_SCENARIO_REPORT=path.md; AGOPENWEB_SCENARIO_PACE=ms per fix (8).
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class TurnInterventionScenarioTests
{
    public const string Title = "Turn intervention scenarios: the careless operator during a U-turn";
    public const string Intro =
        "The field job harness's Olival job (the Case Puma, the 8 m sprayer, AB line at 159°, headland 16 m) with one thing done while " +
        "AgOpenWeb turns, or just before or after it: a U-turn setting changed (radius, distance from boundary, style), skip rows set during " +
        "the turn and with the turn planned, the arrow tapped twice, a nudge, reversing in and out of a turn, AutoSteer off and on half way " +
        "round, and U-turns made by hand with the manual buttons at the ends and in the middle of a pass. For each, the turn the operator acted " +
        "on is followed: the side and pass it was planned for, how far AgOpenWeb's path moved near the tractor, and the pass the implement is on " +
        "after it. Codes and job names stay the same from run to run, so two reports can be diffed.";

    [Test]
    [CancelAfter(5_400_000)]
    public async Task Turn_interventions(CancellationToken ct)
    {
        // The field jobs' lock: every fixture's emulated module binds UDP 8888.
        using var exclusive = FieldJobScenarioTests.OneRunAtATime();
        await Jobs.RunFixtureAsync(TurnInterventions.Jobs().Select(j => ("Careless", j)), "turn-intervention-scenarios-report.md", j => j.ToMarkdown(Title, Intro), ct);
    }
}
