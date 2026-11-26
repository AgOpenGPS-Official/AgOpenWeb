// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

using AgOpenWeb.Models.State;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A U-turn that was refused, could not be made or was missed is shown to the operator
/// (bug report "manual U-turn does nothing", 2026-11-11): the status message alone never
/// reaches the web client, a reported failure does, as a toast.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton
public class YouTurnNoticeTests
{
    [Test]
    public void AYouTurnNotice_IsReportedToTheOperator()
    {
        var vm = new MainViewModelBuilder().Build();
        var said = new List<string>();
        vm.FailureReported += said.Add;

        vm.ApplyGpsCycleResult(new GpsCycleResult());
        Assert.That(said, Is.Empty, "a cycle with nothing to say");

        vm.ApplyGpsCycleResult(new GpsCycleResult
        {
            YouTurnNotice = "Not enough room — turn would put the tool past the boundary",
        });
        Assert.That(said, Is.EqualTo(new[] { "Not enough room — turn would put the tool past the boundary" }));
    }
}
