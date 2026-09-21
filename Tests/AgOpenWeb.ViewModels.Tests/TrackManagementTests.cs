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

    private static Track AB(string name, double e = 0) => new()
    {
        Name = name,
        Points = new List<Vec3> { new(e, 0, 0), new(e, 100, 0) },
    };

    [Test]
    public void DeleteContours_RemovesOnlyContourTracks_AndKeepsCoverage()
    {
        // #107: this used to wipe ALL coverage and every nudge, with no confirmation.
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        var ab = AB("AB1");
        var contour = Track.FromContour("Contour 1", new List<Vec3> { new(0, 0, 0), new(5, 5, 0), new(10, 5, 0) });
        vm.SavedTracks.Add(ab);
        vm.SavedTracks.Add(contour);
        vm.SelectedTrack = contour;

        vm.DeleteContoursCommand!.Execute(null);

        Assert.That(vm.SavedTracks, Is.EquivalentTo(new[] { ab }));
        Assert.That(vm.SelectedTrack, Is.Null, "the deleted contour can't stay selected");
        builder.CoverageMapService.DidNotReceive().ClearAll();
    }

    [Test]
    public void SaveTracks_RightAfterSelectingANewTrack_DoesNotStampThePreviousTracksNudge()
    {
        // #107: State.Guidance is the cycle's mirror; right after a switch it still holds the
        // previous track's pass/nudge, which SaveTracksToFile used to write onto the new track.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-save-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var builder = new MainViewModelBuilder();
            builder.FieldService.ActiveField.Returns(new AgOpenWeb.Models.Field { Name = "F", DirectoryPath = dir });
            var vm = builder.Build();
            var oldTrack = AB("Old");
            var newTrack = AB("New", 50);
            vm.SavedTracks.Add(oldTrack);
            vm.SavedTracks.Add(newTrack);
            vm.SelectedTrack = oldTrack;
            // Mirror of the cycle for the OLD track: pass 3 + 0.2 m nudge.
            vm.State.Guidance.ActiveTrack = oldTrack;
            vm.State.Guidance.HowManyPathsAway = 3;
            vm.State.Guidance.NudgeOffset = 0.2;

            vm.SelectedTrack = newTrack;
            vm.SaveTracksToFile();

            Assert.That(newTrack.NudgeDistance, Is.EqualTo(0), "new track keeps its own (zero) offset");

            // Once the cycle mirrors values FOR the new track, they are persisted.
            vm.State.Guidance.ActiveTrack = newTrack;
            vm.State.Guidance.HowManyPathsAway = 0;
            vm.State.Guidance.NudgeOffset = 0.1;
            vm.SaveTracksToFile();
            Assert.That(newTrack.NudgeDistance, Is.EqualTo(0.1).Within(1e-9));
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
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
