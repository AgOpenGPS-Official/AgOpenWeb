using System;
using System.Collections.Generic;
using System.Linq;

using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.YouTurn;
using NUnit.Framework;

namespace AgValoniaGPS.Models.Tests;

[TestFixture]
public class SagittaTurnGeometryTests
{
    private const double R = 5.0;

    private static double MaxStep(IReadOnlyList<Vec3> path)
    {
        double max = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            if (d > max) max = d;
        }
        return max;
    }

    [Test]
    public void PlainSemicircle_NoSagitta_LandsTwoRadiiAcross()
    {
        // Heading north (theta = 0), turning right, no sagitta -> a clean semicircle.
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: 0.0, angle: Math.PI);

        Assert.That(path.Count, Is.GreaterThan(2));

        var end = path[^1];
        // A 180° turn of radius R offsets laterally by 2R and returns to the start's along-track line.
        Assert.That(end.Easting, Is.EqualTo(2 * R).Within(0.05), "lateral offset should be 2R");
        Assert.That(end.Northing, Is.EqualTo(0.0).Within(0.05), "should return to the start along-track line");
    }

    [Test]
    public void PlainSemicircle_ReversesHeadingByPi()
    {
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: 0.0, angle: Math.PI);

        double endHeading = path[^1].Heading;
        // Heading should have advanced ~PI (mod 2π).
        double delta = Math.Abs(endHeading - Math.PI);
        Assert.That(delta, Is.LessThan(0.02), $"end heading {endHeading} should be ~PI");
    }

    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(8.0)]
    [TestCase(10.0)]
    public void Sagitta_LandsExactlyOnTargetRow(double targetLateral)
    {
        // To land at lateral L, pass pullback = 2R - L. The arc must end at E≈L
        // with heading reversed by π so the exit leg lies on the next track.
        var start = new Vec3(0, 0, 0);
        double pullback = 2 * R - targetLateral;
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: pullback, angle: Math.PI);

        var end = path[^1];
        Assert.That(end.Easting, Is.EqualTo(targetLateral).Within(0.05),
            "landing lateral should equal the target row offset");
        Assert.That(end.Heading, Is.EqualTo(Math.PI).Within(0.02),
            "exit heading should be reversed (parallel to the track)");
    }

    [Test]
    public void Sagitta_AddsCounterArc_StartingTheOppositeWay()
    {
        // With a sagitta, the path must begin with a short counter-arc: for a
        // right turn heading north, the very first move bends LEFT (easting < 0)
        // before the main arc sweeps right.
        var start = new Vec3(0, 0, 0);
        double offset = R; // tight row -> meaningful sagitta
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: offset, angle: Math.PI);

        Assert.That(path.Count, Is.GreaterThan(4));
        Assert.That(path[1].Easting, Is.LessThan(0.0),
            "first point of a sagitta right-turn should bend left (counter-arc lead-in)");
    }

    [Test]
    public void Sagitta_NoSagitta_HaveDifferentPaths()
    {
        var start = new Vec3(0, 0, 0);
        var plain = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, 0.0, Math.PI);
        var sagitta = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, R, Math.PI);

        Assert.That(sagitta.Count, Is.GreaterThan(plain.Count),
            "the counter-arc lead-in should add points");
    }

    [Test]
    public void LeftAndRight_AreMirrored()
    {
        var start = new Vec3(0, 0, 0);
        var right = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true, R, 0.0, Math.PI);
        var left = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: false, R, 0.0, Math.PI);

        Assert.That(right[^1].Easting, Is.EqualTo(+2 * R).Within(0.05));
        Assert.That(left[^1].Easting, Is.EqualTo(-2 * R).Within(0.05));
    }

    [Test]
    public void Points_AreFinelySpaced_NoLargeGaps()
    {
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, R, Math.PI);

        // Segment sizing is radius * 0.1, so steps should stay well under ~0.6 m for R=5.
        Assert.That(MaxStep(path), Is.LessThan(R * 0.15));
    }

    [Test]
    public void ZeroRadius_DoesNotThrow_ReturnsStartOnly()
    {
        var start = new Vec3(1, 2, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, 0.0, 0.0, Math.PI);
        Assert.That(path.Count, Is.EqualTo(1));
    }
}
