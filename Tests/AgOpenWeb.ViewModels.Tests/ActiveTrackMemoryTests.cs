// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#148: tapping a track row activates it, and Auto Track can't override that.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ActiveTrackMemoryTests
{
    private static Track Line(string name, double e) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = true,
        Points = new() { new Vec3(e, 0, 0), new Vec3(e, 100, 0) },
    };

    [Test]
    public void TappingARow_ActivatesThatTrack_AndStopsAutoTrack()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(Line("A", 0));
        vm.SavedTracks.Add(Line("B", 20));
        vm.IsAutoTrackEnabled = true;

        vm.SelectTrackAt(1);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
        Assert.That(vm.HasActiveTrack, Is.True);
        Assert.That(vm.IsAutoTrackEnabled, Is.False);
    }

    [Test]
    public void AHiddenTrack_CannotBeActivated()
    {
        var vm = new MainViewModelBuilder().Build();
        var hidden = Line("A", 0);
        hidden.IsVisible = false;
        vm.SavedTracks.Add(hidden);

        vm.SelectTrackAt(0);

        Assert.That(vm.SelectedTrack, Is.Null);
    }

    [Test]
    public void OutOfRangeIndex_IsIgnored()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(Line("A", 0));
        vm.SelectTrackAt(5);
        Assert.That(vm.SelectedTrack, Is.Null);
    }
}
