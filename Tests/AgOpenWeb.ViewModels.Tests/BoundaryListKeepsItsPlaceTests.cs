// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The boundary panel's list while the operator works down it, tapping Hard (or Drive thru) row
/// after row. The web projector reads the list on the broadcaster thread, ten times a second;
/// the list was cleared, the field file read, and the rows added again, so a tick in between
/// sent the panel an empty list and its scroll went back to the top. Now a refresh swaps in a
/// whole new list, and the toggled row stays selected.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryListKeepsItsPlaceTests
{
    private const int Obstacles = 30;
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

    // A field with many obstacles: the list scrolls, and the field file takes a while to read.
    private static Boundary FieldWithManyObstacles()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 400) };
        for (int i = 0; i < Obstacles; i++)
            b.InnerBoundaries.Add(Square(20 + 12 * i, 50 + 8 * (i % 5), 4));
        return b;
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-bndlist-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = FieldWithManyObstacles(),
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

    private async Task<MainViewModel> OpenAsync()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        Assume.That(vm.BoundaryItems, Has.Count.EqualTo(1 + Obstacles));
        return vm;
    }

    [Test]
    public async Task TogglingHard_KeepsTheRowSelected()
    {
        var vm = await OpenAsync();
        vm.SelectedBoundaryIndex = 20; // as the row's Hard cell sends it (boundary.select, boundary.hard)
        bool was = vm.BoundaryItems[20].IsHard;

        vm.ToggleHardCommand!.Execute(null);

        Assert.That(vm.BoundaryItems[20].IsHard, Is.EqualTo(!was), "toggled");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(20), "the row stays selected");
    }

    [Test]
    public async Task TogglingDriveThrough_KeepsTheRowSelected()
    {
        var vm = await OpenAsync();
        vm.SelectedBoundaryIndex = 12;

        vm.ToggleDriveThroughCommand!.Execute(null);

        Assert.That(vm.BoundaryItems[12].IsDriveThrough, Is.True, "toggled");
        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(12), "the row stays selected");
    }

    [Test]
    public async Task AFieldOpenedAgain_StartsWithNothingSelected()
    {
        var vm = await OpenAsync();
        vm.SelectedBoundaryIndex = 5;

        vm.RefreshBoundaryList();

        Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(-1));
    }

    [Test]
    public async Task AListAlreadyRead_IsNotEmptiedByARefresh()
    {
        // What the projector holds while it encodes the frame.
        var vm = await OpenAsync();
        var held = vm.BoundaryItems;
        bool was = held[3].IsHard;
        vm.SelectedBoundaryIndex = 3;

        vm.ToggleHardCommand!.Execute(null);

        Assert.That(held, Has.Count.EqualTo(1 + Obstacles), "the list read before keeps every row");
        Assert.That(held[3].IsHard, Is.EqualTo(was), "and its values");
        Assert.That(vm.BoundaryItems[3].IsHard, Is.EqualTo(!was), "the next read has the change");
    }

    [Test]
    public async Task WhileHardIsTappedRowAfterRow_TheListIsNeverReadEmptyOrShort()
    {
        // The broadcaster's view: read the list over and over on another thread, as its 10 Hz
        // tick does, while the rows' Hard flags are toggled one after another.
        var vm = await OpenAsync();
        int shortest = int.MaxValue, reads = 0;
        Exception? failed = null;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int n = 0;
                    foreach (var _ in vm.BoundaryItems) n++;
                    shortest = Math.Min(shortest, n);
                    reads++;
                }
            }
            catch (Exception ex) { failed = ex; }
        });

        for (int row = 1; row <= Obstacles; row++)
        {
            vm.SelectedBoundaryIndex = row;
            vm.ToggleHardCommand!.Execute(null);
        }
        stop.Cancel();
        await reader;

        Assert.That(failed, Is.Null, "reading the list while it is refreshed");
        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(shortest, Is.EqualTo(1 + Obstacles), "every read had every row");
    }
}
