// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using AgOpenWeb.Models;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The boundary list's obstacle edits that need geometry, worked out on the host
/// (<see cref="ObstacleEditing"/>): which obstacle a tap on the map means, and Round all.
/// </summary>
[TestFixture]
public class ObstacleEditingTests
{
    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static Boundary Field()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(50, 50, 30));    // 0: a big patch
        b.InnerBoundaries.Add(Square(60, 60, 2));     // 1: a tree inside it
        b.InnerBoundaries.Add(Square(120, 120, 4));   // 2: a tree
        b.InnerBoundaries.Add(new BoundaryPolygon()); // 3: no points, not in the list
        return b;
    }

    [Test]
    public void ATapInside_PicksTheSmallestObstacleHoldingIt()
    {
        var b = Field();
        Assert.That(ObstacleEditing.ObstacleAt(b, 61, 61, reach: 5), Is.EqualTo(1), "the tree, not the patch round it");
        Assert.That(ObstacleEditing.ObstacleAt(b, 52, 52, reach: 5), Is.EqualTo(0));
    }

    [Test]
    public void ATapBeside_PicksTheNearestEdge_WithinReach()
    {
        var b = Field();
        Assert.That(ObstacleEditing.ObstacleAt(b, 122, 124.5, reach: 1), Is.EqualTo(2), "0.5 m off the tree");
        Assert.That(ObstacleEditing.ObstacleAt(b, 122, 126, reach: 1), Is.EqualTo(-1), "2 m off, out of reach");
        Assert.That(ObstacleEditing.ObstacleAt(b, 122, 126, reach: 3), Is.EqualTo(2), "zoomed out");
        Assert.That(ObstacleEditing.ObstacleAt(b, 100, 100, reach: 3), Is.EqualTo(-1), "nothing near");
    }

    [Test]
    public void InsideOne_BeatsTheEdgeOfANearerOne()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(10, 10, 1)); // a small tree whose edge is 0.2 m from the tap
        b.InnerBoundaries.Add(Square(11.2, 9, 5)); // the tap is inside this one
        Assert.That(ObstacleEditing.ObstacleAt(b, 11.3, 10.5, reach: 2), Is.EqualTo(1));
    }

    [Test]
    public void RoundAll_MakesTheTreesRound_AndKeepsTheRest()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(10, 10, 4));  // a tree: made round
        b.InnerBoundaries.Add(Square(30, 30, 20)); // too big for a tree: kept
        var thin = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (60.0, 60.0), (68.0, 60.0), (68.0, 60.5), (60.0, 60.5) })
            thin.Points.Add(new BoundaryPoint(e, n, 0));
        thin.UpdateBounds();
        b.InnerBoundaries.Add(thin);               // a row of stones: kept
        var tree = new BoundaryPolygon();
        tree.SetCircle(new ObstacleCircle(100, 100, 5));
        b.InnerBoundaries.Add(tree);               // a circle already: left alone

        var (made, kept) = ObstacleEditing.MakeObstaclesRound(b);

        Assert.That((made, kept), Is.EqualTo((1, 2)));
        Assert.That(b.InnerBoundaries.Select(i => i.Circle != null), Is.EqualTo(new[] { true, false, false, true }).AsCollection);
        Assert.That(b.InnerBoundaries[0].Circle!.Value.Diameter, Is.EqualTo(5.7).Within(1e-9), "4 m square: 5.66 m, up to 10 cm");
    }
}
