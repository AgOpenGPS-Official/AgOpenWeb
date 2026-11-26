// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Services;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 5.3 / 7.4: a field from KML. Files as Google Earth and GIS tools write them go
/// through the real From KML command (RemoteCreateFromKml, the files under the data root's
/// Import folder) and the field's boundary is checked in metres: the outer ring and its
/// island (also when both are on one line), a Point placemark ignored, the ring's repeated
/// closing point dropped, and the numbers read the same in a comma-decimal culture.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA and the current culture are process-wide
public class KmlImportTests
{
    private string _root = null!;
    private string? _oldData;
    private CultureInfo _oldCulture = null!;

    // A 100 m square at 52° N, 5° E with a 20 m island, in the field plane's metres per degree
    // (the ellipsoid series AgOpenGPS's LocalPlane uses).
    private const double Lat0 = 52.0, Lon0 = 5.0;
    private static readonly double Phi = Lat0 * Math.PI / 180;
    private static readonly double DegPerMLat = 1 / (111132.92 - 559.82 * Math.Cos(2 * Phi) + 1.175 * Math.Cos(4 * Phi) - 0.0023 * Math.Cos(6 * Phi));
    private static readonly double DegPerMLon = 1 / (111412.84 * Math.Cos(Phi) - 93.5 * Math.Cos(3 * Phi) + 0.118 * Math.Cos(5 * Phi));

    private static string Ll(double eM, double nM, bool alt = true) =>
        string.Create(CultureInfo.InvariantCulture, $"{Lon0 + eM * DegPerMLon:F7},{Lat0 + nM * DegPerMLat:F7}") + (alt ? ",0" : "");

    private static string Ring(double e0, double n0, double side, string sep, bool alt = true, bool closed = true)
    {
        var pts = new List<string> { Ll(e0, n0, alt), Ll(e0 + side, n0, alt), Ll(e0 + side, n0 + side, alt), Ll(e0, n0 + side, alt) };
        if (closed) pts.Add(Ll(e0, n0, alt));
        return string.Join(sep, pts);
    }

