// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.YouTurn;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The U-turn skip button's mode and the row count are the field's own (UTurnSkip.json in the
/// field folder). They lived only in memory: closing the field and opening it again kept them,
/// but closing the app put them back to Normal, and the next field opened got the last one used.
/// The whole open and close run here, with the real field service on temporary field folders;
/// a second view model stands for the app started again.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class UTurnSkipKeptPerFieldTests
{
    private string _root = null!;
    private string _a = null!;
    private string _b = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-uturnskip-" + Guid.NewGuid().ToString("N"));
        _a = MakeField("A");
        _b = MakeField("B");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private string MakeField(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (-100.0, -100.0), (100.0, -100.0), (100.0, 100.0), (-100.0, 100.0) })
            outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        Services.GeoJson.GeoJsonFieldService.Save(new Field
        {
            Name = name, DirectoryPath = dir,
            Origin = new Position { Latitude = 52, Longitude = 5 },
            Boundary = new Boundary { OuterBoundary = outer },
        }, tracks: null);
        return dir;
    }

    private static MainViewModel NewApp() => new MainViewModelBuilder { FieldService = new FieldService() }.Build();

    private static (int mode, int rows) Skip(MainViewModel vm) => (vm.UTurnSkipMode, vm.UTurnSkipRows);

    [Test]
    public async Task The_choice_is_back_after_the_app_is_closed_without_closing_the_field()
    {
        var vm = NewApp();
        await vm.OpenFieldAsync(_a, "A");
        vm.CycleUTurnSkipRowsCommand!.Execute(null);
        vm.CycleUTurnSkipRowsCommand!.Execute(null);   // 2 rows
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // alternative
        Assume.That(Skip(vm), Is.EqualTo((1, 2)));

        // The tractor switched off: no close, no exit save. A new start opens the field.
        var again = NewApp();
        Assume.That(Skip(again), Is.EqualTo((0, 0)), "a new start has nothing in memory");
        await again.OpenFieldAsync(_a, "A");

        Assert.That(Skip(again), Is.EqualTo((1, 2)));
        Assert.That(again.IsSkipWorkedMode, Is.False);
        Assert.That(again.IsUTurnSkipRowsEnabled, Is.True);
    }

    [Test]
    public async Task Ignore_worked_tracks_comes_back_after_a_close_and_a_new_start()
    {
        var vm = NewApp();
        await vm.OpenFieldAsync(_a, "A");
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // ignore worked tracks, 1 row
        await vm.CloseFieldAsync();

        var again = NewApp();
        await again.OpenFieldAsync(_a, "A");

        Assert.That(Skip(again), Is.EqualTo((2, 1)));
        Assert.That(again.IsSkipWorkedMode, Is.True);
    }

    [Test]
    public async Task Each_field_keeps_its_own_choice()
    {
        var vm = NewApp();
        await vm.OpenFieldAsync(_a, "A");
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // A: alternative, 1 row

        await vm.OpenFieldAsync(_b, "B");
        Assert.That(Skip(vm), Is.EqualTo((0, 0)), "a field never set opens in Normal, not with the last field's choice");
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // B: ignore worked tracks

        await vm.OpenFieldAsync(_a, "A");
        Assert.That(Skip(vm), Is.EqualTo((1, 1)));
        await vm.OpenFieldAsync(_b, "B");
        Assert.That(Skip(vm), Is.EqualTo((2, 1)));
    }

    [Test]
    public async Task Back_to_normal_is_kept_too_with_the_operators_row_count()
    {
        var vm = NewApp();
        await vm.OpenFieldAsync(_a, "A");
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // alternative
        vm.CycleUTurnSkipRowsCommand!.Execute(null);   // the operator picks 2
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);
        vm.ToggleUTurnSkipRowsCommand!.Execute(null);  // normal, 2 rows
        Assume.That(Skip(vm), Is.EqualTo((0, 2)));

        var again = NewApp();
        await again.OpenFieldAsync(_a, "A");
        Assert.That(Skip(again), Is.EqualTo((0, 2)));
        Assert.That(again.IsUTurnSkipRowsEnabled, Is.False);
    }

    [Test]
    public async Task A_field_whose_choice_was_never_changed_gets_no_file()
    {
        var vm = NewApp();
        await vm.OpenFieldAsync(_a, "A");
        Assume.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(1)), Is.True);
        await vm.CloseFieldAsync();

        Assert.That(File.Exists(Path.Combine(_a, UTurnSkipFileService.FileName)), Is.False);
    }

    [Test]
    public void A_damaged_or_out_of_range_file_reads_as_normal_or_in_range()
    {
        var path = Path.Combine(_a, UTurnSkipFileService.FileName);
        File.WriteAllText(path, "{ not json");
        Assert.That(UTurnSkipFileService.Load(_a), Is.EqualTo(UTurnSkipFileService.Choice.Normal));

        File.WriteAllText(path, "{ \"mode\": 7, \"rows\": 42 }");
        Assert.That(UTurnSkipFileService.Load(_a), Is.EqualTo(new UTurnSkipFileService.Choice(2, 9)));

        Assert.That(UTurnSkipFileService.Load(Path.Combine(_root, "nowhere")), Is.EqualTo(UTurnSkipFileService.Choice.Normal));
    }
}
