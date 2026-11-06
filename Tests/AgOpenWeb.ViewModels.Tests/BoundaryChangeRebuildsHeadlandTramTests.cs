// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tram;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Tram;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The headland and the tram lines are built on the outer boundary. Redrawing a smaller
/// boundary over the old one used to leave both where they were (a headland outside the new
/// field, tram lines along the old edge), and deleting the outer boundary left a headland
/// with no boundary at all. Now a change of the outer boundary rebuilds them and says so.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryChangeRebuildsHeadlandTramTests
{
    private string _root = null!;
    private TramLineService _tram = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.TrackWidth = 1.8;
        config.Tram.TramWidth = 12;
        _root = Path.Combine(Path.GetTempPath(), "aow-bndchange-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(dir);
        var field = new Field { Name = "F", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);

        _tram = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, config);
        var b = new MainViewModelBuilder { FieldService = new FieldService(), TramLineService = _tram };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = b.Build();
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true; // CurrentFieldName follows ActiveField

        Square(0, 200);
        _vm.RemoteCreateHeadlandWholeBoundary(20);
        _vm.TramSystems.Add(new TramSystem { Name = "Tram 1 (Boundary)", ReferenceBoundaryIndex = 0, ReferenceTrackName = null, TramWidth = 12, PassCount = 1 });
        _vm.BuildTramLinesCommand!.Execute(null);

        Assume.That(Area(_vm.CurrentHeadlandLine!), Is.InRange(25_000, 26_000), "a 160 × 160 m headland line to start with");
        Assume.That(_tram.OuterBoundaryTrack, Is.Not.Empty, "tram lines along the boundary to start with");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>Draw a square from (lo, lo) to (hi, hi) on the map.</summary>
    private void Square(double lo, double hi) =>
        _vm.RemoteCreateBoundaryFromMapPoints(new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi) });

    private static double Area(IReadOnlyList<Vec3> ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].Easting * ring[i].Northing - ring[i].Easting * ring[j].Northing;
        return Math.Abs(sum) / 2;
    }

    [Test]
    public void Redrawing_the_boundary_smaller_rebuilds_the_headland_and_the_tram_lines_on_it()
    {
        Square(50, 150);

        var line = _vm.CurrentHeadlandLine!;
        Assert.Multiple(() =>
        {
            Assert.That(_vm.StatusMessage, Does.Contain("headland rebuilt on the new boundary").And.Contain("tram lines rebuilt"));
            Assert.That(_vm.HeadlandSegments, Has.Count.EqualTo(1));
            Assert.That(_vm.HeadlandSegments[0].Offset, Is.EqualTo(20), "the distance is kept");
            Assert.That(Area(line), Is.InRange(3_500, 3_700), "a 60 × 60 m headland line, 20 m inside the new square");
            foreach (var p in line)
                Assert.That(p.Easting, Is.InRange(69.5, 130.5), $"headland point ({p.Easting:F1}, {p.Northing:F1}) 20 m inside the new square");
            foreach (var p in _tram.OuterBoundaryTrack)
            {
                Assert.That(p.Easting, Is.InRange(50, 150), $"tram point ({p.Easting:F1}, {p.Northing:F1}) inside the new square");
                Assert.That(p.Northing, Is.InRange(50, 150), $"tram point ({p.Easting:F1}, {p.Northing:F1}) inside the new square");
            }
        });
    }

    [Test]
    public void Deleting_the_outer_boundary_removes_the_headland_and_the_tram_lines_along_it()
    {
        _vm.RefreshBoundaryList();
        _vm.SelectedBoundaryIndex = 0;
        _vm.DeleteBoundaryCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(_vm.HasBoundary, Is.False, "the outer boundary is gone");
            Assert.That(_vm.StatusMessage, Does.Contain("headland removed").And.Contain("tram lines rebuilt"));
            Assert.That(_vm.HasHeadland, Is.False);
            Assert.That(_vm.HeadlandSegments, Is.Empty);
            Assert.That(_vm.State.Field.HeadlandLine, Is.Null);
            Assert.That(_tram.HasTramLines, Is.False, "no boundary, no boundary tram lines");
        });
    }

    [Test]
    public void Redrawing_the_boundary_drops_the_line_segments_drawn_on_the_old_one()
    {
        _vm.RemoteCreateHeadlandFromMapPoints(false, 15, 0, 0, 200, 0); // a line along the old south edge
        Assume.That(_vm.HeadlandSegments, Has.Count.EqualTo(2));
        var notices = new List<string>();
        _vm.FailureReported += notices.Add;

        Square(50, 150);

        Assert.That(_vm.StatusMessage, Does.Contain("1 line/curve segment(s) of the old one dropped"));
        Assert.That(notices, Has.Some.Contains("Line 2").And.Contains("Draw them again"),
            "the operator is told which drawn line went, not only in the status line");
        Assert.That(_vm.HeadlandSegments.Select(s => s.Type), Is.EqualTo(new[] { Models.Headland.HeadlandSegmentType.Boundary }));
    }
}
