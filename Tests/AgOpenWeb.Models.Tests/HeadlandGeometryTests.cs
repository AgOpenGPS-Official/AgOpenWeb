// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// What valid output of the headland's geometry is (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, item 2),
/// not only that it matches today's: on a bow-tie, duplicate and very close points, near-parallel
/// lines, and a ring in either winding. Every number is finite; a point said to be on a line is
/// on it; a ring said to be inside is inside.
/// </summary>
[TestFixture]
public class HeadlandGeometryTests
{
    private static List<Vec3> Ring(params (double e, double n)[] pts) => pts.Select(p => new Vec3(p.e, p.n, 0)).ToList();
    private static List<BoundaryPoint> Boundary(params (double e, double n)[] pts) => pts.Select(p => new BoundaryPoint(p.e, p.n, 0)).ToList();
    private static readonly (double, double)[] SquareCcw = { (0, 0), (100, 0), (100, 100), (0, 100) };
    private static (double, double)[] Cw((double, double)[] ring) => ring.Reverse().ToArray();

    private static bool Finite(Vec3 p) => double.IsFinite(p.Easting) && double.IsFinite(p.Northing) && double.IsFinite(p.Heading);

    /// <summary>Do two non-adjacent edges of the line cross (inside both, not at an end)?</summary>
    private static bool CrossesItself(List<Vec3> line)
    {
        for (int i = 0; i < line.Count - 1; i++)
            for (int j = i + 2; j < line.Count - 1; j++)
                if (HeadlandGeometry.LineSegmentIntersection(line[i].Easting, line[i].Northing, line[i + 1].Easting, line[i + 1].Northing,
                        line[j].Easting, line[j].Northing, line[j + 1].Easting, line[j + 1].Northing, out double t, out double u)
                    && t > 0.01 && t < 0.99 && u > 0.01 && u < 0.99)
                    return true;
        return false;
    }

    // ── Self-intersections ─────────────────────────────────────────────

    [Test]
    public void A_bow_tie_loses_its_loop_and_does_not_cross_itself()
    {
        // An offset line that folded back over a small feature: the edges 1-2 and 3-4 cross.
        var line = Ring((0, 0), (10, 0), (20, 10), (20, 0), (10, 10), (30, 10), (40, 10));
        Assume.That(CrossesItself(line), "the input is a bow-tie");

        var clean = HeadlandGeometry.RemoveSelfIntersections(line);
        Assert.That(CrossesItself(clean), Is.False);
        Assert.That(clean, Has.All.Matches<Vec3>(Finite));
        Assert.That(clean[0], Is.EqualTo(line[0]), "starts where the line starts");
        Assert.That(clean[^1], Is.EqualTo(line[^1]), "ends where the line ends");
    }

    [Test]
    public void A_line_that_does_not_cross_itself_is_kept_as_it_is()
    {
        var line = Ring((0, 0), (10, 0), (20, 5), (30, 5), (40, 0));
        Assert.That(HeadlandGeometry.RemoveSelfIntersections(line), Is.EqualTo(line));
    }

    [Test]
    public void Duplicate_and_very_close_points_give_finite_points()
    {
        var line = Ring((0, 0), (10, 0), (10, 0), (10, 1e-9), (20, 0), (20, 0), (30, 0));
        var clean = HeadlandGeometry.RemoveSelfIntersections(line);
        Assert.That(clean, Has.All.Matches<Vec3>(Finite));
        Assert.That(CrossesItself(clean), Is.False);
    }

    // ── Area and winding ───────────────────────────────────────────────

    [Test]
    public void The_signed_area_is_the_area_with_the_winding_as_its_sign()
    {
        double ccw = HeadlandGeometry.CalculateSignedArea(Ring(SquareCcw));
        double cw = HeadlandGeometry.CalculateSignedArea(Ring(Cw(SquareCcw)));
        Assert.That(Math.Abs(ccw), Is.EqualTo(10_000).Within(1e-9));
        Assert.That(cw, Is.EqualTo(-ccw).Within(1e-9), "the other winding, the other sign");
    }

