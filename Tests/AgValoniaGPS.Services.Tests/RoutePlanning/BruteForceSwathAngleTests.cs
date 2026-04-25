// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class BruteForceSwathAngleTests
{
    /// <summary>1° step → optimal heading is found within ~half a degree.</summary>
    private const double DegreeTolerance = 0.02;  // ≈ 1.15°

    [Test]
    public void WideRectangle_OptimalHeadingIsAlongLongAxis_East()
    {
        // 10m east-west × 5m north-south. Long axis is along E.
        // Swaths along long axis → 5m perp extent → 5/opWidth swaths.
        // In our heading convention (from +N CW), east = π/2.
        var rect = new List<Vec2> { new(0, 0), new(10, 0), new(10, 5), new(0, 5) };
        double h = BruteForceSwathAngle.FindOptimalHeading(rect, opWidth: 1.0);
        Assert.That(h, Is.EqualTo(Math.PI / 2).Within(DegreeTolerance));
    }

    [Test]
    public void TallRectangle_OptimalHeadingIsAlongLongAxis_North()
    {
        // 5m east × 10m north. Long axis is N. Heading = 0 or π (bidirectional);
        // search range [0, π) → expect 0.
        var rect = new List<Vec2> { new(0, 0), new(5, 0), new(5, 10), new(0, 10) };
        double h = BruteForceSwathAngle.FindOptimalHeading(rect, opWidth: 1.0);
        Assert.That(h, Is.EqualTo(0).Within(DegreeTolerance));
    }

    [Test]
    public void TiltedRectangle_OptimalAlignsWithRotatedLongAxis()
    {
        // 10×5 rectangle rotated 45° CCW (standard math frame). Long axis now
        // points NE = heading π/4 in our convention.
        const double a = 0.7071067811865476;
        var tilted = new List<Vec2>
        {
            new(0, 0),
            new(10 * a, 10 * a),
            new(10 * a - 5 * a, 10 * a + 5 * a),
            new(-5 * a, 5 * a),
        };
        double h = BruteForceSwathAngle.FindOptimalHeading(tilted, opWidth: 1.0);
        Assert.That(h, Is.EqualTo(Math.PI / 4).Within(DegreeTolerance));
    }

    [Test]
    public void OpWidthLargerThanCell_AllAnglesCostOne()
    {
        // 5×5 square with 100m opWidth: every angle yields exactly 1 swath.
        // First angle wins (heading 0).
        var square = new List<Vec2> { new(0, 0), new(5, 0), new(5, 5), new(0, 5) };
        double h = BruteForceSwathAngle.FindOptimalHeading(square, opWidth: 100.0);
        Assert.That(h, Is.EqualTo(0).Within(DegreeTolerance));
    }

    [Test]
    public void EmptyOrDegenerateInput_ReturnsZero()
    {
        Assert.That(BruteForceSwathAngle.FindOptimalHeading(new List<Vec2>(), 1.0),
            Is.EqualTo(0).Within(1e-9));
        Assert.That(BruteForceSwathAngle.FindOptimalHeading(
                new List<Vec2> { new(0, 0), new(1, 0) }, 1.0),
            Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void ZeroOrNegativeOpWidth_ReturnsZero()
    {
        var rect = new List<Vec2> { new(0, 0), new(10, 0), new(10, 5), new(0, 5) };
        Assert.That(BruteForceSwathAngle.FindOptimalHeading(rect, opWidth: 0), Is.EqualTo(0));
        Assert.That(BruteForceSwathAngle.FindOptimalHeading(rect, opWidth: -1), Is.EqualTo(0));
    }

    [Test]
    public void StepDegreesValidation()
    {
        var rect = new List<Vec2> { new(0, 0), new(10, 0), new(10, 5), new(0, 5) };
        Assert.That(() => BruteForceSwathAngle.FindOptimalHeading(rect, 1.0, stepDegrees: 0),
            Throws.ArgumentException);
    }

    [Test]
    public void MinTurnCountObjective_PerpExtentMatchesGeometry()
    {
        // Direct test of the cost function — perpendicular extent of a 10×5
        // rectangle is 5 when measured perpendicular to its long axis (east),
        // 10 when measured perpendicular to its short axis (north).
        var rect = new List<Vec2> { new(0, 0), new(10, 0), new(10, 5), new(0, 5) };
        Assert.That(MinTurnCountObjective.PerpendicularExtent(rect, Math.PI / 2),
            Is.EqualTo(5).Within(1e-9));
        Assert.That(MinTurnCountObjective.PerpendicularExtent(rect, 0),
            Is.EqualTo(10).Within(1e-9));
    }

    [Test]
    public void DecomposedCellRoundTrip_OptimalAngleIsSensible()
    {
        // Run a real decomposition, then find optimal angle for one of its cells.
        // The L-shape's bottom bar is roughly 10×5 — optimal should be along its
        // long axis (east in this construction).
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };
        var cells = BoustrophedonDecomp.Decompose(l, new List<List<Vec2>>(), sweepHeading: 0.0);
        Assert.That(cells.Count, Is.EqualTo(2));

        // Bottom bar = the cell with greater perp extent (≈10m east-west).
        Cell bottom = cells[0];
        double maxExtent = double.NegativeInfinity;
        foreach (var c in cells)
        {
            double ext = MinTurnCountObjective.PerpendicularExtent(c.Polygon, swathHeading: 0.0);
            if (ext > maxExtent) { maxExtent = ext; bottom = c; }
        }

        double h = BruteForceSwathAngle.FindOptimalHeading(bottom, opWidth: 1.0);
        // East ± a few degrees is acceptable (cut-end extension of 1cm slightly
        // offsets the cell vertices; tilted-by-perturbation sweep adds another
        // ~6 millideg).
        Assert.That(h, Is.InRange(Math.PI / 2 - 0.02, Math.PI / 2 + 0.02));
    }
}
