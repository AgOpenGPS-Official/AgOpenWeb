using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#111: Auto Track follows AgOpenGPS — while AutoSteer is off and a track is
/// active it switches to the closest aligned track; with none active it does nothing.</summary>
[TestFixture]
public class AutoTrackSwitchTests
{
    private static MainViewModel Vm()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.IsAutoTrackEnabled = true;
        vm.SavedTracks.Add(Track.FromABLine("West", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SavedTracks.Add(Track.FromABLine("East", new Vec3(20, 0, 0), new Vec3(20, 100, 0)));
        return vm;
    }

    private static Position Near(double e) => new() { Easting = e, Northing = 50, Heading = 0 };

    [Test]
    public void SwitchesToTheClosestTrack_WhenOneIsActive()
    {
        var vm = Vm();
        vm.SelectedTrack = vm.SavedTracks[0];
        vm.UpdateAutoTrackSelection(Near(19));
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("East"));
    }

    [Test]
    public void DoesNothing_WhenNoTrackIsActive()
    {
        var vm = Vm();
        vm.UpdateAutoTrackSelection(Near(19));
        Assert.That(vm.SelectedTrack, Is.Null, "turning a track off must stick");
    }

    [Test]
    public void DoesNothing_WhileEngaged()
    {
        var vm = Vm();
        vm.SelectedTrack = vm.SavedTracks[0];
        vm.IsAutoSteerEngaged = true;
        vm.UpdateAutoTrackSelection(Near(19));
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("West"));
    }
}
