using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Models.AgShare;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.AgShare;

namespace AgOpenWeb.Services.Tests.AgShare;

/// <summary>AgShare download fixes ported from AgOpenGPS #1203, #1214 and #1215.</summary>
[TestFixture]
public class AgShareDownloadTests
{
    private static readonly Wgs84 Origin = new(52.0, 5.0);
    private static readonly LocalPlane Plane = new(Origin, new SharedFieldProperties());

    // What the uploader sends for a local point.
    private static CoordinateDto Up(double e, double n)
    {
        var w = Plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        return new CoordinateDto { Latitude = w.Latitude, Longitude = w.Longitude };
    }

    private static AgShareFieldDto Dto(List<List<CoordinateDto>>? rings = null, List<AbLineUploadDto>? lines = null) => new()
    {
        Id = Guid.NewGuid(), Name = "F", Latitude = Origin.Latitude, Longitude = Origin.Longitude,
        Boundaries = rings ?? new(), AbLines = lines ?? new(),
    };

    [Test]
    public void UploadThenDownload_RoundTripsFarFromTheOrigin()
    {
        // 2 km north-east: with the longitude scale fixed at the origin's latitude the
        // download was ~0.75 m off to the east here.
        var ring = new List<CoordinateDto> { Up(0, 0), Up(2000, 0), Up(2000, 2000), Up(0, 2000) };
        var f = AgShareFieldParser.Parse(Dto(new() { ring }));

        var p = f.Boundaries[0][2];
        Assert.That(p.Easting, Is.EqualTo(2000).Within(0.001));
        Assert.That(p.Northing, Is.EqualTo(2000).Within(0.001));
    }

    [Test]
    public void TheUploadersClosingPoint_IsDropped()
    {
        var ring = new List<CoordinateDto> { Up(0, 0), Up(100, 0), Up(100, 100), Up(0, 0) };
        var f = AgShareFieldParser.Parse(Dto(new() { ring }));
        Assert.That(f.Boundaries[0], Has.Count.EqualTo(3));
    }

    [Test]
    public void ShortRings_AreDropped_AndAShortOuterMeansNoBoundary()
    {
        var outer = new List<CoordinateDto> { Up(0, 0), Up(100, 0), Up(100, 100), Up(0, 100) };
        var hole = new List<CoordinateDto> { Up(10, 10), Up(20, 10), Up(20, 20) };
        var bad = new List<CoordinateDto> { Up(50, 50), Up(60, 50) };

        var f = AgShareFieldParser.Parse(Dto(new() { outer, bad, hole }));
        Assert.That(f.Boundaries, Has.Count.EqualTo(2), "outer + the valid hole");
        Assert.That(f.Boundaries[1][0].Easting, Is.EqualTo(10).Within(0.001));

        f = AgShareFieldParser.Parse(Dto(new() { bad, hole }));
        Assert.That(f.Boundaries, Is.Empty, "a hole must not become the outer boundary");
    }

    [Test]
    public void CurveLastPoint_KeepsTheFinalSegmentsHeading()
    {
        // A curve heading east: every heading, the last included, is east (π/2), not 0.
        var line = new AbLineUploadDto
        {
            Name = "C", Type = "Curve",
            Coords = new() { Up(0, 0), Up(10, 0), Up(20, 0), Up(30, 0) },
        };
        var f = AgShareFieldParser.Parse(Dto(lines: new() { line }));
        var pts = f.AbLines[0].CurvePoints;
        Assert.That(pts, Has.Count.EqualTo(4));
        Assert.That(pts.Select(p => p.Heading), Is.All.EqualTo(Math.PI / 2).Within(1e-6));
    }

