// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A manual U-turn onto a pass less than 5 m away must be driven to its end (#272).
///
/// A manual turn's end is abreast of the point 4 m ahead of the tractor, one pass over. With a
/// narrow pass the tractor drives past that end, a few metres beside it, on its way into the
/// turn. The closest-approach completion read that as "reached the end and now leaving it":
/// the turn was completed 5 m in, guidance stepped to the next pass, and the tractor carried
/// on along it in the same direction.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ManualTurnNarrowPassCompletionTests
{
    private ConfigurationStore _config = null!;
    private YouTurnCreationService _creation = null!;
    private YouTurnStateMachine _stateMachine = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.NumSections = 1;
        _config.Tool.Overlap = 0;
        _config.Guidance.UTurnRadius = 8.0;
        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), _config);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _config);
        _stateMachine = new YouTurnStateMachine(
            _creation, pathing, NullLogger<YouTurnStateMachine>.Instance, _config);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -1000, 0), new Vec3(0, 1000, 0));
    }

    /// <summary>Drives the pivot along the turn path itself, one path point per tick, and
    /// returns the path length still ahead when the turn was completed.</summary>
    private double RemainingWhenCompleted(double toolWidthM, int skipRows, bool turnLeft)
    {
        _config.Tool.Width = toolWidthM;
        _config.Tool.SetSectionWidth(0, toolWidthM * 100);
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true };
        var path = _creation.CreateManualArcPath(new Position { Easting = 0, Northing = 0 },
            abHeading: 0, turnLeft, boundary: null, guidance, skipRows);
        Assert.That(path, Has.Count.GreaterThan(2), "no manual turn path");

        var turn = new YouTurnWorkingState
        {
            IsEnabled = true, IsTriggered = true, IsExecuting = true,
            TurnPath = path, PreviousDistToTurnEnd = double.MaxValue,
        };

        var ahead = new double[path.Count];
        for (int i = path.Count - 2; i >= 0; i--)
            ahead[i] = ahead[i + 1] + Math.Sqrt(
                Math.Pow(path[i + 1].Easting - path[i].Easting, 2) + Math.Pow(path[i + 1].Northing - path[i].Northing, 2));

        for (int i = 0; i < path.Count; i++)
        {
            var pos = new Position
            {
                Easting = path[i].Easting, Northing = path[i].Northing,
                Heading = path[i].Heading * 180.0 / Math.PI,
            };
            var ctx = new YouTurnStateMachine.TickContext(pos, _track, null, null,
                UTurnSkipRows: skipRows, IsSkipWorkedMode: false,
                HeadlandCalculatedWidth: 10.0, HeadlandDistance: 5.0);
            _stateMachine.TickExecutingTurn(in ctx, guidance, turn);
            if (!turn.IsExecuting) return ahead[i];
        }
        Assert.Fail("the turn never completed");
        return 0;
    }

    // tool width (m), rows skipped: pass offsets of 1, 2, 3, 4, 5 and 8 m. The reporter's
    // cases: a 1 m tool worked only with 4 skipped, a 4 m tool only with 1 skipped.
    [TestCase(1.0, 0, true)]
    [TestCase(1.0, 1, false)]
    [TestCase(3.0, 0, true)]
    [TestCase(4.0, 0, true)]
    [TestCase(4.0, 0, false)]
    [TestCase(1.0, 4, true)]
    [TestCase(4.0, 1, false)]
    public void ManualTurn_IsDrivenToItsLastMetres(double toolWidthM, int skipRows, bool turnLeft)
    {
        double remaining = RemainingWhenCompleted(toolWidthM, skipRows, turnLeft);

        // The turn hands back to line guidance with about 4 m of path left, never before.
        Assert.That(remaining, Is.LessThan(4.5),
            $"turn completed with {remaining:F1} m of its path still ahead");
    }
}
