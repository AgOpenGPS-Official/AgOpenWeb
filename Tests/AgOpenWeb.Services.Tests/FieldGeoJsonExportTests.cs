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
/// field.geojson converts with LocalPlane, as live GPS and the AgOpenGPS import do, so its
/// WGS84 is right for GIS tools; From Existing reads an AgOpenGPS source without importing it.
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
    public void Converts_with_LocalPlane()
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
    }

    [Test]
    public void Round_trips()
    {
        var field = NewField("F", FarSquare());
        GeoJsonFieldService.Save(field, tracks: null);

        var (loaded, _) = GeoJsonFieldService.Load(field.DirectoryPath);
        var pts = loaded.Boundary!.OuterBoundary!.Points;
        Assert.That(pts[2].Easting, Is.EqualTo(2100).Within(0.001));
        Assert.That(pts[2].Northing, Is.EqualTo(2000).Within(0.001));
    }

    [Test]
    public void From_Existing_reads_an_AgOpenGPS_source_without_importing_it()
    {
        // An AgOpenGPS field that hasn't been opened yet.
        var source = Path.Combine(_root, "Source");
        Directory.CreateDirectory(source);
        var inv = CultureInfo.InvariantCulture;
        File.WriteAllLines(Path.Combine(source, "Field.txt"), new[]
        {
            "2025-06-15 10:30:00", "$FieldDir", "Source", "$Offsets", "0,0", "Convergence", "0", "StartFix",
            $"{OriginLat.ToString(inv)},{OriginLon.ToString(inv)}",
        });
        new BoundaryFileService().SaveBoundary(new Boundary { OuterBoundary = FarSquare() }, source);

        var fields = new FieldService();
        var copyDir = Path.Combine(_root, "Copy");
        FieldCopyService.CreateFromExisting(fields, source, copyDir, "Copy",
            copyFlags: false, copyMapping: false, copyHeadland: false, copyLines: false);

        Assert.That(File.Exists(Path.Combine(source, "Field.txt")), Is.True, "source left as it was");
        Assert.That(File.Exists(Path.Combine(copyDir, "Field.txt")), Is.False, "copy is field.geojson only");
        var copied = fields.LoadField(copyDir).Boundary!.OuterBoundary!;
        Assert.That(copied.Points.Max(p => p.Easting), Is.EqualTo(2100).Within(0.001));
    }
}
