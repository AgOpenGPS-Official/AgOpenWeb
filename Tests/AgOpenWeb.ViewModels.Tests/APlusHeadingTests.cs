// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A+ line at a typed heading (#335, AgOpenGPS Quick AB nudHeading): point A at the
/// vehicle, B along the given heading; without one, the vehicle's heading as before.
/// And the coordinate lines (AgOpenGPS Build Tracks "Lat/Lon + Heading" and "Lat/Lon"),
/// converted through the field's local plane.
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
        // A is abreast of the vehicle (moved sideways to the first pass, #345): no
        // component along the heading
        var a = vm.SelectedTrack!.Points[0];
        double h = 87.5 * Math.PI / 180;
        Assert.That((a.Easting - 500) * Math.Sin(h) + (a.Northing - 1000) * Math.Cos(h), Is.EqualTo(0).Within(1e-9));
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

    private static readonly Wgs84 Origin = new(52.0, 5.0);

    private static MainViewModel WithField()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.State.Field.LocalPlane = new LocalPlane(Origin, new SharedFieldProperties());
        return vm;
    }

    [Test]
    public void LatLonHeading_StartsAtThePoint_AlongTheHeading()
    {
        var vm = WithField();
        var plane = vm.State.Field.LocalPlane!;
        var expectedA = plane.ConvertWgs84ToGeoCoord(new Wgs84(52.001, 5.002));

        vm.CreateLatLonHeadingLine(52.001, 5.002, 33.3);

        var a = vm.SelectedTrack!.Points[0];
        Assert.That(a.Easting, Is.EqualTo(expectedA.Easting).Within(1e-9));
        Assert.That(a.Northing, Is.EqualTo(expectedA.Northing).Within(1e-9));
        Assert.That(LineHeadingDeg(vm), Is.EqualTo(33.3).Within(1e-9));
        Assert.That(vm.SelectedTrack!.Name, Is.EqualTo("A+ 33.3°"));
    }

    [Test]
    public void LatLonAB_RunsFromAToB()
    {
        var vm = WithField();
        var plane = vm.State.Field.LocalPlane!;
        var a = plane.ConvertWgs84ToGeoCoord(new Wgs84(52.0, 5.0));
        var b = plane.ConvertWgs84ToGeoCoord(new Wgs84(52.001, 5.001));
        double expected = (Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) * 180 / Math.PI + 360) % 360;

        vm.CreateLatLonABLine(52.0, 5.0, 52.001, 5.001);

        Assert.That(vm.SelectedTrack!.Points[0].Easting, Is.EqualTo(a.Easting).Within(1e-9));
        Assert.That(vm.SelectedTrack!.Points[0].Northing, Is.EqualTo(a.Northing).Within(1e-9));
        Assert.That(LineHeadingDeg(vm), Is.EqualTo(expected).Within(1e-9));
        Assert.That(vm.SelectedTrack!.Name, Does.StartWith("AB "));
    }

    [Test]
    public void LatLonAB_PointsTooClose_NoTrack()
    {
        var vm = WithField();

        vm.CreateLatLonABLine(52.0, 5.0, 52.0000001, 5.0);

        Assert.That(vm.SavedTracks, Is.Empty);
    }

    [TestCase(91.0, 5.0)]
    [TestCase(52.0, -181.0)]
    [TestCase(double.NaN, 5.0)]
    public void LatLon_OutOfRange_NoTrack(double lat, double lon)
    {
        var vm = WithField();

        vm.CreateLatLonHeadingLine(lat, lon, 90);

        Assert.That(vm.SavedTracks, Is.Empty);
    }

    [Test]
    public void LatLon_NoField_NoTrack()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.CreateLatLonHeadingLine(52.0, 5.0, 90);

        Assert.That(vm.SavedTracks, Is.Empty);
    }
}
