// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Base;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A section in a notch of the fence, or in the missing corner of an L-shaped field, is outside
/// the field. The quick answer of <see cref="BoundaryPolygon.GetSegmentBoundaryStatus"/> took any
/// point more than 50 m inside the polygon's bounding box as inside the polygon, which is true
/// only of a field with no notch: with "Off outside boundary" off and no headland to shut the
/// sections first, a boom driven across a notch 60 m long sprayed all of it (a tester's field,
/// the simulator steered by hand across the notch: 464 m² outside the fence).
/// </summary>
[TestFixture]
public class SectionInFenceNotchTests
{
    private static BoundaryPolygon Polygon(params (double E, double N)[] corners)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in corners)
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    // 400 x 400 m with a notch cut in from the east side: E 150..400, N 150..250.
    private static BoundaryPolygon FieldWithNotch() => Polygon(
        (0, 0), (400, 0), (400, 150), (150, 150), (150, 250), (400, 250), (400, 400), (0, 400));

    // An L: 400 x 400 m without its north-east quarter (E 200..400, N 200..400).
    private static BoundaryPolygon LShapedField() => Polygon(
        (0, 0), (400, 0), (400, 200), (200, 200), (200, 400), (0, 400));

    [TestCase(0.0, TestName = "In the notch, heading north")]
    [TestCase(Math.PI / 2, TestName = "In the notch, heading east")]
    [TestCase(0.7, TestName = "In the notch, heading north-east")]
    public void A_section_in_the_notch_is_outside(double heading)
    {
        // 100 m and more from every side of the bounding box.
        var r = FieldWithNotch().GetSegmentBoundaryStatus(new Vec2(250, 200), heading, 1.0);
        Assert.Multiple(() =>
        {
            Assert.That(r.IsFullyOutside, Is.True);
            Assert.That(r.InsidePercent, Is.EqualTo(0).Within(1e-9));
        });
    }

    [Test]
    public void A_section_in_the_missing_corner_of_an_L_is_outside()
    {
        var r = LShapedField().GetSegmentBoundaryStatus(new Vec2(300, 300), 0, 4.0);
        Assert.That(r.IsFullyOutside, Is.True);
    }

    [Test]
    public void A_section_deep_in_the_field_is_inside()
    {
        var field = FieldWithNotch();
        Assert.Multiple(() =>
        {
            Assert.That(field.GetSegmentBoundaryStatus(new Vec2(75, 200), 0, 1.0).IsFullyInside, Is.True, "west of the notch");
            Assert.That(field.GetSegmentBoundaryStatus(new Vec2(250, 75), 0, 1.0).IsFullyInside, Is.True, "south of the notch");
            Assert.That(field.GetSegmentBoundaryStatus(new Vec2(250, 325), 0, 1.0).IsFullyInside, Is.True, "north of the notch");
        });
    }

    [Test]
    public void A_section_across_the_notch_edge_is_partly_inside()
    {
        // Heading north, so the section lies east-west across the notch's west end at E 150.
        var r = FieldWithNotch().GetSegmentBoundaryStatus(new Vec2(150.5, 200), 0, 2.0);
        Assert.That(r.InsidePercent, Is.EqualTo(0.375).Within(1e-6));
    }

    /// <summary>The answer everywhere, against the plain rule: where the section's two ends and
    /// its centre are, with no edge of the field crossing it.</summary>
    [Test]
    public void The_quick_answer_agrees_with_the_point_tests_all_over_both_fields()
    {
        var rng = new Random(20261118);
        foreach (var field in new[] { FieldWithNotch(), LShapedField() })
        {
            for (int k = 0; k < 20000; k++)
            {
                double e = -20 + rng.NextDouble() * 440, n = -20 + rng.NextDouble() * 440;
                double heading = rng.NextDouble() * 2 * Math.PI, half = 0.5 + rng.NextDouble() * 6;
                double dE = Math.Sin(heading + Math.PI / 2) * half, dN = Math.Cos(heading + Math.PI / 2) * half;
                bool left = field.IsPointInside(e - dE, n - dN), centre = field.IsPointInside(e, n), right = field.IsPointInside(e + dE, n + dN);
                var r = field.GetSegmentBoundaryStatus(new Vec2(e, n), heading, half);

                if (left && centre && right && r.InsidePercent < 0.999 && !r.CrossesBoundary)
                    Assert.Fail($"({e:0.0},{n:0.0}) heading {heading:0.00} half {half:0.0}: both ends and the centre in, answered {r.InsidePercent:0.00} inside");
                if (!left && !centre && !right && r.InsidePercent > 0.001 && !r.CrossesBoundary)
                    Assert.Fail($"({e:0.0},{n:0.0}) heading {heading:0.00} half {half:0.0}: both ends and the centre out, answered {r.InsidePercent:0.00} inside");
            }
        }
    }
}
