using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Tests that legacy AgOpenGPS field files are automatically converted
/// to GeoJSON on first load (#66).
/// </summary>
[TestFixture]
public class LegacyAutoImportTests
{
    private string _testDir = null!;
    private string _fieldDir = null!;
    private FieldService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AgOpenWeb_Test_{Guid.NewGuid():N}");
        _fieldDir = Path.Combine(_testDir, "TestField");
        Directory.CreateDirectory(_fieldDir);
        _service = new FieldService();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private void WriteLegacyFieldTxt(double lat = 47.1234567, double lon = -93.9876543, double convergence = 0.5)
    {
        File.WriteAllLines(Path.Combine(_fieldDir, "Field.txt"), new[]
        {
            "2025-06-15 10:30:00",           // Timestamp
            "$FieldDir",                      // Header
            "TestField",                      // Field name
            "$Offsets",                       // Header
            "0,0",                            // Offsets X,Y
            "Convergence",                    // Header
            convergence.ToString(CultureInfo.InvariantCulture),
            "StartFix",                       // Header
            $"{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}"
        });
    }

    private void WriteLegacyBoundary(double centerE = 100, double centerN = 200, double size = 50)
    {
        // Write a simple square boundary in legacy format
        var lines = new List<string>
        {
            "$Boundary",
            "False",  // isDriveThru
            "4",      // point count
            $"{centerE - size},{centerN - size},0",
            $"{centerE + size},{centerN - size},0",
            $"{centerE + size},{centerN + size},0",
            $"{centerE - size},{centerN + size},0"
        };
        File.WriteAllLines(Path.Combine(_fieldDir, "Boundary.txt"), lines);
    }

    [Test]
    public void LoadField_LegacyOnly_CreatesGeoJson()
    {
        WriteLegacyFieldTxt();

        _service.LoadField(_fieldDir);

        Assert.That(File.Exists(Path.Combine(_fieldDir, "field.geojson")), Is.True,
            "GeoJSON file should be created after loading legacy field");
    }

