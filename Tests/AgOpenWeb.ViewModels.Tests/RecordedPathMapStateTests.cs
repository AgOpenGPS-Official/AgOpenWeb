// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// What the web map draws of a recorded path played back comes from State.RecordedPath (the
/// Scene's projector reads it; the map service the VM also tells is NullMapService on every head):
/// the way to the path while it is driven, and the path in use, a new list whenever it is
/// replaced, so the Scene is sent again. While a path plays it is the route AutoSteer follows (the
/// pipeline was given it at Play), so it is not replaced until playback stops, as AgOpenGPS
/// disables Pick and Record while it drives a path (btnPathGoStop_Click).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class RecordedPathMapStateTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private readonly List<string> _failures = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-recpath-map-" + Guid.NewGuid().ToString("N"));
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();
        _failures.Clear();
        _vm.FailureReported += _failures.Add;

        var dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(dir);
        var field = new Field { Name = "F", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        GeoJsonFieldService.SaveRecordedPath(dir, "North", Route(east: 0));
        GeoJsonFieldService.SaveRecordedPath(dir, "East", Route(east: 30));
        GeoJsonFieldService.SaveCurrentRecordedPath(dir, Route(east: 0));
        _b.FieldService.SetActiveField(field);
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;

        _vm.State.RecordedPath.RecordedPoints = Route(east: 0);
        _vm.State.Vehicle.Easting = 0; _vm.State.Vehicle.Northing = 0; _vm.State.Vehicle.Heading = 0;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>200 points northwards from (east, 20), as RecordedPathAutoSteerTests.</summary>
    private static List<RecPathPoint> Route(double east)
    {
        var pts = new List<RecPathPoint>();
        for (int i = 0; i < 200; i++) pts.Add(new RecPathPoint(east, 20 + i * 0.5, 0, 8, true));
        return pts;
    }

    private List<RecPathPoint> Play()
    {
        _vm.PlayRecordedPathCommand!.Execute(null);
        Assume.That(_vm.State.RecordedPath.IsDrivingRecordedPath, Is.True, "playback started");
        return _vm.State.RecordedPath.RecordedPoints;
    }

    [Test]
    public void Playing_puts_the_way_to_the_path_in_the_state_from_the_tractor()
    {
        Play();

        var rec = _vm.State.RecordedPath;
        Assert.That(rec.IsFollowingDubinsToPath, Is.True);
        Assert.That(rec.DubinsApproachPath, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(rec.DubinsApproachPath[0].Easting, Is.EqualTo(0).Within(1e-9), "it starts at the tractor");
        Assert.That(rec.DubinsApproachPath[0].Northing, Is.EqualTo(0).Within(1e-9));
        Assert.That(rec.DubinsApproachPath[^1].Northing, Is.EqualTo(20).Within(0.6), "it ends at the start of the path");
    }

    [Test]
    public void Stopping_takes_the_way_to_the_path_away()
    {
        Play();
        _vm.StopPlaybackCommand!.Execute(null);

        Assert.That(_vm.State.RecordedPath.IsDrivingRecordedPath, Is.False);
        Assert.That(_vm.State.RecordedPath.DubinsApproachPath, Is.Empty);
    }

    [Test]
    public void Reverse_replaces_the_path_in_use_with_a_new_list()
    {
        var before = _vm.State.RecordedPath.RecordedPoints;
        _vm.ReverseRecordedPathCommand!.Execute(null);

        Assert.That(_vm.State.RecordedPath.RecordedPoints, Is.Not.SameAs(before));
        Assert.That(_vm.State.RecordedPath.RecordedPoints[0].Northing, Is.EqualTo(20 + 199 * 0.5).Within(1e-9));
    }

    [Test]
    public void While_a_path_plays_another_is_not_picked()
    {
        var playing = Play();

        _vm.PickRecordedPathCommand!.Execute("East");
        _vm.SelectedRecFile = "East";

        Assert.That(_vm.State.RecordedPath.RecordedPoints, Is.SameAs(playing), "the map would show a path AutoSteer does not follow");
        Assert.That(_failures, Is.EqualTo(new[] { "Stop the recorded path first", "Stop the recorded path first" }));
        _b.GpsPipelineService.Received(1).SetRecordedPathGuidance(Arg.Any<IReadOnlyList<Vec3>?>(), Arg.Is<IReadOnlyList<Vec3>?>(r => r != null), Arg.Any<int>());

        // Stopped, it is picked as before.
        _vm.StopPlaybackCommand!.Execute(null);
        _vm.SelectedRecFile = "East";
        Assert.That(_vm.State.RecordedPath.RecordedPoints[0].Easting, Is.EqualTo(30).Within(1e-6));
    }

    [Test]
    public void Opening_the_panel_while_a_reversed_path_plays_keeps_it()
    {
        _vm.ReverseRecordedPathCommand!.Execute(null);
        var playing = Play();

        _vm.ShowRecordedPathDialogCommand!.Execute(null);

        Assert.That(_vm.State.RecordedPath.RecordedPoints, Is.SameAs(playing), "reloaded from the file, the Reverse was lost");
        Assert.That(_vm.AvailableRecFiles, Is.EquivalentTo(new[] { "North", "East" }), "the list is still refreshed");
    }

    [Test]
    public void No_recording_starts_while_a_path_plays()
    {
        Play();

        _vm.StartRecordedPathCommand!.Execute(null);

        Assert.That(_vm.IsRecordingPath, Is.False);
        Assert.That(_failures, Is.EqualTo(new[] { "Stop the recorded path first" }));
    }
}
