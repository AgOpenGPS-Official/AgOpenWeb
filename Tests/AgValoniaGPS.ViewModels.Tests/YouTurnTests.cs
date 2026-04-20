using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Pipeline;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Models.Track;
using NSubstitute;

namespace AgValoniaGPS.ViewModels.Tests;

[TestFixture]
public class YouTurnTests
{
    [Test]
    public void UTurnCommand_WithNoTrack_SetsErrorStatus()
    {
        var vm = new MainViewModelBuilder().Build();

        // No track selected
        Assert.That(vm.SelectedTrack, Is.Null);

        // Execute U-turn command
        vm.UTurnCommand!.Execute(null);

        Assert.That(vm.StatusMessage, Does.Contain("No track"));
    }

    [Test]
    public void YouTurnState_ClearedWhenTrackDeselected()
    {
        var vm = new MainViewModelBuilder().Build();

        var track = new Track
        {
            Name = "AB1",
            Points = new List<Vec3>
            {
                new(0, 0, 0),
                new(0, 100, 0)
            }
        };

        // Select then deselect
        vm.SelectedTrack = track;
        vm.IsYouTurnEnabled = true;
        vm.SelectedTrack = null;

        // YouTurn should be cleared along with the track
        Assert.That(vm.HasActiveTrack, Is.False);
        Assert.That(vm.State.YouTurn.IsTriggered, Is.False);
        Assert.That(vm.State.YouTurn.IsExecuting, Is.False);
    }

    [Test]
    public void IsYouTurnEnabled_CanBeToggled()
    {
        var vm = new MainViewModelBuilder().Build();

        Assert.That(vm.IsYouTurnEnabled, Is.False);

        vm.IsYouTurnEnabled = true;
        Assert.That(vm.IsYouTurnEnabled, Is.True);

        vm.IsYouTurnEnabled = false;
        Assert.That(vm.IsYouTurnEnabled, Is.False);
    }

    // ── Phase C C6/C7 — intent channel locks ───────────────────────────

    [Test]
    public void TriggerManualYouTurnLeft_posts_left_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        vm.TriggerManualYouTurnLeft();

        var batch = builder.Intents.Drain();
        Assert.That(batch.ManualYouTurn, Is.True,
            "TriggerManualYouTurnLeft must post the manual intent with turnLeft=true");
    }

    [Test]
    public void TriggerManualYouTurnRight_posts_right_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        vm.TriggerManualYouTurnRight();

        var batch = builder.Intents.Drain();
        Assert.That(batch.ManualYouTurn, Is.False,
            "TriggerManualYouTurnRight must post the manual intent with turnLeft=false");
    }

    [Test]
    public void ClearYouTurnState_posts_clear_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        vm.ClearYouTurnState();

        var batch = builder.Intents.Drain();
        Assert.That(batch.ClearYouTurn, Is.True,
            "ClearYouTurnState must post the clear intent rather than mutate state directly");
    }

    // ── Phase C C5/C8 — snapshot mirror locks ──────────────────────────

    [Test]
    public void ApplyGpsCycleResult_mirrors_YouTurn_snapshot_onto_StateYouTurn()
    {
        var vm = new MainViewModelBuilder().Build();
        var nextTrack = Track.FromABLine("next", new Vec3(0, 0, 0), new Vec3(0, 100, 0));
        var turnPath = new List<Vec3> { new(0, 0, 0), new(5, 5, 0), new(10, 10, 0) };

        var result = new GpsCycleResult
        {
            YouTurn = new YouTurnSnapshot
            {
                IsEnabled = true,
                IsTriggered = true,
                IsExecuting = true,
                TurnPath = turnPath,
                IsTurnLeft = true,
                NextTrack = nextTrack,
                YouTurnCounter = 7,
                CurrentZone = TractorZone.InCultivatedArea,
            },
        };

        vm.ApplyGpsCycleResult(result);

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.YouTurn.IsEnabled, Is.True);
            Assert.That(vm.State.YouTurn.IsTriggered, Is.True);
            Assert.That(vm.State.YouTurn.IsExecuting, Is.True);
            Assert.That(vm.State.YouTurn.IsTurnLeft, Is.True);
            Assert.That(vm.State.YouTurn.NextTrack, Is.SameAs(nextTrack));
            Assert.That(vm.State.YouTurn.YouTurnCounter, Is.EqualTo(7));
            Assert.That(vm.State.YouTurn.CurrentZone, Is.EqualTo(TractorZone.InCultivatedArea));
            Assert.That(vm.State.YouTurn.TurnPath, Is.Not.Null);
            Assert.That(vm.State.YouTurn.TurnPath!.Count, Is.EqualTo(3));
        });
    }

    [Test]
    public void ApplyGpsCycleResult_JustCompleted_triggers_guidance_resync()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        builder.GpsPipelineService.ClearReceivedCalls();

        var result = new GpsCycleResult
        {
            YouTurn = new YouTurnSnapshot { JustCompleted = true },
        };

        vm.ApplyGpsCycleResult(result);

        // The turn-completion signal must resync guidance state to the pipeline
        // so the cycle's pass-number / track cache reflects the new offset row.
        // Observable via the SetActiveTrack push that SyncGuidanceStateToPipeline does.
        builder.GpsPipelineService.Received().SetActiveTrack(
            Arg.Any<Track?>(),
            Arg.Any<int>(),
            Arg.Any<double>(),
            Arg.Any<bool>());
    }
}
