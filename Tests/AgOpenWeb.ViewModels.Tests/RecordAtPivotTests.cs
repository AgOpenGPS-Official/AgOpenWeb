// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A curve is recorded where the pivot drives, as AgOpenGPS records pivotAxlePos: the cycle's
/// position (antenna-to-pivot shift, roll and drift applied), not the antenna's raw fix. From the
/// fix, the curve sat beside the drive by the antenna's lateral offset and the roll.
/// </summary>
[TestFixture, NonParallelizable]
public class RecordAtPivotTests
{
    [Test]
    public void Curve_points_are_the_pivot_not_the_antenna()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        IReadOnlyList<(double Easting, double Northing)>? recorded = null;
        b.MapService.SetRecordingPoints(Arg.Do<IReadOnlyList<(double Easting, double Northing)>>(p => recorded = p));

        vm.StartCurveRecordingCommand!.Execute(null);
        for (int i = 0; i < 4; i++)
        {
            // The pivot runs north along E = 10; the antenna is 1 m to its right.
            vm.State.Vehicle.UpdateFromGps(new Position { Easting = 10, Northing = 5 * i, Heading = 0 }, 4, 12, 0.8, 1);
            b.GpsService.GpsDataUpdated += Raise.Event<EventHandler<GpsData>>(b.GpsService, new GpsData
            {
                CurrentPosition = new Position { Easting = 11, Northing = 5 * i, Heading = 0, Latitude = 1 },
                FixQuality = 4,
                IsValid = true,
            });
        }

        Assert.That(recorded, Is.Not.Null.And.Count.EqualTo(4));
        Assert.That(recorded!.Select(p => p.Easting), Is.All.EqualTo(10.0));
    }

    // Deleting or hiding the active track turns AutoSteer off, as cycling to another track does:
    // clearing the track alone left AutoSteer engaged with nothing to follow.
    [TestCase(true)]
    [TestCase(false)]
    public void Deleting_or_hiding_the_active_track_turns_AutoSteer_off(bool delete)
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new AgOpenWeb.Models.Base.Vec3(0, -100, 0), new AgOpenWeb.Models.Base.Vec3(0, 100, 0));
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;
        vm.IsAutoSteerAvailable = true;
        vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(vm.IsAutoSteerEngaged, Is.True);

        if (delete) vm.DeleteTrackAt(0);
        else { track.IsVisible = false; vm.OnTrackVisibilityChanged(); }

        Assert.Multiple(() =>
        {
            Assert.That(vm.IsAutoSteerEngaged, Is.False);
            Assert.That(vm.SelectedTrack, Is.Null);
        });
        b.AutoSteerService.Received().Disengage();
    }
}
