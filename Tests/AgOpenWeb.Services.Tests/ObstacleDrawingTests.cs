// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>The corners of an obstacle drawn by taps on the map ("+ Polygon"), and its part in the field.</summary>
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

    private static readonly (double E, double N)[] Field = { (0, 0), (200, 0), (200, 200), (0, 200) };

    // An L: the north-east quarter of a 100 m square is not field.
    private static readonly (double E, double N)[] LField = { (0, 0), (100, 0), (100, 50), (50, 50), (50, 100), (0, 100) };

    [Test]
    public void AnOutlineWhollyInside_IsKeptAsDrawn()
    {
        var drawn = new List<(double E, double N)> { (20.0, 160.0), (40.0, 160.0), (40.0, 180.0), (20.0, 180.0) };

        var part = ObstacleDrawing.InField(drawn, Field);

        Assert.That(part.Pieces, Has.Count.EqualTo(1));
        Assert.That(part.Pieces[0], Is.EqualTo(drawn), "the corners as tapped, in their order");
        Assert.That(part.AsDrawn, Is.True);
        Assert.That(part.Cut, Is.False);
    }

    [Test]
    public void AWetPatchDrawnFromBeyondTheFence_KeepsOnlyTheFieldPart()
    {
        // The end of the passes along the south fence, drawn round from 20 m outside it.
        var drawn = new[] { (150.0, -20.0), (180.0, -20.0), (180.0, 30.0), (150.0, 30.0) };

        var part = ObstacleDrawing.InField(drawn, Field);
        var pieces = part.Pieces;

        Assert.That(pieces, Has.Count.EqualTo(1));
        Assert.That(Area(pieces[0]), Is.EqualTo(30 * 30).Within(0.01), "30 m along the fence, 30 m into the field");
        Assert.That(part.Cut, Is.True);
        Assert.That(part.DrawnArea, Is.EqualTo(30 * 50).Within(0.01));
        Assert.That(pieces[0].Min(p => p.N), Is.EqualTo(0).Within(0.001), "up to the fence, not past it");
        Assert.That(pieces[0].Max(p => p.N), Is.EqualTo(30).Within(0.001));
    }

    [Test]
    public void AnOutlineWithItsCornersInside_IsCutWhereTheFenceReachesIntoIt()
    {
        // Every corner in the L, but the long side runs across the missing quarter.
        var drawn = new[] { (45.0, 45.0), (95.0, 45.0), (45.0, 95.0) };

        var pieces = ObstacleDrawing.InField(drawn, LField).Pieces;

        Assert.That(pieces, Has.Count.EqualTo(1));
        Assert.That(Area(pieces[0]), Is.EqualTo(1250 - 800).Within(0.01), "the triangle less its part in the missing quarter");
    }

    [Test]
    public void AnOutlineThatLeavesTheFieldAndComesBack_IsOnePiecePerPartInside_TheLargestFirst()
    {
        // A U drawn from the field out past the south fence and back in: its two legs are in the field.
        var drawn = new[]
        {
            (150.0, 10.0), (150.0, -20.0), (190.0, -20.0), (190.0, 20.0),
            (180.0, 20.0), (180.0, -10.0), (160.0, -10.0), (160.0, 10.0),
        };

        var part = ObstacleDrawing.InField(drawn, Field);

        Assert.That(part.Pieces.Select(Area), Is.EqualTo(new[] { 200.0, 100.0 }).Within(0.01).AsCollection);
        Assert.That(part.LeftOut, Is.Zero);
    }

    [Test]
    public void ASliverCaughtBesideTheFence_IsLeftOut()
    {
        // The same U, its west leg only 1.5 m into the field: 15 m², under a tenth of the 200 m² leg.
        var drawn = new[]
        {
            (150.0, 1.5), (150.0, -20.0), (190.0, -20.0), (190.0, 20.0),
            (180.0, 20.0), (180.0, -10.0), (160.0, -10.0), (160.0, 1.5),
        };

        var part = ObstacleDrawing.InField(drawn, Field);

        Assert.That(part.Pieces.Select(Area), Is.EqualTo(new[] { 200.0 }).Within(0.01).AsCollection);
        Assert.That(part.LeftOut, Is.EqualTo(1));
    }

    [Test]
    public void AnOutlineOutsideTheField_OrOnlyASliverInIt_HasNoPiece()
    {
        var outside = ObstacleDrawing.InField(new[] { (210.0, 100.0), (230.0, 100.0), (220.0, 120.0) }, Field);
        Assert.That(outside.Pieces, Is.Empty);
        Assert.That(outside.AreaInField, Is.Zero);

        var sliver = ObstacleDrawing.InField(new[] { (10.0, -20.0), (20.0, -20.0), (20.0, 0.05), (10.0, 0.05) }, Field);
        Assert.That(sliver.Pieces, Is.Empty, "0.5 m² in the field is under the least an obstacle has");
        Assert.That(sliver.AreaInField, Is.EqualTo(0.5).Within(0.01), "but something of it is in the field");
    }

    [Test]
    public void AFigureOfEightInside_IsItsTwoLoops_NotTwoHalvesThatCancelOut()
    {
        // Two 100 m² triangles meeting at (20, 20): the outline's signed area is 0.
        var drawn = new[] { (10.0, 10.0), (30.0, 30.0), (30.0, 10.0), (10.0, 30.0) };

        var part = ObstacleDrawing.InField(drawn, Field);

        Assert.That(part.DrawnArea, Is.EqualTo(200).Within(0.01));
        Assert.That(part.Pieces.Select(Area), Is.EqualTo(new[] { 100.0, 100.0 }).Within(0.01).AsCollection);
        Assert.That(part.AsDrawn, Is.False);
        Assert.That(part.Cut, Is.False, "nothing of it is outside the field");
    }

    [Test]
    public void AnOutlineTouchingTheFence_AtACornerOfTheField_IsKeptAsDrawn()
    {
        // A corner of the outline on the L's inner corner, the rest inside: nothing is cut off.
        var drawn = new[] { (30.0, 30.0), (50.0, 30.0), (50.0, 50.0), (30.0, 50.0) };

        var part = ObstacleDrawing.InField(drawn, LField);

        Assert.That(part.AsDrawn, Is.True);
        Assert.That(part.Pieces[0], Is.EqualTo(drawn));
    }

    private static double Area(IReadOnlyList<(double E, double N)> ring)
    {
        double a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            a += p.E * q.N - q.E * p.N;
        }
        return Math.Abs(a) / 2;
    }
}
