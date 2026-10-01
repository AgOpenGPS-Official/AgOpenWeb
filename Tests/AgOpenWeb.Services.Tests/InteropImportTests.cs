using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.IsoXml;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.IsoXml;

namespace AgOpenWeb.Services.Tests;

/// <summary>Interop fixes from the AgOpenGPS audit: ISOXML ids, Twol track files, old Sections.txt.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class InteropImportTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    [Test]
    public void IsoXmlV4_GuidancePatterns_HaveTheirOwnGpnIds()
    {
        var plane = new LocalPlane(new Wgs84(32.59, -87.17), new SharedFieldProperties());
        var bnd = new IsoXmlBoundary { FenceLine = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0) } };
        var tracks = new List<IsoXmlTrack>
        {
            new() { Name = "AB1", Mode = IsoXmlTrackMode.AB, PtA = new Vec2(10, 10), PtB = new Vec2(10, 90) },
            new() { Name = "AB2", Mode = IsoXmlTrackMode.AB, PtA = new Vec2(20, 10), PtB = new Vec2(20, 90) },
        };
        IsoXmlExporter.Export(_dir, "Test", 10000, new List<IsoXmlBoundary> { bnd }, new List<List<Vec3>>(),
            tracks, plane, IsoXmlExporter.IsoXmlVersion.V4, "test", Array.Empty<IsoXmlDevice>());

        string xml = File.ReadAllText(Path.Combine(_dir, "TASKDATA.XML"));
        var ggp = Regex.Matches(xml, "<GGP A=\"(GGP-?\\d+)\"");
        var gpn = Regex.Matches(xml, "<GPN A=\"([^\"]+)\"");
        Assert.That(ggp.Count, Is.EqualTo(2));
        Assert.That(gpn.Count, Is.EqualTo(2));
        foreach (Match m in gpn) Assert.That(m.Groups[1].Value, Does.StartWith("GPN"), "a GPN id, not the group's GGP id");
        Assert.That(gpn[0].Groups[1].Value, Is.Not.EqualTo(gpn[1].Groups[1].Value));
    }

    [Test]
    public void TwolTrackFile_ExtraLinesPerTrack_AreSkipped()
    {
        File.WriteAllLines(Path.Combine(_dir, "TrackLines.txt"), new[]
        {
            "$TwolTracks",
            "AB one", "0", "0,0", "0,100", "0", "2", "True", "0",
            "True", "3.0",                       // Twol: inner/outer flag, half tool width
            "Curve two", "1.5708", "0,0", "20,0", "0.5", "4", "True", "3",
            "0,0,1.5708", "10,0,1.5708", "20,0,1.5708",
            "False", "3.0",
        });

        var tracks = TrackFilesService.Load(_dir);

        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.That(tracks[0].Name, Is.EqualTo("AB one"));
        Assert.That(tracks[1].Name, Is.EqualTo("Curve two"));
        Assert.That(tracks[1].Points, Has.Count.EqualTo(3));
        Assert.That(tracks[1].NudgeDistance, Is.EqualTo(0.5));
    }

    [Test]
    public void LegacySections_WithoutTheColourLine_StillPaint()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var cov = new CoverageMapService(ConfigurationStore.Instance);
        cov.SetFieldBounds(-50, 50, -50, 50);

        // One 2 m × 10 m strip written with an even count (no R,G,B line first).
        File.WriteAllLines(Path.Combine(_dir, "Sections.txt"), new[]
        {
            "4",
            "-1,0,0", "1,0,0",
            "-1,10,0", "1,10,0",
        });
        cov.LoadFromFile(_dir);

        Assert.That(cov.IsPointCovered(0, 5), Is.True);
        Assert.That(cov.IsPointCovered(0, 20), Is.False);
        Assert.That(cov.TotalWorkedArea, Is.EqualTo(20).Within(0.5), "2 m × 10 m");
    }

    [Test]
    public void LegacySections_WithTheColourLine_StillPaint()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var cov = new CoverageMapService(ConfigurationStore.Instance);
        cov.SetFieldBounds(-50, 50, -50, 50);

        File.WriteAllLines(Path.Combine(_dir, "Sections.txt"), new[]
        {
            "5", "27,151,160",
            "-1,0,0", "1,0,0",
            "-1,10,0", "1,10,0",
            "5", "27,151,160", // a second strip over half of the first: counted once
            "-1,5,0", "1,5,0",
            "-1,15,0", "1,15,0",
        });
        cov.LoadFromFile(_dir);

        Assert.That(cov.IsPointCovered(0, 5), Is.True);
        Assert.That(cov.TotalWorkedArea, Is.EqualTo(30).Within(0.5), "2 m × 15 m, overlap counted once");
    }

    [Test]
    public void SavedCoverage_WithAZeroArea_RecoversItFromTheCells()
    {
        // Jobs migrated from Sections.txt before the fix were saved with area 0.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var cov = new CoverageMapService(ConfigurationStore.Instance);
        cov.SetFieldBounds(-50, 50, -50, 50);
        File.WriteAllLines(Path.Combine(_dir, "Sections.txt"), new[]
        {
            "5", "27,151,160", "-1,0,0", "1,0,0", "-1,10,0", "1,10,0",
        });
        cov.LoadFromFile(_dir);
        foreach (var name in new[] { "_totalWorkedArea", "_totalWorkedAreaUser" })
            typeof(CoverageMapService).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(cov, 0.0);
        File.Delete(Path.Combine(_dir, "Sections.txt"));
        cov.SaveToFile(_dir);

        var reloaded = new CoverageMapService(ConfigurationStore.Instance);
        reloaded.SetFieldBounds(-50, 50, -50, 50);
        reloaded.LoadFromFile(_dir);

        Assert.That(reloaded.IsPointCovered(0, 5), Is.True);
        Assert.That(reloaded.TotalWorkedArea, Is.EqualTo(20).Within(0.5));
    }
}
