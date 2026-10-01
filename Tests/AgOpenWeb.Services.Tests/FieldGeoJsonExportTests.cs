// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Globalization;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// field.geojson is an export of the legacy field files: it converts with LocalPlane (as live
/// GPS, AgShare and the legacy files do), still reads files from builds that used GeoConversion,
/// and never overrides newer legacy files (FILE_FORMAT_MODERNIZATION_PLAN.md, question 9).
/// </summary>
[TestFixture]
public class FieldGeoJsonExportTests
{
    private const double OriginLat = 52.0, OriginLon = 5.0;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agopenweb_geojson_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // A 100 m square whose corners start 2 km east of the origin: far enough out that
    // scaling longitude at the origin's latitude instead of the point's is visible.
    private static BoundaryPolygon FarSquare()
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (2000.0, 0.0), (2100.0, 0.0), (2100.0, 2000.0), (2000.0, 2000.0) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private Field NewField(string name, BoundaryPolygon outer) => new()
    {
        Name = name,
        DirectoryPath = Path.Combine(_root, name),
        Origin = new Position { Latitude = OriginLat, Longitude = OriginLon },
        Boundary = new Boundary { OuterBoundary = outer },
    };

    private static List<double[]> OuterRing(string fieldDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fieldDir, "field.geojson")));
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            if (f.GetProperty("properties").GetProperty("role").GetString() == "outer-boundary")
                return f.GetProperty("geometry").GetProperty("coordinates")[0].EnumerateArray()
                        .Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
        throw new InvalidOperationException("no outer boundary");
    }

    [Test]
    public void Export_converts_with_LocalPlane_and_says_so()
    {
        var field = NewField("F", FarSquare());
        GeoJsonFieldService.Save(field, tracks: null);

        var plane = new LocalPlane(new Wgs84(OriginLat, OriginLon), new SharedFieldProperties());
        var ring = OuterRing(field.DirectoryPath);
        var corner = plane.ConvertGeoCoordToWgs84(new GeoCoord(2000, 2100)); // northing, easting
        Assert.That(ring[2][0], Is.EqualTo(corner.Longitude).Within(1e-10));
        Assert.That(ring[2][1], Is.EqualTo(corner.Latitude).Within(1e-10));

        // The old conversion would put that corner measurably elsewhere.
        var (_, oldLon) = new GeoConversion(OriginLat, OriginLon).ToWgs84(new Vec2(2100, 2000));
        double metresPerDegLon = 111412.84 * Math.Cos(corner.Latitude * Math.PI / 180);
        Assert.That(Math.Abs(oldLon - corner.Longitude) * metresPerDegLon, Is.GreaterThan(0.1));

        var json = File.ReadAllText(Path.Combine(field.DirectoryPath, "field.geojson"));
        Assert.That(json, Does.Contain("\"projection\": \"localPlane\""));
    }

    [Test]
    public void Export_round_trips()
    {
        var field = NewField("F", FarSquare());
        GeoJsonFieldService.Save(field, tracks: null);

        var (loaded, _) = GeoJsonFieldService.Load(field.DirectoryPath);
        var pts = loaded.Boundary!.OuterBoundary!.Points;
        Assert.That(pts[2].Easting, Is.EqualTo(2100).Within(0.001));
        Assert.That(pts[2].Northing, Is.EqualTo(2000).Within(0.001));
    }

    [Test]
    public void File_from_an_older_build_still_reads_back_exactly()
    {
        // Older builds wrote GeoConversion coordinates and no "projection" property.
        var geo = new GeoConversion(OriginLat, OriginLon);
        var ring = FarSquare().Points.Append(FarSquare().Points[0])
            .Select(p => { var (lat, lon) = geo.ToWgs84(new Vec2(p.Easting, p.Northing)); return new[] { lon, lat, 0.0 }; })
            .ToArray();
        var dir = Path.Combine(_root, "Old");
        Directory.CreateDirectory(dir);
        var fc = new
        {
            type = "FeatureCollection",
            features = new object[]
            {
                new
                {
                    type = "Feature",
                    geometry = new { type = "Point", coordinates = new[] { OriginLon, OriginLat } },
                    properties = new Dictionary<string, object> { ["role"] = "metadata", ["originLatitude"] = OriginLat, ["originLongitude"] = OriginLon, ["name"] = "Old" },
                },
                new
                {
                    type = "Feature",
                    geometry = new { type = "Polygon", coordinates = new[] { ring } },
                    properties = new Dictionary<string, object> { ["role"] = "outer-boundary" },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "field.geojson"), JsonSerializer.Serialize(fc));

        var (loaded, _) = GeoJsonFieldService.Load(dir);
        var pts = loaded.Boundary!.OuterBoundary!.Points;
        Assert.That(pts[2].Easting, Is.EqualTo(2100).Within(0.001));
        Assert.That(pts[2].Northing, Is.EqualTo(2000).Within(0.001));
    }

    [Test]
    public void From_Existing_copies_the_legacy_boundary_not_a_stale_geojson()
    {
        var fields = new FieldService();
        var source = NewField("Source", FarSquare());
        fields.SaveField(source);

        // The boundary is edited (or re-downloaded, or changed in AgOpenGPS) and only the
        // legacy files are rewritten: field.geojson still has the old square.
        var moved = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (50.0, 0.0), (50.0, 50.0), (0.0, 50.0) })
            moved.Points.Add(new BoundaryPoint(e, n, 0));
        moved.UpdateBounds();
        new BoundaryFileService().SaveBoundary(new Boundary { OuterBoundary = moved }, source.DirectoryPath);

        var copyDir = Path.Combine(_root, "Copy");
        FieldCopyService.CreateFromExisting(fields, source.DirectoryPath, copyDir, "Copy",
            copyFlags: false, copyMapping: false, copyHeadland: false, copyLines: false);

        var copied = new BoundaryFileService().LoadBoundary(copyDir)!.OuterBoundary!;
        Assert.That(copied.Points.Max(p => p.Easting), Is.EqualTo(50).Within(0.001),
            "the copy has the edited boundary");
    }
}
