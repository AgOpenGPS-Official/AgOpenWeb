// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AgOpenWeb.RemoteServer;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// #604: the broadcast loop compares <see cref="ListsWatch"/>'s versions instead of reading the
/// Fields, Vehicles and Tools folders every tick. A version moves when something the lists
/// show changes on disk (the app's own writes and a folder copied in by hand alike), when a
/// panel asks (<c>lists.refresh</c>), and on any command where a folder can't be watched; it
/// stays put for writes the lists don't show (coverage tiles while driving).
/// </summary>
[TestFixture]
public class ListsWatchTests
{
    private string _root = null!, _fields = null!, _vehicles = null!, _tools = null!;
    private ListsWatch _watch = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-lists-" + Guid.NewGuid().ToString("N"));
        _fields = Directory.CreateDirectory(Path.Combine(_root, "Fields")).FullName;
        _vehicles = Directory.CreateDirectory(Path.Combine(_root, "Vehicles")).FullName;
        _tools = Directory.CreateDirectory(Path.Combine(_root, "Tools")).FullName;
        _watch = new ListsWatch();
    }

    [TearDown]
    public void TearDown()
    {
        _watch.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Start()
    {
        _watch.Watch(_fields, _vehicles, _tools);
        Assume.That(_watch.Watching, Is.True, "no FileSystemWatcher on this platform");
        Settle();
    }

    // Past the quiet time with no event, so the next read shows only what happens after this.
    private void Settle()
    {
        Thread.Sleep(ListsWatch.QuietMs + 400);
        _ = _watch.FieldsVersion; _ = _watch.ProfilesVersion;
    }

    private static bool Moves(Func<long> version, long from, int withinMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < withinMs)
        {
            if (version() != from) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool Stays(Func<long> version, long from, int forMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < forMs)
        {
            if (version() != from) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    [Test]
    public void AFieldFolderCopiedIn_MovesTheFieldsVersion()
    {
        Start();
        long v = _watch.FieldsVersion;
        Directory.CreateDirectory(Path.Combine(_fields, "North"));
        Assert.That(Moves(() => _watch.FieldsVersion, v), Is.True);
    }

    [Test]
    public void AJobSaved_MovesTheFieldsVersion()
    {
        var job = Directory.CreateDirectory(Path.Combine(_fields, "North", "jobs", "Spraying")).FullName;
        Start();
        long v = _watch.FieldsVersion;
        File.WriteAllText(Path.Combine(job, "job.json"), "{}");
        Assert.That(Moves(() => _watch.FieldsVersion, v), Is.True);
    }

    [Test]
    public void ACoverageTileWritten_LeavesTheListsAlone()
    {
        var tiles = Directory.CreateDirectory(Path.Combine(_fields, "North", "jobs", "Spraying", "coverage")).FullName;
        Start();
        long v = _watch.FieldsVersion;
        File.WriteAllBytes(Path.Combine(tiles, "t_3_4.png"), new byte[] { 1, 2, 3 });
        Assert.That(Stays(() => _watch.FieldsVersion, v), Is.True);
    }

    [Test]
    public void AProfileSaved_MovesTheProfilesVersion_NotTheFields()
    {
        Start();
        long p = _watch.ProfilesVersion, f = _watch.FieldsVersion;
        File.WriteAllText(Path.Combine(_tools, "Sprayer.json"), "{}");
        Assert.That(Moves(() => _watch.ProfilesVersion, p), Is.True);
        Assert.That(_watch.FieldsVersion, Is.EqualTo(f));
    }

    [Test]
    public void APanelOpening_MovesBothAtOnce()
    {
        Start();
        long f = _watch.FieldsVersion, p = _watch.ProfilesVersion;
        _watch.Refresh();
        Assert.That(_watch.FieldsVersion, Is.Not.EqualTo(f), "no wait: the panel is open");
        Assert.That(_watch.ProfilesVersion, Is.Not.EqualTo(p));
    }

    [Test]
    public void Watched_ACommandChangesNothing()
    {
        Start();
        long f = _watch.FieldsVersion;
        _watch.NoteCommand();
        Assert.That(Stays(() => _watch.FieldsVersion, f), Is.True);
    }

    [Test]
    public void NotWatched_ACommandCounts_AndARefreshStartsWatching()
    {
        var missing = Path.Combine(_root, "NotYet");
        _watch.Watch(missing, _vehicles, _tools);
        Assert.That(_watch.Watching, Is.False, "no such folder");
        Settle();

        long f = _watch.FieldsVersion;
        _watch.NoteCommand();
        Assert.That(Moves(() => _watch.FieldsVersion, f), Is.True, "a command counts while unwatched");

        Directory.CreateDirectory(missing);
        _watch.Refresh();
        _watch.Watch(missing, _vehicles, _tools);
        Assert.That(_watch.Watching, Is.True, "the refresh tried again");
    }

    [Test]
    public void TheSamePaths_TouchNothing()
    {
        Start();
        long f = _watch.FieldsVersion;
        for (int i = 0; i < 50; i++) _watch.Watch(_fields, _vehicles, _tools); // every tick
        Assert.That(Stays(() => _watch.FieldsVersion, f), Is.True);
    }

    [TestCase("field.geojson", true)]
    [TestCase("TrackLines.txt", true)]
    [TestCase("Boundary.txt", true)]
    [TestCase("elevation.csv", false)]
    [TestCase("recorded-paths.geojson", false)]
    public void WhatMatters_InAField(string file, bool matters)
    {
        var root = Path.GetFullPath(_fields);
        Assert.That(ListsWatch.Matters(root, Path.Combine(root, "North", file)), Is.EqualTo(matters));
        Assert.That(ListsWatch.Matters(root, Path.Combine(root, "North")), Is.True, "the field folder itself");
    }
}
