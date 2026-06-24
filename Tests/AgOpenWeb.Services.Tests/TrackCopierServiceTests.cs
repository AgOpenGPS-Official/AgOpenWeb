using System.Collections.Generic;
using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class TrackCopierServiceTests
{
    private static LocalPlane MakePlane(double lat, double lon)
        => new LocalPlane(new Wgs84(lat, lon), new SharedFieldProperties());

    [Test]
    public void ConvertTracks_AbLine_BetweenIdenticalPlanes_PreservesGeometry()
    {
        // Same origin → identity transform. Position should be preserved exactly,
        // heading recomputed from the geometry (still A→B).
        var plane = MakePlane(40.0, -100.0);
        var ab = TrackModel.FromABLine("AB1",
            new Vec3(10, 20, 0),
            new Vec3(30, 50, 0));

        var sut = new TrackCopierService();
        var converted = sut.ConvertTracks(new[] { ab }, plane, plane);

        Assert.That(converted, Has.Count.EqualTo(1));
        var c = converted[0];
        Assert.That(c.Points[0].Easting, Is.EqualTo(10).Within(1e-6));
        Assert.That(c.Points[0].Northing, Is.EqualTo(20).Within(1e-6));
        Assert.That(c.Points[1].Easting, Is.EqualTo(30).Within(1e-6));
        Assert.That(c.Points[1].Northing, Is.EqualTo(50).Within(1e-6));

        // Heading is atan2(dE, dN). dE=20, dN=30 → atan2(20,30) ≈ 0.5880 rad.
        var expectedHeading = System.Math.Atan2(20.0, 30.0);
        Assert.That(c.Points[0].Heading, Is.EqualTo(expectedHeading).Within(1e-6));
        Assert.That(c.Points[1].Heading, Is.EqualTo(expectedHeading).Within(1e-6),
            "AB line: last point inherits heading from second-to-last (which is A→B itself)");
    }

    [Test]
    public void ConvertTracks_BetweenDifferentOrigins_RoundTripsThroughWgs84()
    {
        // Source plane at origin A; a point at (50, 100) in source plane
        // corresponds to a specific WGS84 location. Target plane has a
        // different origin; the same WGS84 location lands at a different
        // (easting, northing) in the target plane. Verify round-trip.
        var sourcePlane = MakePlane(40.0, -100.0);
        var targetPlane = MakePlane(40.001, -100.001);

        var sourcePoint = new Vec3(50, 100, 0);
        var ab = TrackModel.FromABLine("AB1", sourcePoint, new Vec3(150, 200, 0));

        var sut = new TrackCopierService();
        var converted = sut.ConvertTracks(new[] { ab }, sourcePlane, targetPlane);

        // Convert manually for assertion
        var sourceGeo = new GeoCoord(sourcePoint.Northing, sourcePoint.Easting);
        var wgs = sourcePlane.ConvertGeoCoordToWgs84(sourceGeo);
        var expectedTargetGeo = targetPlane.ConvertWgs84ToGeoCoord(wgs);

        Assert.That(converted[0].Points[0].Easting, Is.EqualTo(expectedTargetGeo.Easting).Within(1e-6));
        Assert.That(converted[0].Points[0].Northing, Is.EqualTo(expectedTargetGeo.Northing).Within(1e-6));
    }

    [Test]
    public void ConvertTracks_Curve_RecomputesPerPointHeadings()
    {
        var plane = MakePlane(40.0, -100.0);
        var curve = TrackModel.FromCurve("C1", new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0),  // step east
            new Vec3(10, 10, 0), // step north
            new Vec3(20, 10, 0), // step east
        });

        var sut = new TrackCopierService();
        var converted = sut.ConvertTracks(new[] { curve }, plane, plane);
        var pts = converted[0].Points;

        // pt0→pt1: dE=10, dN=0 → atan2(10,0) = π/2
        Assert.That(pts[0].Heading, Is.EqualTo(System.Math.PI / 2).Within(1e-6));
        // pt1→pt2: dE=0, dN=10 → atan2(0,10) = 0
        Assert.That(pts[1].Heading, Is.EqualTo(0).Within(1e-6));
        // pt2→pt3: dE=10, dN=0 → atan2(10,0) = π/2
        Assert.That(pts[2].Heading, Is.EqualTo(System.Math.PI / 2).Within(1e-6));
        // last point inherits heading from previous
        Assert.That(pts[3].Heading, Is.EqualTo(pts[2].Heading).Within(1e-6));
    }

    [Test]
    public void ConvertTracks_ClosedLoop_LastPointWrapsToFirst()
    {
        var plane = MakePlane(40.0, -100.0);
        var loop = TrackModel.FromCurve("Loop", new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0),
            new Vec3(10, 10, 0),
        }, isClosed: true);

        var sut = new TrackCopierService();
        var converted = sut.ConvertTracks(new[] { loop }, plane, plane);
        var pts = converted[0].Points;

        // Last point's heading should be from pt[^1] back to pt[0]:
        // dE = 0-10 = -10, dN = 0-10 = -10 → atan2(-10,-10) = -3π/4 → normalized = 5π/4
        var expected = System.Math.Atan2(-10.0, -10.0);
        if (expected < 0) expected += 2 * System.Math.PI;
        Assert.That(pts[^1].Heading, Is.EqualTo(expected).Within(1e-6),
            "Closed loop: last point's heading bridges back to first");
    }

    [Test]
    public void ConvertTracks_PreservesMetadata_ResetsWorkedPaths()
    {
        var plane = MakePlane(40.0, -100.0);
        var src = TrackModel.FromABLine("Original", new Vec3(0, 0, 0), new Vec3(10, 10, 0));
        src.NudgeDistance = 0.42;
        src.IsVisible = false;
        src.MarkPathWorked(3);
        src.MarkPathWorked(7);

        var sut = new TrackCopierService();
        var converted = sut.ConvertTracks(new[] { src }, plane, plane)[0];

        Assert.That(converted.Name, Is.EqualTo("Original"));
        Assert.That(converted.Type, Is.EqualTo(TrackType.ABLine));
        Assert.That(converted.NudgeDistance, Is.EqualTo(0.42));
        Assert.That(converted.IsVisible, Is.True, "Copied tracks default to visible regardless of source");
        Assert.That(converted.WorkedPaths, Is.Empty, "WorkedPaths must reset on copy");
    }

    [Test]
    public void ConvertTracks_EmptyInput_ReturnsEmpty()
    {
        var plane = MakePlane(40.0, -100.0);
        var sut = new TrackCopierService();

        Assert.That(sut.ConvertTracks(new TrackModel[0], plane, plane), Is.Empty);
        Assert.That(sut.ConvertTracks(null!, plane, plane), Is.Empty);
    }

    [Test]
    public void ConvertTracks_DoesNotMutateSource()
    {
        var plane = MakePlane(40.0, -100.0);
        var ab = TrackModel.FromABLine("AB", new Vec3(5, 5, 0), new Vec3(15, 25, 0));
        var originalA = ab.Points[0];

        var sut = new TrackCopierService();
        sut.ConvertTracks(new[] { ab }, plane, plane);

        Assert.That(ab.Points[0].Easting, Is.EqualTo(originalA.Easting));
        Assert.That(ab.Points[0].Northing, Is.EqualTo(originalA.Northing));
        Assert.That(ab.Points[0].Heading, Is.EqualTo(originalA.Heading));
    }

    [Test]
    public void CopyTracksToField_AppendsToExistingTargetTracks()
    {
        // Set up two field directories with Field.txt origins, plus an
        // existing TrackLines.txt in the target. Verify the copied track
        // is appended (not overwriting).
        var sourceDir = Path.Combine(Path.GetTempPath(), "TrackCopySource_" + System.Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(Path.GetTempPath(), "TrackCopyTarget_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            WriteFieldTxt(sourceDir, lat: 40.0, lon: -100.0);
            WriteFieldTxt(targetDir, lat: 40.001, lon: -100.001);

            // Pre-existing track in target
            var existing = TrackModel.FromABLine("ExistingAB",
                new Vec3(0, 0, 0), new Vec3(20, 0, 0));
            TrackFilesService.Save(targetDir, new[] { existing });

            // Source track to copy
            var newTrack = TrackModel.FromABLine("FromSource",
                new Vec3(50, 50, 0), new Vec3(150, 100, 0));

            var sut = new TrackCopierService();
            int count = sut.CopyTracksToField(sourceDir, targetDir, new[] { newTrack });

            Assert.That(count, Is.EqualTo(1));

            var afterTracks = TrackFilesService.Load(targetDir);
            Assert.That(afterTracks, Has.Count.EqualTo(2));
            Assert.That(afterTracks[0].Name, Is.EqualTo("ExistingAB"));
            Assert.That(afterTracks[1].Name, Is.EqualTo("FromSource"));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        }
    }

    /// <summary>
    /// Writes a minimal Field.txt that <see cref="FieldPlaneFileService.LoadField"/> can parse.
    /// </summary>
    private static void WriteFieldTxt(string dir, double lat, double lon)
    {
        var path = Path.Combine(dir, "Field.txt");
        // FieldPlaneFileService.LoadField expects:
        // line 1: timestamp
        // line 2: $FieldDir header
        // line 3: field name
        // line 4: $Offsets header
        // line 5: offsets (X,Y)
        // line 6: Convergence header
        // line 7: convergence value
        // line 8: $StartFix header
        // line 9: lat,lon (origin)
        File.WriteAllText(path,
            "2026-01-01T00:00:00Z\n" +
            "$FieldDir\n" +
            "FieldNew\n" +
            "$Offsets\n" +
            "0,0\n" +
            "$Convergence\n" +
            "0\n" +
            "$StartFix\n" +
            $"{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
    }
}
