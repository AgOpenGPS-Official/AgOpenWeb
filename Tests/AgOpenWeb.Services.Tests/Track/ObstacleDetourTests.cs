// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// Going round obstacles chosen as Go around: the pass is bent round each one it runs into,
/// tractor and implement kept outside, never tighter than the turn radius, on the side with
/// room, and not at all when there is no room.
/// </summary>
[TestFixture]
public class ObstacleDetourTests
{
    private const double KeepOut = 4.0;  // a 6 m implement and 1 m margin
    private const double Radius = 8.0;

    private static readonly ObstacleDetour Detour = new(new PolygonOffsetService());

    private static BoundaryPolygon Rect(double x0, double y0, double x1, double y1)
    {
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(x0, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y1, 0));
        poly.Points.Add(new BoundaryPoint(x0, y1, 0));
        poly.UpdateBounds();
        return poly;
    }

    private static BoundaryPolygon Tree(double e, double n, double d, ObstacleAction action = ObstacleAction.GoRound)
    {
        var poly = new BoundaryPolygon { AtObstacle = action };
        poly.SetCircle(new ObstacleCircle(e, n, d));
        return poly;
    }

    private static Boundary Field(double x0, double x1, params BoundaryPolygon[] obstacles)
    {
        var b = new Boundary { OuterBoundary = Rect(x0, -100, x1, 300) };
        b.InnerBoundaries.AddRange(obstacles);
        return b;
    }

    // A pass running north along easting 0.
    private static readonly List<Vec3> North = new() { new Vec3(0, 0, 0), new Vec3(0, 200, 0) };

    private static double ClearanceFrom(IReadOnlyList<Vec3> line, double e, double n) =>
        Segments(line).Min(s => DistanceToSegment(s.A, s.B, e, n));

    private static IEnumerable<(Vec3 A, Vec3 B)> Segments(IReadOnlyList<Vec3> line)
    {
        for (int i = 0; i + 1 < line.Count; i++) yield return (line[i], line[i + 1]);
    }

    private static double DistanceToSegment(Vec3 a, Vec3 b, double e, double n)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / l2, 0, 1) : 0;
        double qx = a.Easting + t * dx - e, qy = a.Northing + t * dy - n;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // The tightest radius along the line, from circles through points about 1 m apart.
    private static double TightestRadius(IReadOnlyList<Vec3> line)
    {
        double best = double.MaxValue;
        for (int i = 4; i + 4 < line.Count; i++)
        {
            Vec3 a = line[i - 4], b = line[i], c = line[i + 4];
            double ab = Dist(a, b), bc = Dist(b, c), ca = Dist(c, a);
            if (ab > 5 || bc > 5) continue; // the long straight ends
            double cross = Math.Abs((b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting));
            if (cross < 1e-9) continue;
            best = Math.Min(best, ab * bc * ca / (2 * cross));
        }
        return best;
    }

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));

    [Test]
    public void A_tree_on_the_pass_is_gone_round_with_the_implement_kept_out()
    {
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, Radius);

        Assert.That(bend, Is.Not.Null);
        Assert.That(bend!.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Blocked, Is.Empty);
        // 3 m of tree and 4 m of keep-out from its centre (the 24-gon's corners reach 3.03 m)
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        // back on the pass well before and after it
        foreach (var p in bend.Points.Where(p => Math.Abs(p.Northing - 100) > 40))
            Assert.That(Math.Abs(p.Easting), Is.LessThan(1e-6), $"off the pass at {p.Northing:F1}");
        // it runs the same way as the pass, with headings to match
        Assert.That(bend.Points[0].Northing, Is.LessThan(bend.Points[^1].Northing));
        Assert.That(bend.Points.Skip(1).Zip(bend.Points, (b, a) => b.Northing - a.Northing).All(d => d > 0));
    }

    [Test]
    public void The_bend_is_never_tighter_than_the_turn_radius()
    {
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, Radius)!;
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(Radius * 0.9));
    }

    // Where the line leaves the pass before the tree and rejoins it after, as distances from
    // the tree's centre along the pass.
    private static (double Before, double After) BendEnds(IReadOnlyList<Vec3> line, double treeN)
    {
        var off = line.Where(p => Math.Abs(p.Easting) > 0.01).ToList();
        return (treeN - off.Min(p => p.Northing), off.Max(p => p.Northing) - treeN);
    }

    [TestCase(4.0)]
    [TestCase(6.0)]
    [TestCase(10.0)]
    public void The_bend_is_as_short_as_its_radius_allows(double radius)
    {
        // A 6 m tree and 4 m kept: the line moves 7 m aside. Two arcs of the radius need about
        // 2 x sqrt(7 R) along the pass, plus the 7 m the grown tree reaches before its centre.
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, radius)!;
        var (before, after) = BendEnds(bend.Points, 100);
        double ideal = 7.0 + 2 * Math.Sqrt(7.0 * radius);
        Assert.That(before, Is.LessThanOrEqualTo(ideal * 1.3), "starts near the tree");
        Assert.That(after, Is.LessThanOrEqualTo(ideal * 1.3), "ends near the tree");
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(radius * 0.9));
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        TestContext.Out.WriteLine($"R {radius}: leaves {before:F1} m before the centre, back {after:F1} m after");
    }

    [Test]
    public void A_tighter_radius_starts_the_bend_nearer_the_obstacle()
    {
        var field = Field(-100, 100, Tree(0, 100, 6));
        var wide = BendEnds(Detour.Compute(North, field, KeepOut, 10.0)!.Points, 100);
        var tight = BendEnds(Detour.Compute(North, field, KeepOut, 4.0)!.Points, 100);
        Assert.That(tight.Before, Is.LessThan(wide.Before - 2));
    }

    [Test]
    public void Near_the_edge_only_half_the_implement_must_stay_inside_the_field()
    {
        // 3 m of half implement and a 2 m margin; the tree 1 m right of the line, the field's
        // edge 10 m left. Going left (the shorter way) puts the line 6 m left, 4 m from the edge:
        // the implement still fits in the field, though not with the margin, and that is allowed.
        var field = Field(-10, 100, Tree(1, 100, 4));
        var bend = Detour.Compute(North, field, 3.0 + 2.0, Radius, margin: 2.0)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Min(p => p.Easting), Is.LessThan(-5.5));
        Assert.That(bend.Points.Min(p => p.Easting), Is.GreaterThanOrEqualTo(-10 + 3.0));
    }

    [Test]
    public void It_goes_round_on_the_side_that_needs_the_smaller_move()
    {
        // The tree's centre 1 m right (east) of a pass heading north: left is shorter.
        var bend = Detour.Compute(North, Field(-100, 100, Tree(1, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.Points.Min(p => p.Easting), Is.LessThan(-5.5));
        Assert.That(bend.Points.Max(p => p.Easting), Is.LessThan(1e-6));
    }

    [Test]
    public void It_goes_round_on_the_field_side_when_the_edge_is_near()
    {
        // The field's edge 8 m left of the pass: going left would take the line out.
        var bend = Detour.Compute(North, Field(-8, 100, Tree(-1, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Min(p => p.Easting), Is.GreaterThan(-1e-6));
        Assert.That(ClearanceFrom(bend.Points, -1, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
    }

    [Test]
    public void With_no_room_either_side_it_is_blocked_and_the_pass_stays_straight()
    {
        var bend = Detour.Compute(North, Field(-6, 6, Tree(0, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.Empty);
        Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.All(p => Math.Abs(p.Easting) < 1e-6));
    }

    [Test]
    public void A_tree_clear_of_the_implement_is_left_alone()
    {
        Assert.That(Detour.Compute(North, Field(-100, 100, Tree(7.5, 100, 6)), KeepOut, Radius), Is.Null);
    }

    [TestCase(ObstacleAction.Turn)]
    [TestCase(ObstacleAction.DriveThrough)]
    public void Only_obstacles_chosen_as_go_round_are_gone_round(ObstacleAction action)
    {
        Assert.That(Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6, action)), KeepOut, Radius), Is.Null);
    }

    [Test]
    public void The_way_round_one_tree_also_goes_round_the_tree_beside_it()
    {
        // A tree on the pass and one 6 m to its left, the field's edge close on the right:
        // the line goes left of both.
        var field = Field(-100, 8, Tree(0, 100, 4), Tree(-6, 100, 4));
        var bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(2.0 + KeepOut - 0.05));
        Assert.That(ClearanceFrom(bend.Points, -6, 100), Is.GreaterThanOrEqualTo(2.0 + KeepOut - 0.05));
    }

    [Test]
    public void A_row_of_trees_on_the_pass_is_gone_round_in_one_bend()
    {
        var field = Field(-100, 100, Tree(0, 60, 5), Tree(0.5, 70, 5), Tree(-0.5, 80, 5));
        var bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Has.Count.EqualTo(3));
        foreach (var (e, n) in new[] { (0.0, 60.0), (0.5, 70.0), (-0.5, 80.0) })
            Assert.That(ClearanceFrom(bend.Points, e, n), Is.GreaterThanOrEqualTo(2.5 + KeepOut - 0.05));
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(Radius * 0.9));
    }

    [Test]
    public void A_curved_pass_is_bent_too()
    {
        // A gentle arc from (0,0) bending east; the tree on it at about 100 m.
        var arc = new List<Vec3>();
        for (int i = 0; i <= 100; i++)
        {
            double a = i * 0.004;   // 0.4 rad over 200 m: radius 500 m
            arc.Add(new Vec3(500 - 500 * Math.Cos(a), 500 * Math.Sin(a), 0));
        }
        var onArc = arc[50];
        var bend = Detour.Compute(arc, Field(-100, 200, Tree(onArc.Easting, onArc.Northing, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(ClearanceFrom(bend.Points, onArc.Easting, onArc.Northing), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        Assert.That(bend.Points[0].Easting, Is.EqualTo(arc[0].Easting).Within(1e-9));
        Assert.That(bend.Points[^1].Northing, Is.EqualTo(arc[^1].Northing).Within(1e-9));
    }

    [Test]
    public void The_same_pass_gets_the_same_bend_back()
    {
        var detour = new ObstacleDetour(new PolygonOffsetService());
        var track = new AgOpenWeb.Models.Track.Track { Points = new List<Vec3>(North) };
        var field = Field(-100, 100, Tree(0, 100, 6));
        var first = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(detour.For(track, 0, North, field, KeepOut, Radius, 1.0), Is.SameAs(first));

        field.InnerBoundaries[0].SetCircle(new ObstacleCircle(0, 120, 6)); // moved
        var moved = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(moved, Is.Not.SameAs(first));
        Assert.That(ClearanceFrom(moved!.Points, 0, 120), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
    }
}
