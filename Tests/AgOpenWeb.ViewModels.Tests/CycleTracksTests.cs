// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Cycle Lines forward and back (#336), as AgOpenGPS btnCycleLines / btnCycleLinesBk:
/// hidden tracks are skipped, the ends wrap, auto track goes off, and a track change
/// stops AutoSteer and auto U-turn.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class CycleTracksTests
{
    private static Track Line(string name, double e, bool visible = true) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = visible,
        Points = new() { new Vec3(e, 0, 0), new Vec3(e, 100, 0) },
    };

    private static MainViewModel WithTracks(params Track[] tracks)
    {
        var vm = new MainViewModelBuilder().Build();
        foreach (var t in tracks) vm.SavedTracks.Add(t);
        return vm;
    }

    [Test]
    public void Forward_SkipsHiddenAndWraps()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false), Line("C", 40));
        vm.ActivateTrackAt(0);

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("C"));

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
    }

    [Test]
    public void Back_SkipsHiddenAndWraps()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false), Line("C", 40));
        vm.ActivateTrackAt(0);

        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("C"));

        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
    }

    [Test]
    public void Cycle_TurnsAutoTrackOff()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20));
        vm.ActivateTrackAt(0);
        vm.IsAutoTrackEnabled = true;

        vm.CycleABLinesBackCommand!.Execute(null);

        Assert.That(vm.IsAutoTrackEnabled, Is.False);
    }

    [Test]
    public void TrackChange_StopsAutoSteerAndUTurn()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20));
        vm.ActivateTrackAt(0);
        vm.IsAutoSteerEngaged = true;
        vm.IsYouTurnEnabled = true;

        vm.CycleABLinesCommand!.Execute(null);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(vm.IsYouTurnEnabled, Is.False);
    }

    [Test]
    public void OnlyOneVisibleTrack_NothingChanges()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false));
        vm.ActivateTrackAt(0);
        vm.IsAutoSteerEngaged = true;

        vm.CycleABLinesCommand!.Execute(null);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
        Assert.That(vm.IsAutoSteerEngaged, Is.True, "no track change, so steering carries on");
    }
}
