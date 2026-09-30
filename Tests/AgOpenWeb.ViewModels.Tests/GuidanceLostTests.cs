// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #169: when the service reports guidance lost, AutoSteer must come off in the app too
/// (the operator re-engages deliberately) and the operator must be told why.
/// </summary>
[TestFixture, NonParallelizable]
public class GuidanceLostTests
{
    [Test]
    public void GuidanceLost_turns_AutoSteer_off_and_reports_why()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerAvailable = true;
        vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(vm.IsAutoSteerEngaged, Is.True);
        string? msg = null; vm.FailureReported += m => msg = m;

        b.AutoSteerService.GuidanceLost += Raise.Event<EventHandler>(b.AutoSteerService, EventArgs.Empty);

        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(msg, Does.StartWith("AutoSteer off: no GPS/guidance update"));
        b.AutoSteerService.Received().Disengage();
    }

    [Test]
    public void GuidanceLost_when_already_off_only_reports()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        string? msg = null; vm.FailureReported += m => msg = m;

        b.AutoSteerService.GuidanceLost += Raise.Event<EventHandler>(b.AutoSteerService, EventArgs.Empty);

        Assert.That(vm.IsAutoSteerEngaged, Is.False, "must not toggle AutoSteer ON");
        Assert.That(msg, Is.Not.Null);
    }
}