    [Test]
    public void A_closing_point_that_repeats_the_first_does_not_change_the_area()
    {
        var open = Ring(SquareCcw);
        var closed = Ring(SquareCcw.Append(SquareCcw[0]).ToArray());
        Assert.That(HeadlandGeometry.CalculateSignedArea(closed), Is.EqualTo(HeadlandGeometry.CalculateSignedArea(open)).Within(1e-9));
    }

    // ── Intersections ──────────────────────────────────────────────────

    [Test]
    public void Two_crossing_segments_meet_at_a_point_on_both()
    {
        Assert.That(HeadlandGeometry.LineSegmentIntersection(0, 0, 10, 10, 0, 10, 10, 0, out double t, out double u), Is.True);
        Assert.That(t, Is.EqualTo(0.5).Within(1e-12));
        Assert.That(u, Is.EqualTo(0.5).Within(1e-12));
    }

    [Test]
    public void Parallel_and_overlapping_segments_do_not_meet()
    {
        Assert.That(HeadlandGeometry.LineSegmentIntersection(0, 0, 10, 0, 0, 1, 10, 1, out _, out _), Is.False, "parallel");
        Assert.That(HeadlandGeometry.LineSegmentIntersection(0, 0, 10, 0, 5, 0, 15, 0, out _, out _), Is.False, "on one line");
        Assert.That(HeadlandGeometry.LineLineIntersection(0, 0, 10, 0, 0, 1, 10, 1, out _, out _), Is.False);
    }

