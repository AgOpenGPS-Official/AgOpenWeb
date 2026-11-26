// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A typo in the headland distance (120 m for 12 m, 5000 m for 50 m) used to build a
/// "headland" bigger than the field, outside it and crossing itself, without a word; NaN
/// went through too. A whole-boundary offset that leaves nothing of the field is refused
/// with the most the field takes, and the headland stays as it was.
/// </summary>
[TestFixture]
public class HeadlandOffsetLimitTests
{
    private MainViewModel _vm = null!;
    private string? _failure;

    [SetUp]
    public void SetUp()
    {
        _vm = new MainViewModelBuilder().Build();
        _vm.FailureReported += m => _failure = m;
        _failure = null;

        // A 200 × 200 m square: the most it takes is just under 100 m.
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (200.0, 0.0), (200.0, 200.0), (0.0, 200.0) })
            outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        _vm.State.Field.CurrentBoundary = new Boundary { OuterBoundary = outer };
    }

    private static double Area(IReadOnlyList<Vec3> ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].Easting * ring[i].Northing - ring[i].Easting * ring[j].Northing;
        return Math.Abs(sum) / 2;
    }

    [Test]
    public void A_5000m_whole_boundary_headland_is_refused_with_the_most_the_field_takes()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(5000);

        Assert.Multiple(() =>
        {
            Assert.That(_failure, Does.Contain("5000 m headland leaves nothing of this field"));
            Assert.That(_failure, Does.Match(@"about (9[5-9]|100) m"), _failure);
            Assert.That(_vm.HeadlandSegments, Is.Empty, "no segment added");
            Assert.That(_vm.HasHeadland, Is.False);
        });
    }

    [Test]
    public void A_120m_typo_for_12m_is_refused_too()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(120);

        Assert.That(_failure, Does.Contain("leaves nothing"));
        Assert.That(_vm.HeadlandSegments, Is.Empty);
    }

    [Test]
    public void A_40m_whole_boundary_headland_is_built()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(40);

        Assert.That(_failure, Is.Null);
        Assert.That(_vm.HeadlandSegments, Has.Count.EqualTo(1));
        Assert.That(Area(_vm.CurrentHeadlandLine!), Is.InRange(14_000, 14_500), "a 120 × 120 m ring");
    }

    [Test]
    public void Changing_the_distance_to_one_that_swallows_the_field_keeps_the_headland_as_it_was()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(40);
        var before = _vm.CurrentHeadlandLine!;

        _vm.RemoteSetHeadlandOffsetAt(0, 5000);

        Assert.Multiple(() =>
        {
            Assert.That(_failure, Does.Contain("leaves nothing"));
            Assert.That(_vm.HeadlandSegments[0].Offset, Is.EqualTo(40));
            Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(Area(before)).Within(1));
        });
    }

    [Test]
    public void A_distance_that_is_not_a_number_is_refused_everywhere()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(double.NaN);
        Assert.That(_failure, Does.Contain("must be a number"));
        Assert.That(_vm.HeadlandSegments, Is.Empty);

        _failure = null;
        _vm.RemoteCreateHeadlandFromMapPoints(false, double.PositiveInfinity, 0, 0, 200, 0);
        Assert.That(_failure, Does.Contain("must be a number"));
        Assert.That(_vm.HeadlandSegments, Is.Empty);

        _vm.RemoteCreateHeadlandWholeBoundary(40);
        _failure = null;
        _vm.RemoteSetHeadlandOffsetAt(0, double.NaN);
        Assert.That(_failure, Does.Contain("must be a number"));
        Assert.That(_vm.HeadlandSegments[0].Offset, Is.EqualTo(40));
    }

    [Test]
    public void Changing_the_distance_to_zero_says_so()
    {
        _vm.RemoteCreateHeadlandWholeBoundary(40);
        _vm.RemoteSetHeadlandOffsetAt(0, 0);

        Assert.That(_failure, Does.Contain("more than 0 m"));
        Assert.That(_vm.HeadlandSegments[0].Offset, Is.EqualTo(40));
    }
}
