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
/// What the tractor does at each obstacle (<see cref="ObstacleAction"/>), chosen in the boundary
/// list one row at a time or for every obstacle from a column's title: saved in the field's
/// file as <c>atObstacle</c>, shown in the list with the obstacle's size, kept across a reopen,
/// and read from a file that has only AgOpenGPS's drive-through flag.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ObstacleChoiceTests
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

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-obstacles-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(50, 50, 30)); // a big patch
        b.InnerBoundaries.Add(Square(120, 120, 3)); // a tree
        b.InnerBoundaries.Add(Square(150, 40, 4)); // a tree
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = b,
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

    private static IEnumerable<string?> AtObstacleInFile(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "field.geojson")));
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = f.GetProperty("properties");
            if (props.GetProperty("role").GetString() != "inner-boundary") continue;
            yield return props.TryGetProperty("atObstacle", out var a) ? a.GetString() : null;
        }
    }

    [Test]
    public async Task ANewObstacle_IsAUTurn_AndTheListShowsItsSize()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();

        Assert.That(vm.BoundaryItems.Select(i => i.IsInner), Is.EqualTo(new[] { false, true, true, true }).AsCollection);
        Assert.That(vm.BoundaryItems.Skip(1).Select(i => i.AtObstacle), Is.All.EqualTo(ObstacleAction.Turn), "as AgOpenGPS: Drive thru = No");
        Assert.That(vm.BoundaryItems[1].SizeMeters, Is.EqualTo(30 * Math.Sqrt(2)).Within(1e-6), "corner to corner");
        Assert.That(vm.BoundaryItems[2].SizeMeters, Is.EqualTo(3 * Math.Sqrt(2)).Within(1e-6));
        Assert.That(AtObstacleInFile(_dir), Is.All.EqualTo("turn"));
    }

    [Test]
    public async Task OneRow_GetsItsChoice_IsSaved_AndStaysSelected()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();

        vm.RemoteSetObstacleAction(2, ObstacleAction.GoRound); // Inner 2, the first tree

        Assert.That(OnDisk(_dir).InnerBoundaries.Select(i => i.AtObstacle),
            Is.EqualTo(new[] { ObstacleAction.Turn, ObstacleAction.GoRound, ObstacleAction.Turn }).AsCollection);
        Assert.That(AtObstacleInFile(_dir), Is.EqualTo(new[] { "turn", "goRound", "turn" }).AsCollection);
        Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries[1].AtObstacle, Is.EqualTo(ObstacleAction.GoRound), "in the live boundary");
        Assert.That(vm.BoundaryItems[2].AtObstacle, Is.EqualTo(ObstacleAction.GoRound), "shown in the list");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(2), "the row stays selected");

        await vm.CloseFieldAsync();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        Assert.That(vm.BoundaryItems[2].AtObstacle, Is.EqualTo(ObstacleAction.GoRound), "after a reopen");
    }

    [Test]
    public async Task AColumnTitle_GivesEveryObstacleTheChoice_AndARowThenChangesOne()
    {
        // Renato's case: most of the obstacles are trees to go round, a few are big.
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();

        vm.RemoteSetAllObstacleActions(ObstacleAction.GoRound);
        Assert.That(OnDisk(_dir).InnerBoundaries.Select(i => i.AtObstacle), Is.All.EqualTo(ObstacleAction.GoRound));
        Assert.That(vm.StatusMessage, Does.Contain("3"));

        vm.RemoteSetObstacleAction(1, ObstacleAction.Turn); // the big patch
        Assert.That(OnDisk(_dir).InnerBoundaries.Select(i => i.AtObstacle),
            Is.EqualTo(new[] { ObstacleAction.Turn, ObstacleAction.GoRound, ObstacleAction.GoRound }).AsCollection);
    }

    [Test]
    public async Task TheOuterRow_HasNoChoice()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();

        vm.RemoteSetObstacleAction(0, ObstacleAction.DriveThrough);

        Assert.That(OnDisk(_dir).OuterBoundary!.AtObstacle, Is.EqualTo(ObstacleAction.Turn));
        Assert.That(OnDisk(_dir).InnerBoundaries.Select(i => i.AtObstacle), Is.All.EqualTo(ObstacleAction.Turn));
        Assert.That(vm.StatusMessage, Does.Contain("inner"));
    }

    [Test]
    public void DriveThrough_IsTheDriveThroughFlag_BothWays()
    {
        var p = Square(0, 0, 5);
        p.AtObstacle = ObstacleAction.GoRound;
        Assert.That(p.IsDriveThrough, Is.False);
        p.IsDriveThrough = false;
        Assert.That(p.AtObstacle, Is.EqualTo(ObstacleAction.GoRound), "clearing the flag keeps go around");
        p.IsDriveThrough = true;
        Assert.That(p.AtObstacle, Is.EqualTo(ObstacleAction.DriveThrough));
        p.IsDriveThrough = false;
        Assert.That(p.AtObstacle, Is.EqualTo(ObstacleAction.Turn), "an AgOpenGPS Drive thru = No is a U-turn");
    }

    [Test]
    public void AFileWithOnlyTheDriveThroughFlag_ReadsAsTheChoice()
    {
        // A field written before atObstacle, or by a tool that knows only isDriveThrough.
        var path = Path.Combine(_dir, "field.geojson");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        int k = 0;
        foreach (var f in node["features"]!.AsArray())
        {
            var props = f!["properties"]!.AsObject();
            if ((string?)props["role"] != "inner-boundary") continue;
            props.Remove("atObstacle");
            props["isDriveThrough"] = k++ == 0;
        }
        File.WriteAllText(path, node.ToJsonString());

        var inner = OnDisk(_dir).InnerBoundaries;

        Assert.That(inner.Select(i => i.AtObstacle),
            Is.EqualTo(new[] { ObstacleAction.DriveThrough, ObstacleAction.Turn, ObstacleAction.Turn }).AsCollection);
    }
}