    [Test]
    public void LoadField_LegacyOnly_PreservesOrigin()
    {
        WriteLegacyFieldTxt(lat: 47.5, lon: -93.2);

        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(47.5).Within(0.0001));
        Assert.That(field.Origin.Longitude, Is.EqualTo(-93.2).Within(0.0001));
    }

    [Test]
    public void LoadField_LegacyOnly_PreservesConvergence()
    {
        WriteLegacyFieldTxt(convergence: 1.23);

        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Convergence, Is.EqualTo(1.23).Within(0.01));
    }

    [Test]
    public void LoadField_LegacyWithBoundary_PreservesBoundaryInGeoJson()
    {
        WriteLegacyFieldTxt();
        WriteLegacyBoundary();

        _service.LoadField(_fieldDir);

        // Verify GeoJSON was created
        Assert.That(GeoJsonFieldService.Exists(_fieldDir), Is.True);

        // Load from GeoJSON and verify boundary survived
        var (geoField, _) = GeoJsonFieldService.Load(_fieldDir);
        Assert.That(geoField.Boundary, Is.Not.Null);
        Assert.That(geoField.Boundary!.OuterBoundary, Is.Not.Null);
        Assert.That(geoField.Boundary.OuterBoundary!.Points.Count, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void LoadField_SecondLoad_ReadsTheImportedGeoJson()
    {
        WriteLegacyFieldTxt(lat: 47.0, lon: -93.0);
        WriteLegacyBoundary(centerE: 100, centerN: 200);

        _service.LoadField(_fieldDir);
        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(47.0).Within(0.001));
        Assert.That(field.Boundary?.OuterBoundary?.Points, Has.Count.EqualTo(4));
    }

    [Test]
    public void LoadField_AgOpenGpsFilesNextToAGeoJson_AreImportedOverIt()
    {
        // AgOpenGPS files dropped into an AgOpenWeb field folder replace what it had.
        var existing = new Field
        {
            Name = "TestField",
            DirectoryPath = _fieldDir,
            Origin = new Position { Latitude = 48.0, Longitude = -94.0 },
        };
        GeoJsonFieldService.Save(existing, new List<AgOpenWeb.Models.Track.Track>());
        WriteLegacyFieldTxt(lat: 47.0, lon: -93.0);
        WriteLegacyBoundary(centerE: 100, centerN: 200);

        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(47.0).Within(0.001));
        Assert.That(field.Boundary?.OuterBoundary?.Points, Has.Count.EqualTo(4));
        Assert.That(File.Exists(Path.Combine(_fieldDir, "Field.txt")), Is.False);
    }

    [Test]
    public void LoadField_AgOpenGpsFieldFiles_AreDeletedOnceImported()
    {
        WriteLegacyFieldTxt();
        WriteLegacyBoundary();
        File.WriteAllLines(Path.Combine(_fieldDir, "Headland.Txt"), new[] { "$Headland", "False", "0" });
        File.WriteAllText(Path.Combine(_fieldDir, "TrackLines.txt"), "$TrackLines");
        File.WriteAllText(Path.Combine(_fieldDir, "Flags.txt"), "$Flags\n0\n");
        File.WriteAllText(Path.Combine(_fieldDir, "Headlines.txt"), "$HeadLines\n");
        File.WriteAllText(Path.Combine(_fieldDir, "Contour.txt"), "$Contour\n2\n1,1,0\n1,9,0\n");
        File.WriteAllText(Path.Combine(_fieldDir, "RecPath.txt"), "$RecPath\n2\n0,0,0,5,True\n0,9,0,5,True\n");
        File.WriteAllText(Path.Combine(_fieldDir, "Old pass.rec"), "$RecPath\n2\n3,0,0,5,False\n3,9,0,5,False\n");
        File.WriteAllText(Path.Combine(_fieldDir, "Elevation.txt"),
            "$FieldDir\nLatitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll\n47.1,-93.9,250.000,4,0.000,0.000,0.000,0.000\n");
        File.WriteAllText(Path.Combine(_fieldDir, "TramLines.txt"), "$OuterTrack,0\n");

        _service.LoadField(_fieldDir);

        Assert.That(Directory.GetFiles(_fieldDir).Select(Path.GetFileName), Is.EquivalentTo(new[]
            { "field.geojson", "contours.geojson", "recorded-paths.geojson", "elevation.csv" }),
            "every AgOpenGPS file imported and deleted");
        Assert.That(GeoJsonFieldService.LoadContours(_fieldDir), Has.Count.EqualTo(1));
        Assert.That(GeoJsonFieldService.LoadCurrentRecordedPath(_fieldDir), Has.Count.EqualTo(2));
        Assert.That(GeoJsonFieldService.ListRecordedPaths(_fieldDir), Is.EqualTo(new[] { "Old pass" }));
        Assert.That(File.ReadAllLines(Path.Combine(_fieldDir, "elevation.csv")), Has.Length.EqualTo(2));
    }

    [Test]
    public void PeekField_ReadsAnAgOpenGpsFieldWithoutImportingIt()
    {
        WriteLegacyFieldTxt(lat: 47.0, lon: -93.0);
        WriteLegacyBoundary();

        var field = _service.PeekField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(47.0).Within(0.001));
        Assert.That(field.Boundary?.OuterBoundary, Is.Not.Null);
        Assert.That(File.Exists(Path.Combine(_fieldDir, "Field.txt")), Is.True, "left as it was");
        Assert.That(GeoJsonFieldService.Exists(_fieldDir), Is.False, "nothing written");
    }

    [Test]
    public void LoadField_EmptyFieldDir_Throws()
    {
        // No Field.txt, no field.geojson
        Assert.Throws<FileNotFoundException>(() => _service.LoadField(_fieldDir));
    }

    [Test]
    public void LoadField_GeoJsonRoundTrip_PreservesOriginAccuracy()
    {
        double lat = 47.1234567;
        double lon = -93.9876543;
        WriteLegacyFieldTxt(lat: lat, lon: lon);

        // First load: legacy -> auto-convert to GeoJSON
        _service.LoadField(_fieldDir);

        // Second load: from GeoJSON
        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(lat).Within(0.0000001),
            "Origin latitude should survive legacy->GeoJSON round-trip with 7dp accuracy");
        Assert.That(field.Origin.Longitude, Is.EqualTo(lon).Within(0.0000001),
            "Origin longitude should survive legacy->GeoJSON round-trip with 7dp accuracy");
    }

    [Test]
    public void FieldExists_LegacyOnly_ReturnsTrue()
    {
        WriteLegacyFieldTxt();

        Assert.That(_service.FieldExists(_fieldDir), Is.True);
    }

    [Test]
    public void FieldExists_GeoJsonOnly_ReturnsTrue()
    {
        var field = new Field
        {
            Name = "TestField",
            DirectoryPath = _fieldDir,
            Origin = new Position { Latitude = 47.0, Longitude = -93.0 },
        };
        GeoJsonFieldService.Save(field, new List<AgOpenWeb.Models.Track.Track>());

        Assert.That(_service.FieldExists(_fieldDir), Is.True);
    }

    [Test]
    public void FieldExists_EmptyDir_ReturnsFalse()
    {
        Assert.That(_service.FieldExists(_fieldDir), Is.False);
    }

    [Test]
    public void LoadField_LocaleCorruptedCoordinates_RejectsInvalidValues()
    {
        // Locale-corrupted file: commas as decimal separators produce invalid values
        // Parser rejects them (out of range), origin stays at default (0,0)
        File.WriteAllLines(Path.Combine(_fieldDir, "Field.txt"), new[]
        {
            "2026-03-27 10:12:22",
            "$FieldDir",
            "test2",
            "$Offsets",
            "0,0",
            "Convergence",
            "0",
            "StartFix",
            "40,71280000,-74,00600000"
        });

        var field = _service.LoadField(_fieldDir);

        // Corrupted values (71280000) are out of range and rejected
        // Origin stays at default (0,0) rather than accepting garbage
        Assert.That(field.Origin.Latitude, Is.EqualTo(0).Within(0.001),
            "Corrupted coordinates should be rejected, origin stays at default");
    }

    [Test]
    public void LoadField_StandardCoordinates_ParsesCorrectly()
    {
        WriteLegacyFieldTxt(lat: 51.5074, lon: -0.1278);

        var field = _service.LoadField(_fieldDir);

        Assert.That(field.Origin.Latitude, Is.EqualTo(51.5074).Within(0.0001));
        Assert.That(field.Origin.Longitude, Is.EqualTo(-0.1278).Within(0.0001));
    }
}
