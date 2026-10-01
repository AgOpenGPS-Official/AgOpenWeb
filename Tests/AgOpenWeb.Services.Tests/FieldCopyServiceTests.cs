// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #107: From Existing copied *.json names nothing writes, so the new field came out empty.
/// These build a real source field on disk and check the copy.
/// </summary>
[TestFixture]
public class FieldCopyServiceTests
{
    private string _root = null!;
    private string _src = null!;
    private FieldService _fields = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-fieldcopy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _fields = new FieldService();

        var field = _fields.CreateField(_root, "Source", new Position { Latitude = 43.5, Longitude = -74.25 });
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 80.0), (0.0, 80.0) })
            outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        var headland = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (10.0, 10.0), (90.0, 10.0), (90.0, 70.0), (10.0, 70.0) })
            headland.Points.Add(new BoundaryPoint(e, n, 0));
        headland.UpdateBounds();
        field.Boundary = new Boundary { OuterBoundary = outer, HeadlandPolygon = headland };
        _fields.SaveField(field);
        _src = field.DirectoryPath;

        File.WriteAllText(Path.Combine(_src, "field.origin"), "43.50000000,-74.25000000");
        File.WriteAllText(Path.Combine(_src, "RecPath.txt"), "rec");
        GeoJsonFieldService.SaveTracks(_src, new[] { AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, 0, 0), new Vec3(0, 50, 0)) });
        GeoJsonFieldService.SaveFlags(_src, new[] { new Flag(5, 5, FlagColor.Red, 1, "Flag 1") });
        File.WriteAllText(Path.Combine(_src, "HeadlandSegments.json"), "[]");
        Directory.CreateDirectory(Path.Combine(_src, "jobs", "job1"));
        File.WriteAllText(Path.Combine(_src, "jobs", "job1", "coverage.bin"), "cov");
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_root, true); } catch { } }

    private string NewDir => Path.Combine(_root, "Copy");

    [Test]
    public void NewField_HasTheNewName_SameOrigin_AndTheBoundary()
    {
        FieldCopyService.CreateFromExisting(_fields, _src, NewDir, "Copy", false, false, false, false);

        var loaded = _fields.LoadField(NewDir);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Name, Is.EqualTo("Copy"), "field.geojson's name must not be the source's");
            Assert.That(loaded.Origin.Latitude, Is.EqualTo(43.5).Within(1e-6));
            Assert.That(loaded.Origin.Longitude, Is.EqualTo(-74.25).Within(1e-6));
            Assert.That(loaded.Boundary?.OuterBoundary?.Points, Has.Count.EqualTo(4));
            Assert.That(File.Exists(Path.Combine(NewDir, "field.origin")), Is.True);
        });
    }

    [Test]
    public void Options_Off_CopyNoOptionalData()
    {
        FieldCopyService.CreateFromExisting(_fields, _src, NewDir, "Copy", false, false, false, false);

        foreach (var f in new[] { "RecPath.txt", "HeadlandSegments.json" })
            Assert.That(File.Exists(Path.Combine(NewDir, f)), Is.False, f);
        Assert.That(GeoJsonFieldService.LoadFlags(NewDir), Is.Empty, "no flags");
        Assert.That(GeoJsonFieldService.LoadTracks(NewDir), Is.Empty, "no lines");
        Assert.That(Directory.Exists(Path.Combine(NewDir, "jobs")), Is.False);
        Assert.That(_fields.LoadField(NewDir).Boundary?.HeadlandPolygon, Is.Null, "no headland");
    }

    [Test]
    public void Options_On_CopyTheRealFiles()
    {
        FieldCopyService.CreateFromExisting(_fields, _src, NewDir, "Copy", true, true, true, true);

        Assert.Multiple(() =>
        {
            Assert.That(GeoJsonFieldService.LoadFlags(NewDir), Has.Count.EqualTo(1), "flags");
            Assert.That(GeoJsonFieldService.LoadTracks(NewDir), Has.Count.EqualTo(1), "lines");
            Assert.That(File.Exists(Path.Combine(NewDir, "RecPath.txt")), "recorded path");
            Assert.That(_fields.LoadField(NewDir).Boundary?.HeadlandPolygon, Is.Not.Null, "headland");
            Assert.That(File.Exists(Path.Combine(NewDir, "HeadlandSegments.json")), "headland segments");
            Assert.That(File.Exists(Path.Combine(NewDir, "jobs", "job1", "coverage.bin")), "applied area");
        });
    }
}
