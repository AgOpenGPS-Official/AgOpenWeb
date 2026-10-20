// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #345, AgOpenGPS ref side (FormQuickAB / FormBuildTracks isRefRightSide): a driven line's
/// (A+, Drive AB, recorded curve) reference goes to the implement's edge, (width - overlap)
/// / 2 + offset to the chosen side, right by default. The reference line is the edge between
/// passes (PassGeometry), so a pass then runs where the tractor drove.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class TrackRefSideTests
{
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var store = ConfigurationStore.Instance;
        store.NumSections = 1;
        store.Tool.SetSectionWidth(0, 600); // 6 m tool → 3 m to the first pass
        store.Tool.Overlap = 0;
        store.Tool.Offset = 0;
        _vm = new MainViewModelBuilder().Build();
        _vm.Easting = 500;
        _vm.Northing = 1000;
        _vm.Heading = 0; // north: right is +E
    }

    private void DriveAB()
    {
        _vm.StartDriveABCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(null);
        _vm.Northing = 1100;
        _vm.SetABPointCommand!.Execute(null);
    }

    private void AssertAllEasting(double expected)
    {
        Assert.That(_vm.SelectedTrack, Is.Not.Null);
        foreach (var p in _vm.SelectedTrack!.Points)
            Assert.That(p.Easting, Is.EqualTo(expected).Within(1e-6));
    }

    [Test]
    public void DriveAB_DefaultsToRight()
    {
        DriveAB();
        AssertAllEasting(503);
    }

    [Test]
    public void DriveAB_Left()
    {
        _vm.TrackRefSideRight = false;
        DriveAB();
        AssertAllEasting(497);
    }

    [Test]
    public void DriveAB_AddsToolOffsetAndSubtractsOverlap()
    {
        ConfigurationStore.Instance.Tool.Offset = 0.5;  // tool sits 0.5 m right
        ConfigurationStore.Instance.Tool.Overlap = 0.2; // half = (6 - 0.2) / 2 = 2.9
        _vm.TrackRefSideRight = false;
        DriveAB();
        AssertAllEasting(500 - 2.9 + 0.5);
    }

    [Test]
    public void APlus_MovesAbreastOfTheVehicle()
    {
        _vm.CreateAPlusLine(90); // east: right is -N
        var a = _vm.SelectedTrack!.Points[0];
        Assert.That(a.Easting, Is.EqualTo(500).Within(1e-9));
        Assert.That(a.Northing, Is.EqualTo(997).Within(1e-9));
    }

    [Test]
    public void RecordedCurve_DefaultsToRight()
    {
        _vm.StartCurveRecordingCommand!.Execute(null);
        for (int n = 1003; n <= 1040; n += 3) _vm.AddCurvePoint(500, n, 0);
        _vm.FinishCurveRecordingCommand!.Execute(null);
        AssertAllEasting(503);
    }
}
