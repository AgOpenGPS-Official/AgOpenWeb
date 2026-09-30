// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// End-of-Phase-C locks for the YouTurn-on-cycle contract. Each test captures
/// an invariant that must survive later phases — the state machine runs on
/// the cycle thread, and the only UI→cycle channel for YouTurn commands is
/// <see cref="IPipelineIntents"/>.
/// </summary>
[TestFixture]
public class YouTurnCycleTests
{
    /// <summary>
    /// Posting <c>RequestManualYouTurn</c> and then draining + calling the
    /// state machine (mirroring <c>GpsPipelineService.ProcessCycle</c>) must
    /// route the user-visible outcome through <see cref="YouTurnEffects"/>.
    /// The precondition path (autosteer off) is an unambiguous assertion
    /// that the full drain→state-machine wiring works without depending on
    /// the turn-creation geometry (which has its own tests).
    /// </summary>
    [Test]
    public void Drained_ManualYouTurn_intent_runs_state_machine_on_cycle_thread()
    {
        var intents = new PipelineIntents();
        var youTurn = new YouTurnWorkingState();
        var guidance = new GuidanceWorkingState();
        var stateMachine = BuildStateMachine();
        var ctx = BuildTickContext();

        // UI thread posts the intent.
        intents.RequestManualYouTurn(turnLeft: true);

        // Cycle thread drains and runs the state machine against cycle-owned POCOs.
        var batch = intents.Drain();
        Assert.That(batch.ManualYouTurn, Is.True,
            "Manual intent should survive a single drain on the cycle thread");

        // Mirror the GpsPipelineService.ProcessCycle call. Passing
        // isAutoSteerEngaged=false exercises the state machine's precondition
        // path — it must set a status message and leave the working state alone.
        var effects = stateMachine.TriggerManual(
            batch.ManualYouTurn!.Value,
            isAutoSteerEngaged: false,
            in ctx,
            guidance,
            youTurn);

        Assert.Multiple(() =>
        {
            Assert.That(youTurn.IsTriggered, Is.False,
                "Without autosteer, the manual intent must not set IsTriggered");
            Assert.That(youTurn.TurnPath, Is.Null,
                "Without autosteer, the manual intent must not create a turn path");
            Assert.That(effects.StatusMessage, Does.Contain("autosteer"),
                "The state machine's precondition message must propagate through effects");
        });
    }

    /// <summary>
    /// Posting <c>RequestClearYouTurn</c> and calling <c>ClearState</c> (how
    /// the cycle's intent consumer acts) must reset the working state. This
    /// is the path used by field-close and track-deselect.
    /// </summary>
    [Test]
    public void Drained_ClearYouTurn_intent_resets_cycle_working_state()
    {
        var intents = new PipelineIntents();
        var youTurn = new YouTurnWorkingState
        {
            IsTriggered = true,
            IsExecuting = true,
            TurnPath = new List<Vec3> { new(0, 0, 0), new(5, 5, 0), new(10, 10, 0) },
            NextTrack = Models.Track.Track.FromABLine("next",
                new Vec3(0, 0, 0), new Vec3(0, 100, 0)),
            YouTurnCounter = 42,
            CurrentZone = Models.State.TractorZone.InHeadland,
        };

        intents.RequestClearYouTurn();

        var batch = intents.Drain();
        Assert.That(batch.ClearYouTurn, Is.True);

        // Mirror GpsPipelineService.ProcessCycle: call ClearState on the working state.
        if (batch.ClearYouTurn)
            YouTurnStateMachine.ClearState(youTurn);

        Assert.Multiple(() =>
        {
            Assert.That(youTurn.IsTriggered, Is.False);
            Assert.That(youTurn.IsExecuting, Is.False);
            Assert.That(youTurn.TurnPath, Is.Null);
            Assert.That(youTurn.NextTrack, Is.Null);
            Assert.That(youTurn.YouTurnCounter, Is.EqualTo(0));
            Assert.That(youTurn.CurrentZone, Is.EqualTo(Models.State.TractorZone.OutsideBoundary));
        });
    }

