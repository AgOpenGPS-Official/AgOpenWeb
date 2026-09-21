using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#109: the web Tracks manager acts on the highlighted row, like AgOpenGPS,
/// not on whatever track happens to be active.</summary>
[TestFixture]
public class TrackHighlightActionTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-vmtrk-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { System.IO.Directory.Delete(_dir, true); } catch { } }

    private MainViewModel VmWithTracks()
    {
        var builder = new MainViewModelBuilder();
        builder.FieldService.ActiveField.Returns(new Field { Name = "F", DirectoryPath = _dir });
        var vm = builder.Build();
        vm.SavedTracks.Add(Track.FromABLine("A", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SavedTracks.Add(Track.FromABLine("B", new Vec3(10, 0, 0), new Vec3(10, 100, 0)));
        return vm;
    }

    [Test]
    public void DeleteTrackAt_DeletesTheHighlightedTrack_AndKeepsTheActiveOne()
    {
        var vm = VmWithTracks();
        var a = vm.SavedTracks[0];
        vm.SelectedTrack = a;

        vm.DeleteTrackAt(1);

        Assert.That(vm.SavedTracks.Select(t => t.Name), Is.EqualTo(new[] { "A" }));
        Assert.That(vm.SelectedTrack, Is.SameAs(a));
    }

    [Test]
    public void DeleteTrackAt_TheActiveTrack_ClearsGuidance()
    {
        var vm = VmWithTracks();
        vm.SelectedTrack = vm.SavedTracks[0];

        vm.DeleteTrackAt(0);

        Assert.That(vm.SelectedTrack, Is.Null);
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
    }

    [Test]
    public void ActivateTrackAt_ActivatesTheHighlightedTrack()
    {
        var vm = VmWithTracks();
        vm.SelectedTrack = vm.SavedTracks[0];

        vm.ActivateTrackAt(1);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
    }

    [Test]
    public void ActivateTrackAt_TheActiveTrack_TurnsGuidanceOff()
    {
        var vm = VmWithTracks();
        vm.SelectedTrack = vm.SavedTracks[1];

        vm.ActivateTrackAt(1);

        Assert.That(vm.SelectedTrack, Is.Null);
    }

    [Test]
    public void ActivateTrackAt_NothingHighlighted_UsesTheFirstVisibleTrack()
    {
        var vm = VmWithTracks();
        vm.SavedTracks[0].IsVisible = false;

        vm.ActivateTrackAt(-1);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
    }

    [Test]
    public void ActivateTrackAt_NoVisibleTracks_ReportsIt()
    {
        var vm = VmWithTracks();
        foreach (var t in vm.SavedTracks) t.IsVisible = false;
        string? heard = null;
        vm.FailureReported += m => heard = m;

        vm.ActivateTrackAt(-1);

        Assert.That(vm.SelectedTrack, Is.Null);
        Assert.That(heard, Is.EqualTo("No visible tracks"));
    }

    [Test]
    public void SwapTrackABAt_ReversesTheHighlightedTrack_NotTheActiveOne()
    {
        var vm = VmWithTracks();
        vm.SelectedTrack = vm.SavedTracks[0];

        vm.SwapTrackABAt(1);

        Assert.That(vm.SavedTracks[1].Points[0].Northing, Is.EqualTo(100));
        Assert.That(vm.SavedTracks[0].Points[0].Northing, Is.EqualTo(0), "active track untouched");
        Assert.That(vm.SelectedTrack, Is.SameAs(vm.SavedTracks[0]));
    }
}
