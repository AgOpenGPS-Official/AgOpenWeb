// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.YouTurn;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The snake order is planned again when the field changes (<see cref="YouTurnStateMachine.SnakeFieldStamp"/>).
/// The stamp read the point counts and four points of the headland line and of the outer boundary,
/// so a corner moved anywhere else, the count the same, kept the order planned for the old field.
/// </summary>
[TestFixture]
public class SnakeFieldStampTests
{
    // A 200 m square with a point every 25 m: 32 corners, of which 0, 8, 16 and 24 were read.
    private static List<(double E, double N)> Ring()
    {
        var ring = new List<(double, double)>();
        for (int i = 0; i < 8; i++) ring.Add((i * 25, 0));
        for (int i = 0; i < 8; i++) ring.Add((200, i * 25));
        for (int i = 0; i < 8; i++) ring.Add((200 - i * 25, 200));
        for (int i = 0; i < 8; i++) ring.Add((0, 200 - i * 25));
        return ring;
    }

    private static Boundary Field(List<(double E, double N)> ring)
    {
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in ring) outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        return new Boundary { OuterBoundary = outer };
    }

    private static List<Vec3> Line(List<(double E, double N)> ring) =>
        ring.Select(p => new Vec3(p.E * 0.9 + 10, p.N * 0.9 + 10, 0)).ToList();

    [Test]
    public void A_boundary_corner_moved_between_the_points_once_read_changes_the_stamp()
    {
        var ring = Ring();
        long before = YouTurnStateMachine.SnakeFieldStamp(Field(ring), Line(Ring()));

        ring[5] = (ring[5].E, ring[5].N - 6); // a notch pushed into the south edge, 125 m along
        long after = YouTurnStateMachine.SnakeFieldStamp(Field(ring), Line(Ring()));

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void A_headland_point_moved_between_the_points_once_read_changes_the_stamp()
    {
        var field = Field(Ring());
        var line = Line(Ring());
        long before = YouTurnStateMachine.SnakeFieldStamp(field, line);

        line[13] = new Vec3(line[13].Easting - 4, line[13].Northing, 0); // one edge's headland made wider
        long after = YouTurnStateMachine.SnakeFieldStamp(field, line);

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void The_same_field_gives_the_same_stamp()
    {
        Assert.That(YouTurnStateMachine.SnakeFieldStamp(Field(Ring()), Line(Ring())),
            Is.EqualTo(YouTurnStateMachine.SnakeFieldStamp(Field(Ring()), Line(Ring()))));
    }
}
