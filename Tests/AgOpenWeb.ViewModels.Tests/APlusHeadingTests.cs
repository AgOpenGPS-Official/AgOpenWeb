// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A+ line at a typed heading (#335, AgOpenGPS Quick AB nudHeading): point A at the
/// vehicle, B along the given heading; without one, the vehicle's heading as before.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class APlusHeadingTests
{
    private static MainViewModel AtPosition(double heading)
    {
        var vm = new MainViewModelBuilder().Build();
        vm.Easting = 500;
        vm.Northing = 1000;
        vm.Heading = heading;
        return vm;
    }

    private static double LineHeadingDeg(MainViewModel vm)
    {
        var p = vm.SelectedTrack!.Points;
        double h = Math.Atan2(p[1].Easting - p[0].Easting, p[1].Northing - p[0].Northing) * 180 / Math.PI;
        return (h + 360) % 360;
    }

    [Test]
    public void TypedHeading_IsUsed_FromTheVehicle()
    {
        var vm = AtPosition(heading: 10);

        vm.CreateAPlusLine(87.5);

        Assert.That(LineHeadingDeg(vm), Is.EqualTo(87.5).Within(1e-9));
        Assert.That(vm.SelectedTrack!.Points[0].Easting, Is.EqualTo(500));
        Assert.That(vm.SelectedTrack!.Points[0].Northing, Is.EqualTo(1000));
    }

    [Test]
    public void NoHeading_UsesTheVehicles()
    {
        var vm = AtPosition(heading: 213.4);

        vm.StartAPlusLineCommand!.Execute(null);

        Assert.That(LineHeadingDeg(vm), Is.EqualTo(213.4).Within(1e-9));
    }

    [TestCase(360.0, 0.0)]
    [TestCase(-90.0, 270.0)]
    [TestCase(450.0, 90.0)]
    public void Heading_IsWrappedTo0To360(double typed, double expected)
    {
        var vm = AtPosition(heading: 10);

        vm.CreateAPlusLine(typed);

        Assert.That(LineHeadingDeg(vm), Is.EqualTo(expected).Within(1e-9));
    }
}