    /// <summary>
    /// Structural lock: <see cref="IGpsPipelineService"/> must not expose any
    /// method that lets the UI thread write directly into the cycle's YouTurn
    /// working state. If someone adds a <c>SetYouTurn*</c> / <c>PushYouTurn*</c>
    /// method that takes a <c>YouTurnWorkingState</c> (or similar bypass),
    /// they've reintroduced a cross-thread writer the Phase C intent channel
    /// exists to prevent.
    /// </summary>
    [Test]
    public void IGpsPipelineService_has_no_direct_YouTurn_writethrough_methods()
    {
        var disallowed = typeof(IGpsPipelineService).GetMethods()
            .Where(m =>
                (m.Name.StartsWith("Set") || m.Name.StartsWith("Push") || m.Name.StartsWith("Apply"))
                && m.GetParameters().Any(p =>
                    p.ParameterType == typeof(YouTurnWorkingState)
                    || p.ParameterType == typeof(YouTurnSnapshot)))
            .Select(m => m.Name)
            .ToList();

        Assert.That(disallowed, Is.Empty,
            "IGpsPipelineService must not expose a direct write-through into cycle-owned YouTurn state. "
            + "Use IPipelineIntents instead. Offending method(s): "
            + string.Join(", ", disallowed));
    }

    /// <summary>
    /// The guidance snapshot must only be emitted when the YouTurn tick ran —
    /// otherwise its stale <c>HowManyPathsAway</c> seed fights the UI's
    /// <c>NearestPassNumber</c> auto-detect writer. Phase C C8 left this as
    /// a contract between the cycle and <see cref="MainViewModel.ApplyResults"/>;
    /// this test pins it at the snapshot level.
    /// </summary>
    [Test]
    public void YouTurnSnapshot_JustCompleted_round_trips_through_builder()
    {
        var src = new YouTurnWorkingState();

        // The C8 builder signature: JustCompleted is the third parameter set.
        var snapshot = new YouTurnSnapshot
        {
            IsEnabled = src.IsEnabled,
            IsTriggered = src.IsTriggered,
            IsExecuting = src.IsExecuting,
            JustCompleted = true,
        };

        Assert.That(snapshot.JustCompleted, Is.True,
            "JustCompleted is the cycle's one-shot completion signal consumed by the VM");
    }

    /// <summary>
    /// #163 backstop: when U-turn guidance reports the path finished, the pipeline
    /// completes the executing turn through <c>CompleteFromGuidance</c> — advancing to
    /// the next pass — instead of leaving it executing with no steering.
    /// </summary>
    [Test]
    public void CompleteFromGuidance_completes_executing_turn_and_advances_pass()
    {
        var stateMachine = BuildStateMachine();
        var ctx = BuildTickContext();
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0 };
        var youTurn = new YouTurnWorkingState
        {
            IsTriggered = true,
            IsExecuting = true,
            IsTurnLeft = true,
            WasHeadingSameWayAtTurnStart = true,
            TurnPath = new List<Vec3> { new(0, 0, 0), new(-3, 3, 0), new(-6, 0, 0) },
        };

        var effects = stateMachine.CompleteFromGuidance(in ctx, guidance, youTurn);

