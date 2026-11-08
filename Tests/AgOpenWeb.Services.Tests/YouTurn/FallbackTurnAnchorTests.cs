// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #174: the simple fallback U-turn was anchored on the tractor's raw position, so a turn
/// created while the tractor was still off the line (right after engaging) was shifted
/// sideways by that cross-track error — 2 m in the report. It must sit on the pass lines.
/// </summary>
[TestFixture, NonParallelizable]
public class FallbackTurnAnchorTests
{
    private ConfigurationStore _config = null!;
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = 2;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tool.SetSectionWidth(1, 600);           // 12 m passes
        _config.Guidance.UTurnRadius = 8;
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
            Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), _config);
    }

    // AB line heading north through E=100; pass n is (n + ½) × 12 m to the right
    // (PassGeometry), so pass -3 is at E=70 and pass -2 at E=82.
    private static readonly AgOpenWeb.Models.Track.Track Track = AgOpenWeb.Models.Track.Track.FromABLine(
        "AB", new Vec3(100, 0, 0), new Vec3(100, 100, 0));

    [Test]
    public void OntoPassLine_moves_the_anchor_sideways_onto_the_pass()
    {
        var g = new GuidanceWorkingState { HowManyPathsAway = -3 };
        var p = _creation.OntoPassLine(new Position { Easting = 72, Northing = 50 }, Track, 0, g);
        Assert.That(p.Easting, Is.EqualTo(70).Within(1e-9));
        Assert.That(p.Northing, Is.EqualTo(50).Within(1e-9), "only the cross-track component changes");
    }

    [Test]
    public void Fallback_turn_legs_sit_on_the_pass_lines_despite_cross_track_error()
    {
        // Heading north on pass -3, turning right onto pass -2 (E=82), tractor 2 m off the line.
        var g = new GuidanceWorkingState { HowManyPathsAway = -3, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { DistanceToHeadland = 20, NextTrackTurnOffset = 12 };
        var tractor = new Position { Easting = 72, Northing = 50, Heading = 0 };

        var path = _creation.SimpleFallback(tractor, abHeading: 0, turnLeft: false, boundary: null,
            g, turn, uTurnSkipRows: 0, headlandDistance: 20, selectedTrack: Track);

        Assume.That(path.Count, Is.GreaterThan(10));
        Assert.That(path[0].Easting, Is.EqualTo(70).Within(0.05), "entry leg on pass -3 (E=70), not the tractor's E=72");
        Assert.That(path[^1].Easting, Is.EqualTo(82).Within(0.05), "exit leg on pass -2 (E=82)");
    }

    // A 200 × 120 m field (0..200 E, 0..120 N, or shorter in the north), the tractor heading
    // north on pass -3 (E=70), 10 m short of the turn line.
    private static Boundary Field(double north) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint> { new(0, 0, 0), new(200, 0, 0), new(200, north, 0), new(0, north, 0) },
        },
    };

    private List<Vec3> FallbackTurn(Boundary field, double turnOffset, double fenceNorth)
    {
        _config.Guidance.UTurnDistanceFromBoundary = 4;
        var g = new GuidanceWorkingState { HowManyPathsAway = -3, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { DistanceToHeadland = 10, NextTrackTurnOffset = turnOffset };
        var tractor = new Position { Easting = 70, Northing = fenceNorth - 14, Heading = 0 };
        return _creation.SimpleFallback(tractor, abHeading: 0, turnLeft: false, field,
            g, turn, uTurnSkipRows: 0, headlandDistance: 0, selectedTrack: Track);
    }

    // #692: a turn to a pass far across (45 m) needs a 22.5 m semicircle; 4 m from the fence
    // it swung 17 m outside the field, and the tractor was sent there. No turn instead.
    [Test]
    public void Fallback_turn_that_would_leave_the_field_is_not_made()
    {
        var path = FallbackTurn(Field(100), turnOffset: 45, fenceNorth: 100);
        Assert.That(path, Is.Empty);
    }

    [Test]
    public void Fallback_turn_with_room_in_the_field_is_still_made()
    {
        var field = Field(120);
        var path = FallbackTurn(field, turnOffset: 12, fenceNorth: 100);
        Assert.That(path.Count, Is.GreaterThan(10));
        Assert.That(YouTurnCreationService.LeavesField(path, field), Is.False);
    }
}
