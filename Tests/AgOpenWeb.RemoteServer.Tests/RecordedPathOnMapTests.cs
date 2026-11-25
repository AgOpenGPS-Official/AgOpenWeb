// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// A recorded path played back: the web map drew neither the way from the tractor to the path
/// (the Dubins approach) nor the path itself unless it was a saved track with "Rec paths" on, so
/// AutoSteer followed lines the operator could not see. The VM gave them to the map service
/// only, and every head registers NullMapService. The Scene now carries both from
/// State.RecordedPath, as AgOpenGPS draws them (CRecordedPath.DrawDubins, DrawRecordedLine).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class RecordedPathOnMapTests
{
    private static (SceneProjector Projector, ApplicationState State) Make()
    {
        var state = new ApplicationState();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = "" });
        var projector = new SceneProjector(state, Substitute.For<ISectionControlService>(),
            Substitute.For<IToolPositionService>(), new ConfigurationStore(), Substitute.For<ICoverageMapService>(),
            Substitute.For<IJobService>(), Substitute.For<IConfigurationService>(), Substitute.For<IAutoSteerService>(),
            Substitute.For<ISmartWasCalibrationService>(), Substitute.For<IUdpCommunicationService>(),
            Substitute.For<INtripProfileService>(), Substitute.For<IFieldService>(), settings,
            Substitute.For<IVehicleProfileService>(), Substitute.For<IPersistentStateService>());
        return (projector, state);
    }

    /// <summary>A route of 200 points northwards from (0, 20), as RecordedPathAutoSteerTests.</summary>
    private static List<RecPathPoint> Route()
    {
        var pts = new List<RecPathPoint>();
        for (int i = 0; i < 200; i++) pts.Add(new RecPathPoint(0, 20 + i * 0.5, 0, 8, true));
        return pts;
    }

    /// <summary>What MainViewModel.StartDrivingRecordedPath leaves in the state.</summary>
    private static List<Vec3> StartPlayback(ApplicationState state)
    {
        var approach = Enumerable.Range(0, 40).Select(i => new Vec3(i * 0.5, i * 0.5, 0)).ToList();
        var rec = state.RecordedPath;
        rec.DubinsApproachPath = approach;
        rec.IsFollowingDubinsToPath = true;
        rec.IsFollowingRecPath = false;
        rec.IsDrivingRecordedPath = true;
        return approach;
    }

    [Test]
    public void While_the_tractor_drives_to_the_recorded_path_the_way_to_it_is_on_the_map()
    {
        var (projector, state) = Make();
        state.RecordedPath.RecordedPoints = Route();
        long before = projector.SceneFingerprint();
        var approach = StartPlayback(state);

        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the Scene is sent again when playback starts");
        var scene = projector.BuildScene(1);
        Assert.That(scene.UTurnPath, Is.Not.Null, "the way to the path is drawn");
        Assert.That(scene.UTurnPath!.Select(p => (p.E, p.N)), Is.EqualTo(approach.Select(p => (p.Easting, p.Northing))));
        Assert.That(scene.RecordedPaths, Has.Count.EqualTo(1), "the path itself is drawn");
        Assert.That(scene.RecordedPaths[0], Has.Count.EqualTo(200));
    }

    [Test]
    public void On_the_route_the_way_to_it_goes_and_the_route_stays()
    {
        var (projector, state) = Make();
        state.RecordedPath.RecordedPoints = Route();
        StartPlayback(state);
        long approaching = projector.SceneFingerprint();

        // MainViewModel.UpdateDubinsApproach at the end of the approach: the list cleared in place.
        var rec = state.RecordedPath;
        rec.IsFollowingDubinsToPath = false;
        rec.IsFollowingRecPath = true;
        rec.DubinsApproachPath.Clear();

        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(approaching), "the Scene is sent again");
        var scene = projector.BuildScene(2);
        Assert.That(scene.UTurnPath, Is.Null);
        Assert.That(scene.RecordedPaths, Has.Count.EqualTo(1));
    }

    [Test]
    public void The_path_in_use_is_drawn_with_Rec_paths_off_and_goes_when_turned_off()
    {
        var (projector, state) = Make();
        Assert.That(state.FieldTools.ShowRecordedPaths, Is.False);
        long none = projector.SceneFingerprint();

        // Picked in the Playback list (MainViewModel.OnRecFileSelected): not played yet.
        state.RecordedPath.RecordedPoints = Route();
        long picked = projector.SceneFingerprint();
        Assert.That(picked, Is.Not.EqualTo(none));
        var scene = projector.BuildScene(1);
        Assert.That(scene.RecordedPaths, Has.Count.EqualTo(1));
        Assert.That(scene.RecordedPaths[0][0].N, Is.EqualTo(20).Within(1e-9));
        Assert.That(scene.UTurnPath, Is.Null, "no way to it before playback, as AgOpenGPS");

        // Turn off (TurnOffRecordedPathCommand): the list cleared in place.
        state.RecordedPath.RecordedPoints.Clear();
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(picked));
        Assert.That(projector.BuildScene(2).RecordedPaths, Is.Empty);
    }

    [Test]
    public void A_reversed_path_sends_the_Scene_again()
    {
        var (projector, state) = Make();
        state.RecordedPath.RecordedPoints = Route();
        long before = projector.SceneFingerprint();

        // ReverseRecordedPathCommand: a new list of the same points the other way.
        var reversed = Route();
        reversed.Reverse();
        state.RecordedPath.RecordedPoints = reversed;

        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before));
        Assert.That(projector.BuildScene(1).RecordedPaths[0][0].N, Is.EqualTo(20 + 199 * 0.5).Within(1e-9));
    }

    [Test]
    public void A_U_turn_path_is_drawn_as_before()
    {
        var (projector, state) = Make();
        state.YouTurn.TurnPath = Enumerable.Range(0, 10).Select(i => new Vec3(5, i, 0)).ToList();

        var scene = projector.BuildScene(1);

        Assert.That(scene.UTurnPath, Has.Count.EqualTo(10));
        Assert.That(scene.UTurnPath![0].E, Is.EqualTo(5).Within(1e-9));
    }
}
