using System.ComponentModel;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

[TestFixture]
public class TrackManagementTests
{
    [Test]
    public void SavedTracks_IsAccessible()
    {
        var vm = new MainViewModelBuilder().Build();

        Assert.That(vm.SavedTracks, Is.Not.Null);
        Assert.That(vm.SavedTracks, Is.Empty);
    }

    [Test]
    public void SelectedTrack_PropertyChange_FiresNotification()
    {
        var vm = new MainViewModelBuilder().Build();

        bool fired = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedTrack))
                fired = true;
        };

        vm.SelectedTrack = new Track
        {
            Name = "Test",
            Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) }
        };

        Assert.That(fired, Is.True);
    }

    [Test]
    public void SettingSelectedTrackToNull_ClearsHasActiveTrack()
    {
        var vm = new MainViewModelBuilder().Build();

        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) }
        };

        vm.SelectedTrack = track;
        Assert.That(vm.HasActiveTrack, Is.True);

        vm.SelectedTrack = null;
        Assert.That(vm.HasActiveTrack, Is.False);
    }

    [Test]
    public void SelectedTrack_Setter_SetsIsActiveOnTrack()
    {
        var vm = new MainViewModelBuilder().Build();

        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) }
        };

        vm.SelectedTrack = track;

        Assert.That(track.IsActive, Is.True);
    }

    [Test]
    public void PreviousTrack_IsDeactivatedOnNewSelection()
    {
        var vm = new MainViewModelBuilder().Build();

        var track1 = new Track
        {
            Name = "AB1",
            Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) }
        };
        var track2 = new Track
        {
            Name = "AB2",
            Points = new List<Vec3> { new(10, 0, 0), new(10, 100, 0) }
        };

        vm.SelectedTrack = track1;
        vm.SelectedTrack = track2;

        Assert.That(track1.IsActive, Is.False);
        Assert.That(track2.IsActive, Is.True);
    }

    [Test]
    public void SwapAB_ReversesPointsAndHeadings_AndKeepsTheLineInPlace()
    {
        // #104: reversing only the points left each heading pointing the old way.
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        double north = 0, south = Math.PI;
        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3> { new(0, 0, north), new(0, 100, north) },
        };
        vm.SelectedTrack = track;
        vm.State.Guidance.HowManyPathsAway = 3;
        vm.State.Guidance.NudgeOffset = 0.2;
        builder.GpsPipelineService.ClearReceivedCalls();

        vm.SwapABPointsCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(track.Points[0].Northing, Is.EqualTo(100));
            Assert.That(track.Points[1].Northing, Is.EqualTo(0));
            Assert.That(track.Points[0].Heading, Is.EqualTo(south).Within(1e-9));
            Assert.That(track.Points[1].Heading, Is.EqualTo(south).Within(1e-9));
            // "Right" flips with the direction → same physical pass / nudge.
            Assert.That(vm.State.Guidance.HowManyPathsAway, Is.EqualTo(-3));
            Assert.That(vm.State.Guidance.NudgeOffset, Is.EqualTo(-0.2).Within(1e-9));
        });
        // The pipeline gets the swapped track + negated offsets (SetActiveTrack also
        // drops its guidance state).
        builder.GpsPipelineService.Received().SetActiveTrack(track, -3,
            Arg.Is<double>(d => Math.Abs(d + 0.2) < 1e-9), Arg.Any<bool>());
    }
}
