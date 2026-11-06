// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Tests.YouTurn;
using AgOpenWeb.Services.Track;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// The pins of <see cref="GoAroundPasses"/> on the scenario field: Tree circle 18 sits between
/// passes 12 and 13 of the AB 0° line, so pass 12 bends west round it and pass 13 east. A U-turn
/// next to the tree takes the tractor from one pass to the other, and the side pinned on pass 12
/// must not bend pass 13 west across the tree in front of the tractor.
/// </summary>
[TestFixture]
public class GoAroundPassesTests
{
    private const int Tree18 = 26; // inner index of Tree circle 18 at (77.4, 74.5)

    private static (GoAroundPasses Passes, ObstacleTestField.Built Field, Models.Track.Track Track) Setup()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Width = 6;
        c.Tool.Offset = 0;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.GoAroundRadius = 6;
        c.Guidance.GoAroundMargin = 1;
        var field = ObstacleTestField.Build(treesGoRound: true);
        var track = ObstacleTestField.Track("AB 0°");
        Assert.That(field.Boundary.InnerBoundaries[Tree18].Points.Average(p => p.Easting), Is.EqualTo(77.4).Within(0.1));
        return (new GoAroundPasses(c, new PolygonOffsetService()), field, track);
    }

    private static (double Dist, List<Vec3> Pass) PassOf(Models.Track.Track track, int pass)
    {
        double dist = Models.Track.PassGeometry.Offset(track, pass, 6, 0, 0);
        return (dist, CurveProcessing.CreateOffsetCurve(track.Points, dist));
    }

    private static double EastingAt(List<Vec3> line, double northing)
    {
        var p = line.OrderBy(v => System.Math.Abs(v.Northing - northing)).First();
        return p.Easting;
    }

    [Test]
    public void The_bend_radius_never_goes_under_the_tractors_minimum_turning_radius()
    {
        var (passes, _, _) = Setup();
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35; // 4.28 m
        c.Guidance.GoAroundRadius = 2;
        Assert.That(passes.Radius, Is.EqualTo(3 / System.Math.Tan(35 * System.Math.PI / 180)).Within(0.01));
        c.Guidance.GoAroundRadius = 10;
        Assert.That(passes.Radius, Is.EqualTo(10));
    }

    [Test]
    public void The_least_margin_grows_as_the_bend_tightens()
    {
        Assert.That(GoAroundPasses.MinMarginFor(6), Is.EqualTo(0.3).Within(1e-9));
        Assert.That(GoAroundPasses.MinMarginFor(10), Is.EqualTo(0.3).Within(1e-9), "no less than on a 6 m bend");
        Assert.That(GoAroundPasses.MinMarginFor(4), Is.EqualTo(0.3 * System.Math.Pow(1.5, 1.5)).Within(1e-9));
        Assert.That(GoAroundPasses.MinMarginFor(4), Is.GreaterThan(0.4), "the scenario tractor cuts a 4 m bend by 0.4 m");

        var (passes, _, _) = Setup();
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35;
        c.Guidance.GoAroundRadius = 2;
        c.Guidance.GoAroundMargin = 0;
        Assert.That(passes.SafetyMargin, Is.EqualTo(GoAroundPasses.MinMarginFor(passes.Radius)).Within(1e-9), "the floor for the radius in force");
        c.Guidance.GoAroundMargin = 1;
        Assert.That(passes.SafetyMargin, Is.EqualTo(1));
    }

    [Test]
    public void The_two_passes_beside_the_tree_bend_away_from_each_other()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var (d13, p13) = PassOf(track, 13);
        Assert.That(d12, Is.EqualTo(75).Within(0.01));
        Assert.That(d13, Is.EqualTo(81).Within(0.01));
        var b12 = passes.For(track, d12, p12, field.Boundary)!;
        var b13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(b12.GoneRound, Does.Contain(Tree18));
        Assert.That(b13.GoneRound, Does.Contain(Tree18));
        Assert.That(EastingAt(b12.Points, 74.5), Is.LessThan(75 - 1), "pass 12 bends west round the tree");
        Assert.That(EastingAt(b13.Points, 74.5), Is.GreaterThan(81 + 1), "pass 13 bends east round it");
    }

    [Test]
    public void A_pin_is_taken_beside_the_tree_on_the_bends_side_and_let_go_on_the_other_side()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var (d13, p13) = PassOf(track, 13);
        var b12 = passes.For(track, d12, p12, field.Boundary)!;

        // In the lead-in, 20 m before the tree and not yet over on the bend's side of its centre:
        // nothing pinned, a flip is harmless there.
        passes.Steered(track, b12, 78, 97);
        Assert.That(passes.PinnedSide(Tree18), Is.EqualTo(0));

        // Alongside the tree on the bend's (west) side: pinned.
        passes.Steered(track, b12, EastingAt(b12.Points, 74.5), 74.5);
        int pinned = passes.PinnedSide(Tree18);
        Assert.That(pinned, Is.Not.EqualTo(0));
        var pinned13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(EastingAt(pinned13.Points, 74.5), Is.LessThan(81), "while the tractor is west of the tree, pass 13 is bent to its side too");

        // After the U-turn next to the tree, the tractor lands on pass 13 east of it.
        passes.Steered(track, pinned13, 82, 66);
        Assert.That(passes.PinnedSide(Tree18), Is.EqualTo(0), "over on the other side, the pin is let go");
        var free13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(EastingAt(free13.Points, 74.5), Is.GreaterThan(81 + 1), "and pass 13 bends east, away from the tractor's tree");
    }

    [Test]
    public void A_pin_holds_while_the_tractor_is_beside_the_tree_on_its_side()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var b12 = passes.For(track, d12, p12, field.Boundary)!;
        passes.Steered(track, b12, EastingAt(b12.Points, 74.5), 74.5);
        int side = passes.PinnedSide(Tree18);
        Assert.That(side, Is.Not.EqualTo(0));

        // A snap moves the line 4 m east, through the tree's centre: the bend keeps the tractor's side.
        var (dSnap, pSnap) = (77.4, CurveProcessing.CreateOffsetCurve(track.Points, 77.4));
        var snapped = passes.For(track, dSnap, pSnap, field.Boundary)!;
        Assert.That(snapped.Rounds.Single(r => r.InnerIndex == Tree18).Side, Is.EqualTo(side));
        Assert.That(EastingAt(snapped.Points, 74.5), Is.LessThan(75), "west, where the tractor is");
    }
}
