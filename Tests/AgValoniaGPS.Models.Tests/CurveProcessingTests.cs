// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Guidance;
using NUnit.Framework;

namespace AgValoniaGPS.Models.Tests;

/// <summary>
/// Tests for the track-extension and reduction methods ported from
/// AgOpenGPS 6.8.2 CTrackMethods (issue #229).
/// </summary>
[TestFixture]
public class CurveProcessingTests
{
    // ──────────────────────────────────────────────────────────────────
    // AddStartEndPoints / AddStartPoints / AddEndPoints
    // ──────────────────────────────────────────────────────────────────

    [Test]
    public void AddStartEndPoints_EmptyInput_ReturnsEmpty()
    {
        var result = CurveProcessing.AddStartEndPoints(new List<Vec3>(), 5, 10);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AddStartEndPoints_NorthwardLine_PrependsAndAppendsAlongHeading()
    {
        // Heading 0 = north → +Northing direction
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0),
        };

        // ptsToAdd=3 with two-sided semantics adds (3-1)=2 at each end
        var result = CurveProcessing.AddStartEndPoints(input, 3, 10);

        Assert.That(result.Count, Is.EqualTo(input.Count + 2 + 2));

        // Two prepended points are 10m and 20m before the start (negative Northing)
        Assert.That(result[0].Northing, Is.EqualTo(-20).Within(1e-6));
        Assert.That(result[1].Northing, Is.EqualTo(-10).Within(1e-6));

        // Originals preserved in order
        Assert.That(result[2].Northing, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[3].Northing, Is.EqualTo(100).Within(1e-6));

