// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Drive AB and Draw AB with A and B on one spot. There is no direction between two equal
/// points: atan2(0, 0) made a line due north that the operator never drew (Set B pressed
/// before moving, a double tap on the map). Both now refuse B like Lat/Lon A-B does, and keep
/// waiting for a B at least 1 m away.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class AbPointsApartTests
{
    private MainViewModel _vm = null!;
    private string? _failure;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var store = ConfigurationStore.Instance;
        store.NumSections = 1;
        store.Tool.SetSectionWidth(0, 600);
        _vm = new MainViewModelBuilder().Build();
        _vm.FailureReported += m => _failure = m;
        _vm.State.Vehicle.Easting = 500;
        _vm.State.Vehicle.Northing = 1000;
        _vm.State.Vehicle.Heading = 90; // facing east: a line made here must not run north
    }

    [Test]
    public void DriveAB_SetBWithoutMoving_IsRefused_AndBCanStillBeSetAfterMoving()
    {
        _vm.StartDriveABCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(null); // A
        _vm.SetABPointCommand.Execute(null);  // B, on the same spot

        Assert.Multiple(() =>
        {
            Assert.That(_failure, Does.Contain("at least 1 m apart"));
            Assert.That(_vm.SavedTracks, Is.Empty, "no line from two equal points");
            Assert.That(_vm.CurrentABCreationMode, Is.EqualTo(ABCreationMode.DriveAB), "still driving the line");
            Assert.That(_vm.CurrentABPointStep, Is.EqualTo(ABPointStep.SettingPointB), "A is kept; B is still wanted");
        });

        _vm.State.Vehicle.Easting = 600; // drove 100 m east
        _vm.SetABPointCommand.Execute(null);

        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(_vm.CurrentABCreationMode, Is.EqualTo(ABCreationMode.None));
        var p = _vm.SavedTracks[0].Points;
        Assert.That(Math.Abs(p[^1].Northing - p[0].Northing), Is.LessThan(1e-6), "the line runs east, the way it was driven");
    }

    [Test]
    public void DrawAB_TappingTheSameSpotTwice_IsRefused_AndTheNextTapElsewhereMakesTheLine()
    {
        _vm.StartDrawABModeCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(new Position { Easting = 10, Northing = 20 }); // A
        _vm.SetABPointCommand.Execute(new Position { Easting = 10.2, Northing = 20.1 }); // a double tap: 22 cm away

        Assert.Multiple(() =>
        {
            Assert.That(_failure, Does.Contain("at least 1 m apart"));
            Assert.That(_vm.SavedTracks, Is.Empty);
            Assert.That(_vm.CurrentABPointStep, Is.EqualTo(ABPointStep.SettingPointB));
        });

        _vm.SetABPointCommand.Execute(new Position { Easting = 110, Northing = 20 }); // B, 100 m east

        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(1));
        var p = _vm.SavedTracks[0].Points;
        Assert.That(Math.Abs(p[^1].Northing - p[0].Northing), Is.LessThan(1e-6), "the line runs east, from A to B");
    }
}
