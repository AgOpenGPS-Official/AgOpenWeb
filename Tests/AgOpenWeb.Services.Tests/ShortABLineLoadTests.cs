// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.GeoJson;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// An AB line whose B is about 1 m from A (AgOpenGPS's A+ line) is read with B 100 m out along it,
/// from field.geojson and from AgOpenGPS's TrackLines.txt, so its passes can be offset (a tester's
/// field: no pass, AutoSteer held the wheels straight).
/// </summary>
[TestFixture]
public class ShortABLineLoadTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"agopenweb_shortab_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Deg(double rad) => (rad * 180.0 / Math.PI + 360.0) % 360.0;
    private static double Bearing(Vec3 a, Vec3 b) => Deg(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing));

    [Test]
    public void A_1_m_line_in_field_geojson_loads_100_m_long_on_its_stored_heading()
    {
        // The tester's line: B 0.961 m from A at 301.01°, its heading stored as 300.91°.
        double stored = Rad(300.913);
        var a = new Vec3(-239.30, 53.00, stored);
        var b = new Vec3(a.Easting + Math.Sin(Rad(301.013)) * 0.961, a.Northing + Math.Cos(Rad(301.013)) * 0.961, stored);
        var line = new TrackModel { Name = "A+300.90325°", Type = AgOpenWeb.Models.Track.TrackType.ABLine, Points = new() { a, b } };
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 39.4194605, Longitude = -7.52944183333333 },
        }, new[] { line });

        var loaded = GeoJsonFieldService.LoadTracks(_dir).Single();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Points, Has.Count.EqualTo(2));
            Assert.That(loaded.Points[0].Easting, Is.EqualTo(a.Easting).Within(0.001), "A where it was");
            Assert.That(loaded.Points[0].Northing, Is.EqualTo(a.Northing).Within(0.001));
            Assert.That(GeometryMath.Distance(loaded.Points[0], loaded.Points[1]), Is.EqualTo(100).Within(1e-6));
            Assert.That(Bearing(loaded.Points[0], loaded.Points[1]), Is.EqualTo(300.913).Within(1e-6), "the stored heading");
            Assert.That(CurveProcessing.CurvePass(loaded.Points, 304.0).Points, Has.Count.EqualTo(2), "a pass 304 m over (its nudge)");
        });
    }

    [Test]
    public void An_A_plus_line_from_TrackLines_txt_loads_100_m_long()
    {
        // AgOpenGPS writes the heading in radians, then A and B to the millimetre.
        double h = Rad(300.90325);
        string Pt(double e, double n) => string.Create(CultureInfo.InvariantCulture, $"{e:0.###},{n:0.###}");
        File.WriteAllLines(Path.Combine(_dir, "TrackLines.txt"), new[]
        {
            "$TrackLines", "A+300.90325°", h.ToString("R", CultureInfo.InvariantCulture),
            Pt(-239.3, 53.0), Pt(-239.3 + Math.Sin(h), 53.0 + Math.Cos(h)), "0", "2", "True", "0",
        });

        var t = TrackFilesService.Load(_dir).Single();

        Assert.Multiple(() =>
        {
            Assert.That(GeometryMath.Distance(t.Points[0], t.Points[1]), Is.EqualTo(100).Within(1e-6));
            Assert.That(Bearing(t.Points[0], t.Points[1]), Is.EqualTo(300.90325).Within(1e-6), "AgOpenGPS's heading, not the rounded 1 m");
        });
    }

    [Test]
    public void An_AB_line_of_ordinary_length_loads_as_it_is()
    {
        File.WriteAllLines(Path.Combine(_dir, "TrackLines.txt"), new[]
        {
            "$TrackLines", "AB 0", "0", "10,20", "10,250", "0", "2", "True", "0",
        });

        var t = TrackFilesService.Load(_dir).Single();

        Assert.That(t.Points[1].Northing, Is.EqualTo(250).Within(1e-9));
    }
}
