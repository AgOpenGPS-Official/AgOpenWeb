// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>The corners of an obstacle drawn by taps on the map ("+ Polygon").</summary>
[TestFixture]
public class ObstacleDrawingTests
{
    [Test]
    public void ADoubleTap_IsOneCorner()
    {
        var corners = ObstacleDrawing.Corners(new[] { (0.0, 0.0), (0.1, 0.05), (10.0, 0.0), (10.0, 10.0), (10.05, 10.1) });

        Assert.That(corners, Is.EqualTo(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) }));
    }

    [Test]
    public void ALastTapBackOnTheFirstCorner_ClosesTheOutline()
    {
        var corners = ObstacleDrawing.Corners(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.1, 0.1) });

        Assert.That(corners, Is.EqualTo(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) }));
    }

    [Test]
    public void CornersFurtherApartThanTheSpacing_AreAllKept()
    {
        var taps = new[] { (0.0, 0.0), (ObstacleDrawing.CornerMinSpacing, 0.0), (1.0, 1.0), (0.0, 1.0) };

        Assert.That(ObstacleDrawing.Corners(taps), Is.EqualTo(taps));
    }
}