    [Test]
    public void Near_parallel_lines_meet_at_a_finite_point_on_both()
    {
        // 1 mm over 100 m: nearly parallel, but they cross.
        Assert.That(HeadlandGeometry.LineLineIntersection(0, 0, 100, 0, 0, -0.001, 100, 0.001, out double ix, out double iy), Is.True);
        Assert.That(double.IsFinite(ix) && double.IsFinite(iy));
        Assert.That(iy, Is.EqualTo(0).Within(1e-9), "on the first line");
        Assert.That(iy, Is.EqualTo(-0.001 + ix / 100 * 0.002).Within(1e-9), "on the second");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_line_across_a_ring_meets_it_nearest_and_farthest_on_its_edges(bool clockwise)
    {
        var ring = Ring(clockwise ? Cw(SquareCcw) : SquareCcw);
        ring.Add(ring[0]); // closed
        var from = new Vec3(-50, 50, 0);
        var to = new Vec3(150, 50, 0);

        int near = HeadlandGeometry.FindLineHeadlandIntersection(from, to, ring, out var pNear);
        int far = HeadlandGeometry.FindLineHeadlandIntersectionFarthest(from, to, ring, out var pFar);
        Assert.That(near, Is.Not.EqualTo(-1));
        Assert.That(far, Is.Not.EqualTo(-1));
        Assert.That(pNear.Easting, Is.EqualTo(0).Within(1e-9), "the near side");
        Assert.That(pFar.Easting, Is.EqualTo(100).Within(1e-9), "the far side");
        Assert.That(pNear.Northing, Is.EqualTo(50).Within(1e-9));
        Assert.That(pFar.Northing, Is.EqualTo(50).Within(1e-9));
    }

    [Test]
    public void A_line_that_stops_short_of_the_ring_does_not_meet_it()
    {
        var ring = Ring(SquareCcw); ring.Add(ring[0]);
        Assert.That(HeadlandGeometry.FindLineHeadlandIntersection(new Vec3(-50, 50, 0), new Vec3(-10, 50, 0), ring, out _), Is.EqualTo(-1));
    }

    // ── Inside the field ───────────────────────────────────────────────

    [TestCase(false)]
    [TestCase(true)]
    public void The_interior_point_of_a_U_shaped_field_is_inside_it(bool clockwise)
    {
        // The average of a U's corners is in its notch, outside the field.
        (double, double)[] u = { (0, 0), (100, 0), (100, 100), (70, 100), (70, 20), (30, 20), (30, 100), (0, 100) };
        var ring = Boundary(clockwise ? Cw(u) : u);
        var (e, n) = HeadlandGeometry.FieldInteriorPoint(ring);
        Assert.That(GeometryMath.IsPointInPolygon(ring, e, n), Is.True, $"({e:0.#}, {n:0.#})");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void An_inward_offset_is_inward_and_an_outward_one_is_not(bool clockwise)
    {
        var field = Boundary(clockwise ? Cw(SquareCcw) : SquareCcw);
        var inward = Ring((10, 10), (90, 10), (90, 90), (10, 90)); inward.Add(inward[0]);
        var outward = Ring((-10, -10), (110, -10), (110, 110), (-10, 110)); outward.Add(outward[0]);
        Assert.That(HeadlandGeometry.IsInwardOffsetOf(inward, field), Is.True);
        Assert.That(HeadlandGeometry.IsInwardOffsetOf(outward, field), Is.False);
    }

    // ── Boundary arcs and the preview ──────────────────────────────────

    [Test]
    public void The_arc_between_two_vertices_is_the_shorter_one_from_the_first_to_the_second()
    {
        var ring = Boundary((0, 0), (10, 0), (20, 0), (20, 10), (20, 20), (10, 20), (0, 20), (0, 10));
        var arc = HeadlandGeometry.ExtractBoundaryRing(ring, 1, 3);
        Assert.That(arc.Select(p => (p.Easting, p.Northing)), Is.EqualTo(new[] { (10.0, 0.0), (20.0, 0.0), (20.0, 10.0) }));
        var back = HeadlandGeometry.ExtractBoundaryRing(ring, 1, 7);
        Assert.That(back.Select(p => (p.Easting, p.Northing)), Is.EqualTo(new[] { (10.0, 0.0), (0.0, 0.0), (0.0, 10.0) }), "the other way round");
    }

    [Test]
    public void The_nearest_vertex_and_the_bounding_diagonal()
    {
        var ring = Boundary(SquareCcw);
        Assert.That(HeadlandGeometry.FindNearestBoundaryVertex(ring, 98, 3), Is.EqualTo(1));
        Assert.That(HeadlandGeometry.BoundaryBboxDiagonal(ring), Is.EqualTo(Math.Sqrt(2) * 100).Within(1e-9));
        Assert.That(HeadlandGeometry.BoundaryBboxDiagonal(Boundary((5, 5), (5, 5))), Is.EqualTo(50), "a field with no size: a length that still reaches past it");
    }

    [Test]
    public void The_preview_s_headings_point_along_the_line_and_stay_finite_on_a_repeated_point()
    {
        var preview = new List<Vec2> { new(0, 0), new(10, 0), new(10, 0), new(10, 10) };
        var line = HeadlandGeometry.ConvertPreviewToVec3(preview)!;
        Assert.That(line, Has.All.Matches<Vec3>(Finite));
        Assert.That(line[0].Heading, Is.EqualTo(Math.PI / 2).Within(1e-12), "east");
        Assert.That(line[^1].Heading, Is.EqualTo(0).Within(1e-12), "north");
        Assert.That(HeadlandGeometry.ConvertPreviewToVec3(new List<Vec2> { new(0, 0), new(1, 0) }), Is.Null, "under three points is no line");
    }

    [Test]
    public void A_segment_s_extensions_carry_its_ends_on_in_the_direction_they_run()
    {
        var seg = new HeadlandSegment { OffsetPoints = Ring((0, 0), (10, 0), (20, 0)), StartExtension = 5, EndExtension = 7 };
        var line = HeadlandGeometry.BuildOffsetLineWithExtensions(seg);
        Assert.That(line.Count, Is.EqualTo(5));
        Assert.That((line[0].Easting, line[0].Northing), Is.EqualTo((-5.0, 0.0)));
        Assert.That((line[^1].Easting, line[^1].Northing), Is.EqualTo((27.0, 0.0)));

        var stub = new HeadlandSegment { OffsetPoints = Ring((0, 0), (0, 0)), StartExtension = 5, EndExtension = 5 };
        Assert.That(HeadlandGeometry.BuildOffsetLineWithExtensions(stub), Has.All.Matches<Vec3>(Finite), "no direction to run in: no extension, no NaN");
    }
}
