// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Xml;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.IsoXml;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 7.3: a field From ISO-XML, the whole import held to AgOpenGPS's. A TASKDATA.XML as
/// a farm management program writes it (the partfield's boundary, an obstacle, a road, a headland,
/// AB and curve guidance patterns, a bare guidance line, and a second field in the same file) goes
/// through the real From ISO-XML command with the real field service, and what lands in the
/// field's files is compared, in metres, with what AgOpenGPS's import builds for the same file
/// (<see cref="AgOpenGpsIsoXmlOracle"/>): the rings, the headland, every track's points.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA is process-wide
public class IsoXmlImportTests
{
    private string _root = null!;
    private string? _oldData;
    private string _importDir = null!;

    // 52° N, 5° E, in the field plane's metres per degree (the ellipsoid series LocalPlane uses).
    private const double Lat0 = 52.0, Lon0 = 5.0;
    private static readonly double Phi = Lat0 * Math.PI / 180;
    private static readonly double DegPerMLat = 1 / (111132.92 - 559.82 * Math.Cos(2 * Phi) + 1.175 * Math.Cos(4 * Phi) - 0.0023 * Math.Cos(6 * Phi));
    private static readonly double DegPerMLon = 1 / (111412.84 * Math.Cos(Phi) - 93.5 * Math.Cos(3 * Phi) + 0.118 * Math.Cos(5 * Phi));

    private static string Pnt(double eM, double nM) => string.Create(CultureInfo.InvariantCulture,
        $"<PNT A=\"2\" C=\"{Lat0 + nM * DegPerMLat:F9}\" D=\"{Lon0 + eM * DegPerMLon:F9}\"/>");

    private static string Pnts(IEnumerable<(double e, double n)> pts) => string.Concat(pts.Select(p => Pnt(p.e, p.n)));

