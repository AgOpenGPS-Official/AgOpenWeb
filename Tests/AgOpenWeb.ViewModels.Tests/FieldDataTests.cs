using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#321: the field data card's trip distance and boundary area.</summary>
[TestFixture]
public class FieldDataTests
{
    [Test]
    public void TripDistance_AddsUpTheTravelBetweenFixes()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.AccumulateTripDistance(0, 0);          // the first fix is only a starting point
        vm.AccumulateTripDistance(0, 3);
        vm.AccumulateTripDistance(4, 3);

        Assert.That(vm.TripDistanceMeters, Is.EqualTo(7).Within(1e-9));
    }

    [Test]
    public void TripDistance_ParkedNoise_IsNotCounted()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.AccumulateTripDistance(0, 0);

        for (int i = 0; i < 1000; i++)
            vm.AccumulateTripDistance(i % 2 == 0 ? 0.03 : -0.03, 0);

        Assert.That(vm.TripDistanceMeters, Is.Zero, "steps under 0.1 m from the last counted point");
    }

    [Test]
    public void TripDistance_SlowTravel_IsCountedOnceItAddsUp()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.AccumulateTripDistance(0, 0);

        for (int i = 1; i <= 100; i++) vm.AccumulateTripDistance(0, i * 0.03); // 3 cm per fix

        Assert.That(vm.TripDistanceMeters, Is.EqualTo(3.0).Within(0.1));
    }

    [Test]
    public void TripDistance_AJumpIsNotTravel()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.AccumulateTripDistance(0, 0);
        vm.AccumulateTripDistance(0, 5);

        vm.AccumulateTripDistance(2000, 5);       // the simulator was moved
        vm.AccumulateTripDistance(2000, 8);

        Assert.That(vm.TripDistanceMeters, Is.EqualTo(8).Within(1e-9), "5 m before the jump, 3 m after");
    }

    [Test]
    public void ResetTrip_ZeroesDistanceAndTripArea()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.AccumulateTripDistance(0, 0);
        vm.AccumulateTripDistance(0, 10);

        vm.ResetTrip();
        vm.AccumulateTripDistance(0, 500);        // first fix after the reset: a starting point

        Assert.That(vm.TripDistanceMeters, Is.Zero);
        b.CoverageMapService.Received(1).ResetUserArea();
    }

    [Test]
    public void OpeningAField_StartsTheTripDistanceAgain()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.AccumulateTripDistance(0, 0);
        vm.AccumulateTripDistance(0, 10);

        vm.IsFieldOpen = true;

        Assert.That(vm.TripDistanceMeters, Is.Zero);
    }

    [Test]
    public void BoundaryArea_IsOuterLessInner_AndZeroWithNoBoundary()
    {
        var vm = new MainViewModelBuilder().Build();
        Assert.That(vm.FieldBoundaryAreaSqM, Is.Zero);

        var boundary = new Boundary { OuterBoundary = Square(0, 0, 100) };
        boundary.InnerBoundaries.Add(Square(40, 40, 20));
        vm.State.Field.CurrentBoundary = boundary;

        Assert.That(vm.FieldBoundaryAreaSqM, Is.EqualTo(100 * 100 - 20 * 20).Within(1e-6));
    }

    private static BoundaryPolygon Square(double e, double n, double side)
    {
        var p = new BoundaryPolygon();
        p.Points.Add(new BoundaryPoint(e, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n + side, 0));
        p.Points.Add(new BoundaryPoint(e, n + side, 0));
        return p;
    }
}
