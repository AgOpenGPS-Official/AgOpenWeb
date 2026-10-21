// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RoundCorners: every corner becomes an arc of the given radius and nothing else moves, so a
/// curve along the fence cuts a corner only as much as a turn of that radius needs.
/// </summary>
[TestFixture]
public class PolygonRoundCornersTests
{
    private readonly PolygonOffsetService _service = new();

    private static double DistanceToRing(List<Vec2> ring, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            best = Math.Min(best, GeometryMath.PointToSegmentDistance(new Vec2(e, n), a, b));
        }
        return best;
    }

    [Test]
    public void ASquaresCorners_BecomeArcsOfTheRadius()
    {
        var square = new List<Vec2> { new(0, 0), new(200, 0), new(200, 200), new(0, 200) };

        var r = _service.RoundCorners(square, 8);

        Assert.That(r, Is.Not.Null);
        // A 90° corner rounded to radius R passes R(√2 − 1) from the corner.
        Assert.That(DistanceToRing(r!, 0, 0), Is.EqualTo(8 * (Math.Sqrt(2) - 1)).Within(0.05));
        Assert.That(DistanceToRing(r!, 200, 200), Is.EqualTo(8 * (Math.Sqrt(2) - 1)).Within(0.05));
        // The sides stay put away from the corners.
        Assert.That(DistanceToRing(r!, 100, 0), Is.LessThan(0.01));
        Assert.That(DistanceToRing(r!, 0, 100), Is.LessThan(0.01));
    }

    [Test]
    public void AnInsideCorner_IsRoundedToo()
    {
        // L-shape: the inside corner at (100, 100) is a turn the other way.
        var l = new List<Vec2> { new(0, 0), new(200, 0), new(200, 100), new(100, 100), new(100, 200), new(0, 200) };

        var r = _service.RoundCorners(l, 8);

        Assert.That(r, Is.Not.Null);
        Assert.That(DistanceToRing(r!, 100, 100), Is.EqualTo(8 * (Math.Sqrt(2) - 1)).Within(0.05));
    }

    [Test]
    public void ASharpCornerOfALongSide_IsCutOnlyAsMuchAsTheRadiusNeeds()
    {
        // The case Chaikin smoothing got wrong: long sides meeting at 60°. Rounded to radius R,
        // the curve passes R(1/sin 30° − 1) = R from the corner, whatever the side lengths.
        var tri = new List<Vec2> { new(0, 0), new(800, 0), new(400, 800 * Math.Sin(Math.PI / 3)) };

        var r = _service.RoundCorners(tri, 8);

        Assert.That(r, Is.Not.Null);
        Assert.That(DistanceToRing(r!, 0, 0), Is.EqualTo(8.0).Within(0.05));
    }

    [Test]
    public void NoRadius_LeavesTheRingAsItIs()
    {
        var square = new List<Vec2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        Assert.That(_service.RoundCorners(square, 0), Is.EqualTo(square));
    }
}