    [Test]
    public async Task Download_OverAnExistingField_KeepsLocalWork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agshare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "Flags.txt", "Headland.txt", "Contour.txt", "Sections.txt" })
                File.WriteAllText(Path.Combine(dir, name), "local " + name);

            var ring = new List<CoordinateDto> { Up(0, 0), Up(100, 0), Up(100, 100) };
            await FieldFileWriter.WriteAllFilesAsync(AgShareFieldParser.Parse(Dto(new() { ring })), dir);

            foreach (var name in new[] { "Flags.txt", "Headland.txt", "Contour.txt", "Sections.txt" })
                Assert.That(File.ReadAllText(Path.Combine(dir, name)), Is.EqualTo("local " + name), name);
            Assert.That(new FieldService().LoadField(dir).Boundary?.OuterBoundary, Is.Not.Null, "boundary in field.geojson");
            Assert.That(File.Exists(Path.Combine(dir, "Boundary.txt")), Is.False, "no AgOpenGPS field files");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task DownloadedField_OpensWithItsBoundaryAndTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "agshare-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "F");
        try
        {
            var outer = new List<CoordinateDto> { Up(0, 0), Up(500, 0), Up(500, 400), Up(0, 400), Up(0, 0) };
            var hole = new List<CoordinateDto> { Up(100, 100), Up(150, 100), Up(150, 150) };
            var lines = new List<AbLineUploadDto>
            {
                new() { Name = "AB", Type = "AB", Coords = new() { Up(10, 10), Up(10, 390) } },
                new() { Name = "C", Type = "Curve", Coords = new() { Up(20, 10), Up(30, 200), Up(20, 390) } },
            };
            await FieldFileWriter.WriteAllFilesAsync(AgShareFieldParser.Parse(Dto(new() { outer, hole }, lines)), dir);

            var field = new FieldService().LoadField(dir);
            Assert.That(field.Boundary!.OuterBoundary!.Points, Has.Count.EqualTo(4));
            Assert.That(field.Boundary.OuterBoundary.Points[2].Easting, Is.EqualTo(500).Within(0.001));
            Assert.That(field.Boundary.OuterBoundary.Points[2].Northing, Is.EqualTo(400).Within(0.001));
            Assert.That(field.Boundary.InnerBoundaries, Has.Count.EqualTo(1));

            var tracks = GeoJson.GeoJsonFieldService.LoadTracks(dir);
            Assert.That(tracks.Select(t => t.Name), Is.EqualTo(new[] { "AB", "C" }));
            Assert.That(tracks[0].Points, Has.Count.EqualTo(2));
            Assert.That(tracks[1].Points, Has.Count.EqualTo(3));
            Assert.That(tracks[1].Points[2].Northing, Is.EqualTo(390).Within(0.001));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task Download_NewField_WritesOnlyTheFieldFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agshare-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FieldFileWriter.WriteAllFilesAsync(AgShareFieldParser.Parse(Dto()), dir);
            Assert.That(File.Exists(Path.Combine(dir, "Flags.txt")), Is.False, "flags live in field.geojson");
            Assert.That(File.Exists(Path.Combine(dir, "TrackLines.txt")), Is.False, "tracks live in field.geojson");
            Assert.That(File.Exists(Path.Combine(dir, "Headland.txt")), Is.False, "the headland lives in field.geojson");
            Assert.That(File.Exists(Path.Combine(dir, "field.geojson")), Is.True);
            Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "agshare.txt", "field.geojson" }));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static BoundaryPolygon Square(double size)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (size, 0.0), (size, size), (0.0, size) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    [Test]
    public async Task Redownload_OverAnEarlierAgOpenGpsDownload_ImportsThenReplacesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agshare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An earlier download from a build that wrote AgOpenGPS files, never opened.
            new FieldPlaneFileService().SaveField(new Field { Name = "F", Origin = new Position { Latitude = 51, Longitude = 4 } }, dir);
            new BoundaryFileService().SaveBoundary(new Boundary { OuterBoundary = Square(40) }, dir);

            var ring = new List<CoordinateDto> { Up(0, 0), Up(100, 0), Up(100, 100) };
            await FieldFileWriter.WriteAllFilesAsync(AgShareFieldParser.Parse(Dto(new() { ring })), dir);

            Assert.That(File.Exists(Path.Combine(dir, "Field.txt")), Is.False);
            Assert.That(File.Exists(Path.Combine(dir, "Boundary.txt")), Is.False);
            var field = new FieldService().LoadField(dir);
            Assert.That(field.Origin.Latitude, Is.EqualTo(Origin.Latitude).Within(1e-9), "the download's origin");
            Assert.That(field.Boundary!.OuterBoundary!.Points.Max(p => p.Easting), Is.EqualTo(100).Within(0.001));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Redownload_ReplacesTheBoundary_AndKeepsTheHeadland()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agshare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fields = new FieldService();
            fields.SaveField(new Field
            {
                Name = "F", DirectoryPath = dir,
                Origin = new Position { Latitude = Origin.Latitude, Longitude = Origin.Longitude },
                Boundary = new Boundary { OuterBoundary = Square(40), HeadlandPolygon = Square(30) },
            });

            var ring = new List<CoordinateDto> { Up(0, 0), Up(100, 0), Up(100, 100) };
            await FieldFileWriter.WriteAllFilesAsync(AgShareFieldParser.Parse(Dto(new() { ring })), dir);

            var b = fields.LoadField(dir).Boundary!;
            Assert.That(b.OuterBoundary!.Points.Max(p => p.Easting), Is.EqualTo(100).Within(0.001), "boundary replaced");
            Assert.That(b.HeadlandPolygon, Is.Not.Null, "headland kept (local work)");
        }
        finally { Directory.Delete(dir, true); }
    }
}
