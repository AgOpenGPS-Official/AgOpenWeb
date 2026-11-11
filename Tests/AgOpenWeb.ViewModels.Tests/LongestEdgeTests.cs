// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Quick AB › Longest Edge runs along the field's longest side, between two corners, not along
/// the longest piece between two boundary points (the web client harness's
/// LONGEST-EDGE-NOT-LONGEST: a 49 m piece of a 244 m side was taken over a 420 m side drawn in
/// shorter pieces).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class LongestEdgeTests
{
    // A 420 m (east-west) by 244 m field: the long sides in 20 m pieces, the short sides in five
    // pieces of 48.8 m, so the longest single piece lies on a short side.
    private static List<BoundaryPoint> Field()
    {
        var pts = new List<BoundaryPoint>();
        void Side(double e0, double n0, double e1, double n1, int pieces)
        {
            for (int i = 0; i < pieces; i++)
                pts.Add(new BoundaryPoint(e0 + (e1 - e0) * i / pieces, n0 + (n1 - n0) * i / pieces, 0));
        }
        Side(0, 0, 420, 0, 21);
        Side(420, 0, 420, 244, 5);
        Side(420, 244, 0, 244, 21);
        Side(0, 244, 0, 0, 5);
        return pts;
    }

    [Test]
    public void Longest_Edge_runs_along_the_longest_side()
    {
        var vm = new MainViewModelBuilder().Build();
        var bnd = new Boundary { OuterBoundary = new BoundaryPolygon { Points = Field() } };
        bnd.OuterBoundary.UpdateBounds();
        vm.State.Field.CurrentBoundary = bnd;

        vm.CreateTrackFromBoundaryCommand!.Execute(null);

        var t = vm.SelectedTrack;
        Assert.That(t, Is.Not.Null);
        var a = t!.Points[0]; var b = t.Points[^1];
        double heading = (Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) * 180 / Math.PI + 360) % 180;
        Assert.That(heading, Is.EqualTo(90).Within(1), "along the 420 m east-west side, not a 244 m north-south one");
        double n = (a.Northing + b.Northing) / 2;
        Assert.That(Math.Min(Math.Abs(n), Math.Abs(n - 244)), Is.LessThan(1), "on the boundary's side");
    }
}
