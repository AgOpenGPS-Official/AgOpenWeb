// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.GeoJson;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A track's point headings in field.geojson are worked out again on load: guidance takes a
/// point's heading as that of the segment starting there. A file written by AgOpenGPS (headings
/// averaged over both neighbours) or by another writer (none, or anything) loads the same.
/// </summary>
[TestFixture]
public class TrackHeadingOnLoadTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"agopenweb_heading_{Guid.NewGuid():N}");
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
    };

    private static double Segment(Vec3 a, Vec3 b)
    {
        double h = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        return h < 0 ? h + 2 * Math.PI : h;
    }

    [Test]
    public void AbLine_WithAWrongStoredHeading_LoadsWithItsOwn()
    {
        // An AB line running east, stored heading north (0, as a missing third value reads).
        var ab = new TrackModel
        {
            Name = "AB",
            Type = AgOpenWeb.Models.Track.TrackType.ABLine,
            Points = new List<Vec3> { new(10, 50, 0), new(90, 50, 0) },
        };
        GeoJsonFieldService.Save(NewField(), new[] { ab });

        var loaded = GeoJsonFieldService.LoadTracks(_dir).Single();

        Assert.That(loaded.Points[0].Heading, Is.EqualTo(Math.PI / 2).Within(1e-6));
        Assert.That(loaded.Points[1].Heading, Is.EqualTo(Math.PI / 2).Within(1e-6));
    }

    [Test]
    public void Curve_WithAveragedHeadings_LoadsWithSegmentHeadings()
    {
        // A bend, each point's heading averaged over its neighbours as AgOpenGPS stores it.
        var pts = new List<Vec3> { new(0, 0, 0), new(0, 10, 0), new(5, 18, 0), new(13, 23, 0), new(23, 25, 0) };
        var averaged = new List<Vec3>();
        for (int i = 0; i < pts.Count; i++)
        {
            var prev = pts[Math.Max(0, i - 1)];
            var next = pts[Math.Min(pts.Count - 1, i + 1)];
            averaged.Add(new Vec3(pts[i].Easting, pts[i].Northing, Segment(prev, next)));
        }
        GeoJsonFieldService.Save(NewField(), new[] { TrackModel.FromCurve("C", averaged) });

        var loaded = GeoJsonFieldService.LoadTracks(_dir).Single().Points;

        for (int i = 0; i < pts.Count - 1; i++)
            Assert.That(loaded[i].Heading, Is.EqualTo(Segment(pts[i], pts[i + 1])).Within(1e-6), $"point {i}");
        Assert.That(loaded[^1].Heading, Is.EqualTo(loaded[^2].Heading).Within(1e-9), "the last point keeps the last segment's");
    }
}
