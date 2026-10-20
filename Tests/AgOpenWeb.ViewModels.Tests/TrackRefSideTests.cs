// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #345, AgOpenGPS ref side (FormQuickAB / FormBuildTracks isRefRightSide): a new line
/// (A+, Drive AB, recorded curve, drawn on the map) is the edge of the work, so the track is
/// saved (width - overlap) / 2 + offset to the chosen side, right by default. Lines from the
/// boundary edges go inward, whichever way the boundary runs.
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

    [Test]
    public void DrawnStraight_MovesToTheChosenSide()
    {
        _vm.TrackRefSideRight = false;
        _vm.StartDrawABModeCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(new Position { Easting = 500, Northing = 1000 });
        _vm.SetABPointCommand!.Execute(new Position { Easting = 500, Northing = 1100 });
        AssertAllEasting(497);
    }

    [Test]
    public void DrawnCurve_DefaultsToRight()
    {
        _vm.StartDrawCurveModeCommand!.Execute(null);
        for (int n = 1000; n <= 1100; n += 50)
            _vm.SetABPointCommand!.Execute(new Position { Easting = 500, Northing = n });
        _vm.FinishDrawCurveCommand!.Execute(null);
        AssertAllEasting(503);
    }

    // A 400 × 300 m field, corners densified the way recorded boundaries are.
    private void SetRectangleBoundary(bool clockwise)
    {
        var corners = new[] { (0.0, 0.0), (0.0, 300.0), (400.0, 300.0), (400.0, 0.0) }; // clockwise in E/N
        if (!clockwise) System.Array.Reverse(corners);
        var pts = new System.Collections.Generic.List<BoundaryPoint>();
        for (int i = 0; i < 4; i++)
        {
            var (e0, n0) = corners[i];
            var (e1, n1) = corners[(i + 1) % 4];
            for (int k = 0; k < 10; k++)
                pts.Add(new BoundaryPoint(e0 + (e1 - e0) * k / 10, n0 + (n1 - n0) * k / 10, 0));
        }
        var bnd = new Boundary { OuterBoundary = new BoundaryPolygon { Points = pts } };
        bnd.OuterBoundary.UpdateBounds();
        _vm.State.Field.CurrentBoundary = bnd;
    }

    // Each edge line lies 3 m inside its edge: 3 m from one side of the field, parallel to it.
    private static void AssertInsideByHalfWidth(AgOpenWeb.Models.Track.Track t)
    {
        var a = t.Points[0];
        var b = t.Points[^1];
        bool northSouth = System.Math.Abs(a.Easting - b.Easting) < 1e-6;
        double across = northSouth ? a.Easting : a.Northing;
        double far = northSouth ? 400 : 300;
        Assert.That(northSouth ? b.Easting : b.Northing, Is.EqualTo(across).Within(1e-6), t.Name + " parallel");
        Assert.That(across, Is.EqualTo(3).Within(1e-6).Or.EqualTo(far - 3).Within(1e-6), t.Name);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void LongestEdge_GoesInward(bool clockwise)
    {
        SetRectangleBoundary(clockwise);
        _vm.TrackRefSideRight = false; // the side choice doesn't apply: inward is the only way
        _vm.CreateTrackFromBoundaryCommand!.Execute(null);
        AssertInsideByHalfWidth(_vm.SelectedTrack!);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AllEdges_GoInward(bool clockwise)
    {
        SetRectangleBoundary(clockwise);
        _vm.CreateTracksFromAllEdgesCommand!.Execute(null);
        Assert.That(_vm.SavedTracks, Has.Count.EqualTo(4));
        foreach (var t in _vm.SavedTracks) AssertInsideByHalfWidth(t);
    }
}
