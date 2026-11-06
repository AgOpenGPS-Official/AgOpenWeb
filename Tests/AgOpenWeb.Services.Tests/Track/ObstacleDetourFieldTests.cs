// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Tests.YouTurn;
using AgOpenWeb.Services.Track;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// The bend on the scenario field (<see cref="ObstacleTestField"/>, trees as Go around): the
/// same pass given as the two points of an AB line and as the planner's densified line gets the
/// same bend, a clump of trees at the field's edge with no room does not cost a lone tree its
/// bend, and a pinned side holds.
/// </summary>
[TestFixture]
public class ObstacleDetourFieldTests
{
    private const double KeepOut = 3 + 1; // half a 6 m implement and the 1 m margin
    private const double Radius = 6;
    private const int Tree1 = 9, Tree3 = 11, Tree4 = 12, Tree5 = 13; // inner indices: the lone tree and the clump at the west edge

    private static (ObstacleTestField.Built Field, List<Vec3> TwoPoints, List<Vec3> Dense) Pass14OnAb70()
    {
        var field = ObstacleTestField.Build(treesGoRound: true);
        var track = ObstacleTestField.Track("AB 70°");
        double dist = Models.Track.PassGeometry.Offset(track, -14, 6, 0, 0); // through Tree circle 1 and the clump
        var twoPoints = CurveProcessing.CreateOffsetCurve(track.Points, dist);
        var dense = CurveProcessing.CreateOffsetCurve(CurveProcessing.CalculateHeadings(Enumerable.Range(0, 301)
            .Select(i => new Vec3(track.Points[0].Easting + (track.Points[1].Easting - track.Points[0].Easting) * i / 300.0,
                track.Points[0].Northing + (track.Points[1].Northing - track.Points[0].Northing) * i / 300.0, 0)).ToList()), dist);
        return (field, twoPoints, dense);
    }

    [Test]
    public void A_clump_with_no_room_at_the_edge_does_not_block_the_lone_tree_on_a_dense_pass()
    {
        var (field, twoPoints, dense) = Pass14OnAb70();
        var detour = new ObstacleDetour(new PolygonOffsetService());

        var sparse = detour.Compute(twoPoints, field.Boundary, KeepOut, Radius)!;
        var fine = detour.Compute(dense, field.Boundary, KeepOut, Radius)!;

        Assert.That(sparse.GoneRound, Is.EqualTo(new[] { Tree1 }));
        Assert.That(sparse.Blocked, Is.EquivalentTo(new[] { Tree3, Tree4, Tree5 }), "the clump at the field's edge has no room");
        Assert.That(fine.GoneRound, Is.EqualTo(sparse.GoneRound), "the densified pass gets the same bend");
        Assert.That(fine.Blocked, Is.EquivalentTo(sparse.Blocked));
        // The same bent geometry: every bent point of one line lies on the other.
        var tree = field.Boundary.InnerBoundaries[Tree1];
        foreach (var p in fine.Points.Where(p => Math.Abs(p.Easting - tree.Points[0].Easting) < 40))
            Assert.That(DistanceTo(sparse.Points, p), Is.LessThan(0.05), $"({p.Easting:F1},{p.Northing:F1})");
        Assert.That(fine.Points.Count, Is.GreaterThan(sparse.Points.Count), "the pass's own points are kept");
    }

    [Test]
    public void The_pass_and_its_densified_twin_are_cached_apart()
    {
        var (field, twoPoints, dense) = Pass14OnAb70();
        var track = ObstacleTestField.Track("AB 70°");
        var detour = new ObstacleDetour(new PolygonOffsetService());

        var sparse = detour.For(track, -81, twoPoints, field.Boundary, KeepOut, Radius, 1.0);
        var fine = detour.For(track, -81, dense, field.Boundary, KeepOut, Radius, 1.0);
        Assert.That(fine, Is.Not.SameAs(sparse));
        Assert.That(fine!.Points.Count, Is.GreaterThan(sparse!.Points.Count));
        Assert.That(detour.For(track, -81, new List<Vec3>(twoPoints), field.Boundary, KeepOut, Radius, 1.0), Is.SameAs(sparse), "a copy of the pass is the same pass");
        Assert.That(detour.For(track, -81, new List<Vec3>(dense), field.Boundary, KeepOut, Radius, 1.0), Is.SameAs(fine));
    }

    [Test]
    public void A_pinned_side_holds_and_a_pinned_tree_with_no_room_on_it_is_blocked()
    {
        var (field, twoPoints, _) = Pass14OnAb70();
        var detour = new ObstacleDetour(new PolygonOffsetService());
        var free = detour.Compute(twoPoints, field.Boundary, KeepOut, Radius)!;
        var round = free.Rounds.Single(r => r.InnerIndex == Tree1);

        var same = detour.Compute(twoPoints, field.Boundary, KeepOut, Radius, 1.0, new Dictionary<int, int> { [Tree1] = round.Side })!;
        Assert.That(same.GoneRound, Is.EqualTo(new[] { Tree1 }));
        Assert.That(same.Rounds.Single().Side, Is.EqualTo(round.Side));

        var other = detour.Compute(twoPoints, field.Boundary, KeepOut, Radius, 1.0, new Dictionary<int, int> { [Tree1] = -round.Side })!;
        Assert.That(other.GoneRound, Is.EqualTo(new[] { Tree1 }), "the other side has room here too");
        Assert.That(other.Rounds.Single().Side, Is.EqualTo(-round.Side), "and the pin picks it");

        // Pinned to the side where the Ditch square is in the way: blocked, never flipped.
        var ditch = field.Obstacles.First(o => o.Name == "Ditch square");
        var tree = field.Obstacles.First(o => o.Name == "Tree circle 1");
        var pass = new List<Vec3> { new(tree.CentreE, tree.CentreN - 200, 0), new(tree.CentreE, tree.CentreN + 200, 0) };
        var north = detour.Compute(pass, field.Boundary, KeepOut, Radius)!;
        Assert.That(north.GoneRound, Is.EqualTo(new[] { Tree1 }));
        int towardsDitch = ditch.CentreE > tree.CentreE ? -1 : 1; // heading north, left is west
        int ditchSide = (ditch.CentreE - tree.CentreE) * towardsDitch > 0 ? towardsDitch : -towardsDitch;
        var pinned = detour.Compute(pass, field.Boundary, KeepOut, Radius, 1.0, new Dictionary<int, int> { [Tree1] = ditchSide })!;
        Assert.That(pinned.Rounds.Count == 0 || pinned.Rounds.Single().Side == ditchSide, "a pin is never flipped");
    }

    private static double DistanceTo(List<Vec3> line, Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].Easting, ay = line[i - 1].Northing, dx = line[i].Easting - ax, dy = line[i].Northing - ay;
            double l2 = dx * dx + dy * dy, t = l2 > 0 ? Math.Clamp(((p.Easting - ax) * dx + (p.Northing - ay) * dy) / l2, 0, 1) : 0;
            double qx = ax + t * dx - p.Easting, qy = ay + t * dy - p.Northing;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }
}
