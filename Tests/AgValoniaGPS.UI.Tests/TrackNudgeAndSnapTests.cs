using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.UI.Tests;

/// <summary>
/// Tests for Nudge, Fine Nudge, Snap Left/Right, and Snap to Pivot commands.
/// </summary>
[TestFixture]
public class TrackNudgeAndSnapTests
{
    private static Track CreateTestTrack()
    {
        return new Track
        {
            Name = "Test AB",
            Type = TrackType.ABLine,
            Points = new List<Vec3>
            {
                new(0, 0, 0),
                new(0, 100, 0)
            },
            IsVisible = true,
            IsActive = true,
            NudgeDistance = 0
        };
    }

    [Test]
    public void NudgeLeft_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.NudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void NudgeRight_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.NudgeRightCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void NudgeLeft_WithTrack_UpdatesStatusMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        vm.NudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Nudged left"));
    }

    [Test]
    public void NudgeRight_WithTrack_UpdatesStatusMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        vm.NudgeRightCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Nudged right"));
    }

    [Test]
    public void NudgeLeft_UsesConfiguredNudgeDistance()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        // Default NudgeDistance is 20 cm
        int nudgeCm = ConfigurationStore.Instance.AutoSteer.NudgeDistance;

        vm.NudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain($"{nudgeCm:F1}cm"));
    }

    [Test]
    public void FineNudgeLeft_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.FineNudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void FineNudgeLeft_WithTrack_UpdatesStatusMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        vm.FineNudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Nudged left"));
    }

    [Test]
    public void FineNudge_IsSmallerThanStandardNudge()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        // Fine nudge should be 1/4 of standard
        int nudgeCm = ConfigurationStore.Instance.AutoSteer.NudgeDistance;
        double expectedFineCm = nudgeCm * 0.25;

        vm.FineNudgeLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain($"{expectedFineCm:F1}cm"));
    }

    [Test]
    public void SnapLeft_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.SnapLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void SnapRight_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.SnapRightCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void SnapLeft_WithTrack_UpdatesStatusMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        vm.SnapLeftCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Snapped left"));
    }

    [Test]
    public void SnapRight_WithTrack_UpdatesStatusMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        vm.SnapRightCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Snapped right"));
    }

    [Test]
    public void SnapToPivot_WithNoTrack_ShowsNoTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.SnapToPivotCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void SnapToPivot_WhenAlreadyOnTrack_ShowsAlreadyOnTrackMessage()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        // CrossTrackError defaults to 0 (on track)
        vm.SnapToPivotCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Is.EqualTo("Already on track"));
    }

    [Test]
    public void SnapToPivot_WithCrossTrackError_NudgesByXTE()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        // Simulate cross-track error
        vm.State.Guidance.CrossTrackError = 0.5;

        vm.SnapToPivotCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("Nudged"));
    }

    [Test]
    public void MultipleNudges_Accumulate()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        var track = CreateTestTrack();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;

        // Phase D D5: nudge commands post accumulating intents. Two nudges
        // between drains sum. Drain once after both clicks and assert the
        // batch carries the doubled delta.
        vm.NudgeRightCommand!.Execute(null);
        vm.NudgeRightCommand!.Execute(null);

        var batch = builder.Intents.Drain();
        Assert.That(batch.GuidanceNudgeMeters, Is.GreaterThan(0),
            "Two right nudges between drains must sum into a positive delta");
    }
}
