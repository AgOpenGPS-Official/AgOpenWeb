// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The U-turn skip button cycles Normal → Alternative → Ignore worked tracks, as AgOpenGPS's
/// btnYouSkipEnable. The two skip modes skip at least one row; back in Normal the operator's own
/// row count comes back (AgOpenGPS resets rowSkipsWidth to the saved setting on every tap), so
/// switching skipping on and off again doesn't leave the U-turns skipping a row (nightly C5).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class UTurnSkipModeTests
{
    [Test]
    public void Skip_on_then_back_to_normal_turns_into_the_next_pass_again()
    {
        var vm = new MainViewModelBuilder().Build();
        Assert.That(vm.UTurnSkipRows, Is.EqualTo(0));

        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        Assert.That((vm.UTurnSkipMode, vm.UTurnSkipRows), Is.EqualTo((1, 1)), "alternative skips at least one row");

        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        Assert.That((vm.UTurnSkipMode, vm.UTurnSkipRows), Is.EqualTo((2, 1)), "ignore worked tracks skips at least one row");

        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        Assert.That((vm.UTurnSkipMode, vm.UTurnSkipRows), Is.EqualTo((0, 0)), "back in normal the U-turn goes into the next pass");
    }

    [Test]
    public void The_operators_own_row_count_is_kept_through_the_modes()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.CycleUTurnSkipRowsCommand!.Execute(null);
        vm.CycleUTurnSkipRowsCommand!.Execute(null);
        Assert.That(vm.UTurnSkipRows, Is.EqualTo(2));

        for (int tap = 0; tap < 3; tap++) vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        Assert.That((vm.UTurnSkipMode, vm.UTurnSkipRows), Is.EqualTo((0, 2)));
    }

    [Test]
    public void A_row_count_chosen_while_skipping_stays_in_normal()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);           // alternative, 1 row
        vm.CycleUTurnSkipRowsCommand!.Execute(null);            // the operator picks 2
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);           // normal
        Assert.That((vm.UTurnSkipMode, vm.UTurnSkipRows), Is.EqualTo((0, 2)));
    }
}
