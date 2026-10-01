// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Save → reopen round trips of a job's coverage (tiles under coverage/).
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageSaveLoadTests
{
    private string _jobDir = null!;
    private ConfigurationStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _jobDir = Path.Combine(Path.GetTempPath(), $"agopenweb_covsave_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_jobDir);
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_jobDir)) Directory.Delete(_jobDir, recursive: true);
    }

    // Paint a north-running strip [e0, e1] x [n0, n1] through the normal section path.
    private static void PaintStrip(CoverageMapService svc, double e0, double e1, double n0, double n1)
    {
        svc.StartMapping(0, new Vec2(e0, n0), new Vec2(e1, n0));
        for (double n = n0 + 1; n <= n1; n += 1)
            svc.AddCoveragePoint(0, new Vec2(e0, n), new Vec2(e1, n));
        svc.StopMapping(0);
    }

    // The display-layer pixel under a world point (0 = unpainted).
    private static ushort DisplayPixelAt(CoverageMapService svc, double e, double n)
    {
        var b = svc.DisplayBoundsWorld!.Value;
        var d = svc.DisplayDimensions!.Value;
        int x = (int)Math.Floor((e - b.MinE) / d.CellSize);
        int y = (int)Math.Floor((n - b.MinN) / d.CellSize);
        return svc.GetDisplayPixels()![(long)y * d.Width + x];
    }

    [Test]
    public void Reopen_without_expansion_keeps_coverage_in_place()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);

        Assert.That(reopened.DisplayBoundsWorld, Is.EqualTo(svc.DisplayBoundsWorld), "bounds unchanged by the load");
        Assert.That(reopened.IsPointCovered(-7, 0), Is.True);
        Assert.That(reopened.IsPointCovered(30, 0), Is.False);
        Assert.That(reopened.TotalWorkedArea, Is.EqualTo(svc.TotalWorkedArea).Within(0.01));
    }

    [Test]
    public void Reopen_after_bounds_expansion_keeps_coverage_in_place()
    {
        // Field opened with the boundary's bounds (as MainViewModel does: boundary box + 50 m).
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);       // inside, no expansion
        PaintStrip(svc, 60, 66, -20, 20);        // within 50 m of the east edge → bounds grow
        Assume.That(svc.DisplayBoundsWorld!.Value.MaxE, Is.GreaterThan(100), "bounds expanded");
        svc.SaveToFile(_jobDir);

        // Reopen: same boundary, so the same starting bounds — the saved file is wider.
        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);

        Assert.Multiple(() =>
        {
            Assert.That(reopened.IsPointCovered(-7, 0), Is.True, "strip painted before the expansion");
            Assert.That(reopened.IsPointCovered(63, 0), Is.True, "strip painted after the expansion");
            Assert.That(reopened.IsPointCovered(30, 0), Is.False, "unpainted ground");
            Assert.That(reopened.IsPointCovered(-40, 0), Is.False, "unpainted ground");
        });
    }

    [Test]
    public void Reopen_after_westward_expansion_keeps_coverage_in_place()
    {
        // Growing west/south moves the grid's min corner, unlike growing east/north.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 4, 10, -20, 20);         // inside, no expansion
        PaintStrip(svc, -66, -60, -20, 20);      // within 50 m of the west edge → bounds grow west
        Assume.That(svc.DisplayBoundsWorld!.Value.MinE, Is.LessThan(-100), "bounds expanded west");
        svc.SaveToFile(_jobDir);

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);

        Assert.Multiple(() =>
        {
            Assert.That(reopened.IsPointCovered(7, 0), Is.True, "strip painted before the expansion");
            Assert.That(reopened.IsPointCovered(-63, 0), Is.True, "strip painted after the expansion");
            Assert.That(reopened.IsPointCovered(30, 0), Is.False, "unpainted ground");
            Assert.That(reopened.IsPointCovered(-30, 0), Is.False, "unpainted ground");
            Assert.That(reopened.TotalWorkedArea, Is.EqualTo(svc.TotalWorkedArea).Within(0.01));
            Assert.That(DisplayPixelAt(reopened, 7, 0), Is.Not.Zero, "display: strip painted before the expansion");
            Assert.That(DisplayPixelAt(reopened, -63, 0), Is.Not.Zero, "display: strip painted after the expansion");
            Assert.That(DisplayPixelAt(reopened, 30, 0), Is.Zero, "display: unpainted ground");
        });
    }

    [Test]
    public void Reopen_field_without_boundary_keeps_coverage()
    {
        // No boundary: bounds come from the first GPS fix (500 m box), and on reopen
        // MainViewModel clears the bounds, loads the job, then waits for GPS again.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBoundsFromPosition(1000, 2000);
        PaintStrip(svc, 1000, 1006, 1980, 2020);
        svc.SaveToFile(_jobDir);

        var reopened = new CoverageMapService(_store);
        reopened.ClearFieldBounds();
        reopened.LoadFromFile(_jobDir);
        // First fix after reopen — MainViewModel.EnsureCoverageBoundsInitialized only sets
        // bounds from the position when none are set yet.
        if (!reopened.IsFieldBoundsSet)
            reopened.SetFieldBoundsFromPosition(1003, 2000);

        Assert.Multiple(() =>
        {
            Assert.That(reopened.DisplayDimensions, Is.EqualTo(svc.DisplayDimensions), "same grid as when saved");
            Assert.That(reopened.BitmapDimensions, Is.EqualTo(svc.BitmapDimensions), "same grid as when saved");
            Assert.That(reopened.IsPointCovered(1003, 2000), Is.True, "painted strip");
            Assert.That(reopened.IsPointCovered(1030, 2000), Is.False, "unpainted ground");
            Assert.That(reopened.TotalWorkedArea, Is.EqualTo(svc.TotalWorkedArea).Within(0.01));
        });
    }

    [Test]
    public void Save_replaces_files_without_leaving_a_temp_file()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);
        PaintStrip(svc, 20, 26, -20, 20);
        svc.SaveToFile(_jobDir);

        Assert.That(Directory.GetFiles(_jobDir, "*.tmp", SearchOption.AllDirectories), Is.Empty);

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);
        Assert.That(reopened.IsPointCovered(23, 0), Is.True, "second save replaced the first");
    }

    [Test]
    public void Saving_while_painting_through_expansions_writes_loadable_files()
    {
        // The GPS cycle paints (and grows the bounds) while the autosave runs on a pool thread.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);

        using var done = new CancellationTokenSource();
        var saver = Task.Run(() =>
        {
            int saves = 0;
            while (!done.IsCancellationRequested || saves == 0)
            {
                svc.SaveToFile(_jobDir);
                saves++;
            }
            return saves;
        });

        // Drive west in 6 m passes: crosses several 50 m-from-edge expansions.
        for (double e = -10; e > -900; e -= 6)
            PaintStrip(svc, e - 6, e, -10, 10);
        done.Cancel();
        Assert.That(saver.Wait(TimeSpan.FromSeconds(60)), Is.True, "saver finished");
        Assert.That(saver.Result, Is.GreaterThan(0));

        svc.SaveToFile(_jobDir);
        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.IsPointCovered(-13, 0), Is.True);
            Assert.That(reopened.IsPointCovered(-850, 0), Is.True);
            Assert.That(reopened.IsPointCovered(-500, 50), Is.False);
        });
    }

    [Test]
    public void Reopen_does_not_grow_bounds_when_display_cells_overhang()
    {
        // 1420 m isn't a whole number of 0.35 m display cells: the display grid overhangs
        // the field by part of a cell, which must not count as saved coverage outside it.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(0, 1420, 0, 1420);
        Assume.That(svc.DisplayDimensions!.Value.CellSize, Is.GreaterThan(0.1), "coarse display grid");
        svc.MarkRectangleCovered(100, 112, 100, 200);
        svc.SaveToFile(_jobDir);

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(0, 1420, 0, 1420);
        reopened.LoadFromFile(_jobDir);

        Assert.That(reopened.BitmapDimensions, Is.EqualTo(svc.BitmapDimensions));
        Assert.That(reopened.DisplayBoundsWorld, Is.EqualTo(svc.DisplayBoundsWorld));
    }

    // Back-date every coverage file so a rewrite is visible in its timestamp.
    private DateTime BackdateFiles()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var f in Directory.GetFiles(_jobDir, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, old);
        return old;
    }

    private string ManifestPath => Path.Combine(_jobDir, "coverage", "manifest.json");
    private DateTime ManifestWriteTime() => File.GetLastWriteTimeUtc(ManifestPath);

    [Test]
    public void Save_skips_the_write_when_nothing_changed()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);
        var old = BackdateFiles();

        svc.SaveToFile(_jobDir);
        Assert.That(ManifestWriteTime(), Is.EqualTo(old), "unchanged: not rewritten");

        PaintStrip(svc, 20, 26, -20, 20);
        svc.SaveToFile(_jobDir);
        Assert.That(ManifestWriteTime(), Is.Not.EqualTo(old), "painted: rewritten");
    }

    [Test]
    public void Save_writes_after_clear_after_a_load_and_to_another_job()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);

        // Delete Applied Area
        var old = BackdateFiles();
        svc.ClearAll();
        svc.SaveToFile(_jobDir);
        Assert.That(ManifestWriteTime(), Is.Not.EqualTo(old), "cleared: rewritten");

        // A reopened job is already on disk: nothing to write until something changes
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);
        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);
        old = BackdateFiles();
        reopened.SaveToFile(_jobDir);
        Assert.That(ManifestWriteTime(), Is.EqualTo(old), "reopened, unchanged: not rewritten");

        // Same coverage, different job folder
        var otherJob = Path.Combine(_jobDir, "other");
        Directory.CreateDirectory(otherJob);
        reopened.SaveToFile(otherJob);
        Assert.That(File.Exists(Path.Combine(otherJob, "coverage", "manifest.json")), Is.True, "other job: written");

        // A manifest removed behind our back is written again
        File.Delete(ManifestPath);
        reopened.SaveToFile(_jobDir);
        Assert.That(File.Exists(ManifestPath), Is.True, "missing manifest: rewritten");
    }

    [Test]
    public void Display_round_trip_keeps_each_section_colour()
    {
        _store.Tool.IsMultiColoredSections = true; // each section paints its own colour
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        svc.StartMapping(0, new Vec2(-10, -20), new Vec2(-4, -20));
        svc.StartMapping(1, new Vec2(20, -20), new Vec2(26, -20));
        for (double n = -19; n <= 20; n += 1)
        {
            svc.AddCoveragePoint(0, new Vec2(-10, n), new Vec2(-4, n));
            svc.AddCoveragePoint(1, new Vec2(20, n), new Vec2(26, n));
        }
        svc.StopMapping(0);
        svc.StopMapping(1);
        ushort a = DisplayPixelAt(svc, -7, 0), b = DisplayPixelAt(svc, 23, 0);
        Assume.That(a, Is.Not.Zero.And.Not.EqualTo(b), "two distinct colours painted");
        svc.SaveToFile(_jobDir);

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);

        Assert.That(DisplayPixelAt(reopened, -7, 0), Is.EqualTo(a));
        Assert.That(DisplayPixelAt(reopened, 23, 0), Is.EqualTo(b));
        Assert.That(DisplayPixelAt(reopened, 60, 0), Is.Zero);
    }
}
