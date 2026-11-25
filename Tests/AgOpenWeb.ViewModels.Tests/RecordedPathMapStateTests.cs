// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// What the web map draws of a recorded path played back comes from State.RecordedPath (the
/// Scene's projector reads it; the map service the VM also tells is NullMapService on every head):
/// the way to the path while it is driven, and the path in use, a new list whenever it is
/// replaced, so the Scene is sent again.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class RecordedPathMapStateTests
{
    private static MainViewModel Loaded()
    {
        var vm = new MainViewModelBuilder().Build();
        var pts = new List<RecPathPoint>();
        for (int i = 0; i < 200; i++) pts.Add(new RecPathPoint(0, 20 + i * 0.5, 0, 8, true));
        vm.State.RecordedPath.RecordedPoints = pts;
        vm.State.Vehicle.Easting = 0; vm.State.Vehicle.Northing = 0; vm.State.Vehicle.Heading = 0;
        return vm;
    }

    [Test]
    public void Playing_puts_the_way_to_the_path_in_the_state_from_the_tractor()
    {
        var vm = Loaded();
        vm.PlayRecordedPathCommand!.Execute(null);

        var rec = vm.State.RecordedPath;
        Assert.That(rec.IsDrivingRecordedPath && rec.IsFollowingDubinsToPath, Is.True);
        Assert.That(rec.DubinsApproachPath, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(rec.DubinsApproachPath[0].Easting, Is.EqualTo(0).Within(1e-9), "it starts at the tractor");
        Assert.That(rec.DubinsApproachPath[0].Northing, Is.EqualTo(0).Within(1e-9));
        var end = rec.DubinsApproachPath[^1];
        Assert.That(end.Northing, Is.EqualTo(20).Within(0.6), "it ends at the start of the path");
    }

    [Test]
    public void Stopping_takes_the_way_to_the_path_away()
    {
        var vm = Loaded();
        vm.PlayRecordedPathCommand!.Execute(null);
        vm.StopPlaybackCommand!.Execute(null);

        Assert.That(vm.State.RecordedPath.IsDrivingRecordedPath, Is.False);
        Assert.That(vm.State.RecordedPath.DubinsApproachPath, Is.Empty);
    }

    [Test]
    public void Reverse_replaces_the_path_in_use_with_a_new_list()
    {
        var vm = Loaded();
        var before = vm.State.RecordedPath.RecordedPoints;
        vm.ReverseRecordedPathCommand!.Execute(null);

        Assert.That(vm.State.RecordedPath.RecordedPoints, Is.Not.SameAs(before));
        Assert.That(vm.State.RecordedPath.RecordedPoints[0].Northing, Is.EqualTo(20 + 199 * 0.5).Within(1e-9));
    }
}