    private static IEnumerable<(double, double)> Square(double e0, double n0, double side) =>
        new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) };

    /// <summary>The field: a 100 m square with its corners cut, and everything a program may put in it.</summary>
    private static IEnumerable<(double, double)> NorthRing() => new[]
    {
        (10.0, 0.0), (90.0, 0.0), (100.0, 10.0), (100.0, 90.0), (90.0, 100.0), (10.0, 100.0), (0.0, 90.0), (0.0, 10.0),
    };

    /// <summary>A gentle bend, 12 points 8 m apart.</summary>
    private static IEnumerable<(double, double)> Bend() =>
        Enumerable.Range(0, 12).Select(i => (50.0 + 6.0 * Math.Sin(i * 0.5), 5.0 + i * 8.0));

    private static string Farm() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ISO11783_TaskData VersionMajor="4" VersionMinor="2" ManagementSoftwareManufacturer="Test" ManagementSoftwareVersion="1" DataTransferOrigin="1">
          <PFD A="PFD1" C="North">
            <PLN A="1" B="Field"><LSG A="1">{Pnts(NorthRing())}</LSG></PLN>
            <PLN A="5" B="Track"><LSG A="1">{Pnts(Square(-20, 0, 10))}</LSG></PLN>
            <PLN A="6" B="Pond"><LSG A="2">{Pnts(Square(40, 40, 20))}</LSG></PLN>
            <PLN A="10" B="Headland"><LSG A="1">{Pnts(Square(10, 10, 80))}</LSG></PLN>
            <GGP A="GGP1" B="AB east"><GPN A="GPN1" B="AB 1" C="1"><LSG A="5">{Pnt(10, 10)}{Pnt(10, 90)}</LSG></GPN></GGP>
            <GGP A="GGP2" B="Curve"><GPN A="GPN2" B="Bend" C="3"><LSG A="5">{Pnts(Bend())}</LSG></GPN></GGP>
            <LSG A="5" B="Side line">{Pnt(90, 5)}{Pnt(90, 95)}</LSG>
          </PFD>
          <PFD A="PFD2" C="South" D="40000">
            <PLN A="1" B="Field"><LSG A="1">{Pnts(Square(300, 0, 200))}</LSG></PLN>
          </PFD>
        </ISO11783_TaskData>
        """;

    private static string LinesOnly() => $"""
        <ISO11783_TaskData VersionMajor="4" VersionMinor="2">
          <PFD A="PFD1" C="Lines">
            <GGP A="GGP1" B="AB east"><GPN A="GPN1" B="AB 1" C="1"><LSG A="5">{Pnt(10, 10)}{Pnt(10, 90)}</LSG></GPN></GGP>
          </PFD>
        </ISO11783_TaskData>
        """;

    private static string RoadOnly() => $"""
        <ISO11783_TaskData VersionMajor="4" VersionMinor="2">
          <PFD A="PFD1" C="Road">
            <PLN A="5" B="Track"><LSG A="1">{Pnts(Square(-20, 0, 10))}</LSG></PLN>
          </PFD>
        </ISO11783_TaskData>
        """;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-isoxml-" + Guid.NewGuid().ToString("N"));
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
        _importDir = Path.Combine(AppDataRoot.Documents, "Import");
        Directory.CreateDirectory(_importDir);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Dataset(string folder, string xml)
    {
        var dir = Path.Combine(_importDir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "TASKDATA.XML");
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        return path;
    }

    private string FieldsDir => Path.Combine(_root, "Fields");

    /// <summary>The whole From ISO-XML command, as the web dialog sends it, waited for.</summary>
    private async Task<(MainViewModel vm, string? failure)> Import(string entry, string name)
    {
        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = FieldsDir });
        var vm = b.Build();
        string? failure = null;
        vm.FailureReported += m => failure = m;
        vm.RemoteCreateFromIsoXml(entry, name);
        var running = ((IAsyncRelayCommand)vm.ConfirmIsoXmlImportDialogCommand!).ExecutionTask;
        if (running != null) await running;
        return (vm, failure);
    }

    private static AgOpenGpsIsoXmlOracle.Field Theirs(string taskDataPath, int fieldIndex)
    {
        var doc = new XmlDocument();
        doc.Load(taskDataPath);
        return AgOpenGpsIsoXmlOracle.Build(doc, fieldIndex) ?? throw new InvalidOperationException("AgOpenGPS can't place the field");
    }

    /// <summary>How far our ring is from AgOpenGPS's, taken either way round (AgOpenGPS turns a
    /// ring to its winding and AgOpenWeb keeps the file's order; the ring is the same).</summary>
    private static double RingDeviation(IReadOnlyList<BoundaryPoint> ours, List<Vec3> theirs)
    {
        if (ours.Count != theirs.Count) return double.PositiveInfinity;
        double Dev(IEnumerable<Vec3> t) => ours.Zip(t, (o, p) => Math.Max(Math.Abs(o.Easting - p.Easting), Math.Abs(o.Northing - p.Northing))).Max();
        return Math.Min(Dev(theirs), Dev(Enumerable.Reverse(theirs)));
    }

    private static double LineDeviation(IReadOnlyList<Vec3> ours, IReadOnlyList<Vec3> theirs)
    {
        if (ours.Count != theirs.Count) return double.PositiveInfinity;
        return ours.Zip(theirs, (o, p) => Math.Max(Math.Abs(o.Easting - p.Easting), Math.Abs(o.Northing - p.Northing))).DefaultIfEmpty(0).Max();
    }

    private const double Tolerance = 0.002; // 2 mm, the field file's rounding

    [Test]
    public async Task TheWholeField_IsWhatAgOpenGpsBuilds()
    {
        var path = Dataset("farm", Farm());
        var theirs = Theirs(path, 0);
        Assume.That(theirs.bndList, Has.Count.EqualTo(2), "AgOpenGPS: the field and the pond, not the road");
        Assume.That(theirs.gArr, Has.Count.EqualTo(3), "AgOpenGPS: the AB pattern, the curve pattern, the bare line");

        var (vm, failure) = await Import(IsoXmlImportCatalog.Scan(_importDir)[0].Name, "North"); // the picker's first entry

        Assert.That(failure, Is.Null);
        Assert.That(vm.IsFieldOpen, Is.True, "the new field is open");
        var dir = Path.Combine(FieldsDir, "North");
        var boundary = new FieldService().LoadField(dir).Boundary!;
        var tracks = GeoJsonFieldService.LoadTracks(dir);
        var headland = GeoJsonFieldService.LoadHeadlandLine(dir).Tracks;
        Assert.Multiple(() =>
        {
            Assert.That(RingDeviation(boundary.OuterBoundary!.Points, theirs.bndList[0].fenceLine), Is.LessThan(Tolerance), "the outer ring");
            Assert.That(boundary.InnerBoundaries, Has.Count.EqualTo(1), "the pond (an interior LSG) and not the road");
            Assert.That(RingDeviation(boundary.InnerBoundaries[0].Points, theirs.bndList[1].fenceLine), Is.LessThan(Tolerance), "the pond");
            Assert.That(boundary.AreaHectares * 10000, Is.EqualTo(theirs.bndList[0].area - theirs.bndList[1].area).Within(0.5), "the area, m²");
            Assert.That(headland, Has.Count.EqualTo(1), "the headland");
            Assert.That(LineDeviation(headland[0].TrackPoints, theirs.bndList[0].hdLine), Is.LessThan(Tolerance), "the headland ring");
            Assert.That(tracks.Select(t => t.Name), Is.EqualTo(theirs.gArr.Select(t => t.name)).AsCollection, "the tracks, by name");
        });
        for (int i = 0; i < theirs.gArr.Count; i++)
        {
            var t = theirs.gArr[i];
            var o = tracks[i];
            if (t.mode == AgOpenGpsIsoXmlOracle.TrackMode.AB)
            {
                Assert.That(o.IsABLine, Is.True, $"{t.name}: an AB line");
                Assert.That(LineDeviation(o.Points, new List<Vec3> { new(t.ptA.Easting, t.ptA.Northing, 0), new(t.ptB.Easting, t.ptB.Northing, 0) }),
                    Is.LessThan(Tolerance), $"{t.name}: A and B");
            }
            else
            {
                Assert.That(o.IsCurve, Is.True, $"{t.name}: a curve");
                Assert.That(o.Points, Has.Count.EqualTo(t.curvePts.Count), $"{t.name}: the processed curve's points (ends extended 100 m, 0.5 m spacing)");
                Assert.That(LineDeviation(o.Points, t.curvePts), Is.LessThan(Tolerance), $"{t.name}: the curve, point for point");
            }
        }
    }

    [Test]
    public async Task AFileFromAnotherProgram_IsWhatAgOpenGpsBuilds()
    {
        // The TASKDATA.XML in TestData: a real export, an 87-point boundary and one AB line.
        var path = Dataset("sample", File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "IsoXml", "TASKDATA.XML")));
        var theirs = Theirs(path, 0);

        var (_, failure) = await Import("sample", "Test1");

        Assert.That(failure, Is.Null);
        var dir = Path.Combine(FieldsDir, "Test1");
        var boundary = new FieldService().LoadField(dir).Boundary!;
        var tracks = GeoJsonFieldService.LoadTracks(dir);
        Assert.Multiple(() =>
        {
            Assert.That(boundary.OuterBoundary!.Points, Has.Count.EqualTo(theirs.bndList[0].fenceLine.Count));
            Assert.That(RingDeviation(boundary.OuterBoundary.Points, theirs.bndList[0].fenceLine), Is.LessThan(Tolerance));
            Assert.That(boundary.AreaHectares * 10000, Is.EqualTo(theirs.bndList[0].area).Within(0.5));
            Assert.That(tracks, Has.Count.EqualTo(1));
            Assert.That(tracks[0].Name, Is.EqualTo(theirs.gArr[0].name));
            Assert.That(tracks[0].Points[0].Easting, Is.EqualTo(theirs.gArr[0].ptA.Easting).Within(Tolerance));
            Assert.That(tracks[0].Points[1].Northing, Is.EqualTo(theirs.gArr[0].ptB.Northing).Within(Tolerance));
        });
    }

    [Test]
    public async Task SeveralFieldsInOneFile_AreListedAndEachCanBeChosen()
    {
        // AgOpenGPS lists every PFD in the file, with its area, and imports the one picked.
        var path = Dataset("farm", Farm());
        var doc = new XmlDocument();
        doc.Load(path);
        var labels = AgOpenGpsIsoXmlOracle.FieldLabels(doc);
        Assume.That(labels, Is.EqualTo(new[] { "North Area: 0.98 Ha", "South Area: 4.00 Ha" }).AsCollection);

        var entries = IsoXmlImportCatalog.Scan(_importDir);
        Assert.That(entries.Select(e => e.FieldName), Is.EqualTo(new[] { "North", "South" }).AsCollection, "both fields offered");
        Assert.That(entries.Select(e => Math.Round(e.AreaHectares, 2)), Is.EqualTo(new[] { 0.98, 4.00 }).AsCollection, "with AgOpenGPS's areas");
        Assert.That(entries.Select(e => e.Name).Distinct().Count(), Is.EqualTo(2), "told apart in the picker");

        var (vm, failure) = await Import(entries[1].Name, "South");

        Assert.That(failure, Is.Null);
        var theirs = Theirs(path, 1);
        var boundary = new FieldService().LoadField(Path.Combine(FieldsDir, "South")).Boundary!;
        Assert.That(RingDeviation(boundary.OuterBoundary!.Points, theirs.bndList[0].fenceLine), Is.LessThan(Tolerance), "the second field, not the first");
        Assert.That(vm.ActiveField?.Name, Is.EqualTo("South"));
    }

    [Test]
    public async Task ADatasetNamedByItsFolder_ImportsItsFirstField()
    {
        // What an older client or a script sends: the folder, as the picker showed it before.
        var path = Dataset("farm", Farm());
        var (vm, failure) = await Import("farm", "North");
        Assert.That(failure, Is.Null);
        Assert.That(vm.ActiveField?.Name, Is.EqualTo("North"));
        var boundary = new FieldService().LoadField(Path.Combine(FieldsDir, "North")).Boundary!;
        Assert.That(RingDeviation(boundary.OuterBoundary!.Points, Theirs(path, 0).bndList[0].fenceLine), Is.LessThan(Tolerance));
    }

    [Test]
    public void AFileWithOneField_IsListedByItsFolder()
    {
        Dataset("sample", File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "IsoXml", "TASKDATA.XML")));
        var entries = IsoXmlImportCatalog.Scan(_importDir);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo("sample"), "as the picker always showed it");
        Assert.That(entries[0].FieldName, Is.EqualTo("Test1"));
    }

    [Test]
    public async Task GuidanceLinesWithoutABoundary_ImportAsAgOpenGpsDoes()
    {
        // AgOpenGPS places the field on the AB line ("Missing Outer Boundary or AB line" is its
        // only refusal) and the field opens with the line and no boundary.
        var path = Dataset("lines", LinesOnly());
        var theirs = Theirs(path, 0);
        Assume.That(theirs.bndList, Is.Empty);
        Assume.That(theirs.gArr, Has.Count.EqualTo(1));

        var (vm, failure) = await Import("lines", "Lines");

        Assert.That(failure, Is.Null, "AgOpenGPS imports it");
        Assert.That(vm.IsFieldOpen, Is.True);
        var dir = Path.Combine(FieldsDir, "Lines");
        var tracks = GeoJsonFieldService.LoadTracks(dir);
        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(tracks[0].Name, Is.EqualTo(theirs.gArr[0].name));
        Assert.That(vm.HasBoundary, Is.False);
    }

    [Test]
    public async Task NothingToPlaceTheFieldOn_IsRefused_AndNothingIsMade()
    {
        var path = Dataset("road", RoadOnly());
        var doc = new XmlDocument();
        doc.Load(path);
        Assume.That(AgOpenGpsIsoXmlOracle.Build(doc, 0), Is.Null, "AgOpenGPS: Missing Outer Boundary or AB line");

        var (vm, failure) = await Import("road", "Road");

        Assert.That(failure, Is.Not.Null.And.Contains("boundary"));
        Assert.That(vm.IsFieldOpen, Is.False);
        Assert.That(Directory.Exists(Path.Combine(FieldsDir, "Road")), Is.False, "no field is made");
    }
}
