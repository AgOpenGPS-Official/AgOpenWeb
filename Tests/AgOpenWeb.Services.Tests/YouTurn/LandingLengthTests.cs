// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn may only land on a pass with room inside the field at the end the tractor is
/// heading for. "Inside anywhere" was enough before: on a real olive grove the turn went onto
/// a corner pass with 4 m inside, the tractor came off the arc in the headland and AutoSteer
/// drove it out through the boundary; on an hourglass field the turn went onto a pass that
/// only clips this lobe's bay and is long in the other lobe, with the same end.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LandingLengthTests
{
    private const double ToolWidth = 4.0;

    private YouTurnPathingService _pathing = null!;
    private Boundary _boundary = null!;
    private List<Vec3> _cultivated = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);

        // Fence at ±50. The cultivated area (the headland line) is an hourglass: a 80 x 80 m
        // square with a notch in its east side, from (40,-30) in to (6,0) and out to (40,30).
        // Pass k is the line x = 4k + 2 (half a tool from the AB line), so its runs at the
        // north and south ends are 80 - (4k + 2 - 6) * 30 / 34 metres each... the ones used:
        // pass 5: 25.9 m, pass 6: 22.4 m, pass 7: 18.8 m, pass 8: 15.3 m.
        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var ring = new List<Vec2>
        {
            new(-40, -40), new(40, -40), new(40, -30), new(6, 0), new(40, 30), new(40, 40), new(-40, 40),
        };
        _cultivated = ring.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

        // AB line due north on x = 0.
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    private static Position At(double e, double n) => new() { Easting = e, Northing = n };

    [Test]
    public void Pass_inside_only_at_the_far_lobe_is_not_landable_from_here()
    {
        // Pass 7 (x = 30) is inside for 18.8 m at each end and in the notch's shadow between.
        // Inside somewhere, so the old rule took it.
        Assert.That(_pathing.IsPathInsideCultivated(_track, 0, 7, _boundary, _cultivated), Is.True);

        // Heading south on pass 5 (x = 22) at y = -30, 10 m from the south edge: pass 7 has
        // 18.8 m at that end, less than MinLandingLength; pass 6 has 22.4 m.
        Assert.That(_pathing.LandingLength(_track, 0, 7, _boundary, _cultivated, At(22, -30), false, 10), Is.EqualTo(18.8).Within(0.3));
        Assert.That(_pathing.IsPathLandable(_track, 0, 7, _boundary, _cultivated, At(22, -30), false, 10), Is.False);
        Assert.That(_pathing.LandingLength(_track, 0, 6, _boundary, _cultivated, At(22, -30), false, 10), Is.EqualTo(22.4).Within(0.3));
        Assert.That(_pathing.IsPathLandable(_track, 0, 6, _boundary, _cultivated, At(22, -30), false, 10), Is.True);
    }

    [Test]
    public void A_full_length_pass_lands_with_its_whole_run()
    {
        // Pass -2 (x = -6) runs the whole 80 m.
        Assert.That(_pathing.LandingLength(_track, 0, -2, _boundary, _cultivated, At(2, -30), false, 10), Is.EqualTo(80).Within(0.3));
        Assert.That(_pathing.IsPathLandable(_track, 0, -2, _boundary, _cultivated, At(2, -30), false, 10), Is.True);
    }

    [Test]
    public void The_run_at_the_end_ahead_counts_not_the_notch_abreast_of_the_tractor()
    {
        // Heading north on pass 5 (x = 22) at y = -5, 45 m from the north edge, in the notch's
        // shadow: abreast of the tractor pass 6 (x = 26) is in the notch (outside), but at the
        // north end it runs 22.4 m: the landing. (A turn is planned from the start of a pass,
        // with the far headland in range.) Pass 8 (x = 34) has 15.3 m there: no landing.
        Assert.That(_pathing.LandingLength(_track, 0, 6, _boundary, _cultivated, At(22, -5), true, 45), Is.EqualTo(22.4).Within(0.3));
        Assert.That(_pathing.IsPathLandable(_track, 0, 6, _boundary, _cultivated, At(22, -5), true, 45), Is.True);
        Assert.That(_pathing.LandingLength(_track, 0, 8, _boundary, _cultivated, At(22, -5), true, 45), Is.EqualTo(15.3).Within(0.3));
        Assert.That(_pathing.IsPathLandable(_track, 0, 8, _boundary, _cultivated, At(22, -5), true, 45), Is.False);
    }

    [Test]
    public void A_pass_with_nothing_inside_within_reach_has_no_landing()
    {
        // Pass 12 (x = 50) is on the fence, in the headland along its whole length.
        Assert.That(_pathing.LandingLength(_track, 0, 12, _boundary, _cultivated, At(42, -30), false, 10), Is.EqualTo(0));
        Assert.That(_pathing.IsPathLandable(_track, 0, 12, _boundary, _cultivated, At(42, -30), false, 10), Is.False);
    }

    [Test]
    public void A_pass_the_notch_cuts_goes_on_past_it_inside_the_field()
    {
        // Heading north on pass 6 (x = 26) at y = -35: the notch's edge is 17.4 m ahead, the pass
        // is in the notch for 35.3 m (inside the fence all the way) and inside again to the north.
        // Not the end of the field: no turn, drive on.
        double? gap = _pathing.GapAhead(_track, 0, 6, _boundary, _cultivated, At(26, -35), true, 17.4);
        Assert.That(gap, Is.Not.Null);
        Assert.That(gap!.Value, Is.EqualTo(35.3).Within(1.5));

        // The ray measures along the tractor's heading: a few metres short of the line's true
        // crossing is the same gap.
        gap = _pathing.GapAhead(_track, 0, 6, _boundary, _cultivated, At(26, -35), true, 12);
        Assert.That(gap, Is.Not.Null);
        Assert.That(gap!.Value, Is.EqualTo(35.3).Within(1.5));
    }

    [Test]
    public void A_pass_that_leaves_the_field_past_the_headland_ends_there()
    {
        // Pass -2 (x = -6) heading north at y = 30: the headland 10 m ahead, the fence 10 m on.
        Assert.That(_pathing.GapAhead(_track, 0, -2, _boundary, _cultivated, At(-6, 30), true, 10), Is.Null);
        // Pass 7 (x = 30) heading south at y = -30: the south edge 10 m ahead, the fence 10 m on
        // (the notch, where the pass goes on, is behind the tractor).
        Assert.That(_pathing.GapAhead(_track, 0, 7, _boundary, _cultivated, At(30, -30), false, 10), Is.Null);
    }

    [Test]
    public void The_next_line_choice_skips_a_side_whose_pass_cannot_be_landed_on()
    {
        // On pass 6 (x = 26) heading south at y = -30: pass 7 to the east has 18.8 m here, pass 5
        // to the west 25.9 m. Inside anywhere, both sides would do and the step would keep going
        // east; with the tractor's position only the west side is left.
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var (insideAnywhere, positiveAnywhere) = _pathing.WouldNextLineBeInsideBoundary(_track, 0, guidance, _boundary, _cultivated, 0, keepPositive: true);
        Assert.That(insideAnywhere, Is.True);
        Assert.That(positiveAnywhere, Is.True, "inside anywhere keeps stepping east");

        var (inside, positive) = _pathing.WouldNextLineBeInsideBoundary(_track, 0, guidance, _boundary, _cultivated, 0, keepPositive: true, At(26, -30), 10);
        Assert.That(inside, Is.True);
        Assert.That(positive, Is.False, "only the west pass can be landed on");

        // On pass 7 (x = 30) the only neighbour with room is pass 6 (22.4 m); pass 8 has 15.3 m.
        guidance = new GuidanceWorkingState { HowManyPathsAway = 7, IsHeadingSameWay = false };
        (inside, positive) = _pathing.WouldNextLineBeInsideBoundary(_track, 0, guidance, _boundary, _cultivated, 0, keepPositive: true, At(30, -30), 10);
        Assert.That(inside, Is.True);
        Assert.That(positive, Is.False);
    }
}
