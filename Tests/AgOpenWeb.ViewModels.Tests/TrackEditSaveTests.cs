using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#111: saving an on-map edit of a track must not make it the active track.</summary>
[TestFixture]
public class TrackEditSaveTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-trkedit-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { System.IO.Directory.Delete(_dir, true); } catch { } }

    [Test]
    public void EditingAnInactiveTrack_KeepsTheActiveOne()
    {
        var b = new MainViewModelBuilder();
        b.FieldService.ActiveField.Returns(new Field { Name = "F", DirectoryPath = _dir });
        var vm = b.Build();
        vm.SavedTracks.Add(Track.FromABLine("A", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SavedTracks.Add(Track.FromABLine("B", new Vec3(10, 0, 0), new Vec3(10, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];

        vm.RemoteSaveTrackEdit(1, new List<(double e, double n)> { (12, 0), (12, 100) });

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
        Assert.That(vm.SavedTracks[1].Points[0].Easting, Is.EqualTo(12));
    }
}
