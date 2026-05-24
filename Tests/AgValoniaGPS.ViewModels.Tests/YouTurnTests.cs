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

    // ── Phase D D4/D5 — guidance-command intent channel locks ──────────

    [Test]
    public void SnapLeftCommand_posts_snap_left_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        vm.SavedTracks.Add(Track.FromABLine("t", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];
        builder.Intents.Drain(); // discard any intents posted by SelectedTrack setter

        vm.SnapLeftCommand!.Execute(null);

        Assert.That(builder.Intents.Drain().GuidanceSnap, Is.True,
            "SnapLeftCommand must post RequestGuidanceSnap(left: true)");
    }

    [Test]
    public void SnapRightCommand_posts_snap_right_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        vm.SavedTracks.Add(Track.FromABLine("t", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];
        builder.Intents.Drain();

        vm.SnapRightCommand!.Execute(null);

        Assert.That(builder.Intents.Drain().GuidanceSnap, Is.False,
            "SnapRightCommand must post RequestGuidanceSnap(left: false)");
    }

    [Test]
    public void NudgeLeftCommand_posts_negative_nudge_delta()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        vm.SavedTracks.Add(Track.FromABLine("t", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];
        builder.Intents.Drain();

        vm.NudgeLeftCommand!.Execute(null);

        Assert.That(builder.Intents.Drain().GuidanceNudgeMeters, Is.LessThan(0),
            "NudgeLeft must post a negative nudge delta");
    }

    [Test]
    public void ResetNudgeCommand_posts_reset_intent()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        vm.SavedTracks.Add(Track.FromABLine("t", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
        vm.SelectedTrack = vm.SavedTracks[0];
        builder.Intents.Drain();

        vm.ResetNudgeCommand!.Execute(null);

        Assert.That(builder.Intents.Drain().GuidanceResetNudge, Is.True,
            "ResetNudgeCommand must post RequestGuidanceResetNudge");
    }

    // ── Phase D D7 — full Guidance snapshot mirror ─────────────────────

    [Test]
    public void ApplyGpsCycleResult_mirrors_full_Guidance_snapshot_onto_StateGuidance()
    {
        var vm = new MainViewModelBuilder().Build();
        var snapshot = new GuidanceSnapshot
        {
            CrossTrackError = 0.42,
            HeadingError = 0.05,
            SteerAngle = 1.7,
            SteerAngleRaw = 170,
            DistanceOffRaw = 420,
            PpIntegral = 0.1,
            GoalPoint = new Vec2(10, 20),
            RadiusPoint = new Vec2(30, 40),
            PurePursuitRadius = 5.5,
            IsHeadingSameWay = true,
            IsReverse = false,
            HowManyPathsAway = 3,
            NudgeOffset = 0.15,
            CurrentLineLabel = "4R",
            IsContourMode = false,
            HasGuidance = true,
        };

        vm.ApplyGpsCycleResult(new GpsCycleResult { Guidance = snapshot });

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Guidance.CrossTrackError, Is.EqualTo(0.42));
            Assert.That(vm.State.Guidance.SteerAngle, Is.EqualTo(1.7));
            Assert.That(vm.State.Guidance.HowManyPathsAway, Is.EqualTo(3));
            Assert.That(vm.State.Guidance.NudgeOffset, Is.EqualTo(0.15));
            Assert.That(vm.State.Guidance.CurrentLineLabel, Is.EqualTo("4R"));
            Assert.That(vm.State.Guidance.GoalPoint.Easting, Is.EqualTo(10));
            Assert.That(vm.State.Guidance.IsHeadingSameWay, Is.True);
        });
    }

    // ---- #421: no U-turns on a closed (polygon) track ----

    private static Track ClosedCurve() => new()
    {
        Name = "Polygon",
        Type = TrackType.Curve,
        IsClosed = true,
        Points = new List<Vec3>
        {
            new(0, 0, 0), new(100, 0, GeometryMath.PIBy2),
            new(100, 100, Math.PI), new(0, 100, Math.PI + GeometryMath.PIBy2),
            new(0, 0, 0)
        }
    };

    [Test]
    public void ClosedTrack_HidesUTurnButton_AndFlagsClosed()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SelectedTrack = ClosedCurve();

        Assert.That(vm.IsActiveTrackClosed, Is.True);
        Assert.That(vm.IsUTurnButtonVisible, Is.False, "U-turn button must be hidden on a polygon track");
    }

    [Test]
    public void ToggleYouTurn_OnClosedTrack_StaysDisabled()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SelectedTrack = ClosedCurve();

        vm.ToggleYouTurnCommand!.Execute(null);

        Assert.That(vm.IsYouTurnEnabled, Is.False, "toggling must not enable U-turns on a polygon track");
        Assert.That(vm.StatusMessage, Does.Contain("polygon"));
    }

    [Test]
    public void SelectingClosedTrack_DisablesActiveYouTurn()
    {
        var vm = new MainViewModelBuilder().Build();

        // Enable on an open AB line, then switch to a closed track.
        vm.SelectedTrack = new Track { Name = "AB", Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) } };
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = ClosedCurve();

        Assert.That(vm.IsYouTurnEnabled, Is.False, "selecting a polygon track must turn U-turns off");
    }
}
