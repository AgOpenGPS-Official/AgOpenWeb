using AgOpenWeb.Models;
using AgOpenWeb.Services.Fields;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class FieldServiceFindFieldsNearTests
{
    private string _root = null!;
    private FieldService _svc = null!;

    // Two reference points in west Alabama, ~ a few km apart, plus one
    // far away in Texas. Real lat/lons so the haversine math is exercised.
    private const double TuscaloosaLat = 33.2098;
    private const double TuscaloosaLon = -87.5692;
    private const double NorthportLat = 33.2293;       // ~3 km from Tuscaloosa
    private const double NorthportLon = -87.5772;
    private const double DallasLat = 32.7767;          // ~750 km from Tuscaloosa
    private const double DallasLon = -96.7970;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agvalonia_findnear_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _svc = new FieldService();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string SeedField(string name, double lat, double lon)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var field = new Field
        {
            Name = name,
            DirectoryPath = dir,
            Origin = new Position { Latitude = lat, Longitude = lon }
        };
        FieldJsonService.Save(field, dir);
        return dir;
    }

    [Test]
    public void FindFieldsNear_EmptyRoot_ReturnsEmpty()
    {
        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 10);
        Assert.That(nearby, Is.Empty);
    }

    [Test]
    public void FindFieldsNear_NonexistentRoot_ReturnsEmpty()
    {
        var nearby = _svc.FindFieldsNear(
            Path.Combine(_root, "missing"), TuscaloosaLat, TuscaloosaLon, 10);
        Assert.That(nearby, Is.Empty);
    }

    [Test]
    public void FindFieldsNear_FiltersByMaxKm()
    {
        SeedField("nearby", NorthportLat, NorthportLon);
        SeedField("faraway", DallasLat, DallasLon);

        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 50);

        Assert.That(nearby, Has.Count.EqualTo(1));
        Assert.That(nearby[0].Name, Is.EqualTo("nearby"));
    }

    [Test]
    public void FindFieldsNear_OrdersByDistanceAscending()
    {
        SeedField("at-tuscaloosa", TuscaloosaLat, TuscaloosaLon);
        SeedField("northport",     NorthportLat,  NorthportLon);

        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 100);

        Assert.That(nearby, Has.Count.EqualTo(2));
        Assert.That(nearby[0].Name, Is.EqualTo("at-tuscaloosa"));
        Assert.That(nearby[1].Name, Is.EqualTo("northport"));
        Assert.That(nearby[0].DistanceKm, Is.LessThan(nearby[1].DistanceKm));
    }

    [Test]
    public void FindFieldsNear_DistanceCalculationIsHaversine()
    {
        SeedField("nearby", NorthportLat, NorthportLon);

        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 100);

        // The two coords above are ~3 km apart (≈2.8 km by haversine);
        // a tight bound here catches both off-by-1000 unit confusion
        // (km vs m) and any sign-flip in lat/lon.
        Assert.That(nearby[0].DistanceKm, Is.GreaterThan(2.0).And.LessThan(4.0));
    }

    [Test]
    public void FindFieldsNear_SkipsFieldsWithZeroOrigin()
    {
        // A field that was never georeferenced persists Origin (0, 0).
        // Including it would say it's ~10000 km from any real query point,
        // but for the InField shortcut a (0,0) field is just noise.
        SeedField("ungeocoded", 0, 0);
        SeedField("real", NorthportLat, NorthportLon);

        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 10);

        Assert.That(nearby, Has.Count.EqualTo(1));
        Assert.That(nearby[0].Name, Is.EqualTo("real"));
    }

    [Test]
    public void FindFieldsNear_SkipsDirectoriesWithoutMetadata()
    {
        // A bare directory under FieldsRoot (no field.json, no Field.txt) —
        // someone mkdir'd a folder. Must not crash; just gets skipped.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        SeedField("real", NorthportLat, NorthportLon);

        var nearby = _svc.FindFieldsNear(_root, TuscaloosaLat, TuscaloosaLon, 10);

        Assert.That(nearby, Has.Count.EqualTo(1));
        Assert.That(nearby[0].Name, Is.EqualTo("real"));
    }
}
