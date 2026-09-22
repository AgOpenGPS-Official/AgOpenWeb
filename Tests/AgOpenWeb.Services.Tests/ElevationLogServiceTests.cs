// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>#112: Elevation.txt always starts with its header, even when the log was
/// switched on for a field that was created without it.</summary>
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

    [Test]
    public void FirstFlushOnAFieldWithoutTheFile_WritesTheHeaderFirst()
    {
        var svc = new ElevationLogService { IsEnabled = true };
        svc.LogPoint(32.5, -87.1, 100, 3, 4, 0, 0, 0, 0);
        svc.LogPoint(32.6, -87.2, 101, 3, 4, 10, 10, 0, 0);

        svc.Flush(_dir);

        var lines = File.ReadAllLines(Path.Combine(_dir, "Elevation.txt"));
        Assert.That(lines[0], Is.EqualTo("$Elevation"));
        Assert.That(lines[2], Is.EqualTo("StartLat,32.5000000"), "start = first logged fix");
        Assert.That(lines[3], Is.EqualTo("StartLon,-87.1000000"));
        Assert.That(lines[4], Does.StartWith("Latitude,Longitude,Elevation"));
        Assert.That(lines, Has.Length.EqualTo(7));
    }

    [Test]
    public void LaterFlushes_AppendWithoutAnotherHeader()
    {
        var svc = new ElevationLogService { IsEnabled = true };
        svc.LogPoint(32.5, -87.1, 100, 3, 4, 0, 0, 0, 0);
        svc.Flush(_dir);
        svc.LogPoint(32.6, -87.2, 101, 3, 4, 10, 10, 0, 0);
        svc.Flush(_dir);

        var text = File.ReadAllText(Path.Combine(_dir, "Elevation.txt"));
        Assert.That(text.Split("$Elevation").Length - 1, Is.EqualTo(1));
        Assert.That(File.ReadAllLines(Path.Combine(_dir, "Elevation.txt")), Has.Length.EqualTo(7));
    }
}
