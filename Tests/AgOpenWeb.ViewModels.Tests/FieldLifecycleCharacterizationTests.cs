// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// What a field leaves behind when it is closed, and what of one field shows in the next, held
/// in <c>TestData/FieldLifecycle.txt</c>. The refactor in <c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c>
/// (item 6) gives each domain an open and a close step run by a field session, with the same steps
/// in the same order as today: the same things must be left behind after it, no more and no fewer.
///
/// No list of what a field sets up is written here (that list, kept by hand, is what let six
/// close-field bugs through since October). Every public property of the view model and of its
/// <c>State</c> is read instead (<see cref="VmStateReader"/>), on a view model that never opened a field, and again after a
/// field was opened, used and closed: whatever differs was left behind. A new property is covered
/// without anyone adding it. Then a second field is opened after the first, and compared with the
/// same field opened on a fresh view model: whatever differs is the first field showing through.
/// What differs between two fresh view models (times, counters) is noise and not counted.
///
/// The record holds the names of what differs, not the values. A change that is meant (a fix that
/// clears one more thing on close) makes the file again, on purpose:
/// <c>AGOPENWEB_UPDATE_FIELD_LIFECYCLE=1 dotnet test --filter FieldLifecycleCharacterizationTests</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FieldLifecycleCharacterizationTests
{
    private string _root = null!;
    private string _dirA = null!, _dirB = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-lifecycle-" + Guid.NewGuid().ToString("N"));
        _dirA = Path.Combine(_root, "Lifecycle A"); // names no other test uses: the last field opened is kept between view models
        _dirB = Path.Combine(_root, "Lifecycle B");
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);

        // Field A carries a boundary with an island, an AB line and a curve, and a headland line;
        // flags are placed once it is open.
        var a = new Boundary { OuterBoundary = Square(-100, -100, 200) };
        a.InnerBoundaries.Add(Square(20, 20, 15));
        GeoJsonFieldService.Save(new Field
        {
            Name = "Lifecycle A", DirectoryPath = _dirA, Origin = new Position { Latitude = 47.3127, Longitude = 8.6511 }, Boundary = a, // an origin no other test uses: the last position is kept between view models
        }, new[]
        {
            Track.FromABLine("AB A", new Vec3(-60, -100, 0), new Vec3(-60, 100, 0)),
            Track.FromCurve("Curve A", Enumerable.Range(0, 201).Select(i => new Vec3(-20 + 4 * Math.Sin(i / 30.0), -100 + i, 0)).ToList()),
        });
        var headland = new HeadlandLine();
        headland.Tracks.Add(new HeadlandPath { Name = "Headland", TrackPoints = Ring(-84, -84, 168), MoveDistance = 16 });
        GeoJsonFieldService.SaveHeadlandLine(_dirA, headland);

        // Field B: another place, a plain square and one AB line.
        GeoJsonFieldService.Save(new Field
        {
            Name = "Lifecycle B", DirectoryPath = _dirB, Origin = new Position { Latitude = 47.3301, Longitude = 8.6702 },
            Boundary = new Boundary { OuterBoundary = Square(-50, -80, 100) },
        }, new[] { Track.FromABLine("AB B", new Vec3(0, -80, 0), new Vec3(0, 80, 0)) });
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task What_a_field_leaves_behind_is_what_it_left_before_the_refactor()
    {
        var noise = Differences(Read(Fresh()), Read(Fresh()));

        // Open A, use it, close it.
        var vm = Fresh();
        var before = Read(vm);
        await vm.OpenFieldAsync(_dirA, "Lifecycle A");
        Assume.That(vm.IsFieldOpen, "field A did not open");
        var opened = Differences(before, Read(vm)).Except(noise).ToList();
        Assert.That(opened, Has.Count.GreaterThan(30), "opening a field changed too little: the reading does not reach the field's state");
        Assert.That(opened, Does.Contain("vm.State.Field.ActiveField.Boundary.OuterBoundary.Points"), "the boundary is not read");
        vm.ActivateTrackAt(1);
        vm.PlaceFlagAtWorldPosition(10, 10, FlagColor.Red);
        vm.PlaceFlagAtWorldPosition(-30, 50, FlagColor.Green);
        vm.RenameFlagAt(0, "Stone");
        await vm.CloseFieldAsync();
        var leftBehind = Differences(before, Read(vm)).Except(noise).ToList();

        // Open B after A, against B on a fresh view model.
        await vm.OpenFieldAsync(_dirB, "Lifecycle B");
        var fresh = Fresh();
        await fresh.OpenFieldAsync(_dirB, "Lifecycle B");
        var fresh2 = Fresh();
        await fresh2.OpenFieldAsync(_dirB, "Lifecycle B");
        var openNoise = Differences(Read(fresh), Read(fresh2)).ToList(); // a field's id is new on every load
        var showingThrough = Differences(Read(fresh), Read(vm)).Except(noise).Except(openNoise).ToList();

        var now = new List<string>();
        now.Add("# What a field leaves behind on close, and what of field A shows when B is opened after it.");
        now.Add("# Made by FieldLifecycleCharacterizationTests (AGOPENWEB_UPDATE_FIELD_LIFECYCLE=1); do not edit by hand.");
        now.AddRange(leftBehind.OrderBy(s => s, StringComparer.Ordinal).Select(s => "left behind: " + s));
        now.AddRange(showingThrough.OrderBy(s => s, StringComparer.Ordinal).Select(s => "showing through: " + s));

        string file = RecordFile();
        if (Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_FIELD_LIFECYCLE") == "1")
        {
            File.WriteAllLines(file, now);
            Assert.Pass($"wrote {now.Count - 2} lines to {file}");
        }

        var expected = File.ReadAllLines(file).Where(l => !l.StartsWith('#')).ToList();
        var actual = now.Where(l => !l.StartsWith('#')).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Except(expected), Is.Empty, "now left behind or showing through, and not before");
            Assert.That(expected.Except(actual), Is.Empty, "left behind or showing through before, and not now");
        });
    }

    /// <summary>
    /// #965: field A's headland was made 16 m in. Closed, and B (no headland) opened after it, the
    /// headland builder starts from the 12 m a fresh start gives, not from A's 16 m.
    /// </summary>
    [Test]
    public async Task The_headland_distance_of_a_closed_field_is_not_kept()
    {
        var vm = Fresh();
        double fresh = vm.HeadlandDistance;
        await vm.OpenFieldAsync(_dirA, "Lifecycle A");
        Assume.That(vm.HeadlandDistance, Is.EqualTo(16), "field A's headland distance was not loaded");

        await vm.CloseFieldAsync();
        Assert.That(vm.HeadlandDistance, Is.EqualTo(fresh), "after closing A");

        await vm.OpenFieldAsync(_dirA, "Lifecycle A");
        await vm.OpenFieldAsync(_dirB, "Lifecycle B"); // straight from A to B, without closing first
        Assert.That(vm.HeadlandDistance, Is.EqualTo(fresh), "B opened after A");
    }

    private MainViewModel Fresh()
    {
        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        return b.Build();
    }

    private static Dictionary<string, string> Read(MainViewModel vm) => VmStateReader.Read(vm);

    private static IEnumerable<string> Differences(Dictionary<string, string> a, Dictionary<string, string> b) =>
        VmStateReader.Differences(a, b);

    // ── Fields ─────────────────────────────────────────────────────────

    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static List<Vec3> Ring(double e0, double n0, double side)
    {
        var r = new List<Vec3>();
        for (double x = 0; x < side; x += 2) r.Add(new Vec3(e0 + x, n0, 0));
        for (double y = 0; y < side; y += 2) r.Add(new Vec3(e0 + side, n0 + y, 0));
        for (double x = side; x > 0; x -= 2) r.Add(new Vec3(e0 + x, n0 + side, 0));
        for (double y = side; y > 0; y -= 2) r.Add(new Vec3(e0, n0 + y, 0));
        return r;
    }

    private static string RecordFile([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "TestData", "FieldLifecycle.txt");
}
