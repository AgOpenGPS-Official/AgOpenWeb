// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A wide turn's landing is measured where the wide turn lands: where the next pass first leaves the
/// turn area going on from abreast of the tractor, not abreast of the end. Field job J11 on Couto
/// Ribeira (2026-11-16, a 42 m step and a 10 m radius): a slit in the fence (a track or ditch drawn
/// into the boundary) cuts across the passes at a slant near one end; the next pass abreast of this
/// pass's end is on its far side, the landing was measured from there, and the turn was refused
/// with the next pass on this side of the slit and 24 % of the field left. A U-turn lands abreast
/// of the end, so for it the far side is still the landing.
/// </summary>
[TestFixture, NonParallelizable]
public class LandingOfAWideTurnTests
{
    private ConfigurationStore _c = null!;
    private YouTurnPathingService _pathing = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _c = ConfigurationStore.Instance;
        _c.Tool.Overlap = 0;
        _c.NumSections = 1;
        _c.Tool.SetSectionWidth(0, 800);
        _c.Guidance.UTurnDistanceFromBoundary = 2.0;
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _c);
    }

    // The square ±100 with a slit 3 m wide from the north edge down to y = -60, slanting west as it
    // goes south (x = 20..23 at the top, -20..-17 at the bottom); the turn area is 2 m off it all.
    private static readonly Boundary Fence = new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(-100, -100), new(100, -100), new(100, 100), new(23, 100), new(-17, -60), new(-20, -60), new(20, 100), new(-100, 100) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        },
    };
    private static readonly List<Vec3> TurnArea = new()
    {
        new(-98, -98, 0), new(98, -98, 0), new(98, 98, 0), new(25, 98, 0), new(-15, -62, 0), new(-22, -62, 0), new(18, 98, 0), new(-98, 98, 0),
    };

    private static AgOpenWeb.Models.Track.Track Ab() =>
        AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

    private static AgOpenWeb.Models.Track.Track Curve() => new()
    {
        Name = "curve",
        Points = Enumerable.Range(0, 201).Select(i => new Vec3(0, -100 + i, 0)).ToList(),
    };

    private (AgOpenWeb.Models.Track.Track Track, Position At, double Ahead) Scenario(bool curve, double fromY = -70)
    {
        var track = curve ? Curve() : Ab();
        double x0 = AgOpenWeb.Models.Track.PassGeometry.Offset(track, 0, 8.0);
        double x1 = AgOpenWeb.Models.Track.PassGeometry.Offset(track, -1, 8.0);
        // Pass 0 heading north ends where it meets the turn area's edge east of the slit.
        double endY = 98 - 4 * (25 - x0);
        // The turn area's west edge of the slit: x = -22 + (y + 62) / 4.
        Assert.That(x1, Is.LessThan(-22 + (endY + 62) / 4), "the scenario: abreast on pass -1 is across the slit, in the turn area there");
        return (track, new Position { Easting = x0, Northing = fromY, Heading = 0 }, endY - fromY);
    }

    // From y = -99: just out of the last turn, in the headland, the next pass abreast outside the
    // turn area too (J11: the check is made as the turn before hands over).
    [TestCase(false, -70.0)]
    [TestCase(true, -70.0)]
    [TestCase(false, -99.0)]
    [TestCase(true, -99.0)]
    public void A_wide_turn_lands_on_the_next_pass_this_side_of_a_slit_in_the_fence(bool curve, double fromY)
    {
        _c.Guidance.UTurnRadius = 3.0; // an 8 m step: a wide turn
        var (track, at, ahead) = Scenario(curve, fromY);

        var run = _pathing.Landing(track, 0, -1, Fence, TurnArea, at, headingSameWay: true, headlandAhead: ahead, stopAt: 20, fromPath: 0);
        Assert.That(run.Length, Is.GreaterThanOrEqualTo(20), "pass -1 this side of the slit, from where it enters it down to y = -98");
        Assert.That(_pathing.IsPathLandable(track, 0, -1, Fence, TurnArea, at, headingSameWay: true, headlandAhead: ahead, fromPath: 0), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_U_turn_lands_abreast_of_the_end_on_the_sliver_and_is_refused(bool curve)
    {
        _c.Guidance.UTurnRadius = 6.0; // an 8 m step: a U-turn
        var (track, at, ahead) = Scenario(curve);

        Assert.That(_pathing.LandingLength(track, 0, -1, Fence, TurnArea, at, headingSameWay: true, headlandAhead: ahead, stopAt: 20, fromPath: 0),
            Is.LessThan(20));
        Assert.That(_pathing.IsPathLandable(track, 0, -1, Fence, TurnArea, at, headingSameWay: true, headlandAhead: ahead, fromPath: 0), Is.False);
    }
}
