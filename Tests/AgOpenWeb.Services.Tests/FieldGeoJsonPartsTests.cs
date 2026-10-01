// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.GeoJson;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Tracks, flags and headland lines live in field.geojson next to the field itself. Each is
/// saved on its own and must leave the others alone; AgOpenGPS's TrackLines.txt, ABLines.txt,
/// Flags.txt and Headlines.txt are imported once and deleted.
/// </summary>
[TestFixture]
public class FieldGeoJsonPartsTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"agopenweb_parts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Field NewField() => new()
    {
        Name = "F",
        DirectoryPath = _dir,
        Origin = new Position { Latitude = 52.0, Longitude = 5.0 },
        Boundary = new Boundary { OuterBoundary = Square(0, 100) },
    };

    private static BoundaryPolygon Square(double min, double max)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (min, min), (max, min), (max, max), (min, max) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static TrackModel Ab(string name) => TrackModel.FromABLine(name, new Vec3(10, 10, 0), new Vec3(10, 90, 0));

    private static HeadlandLine Headland() => new()
    {
        Tracks =
        {
            new HeadlandPath
            {
                Name = "Headland", MoveDistance = 12.5, Mode = 0, APointIndex = 3,
                TrackPoints = { new Vec3(5, 5, 0.1), new Vec3(95, 5, 0.2), new Vec3(95, 95, 0.3), new Vec3(5, 95, 0.4) },
            },
        },
    };

    [Test]
    public void Each_part_round_trips_and_leaves_the_others_alone()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);

        GeoJsonFieldService.SaveTracks(_dir, new[] { Ab("A"), Ab("B") });
        var flag = new Flag(20, 30, FlagColor.Blue, 7, "Rock") { Notes = "big one" };
        GeoJsonFieldService.SaveFlags(_dir, new[] { flag });
        GeoJsonFieldService.SaveHeadlandLine(_dir, Headland());

        // Saving one part again doesn't touch the others.
        GeoJsonFieldService.SaveTracks(_dir, new[] { Ab("C") });

        Assert.Multiple(() =>
        {
            Assert.That(GeoJsonFieldService.LoadTracks(_dir).Select(t => t.Name), Is.EqualTo(new[] { "C" }));

            var f = GeoJsonFieldService.LoadFlags(_dir).Single();
            Assert.That((f.Name, f.FlagColor, f.UniqueNumber, f.Notes), Is.EqualTo(("Rock", FlagColor.Blue, 7, "big one")));
            Assert.That(f.Easting, Is.EqualTo(20).Within(0.001));
            Assert.That(f.Northing, Is.EqualTo(30).Within(0.001));

            var h = GeoJsonFieldService.LoadHeadlandLine(_dir).Tracks.Single();
            Assert.That((h.Name, h.MoveDistance, h.APointIndex), Is.EqualTo(("Headland", 12.5, 3)));
            Assert.That(h.TrackPoints, Has.Count.EqualTo(4));
            Assert.That(h.TrackPoints[2].Easting, Is.EqualTo(95).Within(0.001));
            Assert.That(h.TrackPoints[2].Heading, Is.EqualTo(0.3).Within(1e-9));

            Assert.That(new FieldService().LoadField(_dir).Boundary?.OuterBoundary, Is.Not.Null, "boundary kept");
        });
    }

    [Test]
    public void Saving_the_field_keeps_its_tracks_flags_and_headland_lines()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);
        GeoJsonFieldService.SaveTracks(_dir, new[] { Ab("A") });
        GeoJsonFieldService.SaveFlags(_dir, new[] { new Flag(1, 2, FlagColor.Red, 1, "Flag 1") });
        GeoJsonFieldService.SaveHeadlandLine(_dir, Headland());

        // A boundary edit saves the field.
        var field = NewField();
        field.Boundary!.OuterBoundary = Square(0, 200);
        new FieldService().SaveField(field);

        Assert.Multiple(() =>
        {
            Assert.That(GeoJsonFieldService.LoadTracks(_dir), Has.Count.EqualTo(1));
            Assert.That(GeoJsonFieldService.LoadFlags(_dir), Has.Count.EqualTo(1));
            Assert.That(GeoJsonFieldService.LoadHeadlandLine(_dir).Tracks, Has.Count.EqualTo(1));
            Assert.That(new FieldService().LoadField(_dir).Boundary!.OuterBoundary!.Points.Max(p => p.Easting),
                Is.EqualTo(200).Within(0.001));
        });
    }

    [Test]
    public void AgOpenGPS_tracks_flags_and_headland_lines_are_imported_then_deleted()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);
        TrackFilesService.Save(_dir, new[] { Ab("FromTrackLines") });
        File.WriteAllText(Path.Combine(_dir, TrackFilesService.AbLinesFileName), "Old,0,0,0,0,50\n");
        FlagFilesService.Save(_dir, new[] { new Flag(3, 4, FlagColor.Green, 2, "Gate") }, 52.0, 5.0);
        HeadlandLineSerializer.Save(_dir, Headland());

        new FieldService().LoadField(_dir);

        Assert.Multiple(() =>
        {
            Assert.That(GeoJsonFieldService.LoadTracks(_dir).Select(t => t.Name),
                Is.EquivalentTo(new[] { "FromTrackLines", "Old" }), "TrackLines.txt + ABLines.txt");
            var f = GeoJsonFieldService.LoadFlags(_dir).Single();
            Assert.That((f.Name, f.FlagColor), Is.EqualTo(("Gate", FlagColor.Green)));
            Assert.That(GeoJsonFieldService.LoadHeadlandLine(_dir).Tracks.Single().MoveDistance, Is.EqualTo(12.5));
            foreach (var name in new[] { "TrackLines.txt", "ABLines.txt", "Flags.txt", "Headlines.txt" })
                Assert.That(File.Exists(Path.Combine(_dir, name)), Is.False, name);
        });
    }

    [Test]
    public void Peeks_read_unimported_files_without_changing_the_folder()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);
        GeoJsonFieldService.SaveTracks(_dir, new[] { Ab("InGeoJson") });
        TrackFilesService.Save(_dir, new[] { Ab("InTrackLines") });
        FlagFilesService.Save(_dir, new[] { new Flag(3, 4, FlagColor.Green, 2, "Gate") }, 52.0, 5.0);
        HeadlandLineSerializer.Save(_dir, Headland());

        var fields = new FieldService();
        Assert.Multiple(() =>
        {
            // An unimported TrackLines.txt is what the field will have once opened.
            Assert.That(fields.PeekTracks(_dir).Select(t => t.Name), Is.EqualTo(new[] { "InTrackLines" }));
            Assert.That(fields.PeekFlags(_dir).Single().Name, Is.EqualTo("Gate"));
            Assert.That(fields.PeekHeadlandLine(_dir).Tracks, Has.Count.EqualTo(1));
            foreach (var name in new[] { "TrackLines.txt", "Flags.txt", "Headlines.txt" })
                Assert.That(File.Exists(Path.Combine(_dir, name)), Is.True, name + " left as it was");
        });
    }

    [Test]
    public void A_part_needs_the_field_file()
    {
        Assert.Throws<FileNotFoundException>(() => GeoJsonFieldService.SaveTracks(_dir, new[] { Ab("A") }));
        Assert.That(new FieldService().PeekTracks(_dir), Is.Empty);
    }
}