    /// <summary>Google Earth: the coordinates on their own indented lines, a Point placemark too.</summary>
    private static string GoogleEarth() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
        	<Placemark><name>North</name><Polygon>
        		<outerBoundaryIs><LinearRing><coordinates>
        			{Ring(0, 0, 100, " ")}
        		</coordinates></LinearRing></outerBoundaryIs>
        		<innerBoundaryIs><LinearRing><coordinates>
        			{Ring(40, 40, 20, " ")}
        		</coordinates></LinearRing></innerBoundaryIs>
        	</Polygon></Placemark>
        	<Placemark><name>Gate</name><Point><coordinates>{Ll(50, 0)}</coordinates></Point></Placemark>
        </Document></kml>
        """;

    /// <summary>A GIS export: one line per point, no altitude, no closing point.</summary>
    private static string GisExport() =>
        "<kml><Placemark><Polygon><outerBoundaryIs><LinearRing><coordinates>\n" +
        Ring(0, 0, 100, "\n", alt: false, closed: false) +
        "\n</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></kml>";

    /// <summary>GDAL's KML driver (a QGIS export): the whole polygon, outer ring and island,
    /// on one line.</summary>
    private static string OneLinePolygon() =>
        "<kml><Document><Placemark><name>North</name>\n" +
        $"<Polygon><outerBoundaryIs><LinearRing><coordinates>{Ring(0, 0, 100, " ")}</coordinates></LinearRing></outerBoundaryIs>" +
        $"<innerBoundaryIs><LinearRing><coordinates>{Ring(40, 40, 20, " ")}</coordinates></LinearRing></innerBoundaryIs></Polygon>\n" +
        "</Placemark></Document></kml>";

    [SetUp]
    public void SetUp()
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-kml-" + Guid.NewGuid().ToString("N"));
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppDataRoot.Documents, "Import"));
        _oldCulture = CultureInfo.CurrentCulture;
    }

    [TearDown]
    public void TearDown()
    {
        CultureInfo.CurrentCulture = _oldCulture;
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { System.IO.Directory.Delete(_root, true); } catch { }
    }

    private Boundary Import(string kml, string fileName = "north.kml")
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppDataRoot.Documents, "Import", fileName), kml);
        var b = new MainViewModelBuilder();
        var fieldsDir = System.IO.Path.Combine(_root, "Fields");
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = fieldsDir });
        Boundary? saved = null;
        b.FieldService.LoadField(Arg.Any<string>()).Returns(ci => new Field { Name = "North", DirectoryPath = ci.Arg<string>() });
        b.FieldService.When(f => f.SaveField(Arg.Any<Field>())).Do(ci => saved ??= ci.Arg<Field>().Boundary);
        var vm = b.Build();
        string? failure = null;
        vm.FailureReported += m => failure = m;

        vm.RemoteCreateFromKml(fileName, "North");

        Assert.That(failure, Is.Null);
        Assert.That(saved, Is.Not.Null, "the new field is saved with the KML's boundary");
        return saved!;
    }

    private static (double w, double h) Size(BoundaryPolygon p) =>
        (p.Points.Max(q => q.Easting) - p.Points.Min(q => q.Easting), p.Points.Max(q => q.Northing) - p.Points.Min(q => q.Northing));

    [Test]
    public void GoogleEarthFile_GivesTheFieldAndItsIsland_InMetres()
    {
        var boundary = Import(GoogleEarth());

        Assert.Multiple(() =>
        {
            var (w, h) = Size(boundary.OuterBoundary!);
            Assert.That(w, Is.EqualTo(100).Within(0.2), "outer width");
            Assert.That(h, Is.EqualTo(100).Within(0.2), "outer height");
            Assert.That(boundary.OuterBoundary!.Points, Has.Count.EqualTo(4), "the repeated closing point is dropped");
            Assert.That(boundary.InnerBoundaries, Has.Count.EqualTo(1), "the island, and not the Point placemark");
            var (iw, ih) = Size(boundary.InnerBoundaries[0]);
            Assert.That(iw, Is.EqualTo(20).Within(0.1));
            Assert.That(ih, Is.EqualTo(20).Within(0.1));
            Assert.That(boundary.AreaHectares, Is.EqualTo((100 * 100 - 20 * 20) / 10000.0).Within(0.005));
        });
    }

    [Test]
    public void GisExport_OnePointPerLine_NoAltitude()
    {
        var boundary = Import(GisExport(), "gis.kml");
        var (w, h) = Size(boundary.OuterBoundary!);
        Assert.Multiple(() =>
        {
            Assert.That(boundary.OuterBoundary!.Points, Has.Count.EqualTo(4));
            Assert.That(w, Is.EqualTo(100).Within(0.2));
            Assert.That(h, Is.EqualTo(100).Within(0.2));
            Assert.That(boundary.InnerBoundaries, Is.Empty);
        });
    }

    [Test]
    public void OneLinePolygon_KeepsTheIsland()
    {
        // Only the first ring of each line was read, so the island was lost.
        var boundary = Import(OneLinePolygon(), "qgis.kml");
        Assert.Multiple(() =>
        {
            var (w, h) = Size(boundary.OuterBoundary!);
            Assert.That(w, Is.EqualTo(100).Within(0.2), "outer width");
            Assert.That(h, Is.EqualTo(100).Within(0.2), "outer height");
            Assert.That(boundary.InnerBoundaries, Has.Count.EqualTo(1), "the island on the same line");
            var (iw, ih) = Size(boundary.InnerBoundaries[0]);
            Assert.That(iw, Is.EqualTo(20).Within(0.1));
            Assert.That(ih, Is.EqualTo(20).Within(0.1));
        });
    }

    [Test]
    public void ACommaDecimalCulture_ReadsTheSameNumbers()
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        var boundary = Import(GoogleEarth());
        var (w, _) = Size(boundary.OuterBoundary!);
        Assert.That(w, Is.EqualTo(100).Within(0.2));
    }

    [Test]
    public void TheFieldIsCentredOnTheKml()
    {
        // The origin is the middle of the points, so the field's metres stay small.
        var boundary = Import(GisExport(), "gis.kml");
        var pts = boundary.OuterBoundary!.Points;
        Assert.That(pts.Average(p => p.Easting), Is.EqualTo(0).Within(0.5));
        Assert.That(pts.Average(p => p.Northing), Is.EqualTo(0).Within(0.5));
    }

    [TestCase("../Escaped")]
    [TestCase("North/South")]
    public void AFieldNameThatIsAPath_IsRefused_AndNothingIsMade(string name)
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppDataRoot.Documents, "Import", "north.kml"), GoogleEarth());
        var b = new MainViewModelBuilder();
        var fieldsDir = System.IO.Path.Combine(_root, "Fields");
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = fieldsDir });
        var vm = b.Build();
        string? failure = null;
        vm.FailureReported += m => failure = m;

        vm.RemoteCreateFromKml("north.kml", name);

        Assert.That(failure, Does.Contain("can't contain"));
        Assert.That(System.IO.Directory.Exists(System.IO.Path.Combine(_root, "Escaped")), Is.False, "nothing outside the Fields folder");
        Assert.That(System.IO.Directory.Exists(fieldsDir) && System.IO.Directory.EnumerateFileSystemEntries(fieldsDir).Any(), Is.False);
        b.FieldService.DidNotReceive().SaveField(Arg.Any<Field>());
    }
}
