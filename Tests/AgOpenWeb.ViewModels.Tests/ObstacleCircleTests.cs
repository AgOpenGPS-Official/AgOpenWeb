// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Round obstacles (trees) from the boundary list: a circle placed at a tapped centre, a drawn
/// obstacle made round, every one up to a tree's size at once, a circle's diameter and centre changed.
/// The circle is saved in the field's file beside its polygon and lost when the polygon is
/// changed in another program.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ObstacleCircleTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;

    private static BoundaryPolygon Square(double e0, double n0, double side, ObstacleAction at = ObstacleAction.Turn)
    {
        var p = new BoundaryPolygon { AtObstacle = at };
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-circles-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(50, 50, 30)); // a big patch, U-turn
        b.InnerBoundaries.Add(Square(120, 120, 4, ObstacleAction.DriveThrough)); // a tree drawn as a square
        b.InnerBoundaries.Add(Square(150, 40, 3, ObstacleAction.DriveThrough)); // another
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 39.42, Longitude = -7.53 }, Boundary = b,
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

    private async Task<MainViewModel> Open()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        return vm;
    }

    [Test]
    public async Task ACircle_PlacedOnTheMap_IsAHardDriveThroughObstacle_SavedAndSelected()
    {
        var vm = await Open();

        vm.RemoteAddObstacleCircle(80, 150, 7.5);

        var inner = OnDisk(_dir).InnerBoundaries;
        Assert.That(inner, Has.Count.EqualTo(4));
        var c = inner[3].Circle;
        Assert.That(c, Is.Not.Null, "kept as a circle after a reload");
        Assert.That(c!.Value.CenterEasting, Is.EqualTo(80).Within(0.005));
        Assert.That(c.Value.CenterNorthing, Is.EqualTo(150).Within(0.005));
        Assert.That(c.Value.Diameter, Is.EqualTo(7.5));
        Assert.That(inner[3].AtObstacle, Is.EqualTo(ObstacleAction.GoRound), "a tree is gone round");
        Assert.That(inner[3].IsHard, Is.True, "a new obstacle is Hard");
        Assert.That(inner[3].IsPointInside(80 + 3.7, 150), Is.True, "the polygon holds the circle");
        Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries, Has.Count.EqualTo(4), "in the live boundary");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(4), "its row is selected");
        Assert.That(vm.BoundaryItems[4].CircleDiameter, Is.EqualTo(7.5));
        Assert.That(vm.BoundaryItems[4].SizeMeters, Is.EqualTo(7.5), "a circle's size is its diameter");
        Assert.That(vm.BoundaryItems[2].CircleDiameter, Is.Zero, "a drawn obstacle is not a circle");
    }

    [Test]
    public async Task ACircle_OutsideTheField_OrOfNoSize_IsRefused()
    {
        var vm = await Open();

        vm.RemoteAddObstacleCircle(250, 100, 6);
        Assert.That(vm.StatusMessage, Does.Contain("inside the field"));
        vm.RemoteAddObstacleCircle(100, 100, 0.2);
        Assert.That(vm.StatusMessage, Does.Contain("across"));

        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ADrawnObstacle_MadeRound_IsTheSmallestCircleThatHoldsIt()
    {
        var vm = await Open();

        vm.RemoteMakeObstacleRound(2); // Inner 2, the 4 m square

        var c = OnDisk(_dir).InnerBoundaries[1].Circle;
        Assert.That(c, Is.Not.Null);
        Assert.That(c!.Value.CenterEasting, Is.EqualTo(122).Within(0.005));
        Assert.That(c.Value.CenterNorthing, Is.EqualTo(122).Within(0.005));
        Assert.That(c.Value.Diameter, Is.EqualTo(5.7), "4 x sqrt 2, rounded up to 10 cm");
        Assert.That(OnDisk(_dir).InnerBoundaries[1].AtObstacle, Is.EqualTo(ObstacleAction.DriveThrough), "the choice is kept");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(2), "the row stays selected");
    }

    [Test]
    public async Task RoundAll_MakesEveryTreeRound_AndLeavesTheOthers()
    {
        // A strip of stones 20 m by 1 m (its circle 15 times its area), and a ditch 60 m by 20 m:
        // a 63 m circle, only 2.6 times its area, but no tree is that big.
        var fs = new FieldService();
        var field = fs.LoadField(_dir);
        var strip = new BoundaryPolygon { AtObstacle = ObstacleAction.DriveThrough };
        foreach (var (e, n) in new[] { (20.0, 170.0), (40.0, 170.0), (40.0, 171.0), (20.0, 171.0) })
            strip.Points.Add(new BoundaryPoint(e, n, 0));
        strip.UpdateBounds();
        field.Boundary!.InnerBoundaries.Add(strip);
        var ditch = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (100.0, 10.0), (160.0, 10.0), (160.0, 30.0), (100.0, 30.0) })
            ditch.Points.Add(new BoundaryPoint(e, n, 0));
        ditch.UpdateBounds();
        field.Boundary!.InnerBoundaries.Add(ditch);
        fs.SaveField(field);
        var vm = await Open();

        vm.RemoteMakeObstaclesRound();

        var inner = OnDisk(_dir).InnerBoundaries;
        Assert.That(inner.Select(i => i.Circle.HasValue), Is.EqualTo(new[] { false, true, true, false, false }).AsCollection);
        Assert.That(inner[0].Points, Has.Count.EqualTo(4), "the 30 m patch keeps its shape");
        Assert.That(inner[3].Points, Has.Count.EqualTo(4), "the long, thin strip keeps its shape");
        Assert.That(inner[4].Points, Has.Count.EqualTo(4), "the ditch keeps its shape");
        Assert.That(vm.StatusMessage, Does.Contain("2 obstacles made round").And.Contain("3 long and thin or too big"));
    }

    [Test]
    public async Task ACircle_GetsANewDiameter_AndANewCentre()
    {
        var vm = await Open();
        vm.RemoteAddObstacleCircle(80, 150, 6);

        vm.RemoteSetObstacleCircleDiameter(4, 9);
        var c = OnDisk(_dir).InnerBoundaries[3].Circle!.Value;
        Assert.That(c.Diameter, Is.EqualTo(9));
        Assert.That(c.CenterEasting, Is.EqualTo(80).Within(0.005), "round the same centre");

        vm.RemoteMoveObstacleCircle(4, 90, 140);
        c = OnDisk(_dir).InnerBoundaries[3].Circle!.Value;
        Assert.That(c.CenterEasting, Is.EqualTo(90).Within(0.005));
        Assert.That(c.CenterNorthing, Is.EqualTo(140).Within(0.005));
        Assert.That(c.Diameter, Is.EqualTo(9), "the diameter is kept");
        Assert.That(OnDisk(_dir).InnerBoundaries[3].IsPointInside(80, 150), Is.False, "the polygon moved with it");
    }

    [Test]
    public async Task TheOuterRow_CannotBeMadeRound()
    {
        var vm = await Open();

        vm.RemoteMakeObstacleRound(0);
        vm.RemoteSetObstacleCircleDiameter(0, 5);

        Assert.That(OnDisk(_dir).OuterBoundary!.Points, Has.Count.EqualTo(4));
        Assert.That(vm.StatusMessage, Does.Contain("inner"));
    }

    [Test]
    public async Task TheFile_HoldsCentreAndDiameter_AndAPolygonEditedElsewhereLosesTheCircle()
    {
        var vm = await Open();
        vm.RemoteAddObstacleCircle(80, 150, 6);

        var path = Path.Combine(_dir, "field.geojson");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var tree = node["features"]!.AsArray()
            .Where(f => (string?)f!["properties"]!["role"] == "inner-boundary").Last()!;
        Assert.That((double)tree["properties"]!["circleDiameter"]!, Is.EqualTo(6));
        var centre = tree["properties"]!["circleCenter"]!.AsArray();
        Assert.That((double)centre[0]!, Is.EqualTo(-7.53).Within(0.01), "[lon, lat]");
        Assert.That((double)centre[1]!, Is.EqualTo(39.42).Within(0.01));
        Assert.That(tree["geometry"]!["coordinates"]![0]!.AsArray(), Has.Count.EqualTo(ObstacleCircle.Segments + 1), "the polygon is still written");

        // A GIS program moves one corner and keeps the properties.
        var corner = tree["geometry"]!["coordinates"]![0]![3]!.AsArray();
        corner[0] = (double)corner[0]! + 0.00002;
        File.WriteAllText(path, node.ToJsonString());

        var inner = OnDisk(_dir).InnerBoundaries[3];
        Assert.That(inner.Circle, Is.Null, "no longer the circle's polygon");
        Assert.That(inner.Points, Has.Count.EqualTo(ObstacleCircle.Segments), "its shape is kept");
    }

    [Test]
    public async Task AnObstacle_DrawnOnTheMap_IsAHardUTurnObstacle_SavedAndSelected()
    {
        var vm = await Open();

        // Five taps: a double tap on the second corner and the first corner tapped again to close.
        vm.RemoteAddObstacleFromMapPoints(new[] { (20.0, 160.0), (40.0, 160.0), (40.05, 160.05), (40.0, 180.0), (20.0, 180.0), (20.1, 160.0) });

        var inner = OnDisk(_dir).InnerBoundaries;
        Assert.That(inner, Has.Count.EqualTo(4));
        Assert.That(inner[3].Points, Has.Count.EqualTo(4), "a repeated corner and the closing tap are dropped");
        Assert.That(inner[3].AtObstacle, Is.EqualTo(ObstacleAction.Turn), "what is drawn by hand gets a U-turn");
        Assert.That(inner[3].IsHard, Is.True, "a new obstacle is Hard");
        Assert.That(inner[3].Circle, Is.Null);
        Assert.That(inner[3].IsPointInside(30, 170), Is.True);
        Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries, Has.Count.EqualTo(4), "in the live boundary");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(4), "its row is selected");
        Assert.That(vm.StatusMessage, Does.Contain("4 corners"));
    }

    [Test]
    public async Task AnObstacle_DrawnOutsideTheField_OrTooSmall_IsRefused()
    {
        var vm = await Open();

        vm.RemoteAddObstacleFromMapPoints(new[] { (190.0, 100.0), (210.0, 100.0), (200.0, 120.0) });
        Assert.That(vm.StatusMessage, Does.Contain("inside the field"));
        vm.RemoteAddObstacleFromMapPoints(new[] { (100.0, 100.0), (100.05, 100.05), (100.1, 100.0) });
        Assert.That(vm.StatusMessage, Does.Contain("three corners"));
        vm.RemoteAddObstacleFromMapPoints(new[] { (100.0, 100.0), (100.5, 100.0), (101.0, 100.0) });
        Assert.That(vm.StatusMessage, Does.Contain("no area"));

        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(3));
    }
}
