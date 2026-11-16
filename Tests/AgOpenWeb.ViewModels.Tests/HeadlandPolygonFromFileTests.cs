// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A field whose file carries its headland twice, as a headland line and as a polygon kept with the
/// boundary (an import, a field from a test rig), and no headland segments: the Field Builder lists
/// no headland. Delete All took the line off the map, but the polygon stayed in the boundary, and
/// the next change of the boundary (the outer boundary made hard) put it back as the headland and
/// wrote it to the file again (seen on a tester's fields). A headland made in the Field
/// Builder was replaced by the old polygon the same way. The headland the operator makes, or
/// removes, is the field's headland now: the polygon goes.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class HeadlandPolygonFromFileTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;

    private static BoundaryPolygon Square(double lo, double hi)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-hlpoly-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        // A 200 m square with its headland 20 m in, as a polygon with the boundary and as a headland line.
        var headland = Square(20, 180);
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 },
            Boundary = new Boundary { OuterBoundary = Square(0, 200), HeadlandPolygon = headland },
        }, tracks: null);
        var line = headland.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        line.Add(line[0]);
        GeoJsonFieldService.SaveHeadlandLine(_dir, new HeadlandLine
        {
            Tracks = { new HeadlandPath { Name = "Headland", TrackPoints = line, MoveDistance = 20 } },
        });
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private async Task<MainViewModel> OpenAsync()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        return vm;
    }

    private int HeadlandPolygonsInFile()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "field.geojson")));
        return doc.RootElement.GetProperty("features").EnumerateArray().Count(f =>
            f.TryGetProperty("properties", out var p) && p.TryGetProperty("role", out var r) && r.GetString() == "headland");
    }

    /// <summary>The outer boundary's Hard tapped in the boundary list, as the tester did after Delete All.</summary>
    private static void ToggleTheOuterBoundaryHard(MainViewModel vm, bool hard)
    {
        vm.RefreshBoundaryList();
        vm.SelectedBoundaryIndex = 0;
        Assume.That(vm.BoundaryItems[0].BoundaryType, Is.EqualTo("Outer"));
        vm.ToggleHardCommand!.Execute(null);
        Assume.That(vm.BoundaryItems[0].IsHard, Is.EqualTo(hard));
    }

    private static double West(IReadOnlyList<Vec3> line) => line.Min(p => p.Easting);

    [Test]
    public async Task Delete_all_removes_a_headland_the_file_also_carried_as_a_polygon()
    {
        var vm = await OpenAsync();
        Assume.That(vm.HasHeadland, Is.True, "the field opens with its headland");
        Assume.That(vm.HeadlandSegments, Is.Empty, "and nothing in the Field Builder's list");
        Assume.That(HeadlandPolygonsInFile(), Is.EqualTo(1));

        vm.RemoteDeleteAllHeadland();
        ToggleTheOuterBoundaryHard(vm, hard: true);

        Assert.Multiple(() =>
        {
            Assert.That(vm.HasHeadland, Is.False, "a change of the boundary doesn't bring it back");
            Assert.That(vm.State.Field.HeadlandLine, Is.Null);
            Assert.That(vm.CurrentBoundary!.HeadlandPolygon, Is.Null);
            Assert.That(HeadlandPolygonsInFile(), Is.EqualTo(0), "gone from the field's file");
        });

        var reopened = await OpenAsync();
        ToggleTheOuterBoundaryHard(reopened, hard: false);
        Assert.That(reopened.HasHeadland, Is.False, "nor does opening the field again and changing the boundary");
    }

    [Test]
    public async Task A_headland_made_in_the_field_builder_replaces_the_polygon_for_good()
    {
        var vm = await OpenAsync();
        Assume.That(West(vm.State.Field.HeadlandLine!), Is.EqualTo(20).Within(0.01));

        vm.RemoteCreateHeadlandWholeBoundary(10);
        Assume.That(West(vm.State.Field.HeadlandLine!), Is.EqualTo(10).Within(0.5), "the 10 m headland made now");
        ToggleTheOuterBoundaryHard(vm, hard: true);

        Assert.Multiple(() =>
        {
            Assert.That(West(vm.State.Field.HeadlandLine!), Is.EqualTo(10).Within(0.5), "the boundary made hard keeps the headland made");
            Assert.That(vm.CurrentBoundary!.HeadlandPolygon, Is.Null);
            Assert.That(HeadlandPolygonsInFile(), Is.EqualTo(0));
        });

        var reopened = await OpenAsync();
        Assert.That(West(reopened.State.Field.HeadlandLine!), Is.EqualTo(10).Within(0.5), "and the field opens with it");
    }
}