        // Two appended points are 10m and 20m past the end
        Assert.That(result[4].Northing, Is.EqualTo(110).Within(1e-6));
        Assert.That(result[5].Northing, Is.EqualTo(120).Within(1e-6));
    }

    [Test]
    public void AddStartPoints_OnlyPrepends_ptsToAddCount()
    {
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0),
        };

        // Single-sided semantics: ptsToAdd=3 adds 3 points
        var result = CurveProcessing.AddStartPoints(input, 3, 10);

        Assert.That(result.Count, Is.EqualTo(input.Count + 3));
        Assert.That(result[0].Northing, Is.EqualTo(-30).Within(1e-6));
        Assert.That(result[1].Northing, Is.EqualTo(-20).Within(1e-6));
        Assert.That(result[2].Northing, Is.EqualTo(-10).Within(1e-6));
        Assert.That(result[3].Northing, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[4].Northing, Is.EqualTo(100).Within(1e-6));
    }

    [Test]
    public void AddEndPoints_OnlyAppends_ptsToAddCount()
    {
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0),
        };

        var result = CurveProcessing.AddEndPoints(input, 3, 10);

        Assert.That(result.Count, Is.EqualTo(input.Count + 3));
        Assert.That(result[0].Northing, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[1].Northing, Is.EqualTo(100).Within(1e-6));
        Assert.That(result[2].Northing, Is.EqualTo(110).Within(1e-6));
        Assert.That(result[3].Northing, Is.EqualTo(120).Within(1e-6));
        Assert.That(result[4].Northing, Is.EqualTo(130).Within(1e-6));
    }

    [Test]
    public void AddStartEndPoints_EastwardLine_ProjectsAlongEasting()
    {
        // Heading π/2 = east → +Easting direction
        var input = new List<Vec3>
        {
            new Vec3(0, 0, Math.PI / 2),
            new Vec3(100, 0, Math.PI / 2),
        };

        var result = CurveProcessing.AddStartEndPoints(input, 3, 10);

        // Prepend goes -Easting, append goes +Easting
        Assert.That(result[0].Easting, Is.EqualTo(-20).Within(1e-6));
        Assert.That(result[1].Easting, Is.EqualTo(-10).Within(1e-6));
        Assert.That(result[4].Easting, Is.EqualTo(110).Within(1e-6));
        Assert.That(result[5].Easting, Is.EqualTo(120).Within(1e-6));
    }

    // ──────────────────────────────────────────────────────────────────
    // ReducePointsByAngle
    // ──────────────────────────────────────────────────────────────────

    [Test]
    public void ReducePointsByAngle_FewerThanSixPoints_ReturnsCopy()
    {
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(0, 1, 0),
        };
        var result = CurveProcessing.ReducePointsByAngle(input);
        Assert.That(result.Count, Is.EqualTo(input.Count));
    }

    [Test]
    public void ReducePointsByAngle_StraightLine_KeepsBoundaryPoints()
    {
        // 10 collinear points heading north. Endpoints (first 2, last 2)
        // are always preserved; interior points may be dropped because
        // straight-line headings produce zero accumulated delta and
        // close-spaced points won't reach the spread threshold.
        var input = new List<Vec3>();
        for (int i = 0; i < 10; i++)
            input.Add(new Vec3(0, i * 0.1, 0)); // 10cm spacing → well under spread²*0.95 (~3.8 m²)

        var result = CurveProcessing.ReducePointsByAngle(input);

        // Always-keep boundary slots (4 of them) are guaranteed
        Assert.That(result.Count, Is.GreaterThanOrEqualTo(4));
        // First two preserved
        Assert.That(result[0].Northing, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[1].Northing, Is.EqualTo(0.1).Within(1e-6));
        // Last two preserved
        Assert.That(result[result.Count - 2].Northing, Is.EqualTo(0.8).Within(1e-6));
        Assert.That(result[result.Count - 1].Northing, Is.EqualTo(0.9).Within(1e-6));
    }

    [Test]
    public void ReducePointsByAngle_CurvyTrack_ReducesPointCount()
    {
        // A track that wiggles enough to trigger the spread/distance threshold
        // but not so much that every point is forced kept.
        var input = new List<Vec3>();
        for (int i = 0; i < 100; i++)
        {
            double n = i * 0.05; // 5cm spacing — most points fail spread threshold
            input.Add(new Vec3(0, n, 0));
        }
        var result = CurveProcessing.ReducePointsByAngle(input, angleDelta: 0.005, spread: 2);

        // Should reduce well below 100, but always keep at least the 4 boundary
        Assert.That(result.Count, Is.LessThan(input.Count));
        Assert.That(result.Count, Is.GreaterThanOrEqualTo(4));
    }

    // ──────────────────────────────────────────────────────────────────
    // ChaikinsSmooth
    // ──────────────────────────────────────────────────────────────────

    [Test]
    public void ChaikinsSmooth_SinglePoint_ReturnsCopy()
    {
        var input = new List<Vec3> { new Vec3(0, 0, 0) };
        var result = CurveProcessing.ChaikinsSmooth(input, 2);
        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void ChaikinsSmooth_ZeroIterations_ReturnsCopy()
    {
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0),
            new Vec3(20, 5, 0),
        };
        var result = CurveProcessing.ChaikinsSmooth(input, 0);
        Assert.That(result.Count, Is.EqualTo(3));
    }

    [Test]
    public void ChaikinsSmooth_PreserveEndpointsTrue_FirstAndLastUnchanged()
    {
        // Sharp corner at (10, 0)
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0),
            new Vec3(10, 10, 0),
        };
        var result = CurveProcessing.ChaikinsSmooth(input, 2, preserveEndPoints: true);

        // Endpoints are byte-identical
        Assert.That(result[0].Easting, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[0].Northing, Is.EqualTo(0).Within(1e-6));
        Assert.That(result[result.Count - 1].Easting, Is.EqualTo(10).Within(1e-6));
        Assert.That(result[result.Count - 1].Northing, Is.EqualTo(10).Within(1e-6));

        // Interior point at (10,0) gets rounded — no point should be exactly at the corner now
        bool foundCorner = false;
        for (int i = 1; i < result.Count - 1; i++)
        {
            if (Math.Abs(result[i].Easting - 10) < 1e-6 && Math.Abs(result[i].Northing) < 1e-6)
            {
                foundCorner = true;
                break;
            }
        }
        Assert.That(foundCorner, Is.False, "Sharp corner should have been smoothed away");
    }

    [Test]
    public void ChaikinsSmooth_DoublesSegmentCountPerIteration()
    {
        // Open polyline of 4 points, 1 iteration with preserveEndPoints=true.
        // Original has 3 segments. After one iteration with endpoint preservation:
        //   start + 2 cuts per segment + end = 1 + 2*3 + 1 = 8 points.
        var input = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0),
            new Vec3(10, 10, 0),
            new Vec3(20, 10, 0),
        };
        var result = CurveProcessing.ChaikinsSmooth(input, 1, preserveEndPoints: true);

        Assert.That(result.Count, Is.EqualTo(8));
    }
}
