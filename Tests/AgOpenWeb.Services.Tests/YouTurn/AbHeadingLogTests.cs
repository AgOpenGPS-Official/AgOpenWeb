// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The AB line's debug heading was logged on every 30th count of the U-turn counter: in a
/// simulator bug report it was 1273 of the 2000 log lines a report keeps, so the report reached
/// back only 21 minutes. It is logged when the heading changes.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class AbHeadingLogTests
{
    private sealed class CaptureLogger : ILogger<YouTurnStateMachine>
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, ex));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private CaptureLogger _log = null!;
    private YouTurnStateMachine _sm = null!;
    private Boundary _boundary = null!;
    private List<Vec3> _cultivated = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 400);
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _log = new CaptureLogger();
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            _log, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _cultivated = offset.CalculatePointHeadings(new List<Vec2>
        {
            new(-40, -40), new(40, -40), new(40, 40), new(-40, 40),
        });
    }

    private YouTurnStateMachine.TickContext Ctx(Models.Track.Track track) => new(
        // Pass 0 (x = 2) heading north, 15 m before the north end: a turn is planned.
        new Position { Easting = 2, Northing = 25, Heading = 0, Speed = 2.78 },
        track, _boundary, _cultivated,
        UTurnSkipRows: 0, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsAlternateSkipMode: false);

    private int AbHeadingLines() => _log.Messages.Count(m => m.StartsWith("[YouTurn] AB Line: abHeading=", StringComparison.Ordinal));

    [Test]
    public void The_AB_heading_is_logged_once_while_it_stays_the_same()
    {
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        // A minute at 10 Hz, as the pipeline ticks: the counter goes up before each tick.
        for (int i = 0; i < 600; i++)
        {
            turn.YouTurnCounter++;
            _sm.Tick(Ctx(track), guidance, turn);
        }
        Assert.That(turn.TurnPath, Is.Not.Null, "the scenario: a turn is planned");
        Assert.That(AbHeadingLines(), Is.EqualTo(1));

        // Another line, another heading: logged again, once.
        var angled = Models.Track.Track.FromABLine("AB2", new Vec3(-20, -100, 0), new Vec3(20, 100, 0));
        for (int i = 0; i < 600; i++)
        {
            turn.YouTurnCounter++;
            _sm.Tick(Ctx(angled), guidance, turn);
        }
        Assert.That(AbHeadingLines(), Is.EqualTo(2));
    }
}
