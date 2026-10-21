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
        Assert.That(track!.Points.Any(p => p.Easting < 10 && p.Northing is > 150 and < 185), Is.True, "runs up the west side");
        Assert.That(track.Points.Any(p => p.Northing > 190 && p.Easting is > 15 and < 50), Is.True, "then along the north side");
    }

    [Test]
    public void SamePoint_IsRefused()
    {
        _vm.RemoteCreateBoundaryCurveSegment(0, 50, 0, 50.5);
        Assert.That(_vm.SavedTracks, Is.Empty);
    }
}
