// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Auto Track must not override the track the operator picked: it switches to the nearest
/// track every second while AutoSteer is off, so picking one by hand turns it off
/// (AgOpenGPS btnTrack / btnCycleLines), and it's off by default (CTrack.isAutoTrack).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class AutoTrackManualPickTests
{
    private static Track Line(string name, double e) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = true,
        Points = new() { new Vec3(e, 0, 0), new Vec3(e, 100, 0) },
    };

    [Test]
    public void AutoTrack_IsOffByDefault() => Assert.That(new DisplayConfig().AutoTrack, Is.False);

    [Test]
    public void ActivatingATrack_TurnsAutoTrackOff()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(Line("A", 0));
        vm.SavedTracks.Add(Line("B", 20));
        vm.IsAutoTrackEnabled = true;

        vm.ActivateTrackAt(1);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
        Assert.That(vm.IsAutoTrackEnabled, Is.False, "the operator's pick must stick");
    }

    [Test]
    public void AutoTrack_DoesNotSwitchWhileOff()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(Line("A", 0));
        vm.SavedTracks.Add(Line("B", 20));
        vm.ActivateTrackAt(1);   // "B", and Auto Track off

        // Sitting next to "A": with Auto Track off nothing moves the choice.
        vm.UpdateAutoTrackSelection(new Models.Position { Easting = 0, Northing = 50, Heading = 0 });

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
    }
}
