// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The tiled coverage format (CoverageTileStore): a save rewrites only the tiles painted since
/// the last one, tiles survive bounds growth, and damage costs a tile rather than the job.
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageTileTests
{
    private string _jobDir = null!;
    private ConfigurationStore _store = null!;
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _jobDir = Path.Combine(Path.GetTempPath(), $"agopenweb_covtiles_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_jobDir);
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_jobDir)) Directory.Delete(_jobDir, recursive: true);
    }

    private string CoverageDir => Path.Combine(_jobDir, "coverage");
    private string DetectDir => Path.Combine(CoverageDir, "d");

    private static void PaintStrip(CoverageMapService svc, double e0, double e1, double n0, double n1)
    {
        svc.StartMapping(0, new Vec2(e0, n0), new Vec2(e1, n0));
        for (double n = n0 + 1; n <= n1; n += 1)
            svc.AddCoveragePoint(0, new Vec2(e0, n), new Vec2(e1, n));
        svc.StopMapping(0);
    }

    private CoverageMapService Reopen(double minE = -100, double maxE = 100, double minN = -100, double maxN = 100)
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(minE, maxE, minN, maxN);
        svc.LoadFromFile(_jobDir);
        return svc;
    }

    private Dictionary<string, DateTime> BackdateTiles()
    {
        var files = Directory.GetFiles(CoverageDir, "*.tile", SearchOption.AllDirectories);
        foreach (var f in files) File.SetLastWriteTimeUtc(f, Old);
        return files.ToDictionary(f => Path.GetRelativePath(CoverageDir, f), _ => Old);
    }

    private List<string> RewrittenTiles() =>
        Directory.GetFiles(CoverageDir, "*.tile", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) != Old)
            .Select(f => Path.GetRelativePath(CoverageDir, f).Replace('\\', '/'))
            .OrderBy(f => f).ToList();

    [Test]
    public void Tiles_are_world_anchored_and_only_hold_coverage()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 10, 16, 10, 20);          // all inside detection tile (0,0): [0, 102.4) m
        svc.SaveToFile(_jobDir);

        Assert.That(Directory.GetFiles(DetectDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "0_0.tile" }));
        var displayDirs = Directory.GetDirectories(CoverageDir, "s*");
        Assert.That(displayDirs, Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(displayDirs[0]), Is.Not.Empty);
        Assert.That(File.Exists(Path.Combine(CoverageDir, "manifest.json")), Is.True);
    }

    [Test]
    public void Save_rewrites_only_the_tiles_painted_since_the_last_save()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-300, 300, -300, 300);
        PaintStrip(svc, -250, -244, -250, -200);  // tile (-3,-3)
        PaintStrip(svc, 10, 16, 10, 40);          // tile (0,0)
        PaintStrip(svc, 210, 216, 210, 240);      // tile (2,2)
        svc.SaveToFile(_jobDir);
        Assume.That(Directory.GetFiles(DetectDir), Has.Length.EqualTo(3));
        BackdateTiles();

        PaintStrip(svc, 30, 36, 30, 40);          // tile (0,0) only
        svc.SaveToFile(_jobDir);

        var rewritten = RewrittenTiles();
        Assert.That(rewritten.Where(f => f.StartsWith("d/")), Is.EqualTo(new[] { "d/0_0.tile" }));
        Assert.That(rewritten.Count(f => f.StartsWith('s')), Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(2),
            "display: the tile(s) under the new strip");

        var reopened = Reopen(-300, 300, -300, 300);
        Assert.That(reopened.IsPointCovered(-247, -230), Is.True);
        Assert.That(reopened.IsPointCovered(33, 35), Is.True);
        Assert.That(reopened.IsPointCovered(213, 230), Is.True);
    }

    [Test]
    public void Growing_the_bounds_leaves_written_tiles_alone()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        svc.SaveToFile(_jobDir);
        var cellBefore = svc.DisplayDimensions!.Value.CellSize;
        BackdateTiles();

        PaintStrip(svc, -66, -60, -20, 20);       // within 50 m of the west edge → bounds grow west
        Assume.That(svc.DisplayBoundsWorld!.Value.MinE, Is.LessThan(-100), "bounds expanded");
        Assume.That(svc.DisplayDimensions!.Value.CellSize, Is.EqualTo(cellBefore), "same display cell size");
        svc.SaveToFile(_jobDir);

        Assert.That(RewrittenTiles().Where(f => f.StartsWith("d/")), Is.EqualTo(new[] { "d/-1_-1.tile", "d/-1_0.tile" }),
            "only the tiles under the new strip; the first strip's tiles weren't renumbered");
        var reopened = Reopen();
        Assert.That(reopened.IsPointCovered(-7, 0), Is.True);
        Assert.That(reopened.IsPointCovered(-63, 0), Is.True);
    }

    [Test]
    public void A_damaged_tile_costs_that_tile_not_the_job()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-300, 300, -300, 300);
        PaintStrip(svc, -250, -244, -250, -200);  // tile (-3,-3)
        PaintStrip(svc, 10, 16, 10, 40);          // tile (0,0)
        svc.SaveToFile(_jobDir);

        // Truncate one tile (a torn write), flip a byte in nothing else.
        var torn = Path.Combine(DetectDir, "0_0.tile");
        var bytes = File.ReadAllBytes(torn);
        File.WriteAllBytes(torn, bytes.AsSpan(0, bytes.Length - 5).ToArray());

        var reopened = Reopen(-300, 300, -300, 300);
        Assert.That(reopened.IsPointCovered(-247, -230), Is.True, "intact tile loads");
        Assert.That(reopened.IsPointCovered(13, 30), Is.False, "torn tile skipped");
    }

    [Test]
    public void A_flipped_payload_byte_fails_the_crc()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 10, 16, 10, 40);
        svc.SaveToFile(_jobDir);

        var tile = Path.Combine(DetectDir, "0_0.tile");
        var bytes = File.ReadAllBytes(tile);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(tile, bytes);

        Assert.That(Reopen().IsPointCovered(13, 30), Is.False);
    }

    [Test]
    public void Lost_manifest_is_rebuilt_from_the_tiles()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(1000, 1500, 2000, 2500);   // no-boundary style box away from the origin
        PaintStrip(svc, 1210, 1216, 2210, 2260);
        svc.SaveToFile(_jobDir);
        File.Delete(Path.Combine(CoverageDir, "manifest.json"));
        foreach (var bak in Directory.GetFiles(CoverageDir, "manifest.json*")) File.Delete(bak);

        var reopened = new CoverageMapService(_store);
        reopened.LoadFromFile(_jobDir);                // no bounds yet: they come from the tiles

        Assert.That(reopened.IsPointCovered(1213, 2230), Is.True);
        Assert.That(reopened.TotalWorkedArea, Is.EqualTo(svc.TotalWorkedArea).Within(svc.TotalWorkedArea * 0.2),
            "recounted from the cells (no overlap, so close to the painted area)");
    }

    [Test]
    public void Delete_applied_area_removes_the_tiles()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 10, 16, 10, 40);
        svc.SaveToFile(_jobDir);

        svc.ClearAll();
        svc.SaveToFile(_jobDir);

        Assert.That(Directory.GetFiles(CoverageDir, "*.tile", SearchOption.AllDirectories), Is.Empty);
        var reopened = Reopen();
        Assert.That(reopened.IsPointCovered(13, 30), Is.False, "cleared coverage doesn't come back");
        Assert.That(reopened.TotalWorkedArea, Is.Zero);
    }

    [Test]
    public void Display_quality_change_moves_display_tiles_and_keeps_detection_tiles()
    {
        _store.Display.DisplayResolutionMultiplier = 1.0;
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 10, 16, 10, 40);
        svc.SaveToFile(_jobDir);
        var oldDisplayDir = Directory.GetDirectories(CoverageDir, "s*").Single();
        BackdateTiles();

        _store.Display.DisplayResolutionMultiplier = 2.5;
        svc.RebuildDisplayForResolutionChange();
        svc.SaveToFile(_jobDir);

        var displayDirs = Directory.GetDirectories(CoverageDir, "s*");
        Assert.That(displayDirs, Has.Length.EqualTo(1));
        Assert.That(displayDirs[0], Is.Not.EqualTo(oldDisplayDir), "new cell size, new folder; old one removed");
        Assert.That(RewrittenTiles().Where(f => f.StartsWith("d/")), Is.Empty, "detection untouched");

        var reopened = Reopen();
        Assert.That(reopened.IsPointCovered(13, 30), Is.True);
        Assert.That(reopened.DisplayDimensions!.Value.CellSize, Is.EqualTo(svc.DisplayDimensions!.Value.CellSize));
    }

    [Test]
    public void Display_tiles_at_another_cell_size_are_resampled_on_load()
    {
        _store.Display.DisplayResolutionMultiplier = 1.0;
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, 10, 16, 10, 40);
        svc.SaveToFile(_jobDir);

        _store.Display.DisplayResolutionMultiplier = 4.0;   // reopen at a coarser quality
        var reopened = Reopen();
        var b = reopened.DisplayBoundsWorld!.Value;
        var d = reopened.DisplayDimensions!.Value;
        int x = (int)Math.Floor((13 - b.MinE) / d.CellSize), y = (int)Math.Floor((30 - b.MinN) / d.CellSize);
        Assert.That(d.CellSize, Is.EqualTo(0.4).Within(1e-9));
        Assert.That(reopened.GetDisplayPixels()![y * d.Width + x], Is.Not.Zero, "display resampled from the saved tiles");

        // The next save writes display tiles at the new size and drops the old folder.
        PaintStrip(reopened, 50, 56, 10, 20);
        reopened.SaveToFile(_jobDir);
        Assert.That(Directory.GetDirectories(CoverageDir, "s*"), Has.Length.EqualTo(1));
    }

    [Test]
    public void Legacy_bin_files_are_imported_then_removed_after_the_first_tiled_save()
    {
        // A pre-tile job: coverage_detect.bin (COVD) with a 6 m x 40 m block at E 10..16, N 10..50.
        WriteLegacyDetection(-100, -100, 2000, 2000, (e, n) => e >= 10 && e < 16 && n >= 10 && n < 50);

        var svc = Reopen();
        Assert.That(svc.IsPointCovered(13, 30), Is.True, "imported from coverage_detect.bin");

        svc.SaveToFile(_jobDir);
        Assert.That(File.Exists(Path.Combine(_jobDir, "coverage_detect.bin")), Is.False, "removed once tiles exist");
        Assert.That(Directory.GetFiles(DetectDir), Is.Not.Empty);

        var again = Reopen();
        Assert.That(again.IsPointCovered(13, 30), Is.True, "now read from the tiles");
        Assert.That(again.IsPointCovered(30, 30), Is.False);
    }

    [Test]
    public void Random_tile_is_stored_raw_and_uniform_tile_as_runs()
    {
        var store = new CoverageTileStore(_jobDir);
        var rle = new CoverageTileStore.RunBuffer();
        var random = new byte[CoverageTileStore.DetectTileBytes];
        new Random(42).NextBytes(random);
        var uniform = new byte[CoverageTileStore.DetectTileBytes];
        uniform.AsSpan(1000, 50_000).Fill(0xFF);

        long rawSize = store.WriteDetectionTile(0, 0, random, rle);
        long runSize = store.WriteDetectionTile(1, 0, uniform, rle);
        store.CommitTiles();

        Assert.That(rawSize, Is.LessThanOrEqualTo(CoverageTileStore.DetectTileBytes + 32), "never bigger than raw + header");
        Assert.That(runSize, Is.LessThan(100));
        var back = new byte[CoverageTileStore.DetectTileBytes];
        Assert.That(store.TryReadDetectionTile(0, 0, back), Is.True);
        Assert.That(back, Is.EqualTo(random));
        Assert.That(store.TryReadDetectionTile(1, 0, back), Is.True);
        Assert.That(back, Is.EqualTo(uniform));
    }

    // Writes a COVD v1 file the way pre-tile builds did: header + [run:u16][value:u8] runs.
    private void WriteLegacyDetection(double originE, double originN, int width, int height, Func<double, double, bool> covered)
    {
        var bits = new byte[((long)width * height + 7) / 8];
        long count = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (covered(originE + (x + 0.5) * 0.1, originN + (y + 0.5) * 0.1))
                {
                    long i = (long)y * width + x;
                    bits[i / 8] |= (byte)(1 << (int)(i % 8));
                    count++;
                }
        using var w = new BinaryWriter(File.Create(Path.Combine(_jobDir, "coverage_detect.bin")));
        w.Write("COVD".ToCharArray());
        w.Write((byte)1);
        w.Write(0.1f);
        w.Write(originE);
        w.Write(originN);
        w.Write((uint)width);
        w.Write((uint)height);
        w.Write(count * 0.01);
        for (int i = 0; i < bits.Length;)
        {
            int run = 1;
            while (i + run < bits.Length && bits[i + run] == bits[i] && run < 65535) run++;
            w.Write((ushort)run);
            w.Write(bits[i]);
            i += run;
        }
    }
}
