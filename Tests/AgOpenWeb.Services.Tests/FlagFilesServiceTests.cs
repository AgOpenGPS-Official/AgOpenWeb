// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>#107: flags persist as Flags.txt in AgOpenGPS's format.</summary>
[TestFixture]
public class FlagFilesServiceTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [Test]
    public void SaveThenLoad_RoundTripsPositionColorIdAndName()
    {
        var flags = new List<Flag>
        {
            new(12.345, -67.891, FlagColor.Yellow, 3, "Wet spot"),
            new(-5, 100.5, FlagColor.Cyan, 7, "Rock, big"), // comma in the name
        };

        FlagFilesService.Save(_dir, flags, 43.7128, -74.006);
        var loaded = FlagFilesService.Load(_dir);

        Assert.That(loaded, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(loaded[0].Easting, Is.EqualTo(12.345).Within(1e-3));
            Assert.That(loaded[0].Northing, Is.EqualTo(-67.891).Within(1e-3));
            Assert.That(loaded[0].FlagColor, Is.EqualTo(FlagColor.Yellow));
            Assert.That(loaded[0].UniqueNumber, Is.EqualTo(3));
            Assert.That(loaded[0].Name, Is.EqualTo("Wet spot"));
            Assert.That(loaded[1].Name, Is.EqualTo("Rock  big"), "commas can't survive AgOpenGPS's fixed notes column");
            Assert.That(loaded[1].FlagColor, Is.EqualTo(FlagColor.Cyan));
        });
    }

    [Test]
    public void Save_WritesAgOpenGpsLayout_WithLatLonFromTheOrigin()
    {
        FlagFilesService.Save(_dir, new List<Flag> { new(0, 0, FlagColor.Red, 1, "Flag 1") }, 43.7128, -74.006);

        var lines = File.ReadAllLines(Path.Combine(_dir, "Flags.txt"));
        Assert.That(lines[0], Is.EqualTo("$Flags"));
        Assert.That(lines[1], Is.EqualTo("1"));
        var w = lines[2].Split(',');
        Assert.That(w, Has.Length.EqualTo(8), "lat,lon,easting,northing,heading,color,id,notes");
        Assert.That(double.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(43.7128).Within(1e-6));
        Assert.That(double.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(-74.006).Within(1e-6));
    }

    [Test]
    public void Load_ReadsAgOpenGpsFiles_IncludingTheOldSixColumnForm()
    {
        File.WriteAllLines(Path.Combine(_dir, "Flags.txt"), new[]
        {
            "$Flags", "3",
            "43.1,-74.2,10.5,20.25,1.2,1,4,Fence post",   // 8 columns
            "43.1,-74.2,-3,4,2,5",                         // old 6-column: color,id
            "garbage line",                                 // skipped
        });

        var loaded = FlagFilesService.Load(_dir);

        Assert.That(loaded, Has.Count.EqualTo(2));
        Assert.That(loaded[0].Name, Is.EqualTo("Fence post"));
        Assert.That(loaded[0].FlagColor, Is.EqualTo(FlagColor.Green));
        Assert.That(loaded[1].UniqueNumber, Is.EqualTo(5));
        Assert.That(loaded[1].FlagColor, Is.EqualTo(FlagColor.Yellow));
        Assert.That(loaded[1].Name, Is.EqualTo("Flag 5"));
    }

    [Test]
    public void Load_NoFile_IsEmpty() => Assert.That(FlagFilesService.Load(_dir), Is.Empty);
}
