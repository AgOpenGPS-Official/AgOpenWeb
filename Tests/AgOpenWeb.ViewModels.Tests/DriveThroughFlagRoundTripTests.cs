// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Headland;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 5.5: an inner boundary's drive-through flag, round trip. In AgOpenGPS the boundary
/// list (FormBoundary) toggles it for an inner boundary only (the outer reads "--"), Boundary.txt
/// carries it as a True/False line before each ring (BoundaryFiles), and the headland builder
/// and the U-turn's turn lines leave a drive-through boundary out (CHead, CTurn). Here the flag
/// is toggled through the real command, read back from the field's file, shown in the list,
/// kept across a close and reopen, honoured by the headland builder, and taken over from an
/// AgOpenGPS field's Boundary.txt.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class DriveThroughFlagRoundTripTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;

    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static Boundary FieldWithTwoIslands()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(50, 50, 20));
        b.InnerBoundaries.Add(Square(120, 120, 10));
        return b;
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-drivethru-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = FieldWithTwoIslands(),
        }, tracks: null);
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private static Boundary OnDisk(string dir) => new FieldService().LoadField(dir).Boundary!;

    [Test]
    public async Task ToggledOnAnIsland_ItIsSaved_Shown_AndThereAfterAReopen()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        Assume.That(vm.BoundaryItems.Select(i => i.BoundaryType), Is.EqualTo(new[] { "Outer", "Inner 1", "Inner 2" }).AsCollection);
        Assume.That(vm.BoundaryItems.All(i => !i.IsDriveThrough));

        vm.SelectedBoundaryIndex = 1; // Inner 1, as the list's row sends it (boundary.select, boundary.driveThru)
        vm.ToggleDriveThroughCommand!.Execute(null);

        Assert.That(OnDisk(_dir).InnerBoundaries[0].IsDriveThrough, Is.True, "in the field's file");
        Assert.That(OnDisk(_dir).InnerBoundaries[1].IsDriveThrough, Is.False, "the other island untouched");
        Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries[0].IsDriveThrough, Is.True, "in the live boundary");
        Assert.That(vm.BoundaryItems[1].IsDriveThrough, Is.True, "shown in the list");

        await vm.CloseFieldAsync();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries[0].IsDriveThrough, Is.True, "after a reopen");
        Assert.That(vm.BoundaryItems[1].IsDriveThrough, Is.True);

        vm.SelectedBoundaryIndex = 1;
        vm.ToggleDriveThroughCommand.Execute(null);
        Assert.That(OnDisk(_dir).InnerBoundaries[0].IsDriveThrough, Is.False, "toggled back");
        Assert.That(vm.BoundaryItems[1].IsDriveThrough, Is.False);
    }

    [Test]
    public async Task TheOuterBoundary_CannotBeDriveThrough_AsAgOpenGps()
    {
        // FormBoundary: the outer row reads "--" and its flag is kept false.
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        vm.SelectedBoundaryIndex = 0;

        vm.ToggleDriveThroughCommand!.Execute(null);

        Assert.That(OnDisk(_dir).OuterBoundary!.IsDriveThrough, Is.False);
        Assert.That(vm.BoundaryItems[0].IsDriveThrough, Is.False);
        Assert.That(vm.StatusMessage, Does.Contain("outer"));
    }

    [Test]
    public void ADriveThroughIsland_GetsNoHeadlandBand_AsAgOpenGps()
    {
        // CHead / HeadlandBuilder: a drive-through inner boundary is skipped.
        var boundary = FieldWithTwoIslands();
        boundary.InnerBoundaries[0].IsDriveThrough = true;
        var result = new HeadlandBuilderService(new PolygonOffsetService())
            .BuildAllHeadlands(boundary, new HeadlandBuildOptions { Distance = 5, IncludeInnerBoundaries = true });
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.InnerHeadlandLines, Has.Count.EqualTo(1), "a band around the obstacle, none around the drive-through island");
        var band = result.InnerHeadlandLines![0];
        Assert.That(band.Average(p => p.Easting), Is.EqualTo(125).Within(1), "the band is the second island's (at 120..130)");
    }

    [Test]
    public void AnAgOpenGpsField_KeepsItsFlags_WhenImported()
    {
        // BoundaryFiles.Save: "$Boundary", then per ring its isDriveThru, its count, "e,n,h" lines.
        var dir = Path.Combine(_root, "Legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "Field.txt"), new[]
        {
            DateTime.Now.ToString("yyyy-MMMM-dd hh:mm:ss tt", CultureInfo.InvariantCulture), "$FieldDir", "Legacy", "$Offsets", "0,0",
            "Convergence", "0", "StartFix", "52,5",
        });
        var rings = new (bool isDriveThru, BoundaryPolygon ring)[] { (false, Square(0, 0, 200)), (true, Square(50, 50, 20)), (false, Square(120, 120, 10)) };
        var lines = new List<string> { "$Boundary" };
        foreach (var (isDriveThru, ring) in rings)
        {
            lines.Add(isDriveThru.ToString());
            lines.Add(ring.Points.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var p in ring.Points)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{p.Easting:F3},{p.Northing:F3},{0.0:F5}"));
        }
        File.WriteAllLines(Path.Combine(dir, "Boundary.txt"), lines);

        var imported = new FieldService().LoadField(dir).Boundary!;

        Assert.That(imported.InnerBoundaries, Has.Count.EqualTo(2));
        Assert.That(imported.InnerBoundaries[0].IsDriveThrough, Is.True, "the flag came over");
        Assert.That(imported.InnerBoundaries[1].IsDriveThrough, Is.False);
        Assert.That(imported.OuterBoundary!.IsDriveThrough, Is.False);
        Assert.That(File.Exists(Path.Combine(dir, "Boundary.txt")), Is.False, "imported once and deleted");
        Assert.That(OnDisk(dir).InnerBoundaries[0].IsDriveThrough, Is.True, "and in field.geojson from now on");
    }
}