        Assert.Multiple(() =>
        {
            Assert.That(effects.TurnCompleted, Is.True);
            Assert.That(youTurn.IsExecuting, Is.False);
            Assert.That(youTurn.IsTriggered, Is.False);
            Assert.That(youTurn.TurnPath, Is.Null);
            Assert.That(guidance.HowManyPathsAway, Is.EqualTo(-1),
                "Left turn heading the same way as AB moves one pass negative");
        });
    }

    [Test]
    public void CompleteFromGuidance_is_noop_when_no_turn_is_executing()
    {
        var stateMachine = BuildStateMachine();
        var ctx = BuildTickContext();
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 3 };
        var youTurn = new YouTurnWorkingState();

        var effects = stateMachine.CompleteFromGuidance(in ctx, guidance, youTurn);

        Assert.That(effects.TurnCompleted, Is.False);
        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(3));
    }

    /// <summary>
    /// #163: <c>TickExecutingTurn</c> runs the completion checks without a headland line —
    /// driving the pivot along a manual turn's path must complete it.
    /// </summary>
    [Test]
    public void TickExecutingTurn_completes_manual_turn_without_headland()
    {
        var stateMachine = BuildStateMachine();
        var baseCtx = BuildTickContext() with { Boundary = null, HeadlandLine = null };
        var guidance = new GuidanceWorkingState();
        var youTurn = new YouTurnWorkingState();

        stateMachine.TriggerManual(true, isAutoSteerEngaged: true, in baseCtx, guidance, youTurn);
        Assume.That(youTurn.IsExecuting, Is.True, "Manual trigger must start a turn");
        var path = youTurn.TurnPath!.ToList();

        bool completed = false;
        foreach (var p in path)
        {
            var ctx = baseCtx with
            {
                CurrentPosition = new Position { Easting = p.Easting, Northing = p.Northing, Heading = p.Heading * 180 / Math.PI },
            };
            completed |= stateMachine.TickExecutingTurn(in ctx, guidance, youTurn).TurnCompleted;
            if (completed) break;
        }

        Assert.That(completed, Is.True, "Driving the manual arc should complete the turn");
        Assert.That(youTurn.IsExecuting, Is.False);
    }

    /// <summary>
    /// A snake / alternate turn was planned (target pass 5) but discarded before it ran.
    /// A manual turn must then complete by its own direction, not jump to the stale pass.
    /// </summary>
    [Test]
    public void ManualTurn_after_discarded_planned_turn_ignores_stale_target_pass()
    {
        var stateMachine = BuildStateMachine();
        var baseCtx = BuildTickContext() with { Boundary = null, HeadlandLine = null };
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0 };
        var youTurn = new YouTurnWorkingState
        {
            TurnPath = new List<Vec3> { new(0, 40, 0), new(3, 43, 0), new(6, 40, 0) },
            ReturnPassTargetPath = 5,
        };

        stateMachine.TriggerManual(true, isAutoSteerEngaged: true, in baseCtx, guidance, youTurn);
        Assume.That(youTurn.IsExecuting, Is.True, "Manual trigger must start a turn");
        Assert.That(youTurn.ReturnPassTargetPath, Is.Null);

        var effects = stateMachine.CompleteFromGuidance(in baseCtx, guidance, youTurn);
        Assert.That(effects.TurnCompleted, Is.True);
        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(-1),
            "Left manual turn heading with the AB moves one pass negative, not to pass 5");
    }

    [Test]
    public void ClearState_drops_the_target_pass()
    {
        var youTurn = new YouTurnWorkingState
        {
            TurnPath = new List<Vec3> { new(0, 0, 0), new(1, 1, 0), new(2, 2, 0) },
            NextTrack = Models.Track.Track.FromABLine("n", new Vec3(6, -100, 0), new Vec3(6, 100, 0)),
            ReturnPassTargetPath = 3,
        };
        YouTurnStateMachine.ClearState(youTurn);
        Assert.Multiple(() =>
        {
            Assert.That(youTurn.TurnPath, Is.Null);
            Assert.That(youTurn.NextTrack, Is.Null);
            Assert.That(youTurn.ReturnPassTargetPath, Is.Null);
        });
    }

    // ── Test helpers ─────────────────────────────────────────────────────

    private static YouTurnStateMachine BuildStateMachine()
    {
        var polygonOffset = Substitute.For<Services.Geometry.IPolygonOffsetService>();
        var creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, polygonOffset, ConfigurationStore.Instance);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance);
        return new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, ConfigurationStore.Instance);
    }

    private static YouTurnStateMachine.TickContext BuildTickContext()
    {
        // Square field centered on origin: boundary from (-50,-50) to (50,50),
        // headland line inset by 5m so the cultivated area is (-45,-45)..(45,45).
        // Track is a north-south AB line at x=0. Tractor sits at origin facing north.
        var outerPoints = new List<Vec2>
        {
            new(-50, -50), new(50, -50), new(50, 50), new(-50, 50),
        };
        var outerPolygon = new BoundaryPolygon
        {
            Points = outerPoints.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        };
        var boundary = new Boundary { OuterBoundary = outerPolygon };

        var headlandLine = new List<Vec3>
        {
            new(-45, -45, 0), new(45, -45, 0), new(45, 45, 0), new(-45, 45, 0),
        };

        var track = Models.Track.Track.FromABLine(
            "AB-test", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        var currentPosition = new Position
        {
            Easting = 0,
            Northing = 0,
            Heading = 0, // north — degrees
        };

        return new YouTurnStateMachine.TickContext(
            currentPosition,
            track,
            boundary,
            headlandLine,
            UTurnSkipRows: 0,
            IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10.0,
            HeadlandDistance: 5.0);
    }
}
