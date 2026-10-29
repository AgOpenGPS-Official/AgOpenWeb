// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// "Bnd. Curve": tap A and B on the boundary, get a curve along the fence between them. A
/// boundary with few vertices (a rectangle drawn from its four corners) must still give one
/// side: taps land on the fence where they're nearest, not on the nearest corner.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryCurveSegmentTests
{
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.NumSections = 1;
        ConfigurationStore.Instance.Tool.SetSectionWidth(0, 600);
        _vm = new MainViewModelBuilder().Build();
        var bnd = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<BoundaryPoint>
                {
                    new(0, 0, 0), new(100, 0, 0), new(100, 200, 0), new(0, 200, 0),
                }
            }
        };
        bnd.OuterBoundary.UpdateBounds();
        _vm.State.Field.CurrentBoundary = bnd;
    }

    [Test]
    public void TwoTapsOnOneSide_GiveAStraightRunAlongIt()
    {
        _vm.RemoteCreateBoundaryCurveSegment(0, 50, 0, 150);

        var track = _vm.SelectedTrack;
        Assert.That(track, Is.Not.Null);
        Assert.That(track!.Name, Is.EqualTo("Boundary Curve"));
        Assert.That(track.NoPassOffset, Is.True);
        var inField = track.Points.Where(p => p.Northing is > 60 and < 140).ToList();
        Assert.That(inField, Is.Not.Empty);
        double e0 = inField[0].Easting;
        Assert.That(e0, Is.InRange(0.0, 10.0), "on the west side, at most the inset inside it");
        foreach (var p in inField) Assert.That(p.Easting, Is.EqualTo(e0).Within(1e-6));
    }

    [Test]
    public void TapsOnAdjacentSides_TurnTheCorner()
    {
        _vm.RemoteCreateBoundaryCurveSegment(0, 150, 50, 200);

        var track = _vm.SelectedTrack;
        Assert.That(track, Is.Not.Null);
        Assert.That(DistanceToLine(track!.Points, 0, 170), Is.LessThan(0.5), "runs up the west side");
        Assert.That(DistanceToLine(track.Points, 30, 200), Is.LessThan(0.5), "then along the north side");
    }

    [Test]
    public void ACorner_IsRoundedToTheUTurnRadius()
    {
        // #422 follow-up: the curve rounds the corner as tightly as the rig can turn, not by a
        // share of the side lengths (which left big gaps on long sides).
        var vm = new MainViewModelBuilder { PolygonOffsetService = new AgOpenWeb.Services.Geometry.PolygonOffsetService() }.Build();
        vm.State.Field.CurrentBoundary = _vm.State.Field.CurrentBoundary;
        ConfigurationStore.Instance.Guidance.UTurnRadius = 8;
        double inset = ConfigurationStore.Instance.ActualToolWidth / 2
            + ConfigurationStore.Instance.Guidance.UTurnDistanceFromBoundary / 2;

        vm.RemoteCreateBoundaryCurveSegment(0, 150, 50, 200); // up the west side, round the NW corner

        var pts = vm.SelectedTrack!.Points;
        double cornerE = inset, cornerN = 200 - inset;
        double nearest = pts.Min(p => Math.Sqrt((p.Easting - cornerE) * (p.Easting - cornerE) + (p.Northing - cornerN) * (p.Northing - cornerN)));
        Assert.That(nearest, Is.EqualTo(8 * (Math.Sqrt(2) - 1)).Within(0.1));
    }

    private static double DistanceToLine(IReadOnlyList<AgOpenWeb.Models.Base.Vec3> pts, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 1; i < pts.Count; i++)
            best = Math.Min(best, AgOpenWeb.Models.Base.GeometryMath.PointToSegmentDistance(
                new AgOpenWeb.Models.Base.Vec2(e, n),
                new AgOpenWeb.Models.Base.Vec2(pts[i - 1].Easting, pts[i - 1].Northing),
                new AgOpenWeb.Models.Base.Vec2(pts[i].Easting, pts[i].Northing)));
        return best;
    }

    [Test]
    public void TheLongWayRound_WhenItsPicked()
    {
        // #450: A and B on the west side; the operator picks the way round past the east side.
        _vm.RemoteCreateBoundaryCurveSegment(0, 50, 0, 150, new AgOpenWeb.Models.Base.Vec2(100, 100));

        var pts = _vm.SelectedTrack!.Points;
        Assert.That(DistanceToLine(pts, 100, 100), Is.LessThan(10), "runs down the east side");
        Assert.That(DistanceToLine(pts, 50, 0), Is.LessThan(10), "and along the south side");
        // Both ends face each other across the gap between the taps: they run on towards each
        // other, but stop short of halfway instead of overlapping.
        Assert.That(pts[0].Easting, Is.EqualTo(pts[^1].Easting).Within(1e-6), "both ends on the west side");
        Assert.That(pts[0].Northing, Is.LessThan(pts[^1].Northing - 1.5), "the ends stop apart, not overlapping");
    }

    [Test]
    public void TheShortWayRound_WhenItsPicked()
    {
        _vm.RemoteCreateBoundaryCurveSegment(0, 50, 0, 150, new AgOpenWeb.Models.Base.Vec2(0, 100));

        Assert.That(DistanceToLine(_vm.SelectedTrack!.Points, 0, 100), Is.LessThan(10));
        Assert.That(DistanceToLine(_vm.SelectedTrack.Points, 100, 100), Is.GreaterThan(20));
    }

    [Test]
    public void ATapOnACorner_RunsOnTheWayTheCurveGoes()
    {
        // B lands on the NE corner, a boundary vertex, at the end of a walk against the ring's
        // order (the short way from the north side): the curve ends heading east, so it runs
        // on east, not due north from a repeated end point.
        _vm.RemoteCreateBoundaryCurveSegment(50, 200, 100, 200);

        var pts = _vm.SelectedTrack!.Points;
        Assert.That(pts[^1].Easting, Is.GreaterThan(105));
        Assert.That(pts[^1].Northing, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public void SamePoint_IsRefused()
    {
        _vm.RemoteCreateBoundaryCurveSegment(0, 50, 0, 50.5);
        Assert.That(_vm.SavedTracks, Is.Empty);
    }
}
