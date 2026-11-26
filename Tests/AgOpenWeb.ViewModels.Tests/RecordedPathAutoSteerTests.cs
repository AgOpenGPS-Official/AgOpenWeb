// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A played recorded route is a line AutoSteer can follow (P7, as AgOpenGPS steers the module
/// along it): with no track, AutoSteer engages while the route plays; the GPS pipeline gets the
/// approach and the route together; starting and stopping playback turn AutoSteer off.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class RecordedPathAutoSteerTests
{
    private static (MainViewModel vm, MainViewModelBuilder b) Playing()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        var pts = new List<RecPathPoint>();
        for (int i = 0; i < 200; i++) pts.Add(new RecPathPoint(0, 20 + i * 0.5, 0, 8, true));
        vm.State.RecordedPath.RecordedPoints = pts;
        vm.State.Vehicle.Easting = 0; vm.State.Vehicle.Northing = 0; vm.State.Vehicle.Heading = 0;
        vm.PlayRecordedPathCommand!.Execute(null);
        Assert.That(vm.State.RecordedPath.IsDrivingRecordedPath, Is.True, "playback started");
        return (vm, b);
    }

    [Test]
    public void AutoSteer_engages_on_a_playing_route_with_no_track()
    {
        var (vm, b) = Playing();
        Assert.That(vm.SelectedTrack, Is.Null);
        Assert.That(vm.IsAutoSteerAvailable, Is.True);
        vm.ToggleAutoSteerCommand!.Execute(null);
        Assert.That(vm.IsAutoSteerEngaged, Is.True);
        b.GpsPipelineService.Received().SetRecordedPathGuidance(Arg.Is<IReadOnlyList<Vec3>?>(p => p != null && p.Count >= 2), Arg.Is<IReadOnlyList<Vec3>?>(p => p != null && p.Count == 200), Arg.Any<int>());
    }

    [Test]
    public void Stopping_playback_turns_AutoSteer_off_and_takes_the_route_away()
    {
        var (vm, b) = Playing();
        vm.ToggleAutoSteerCommand!.Execute(null);
        vm.StopPlaybackCommand!.Execute(null);
        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(vm.IsAutoSteerAvailable, Is.False, "no track, no route");
        b.GpsPipelineService.Received().SetRecordedPathGuidance(null, null, 0);
    }
}
