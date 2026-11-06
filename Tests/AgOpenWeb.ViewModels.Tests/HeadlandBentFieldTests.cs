// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A whole-boundary headland on a bent field. The closed inward offset used to be kept only
/// when it contained the average of the boundary's vertices; on an L or a U that point lies
/// near the edge or outside the field, so a correct 12 m offset was refused and the headland
/// came out of the open-line fallback: on the field edge and crossing itself, or not at all.
/// </summary>
[TestFixture]
public class HeadlandBentFieldTests
{
    /// <summary>An L, 60 m wide arms: the vertex average (120, 120) is outside the field.</summary>
    private static Boundary LField() => Ring((0, 0), (300, 0), (300, 60), (60, 60), (60, 300), (0, 300));

    /// <summary>A U, 50 m wide arms: the vertex average (125, 137.5) is in the gap between them.</summary>
    private static Boundary UField() =>
        Ring((0, 0), (250, 0), (250, 250), (200, 250), (200, 50), (50, 50), (50, 250), (0, 250));

    private static Boundary Ring(params (double e, double n)[] pts)
    {
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in pts) outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        return new Boundary { OuterBoundary = outer };
    }

    private static double DistanceToEdge(IReadOnlyList<BoundaryPoint> ring, Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            best = Math.Min(best, GeometryMath.PointToSegmentDistance(p.Easting, p.Northing,
                ring[j].Easting, ring[j].Northing, ring[i].Easting, ring[i].Northing));
        return best;
    }

    private static MainViewModel VmWith(Boundary boundary)
    {
        var vm = new MainViewModelBuilder().Build();
        vm.State.Field.CurrentBoundary = boundary;
        return vm;
    }

    private static void AssertHeadlandIsTheInwardOffset(MainViewModel vm, Boundary boundary, double offset)
    {
        var ring = boundary.OuterBoundary!.Points;
        var seg = vm.HeadlandSegments[0];
        var line = vm.CurrentHeadlandLine;
        Assert.That(line, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(seg.IsEffective, Is.True, "the whole-boundary segment made the headland");
            Assert.That(vm.StatusMessage, Does.StartWith("Headland modified"), vm.StatusMessage);
            Assert.That(line!.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var p in line)
            {
                Assert.That(GeometryMath.IsPointInPolygon(ring, p.Easting, p.Northing), Is.True,
                    $"({p.Easting:F1}, {p.Northing:F1}) is inside the field");
                Assert.That(DistanceToEdge(ring, p), Is.GreaterThanOrEqualTo(offset - 0.5),
                    $"({p.Easting:F1}, {p.Northing:F1}) is {offset} m from the edge");
            }
        });
    }

    [Test]
    public void L_field_12m_headland_is_the_inward_offset_not_the_edge()
    {
        var field = LField();
        var vm = VmWith(field);

        vm.RemoteCreateHeadlandWholeBoundary(12);

        AssertHeadlandIsTheInwardOffset(vm, field, 12);
        // Two 276 × 36 arms sharing a 36 × 36 corner, less the rounded inside corner.
        Assert.That(GeometryMath.PolygonArea(vm.CurrentHeadlandLine!), Is.InRange(18_300, 18_700));
    }

    [Test]
    public void U_field_16m_headland_exists_and_keeps_off_the_edge()
    {
        var field = UField();
        var vm = VmWith(field);

        vm.RemoteCreateHeadlandWholeBoundary(16);

        AssertHeadlandIsTheInwardOffset(vm, field, 16);
        // 50 m arms leave an 18 m core: well under half the field.
        Assert.That(GeometryMath.PolygonArea(vm.CurrentHeadlandLine!),
            Is.GreaterThan(1_000).And.LessThan(GeometryMath.PolygonArea(field.OuterBoundary!.Points) / 2));
    }

    [Test]
    public void The_interior_point_of_an_L_is_deep_inside_it_where_the_vertex_average_is_not()
    {
        var ring = LField().OuterBoundary!.Points;

        Assert.That(GeometryMath.IsPointInPolygon(ring, 120, 120), Is.False, "the vertex average is outside the L");
        Assert.That(GeometryMath.TryInteriorPoint(ring, out double e, out double n), Is.True);
        Assert.That(GeometryMath.IsPointInPolygon(ring, e, n), Is.True);
        Assert.That(DistanceToEdge(ring, new Vec3(e, n, 0)), Is.GreaterThan(25), "about the arm's half width of 30 m");
    }
}
