// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Linq;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>The elevation log is elevation.csv: a header row, then a row per sample (#112: the
/// header is there even when the log was switched on for an existing field).</summary>
[TestFixture]
public class ElevationLogServiceTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    private string Csv => Path.Combine(_dir, ElevationLogService.FileName);

    [Test]
    public void FirstFlush_WritesTheHeaderFirst()
    {
        var svc = new ElevationLogService { IsEnabled = true };
        svc.LogPoint(32.5, -87.1, 100, 3, 4, 0, 0, 0, 0);
        svc.LogPoint(32.6, -87.2, 101, 3, 4, 10, 10, 0, 0);
        svc.Flush(_dir);

        Assert.That(File.ReadAllBytes(Csv)[0], Is.EqualTo((byte)'l'), "no byte-order mark");
        var lines = File.ReadAllLines(Csv);
        Assert.That(lines[0], Is.EqualTo("latitude,longitude,elevation,fixQuality,easting,northing,heading,roll"));
        Assert.That(lines[1], Is.EqualTo("32.5000000,-87.1000000,97.000,4,0.000,0.000,0.000,0.000"));
        Assert.That(lines, Has.Length.EqualTo(3));
    }

    [Test]
    public void LaterFlushes_AppendWithoutAnotherHeader()
    {
        var svc = new ElevationLogService { IsEnabled = true };
        svc.LogPoint(32.5, -87.1, 100, 3, 4, 0, 0, 0, 0);
        svc.Flush(_dir);
        svc.LogPoint(32.6, -87.2, 101, 3, 4, 10, 10, 0, 0);
        svc.Flush(_dir);

        var lines = File.ReadAllLines(Csv);
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines.Count(l => l.StartsWith("latitude,")), Is.EqualTo(1));
    }

    [Test]
    public void AgOpenGPS_ElevationTxt_IsImportedThenDeleted()
    {
        File.WriteAllLines(Path.Combine(_dir, "Elevation.txt"), new[]
        {
            "2025-June-15 10:30:00 AM", "$FieldDir", "Elevation", "$Offsets", "0,0", "Convergence", "0", "StartFix", "32.5,-87.1",
            "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll",
            "32.5000000,-87.1000000,97.000,4,0.000,0.000,0.000,0.000",
            "32.6000000,-87.2000000,98.000,4,10.000,10.000,0.000,0.000",
        });

        ElevationLogService.ImportAgOpenGpsFile(_dir);

        Assert.That(File.Exists(Path.Combine(_dir, "Elevation.txt")), Is.False);
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(3));
    }
}
