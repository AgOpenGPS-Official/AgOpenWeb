// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A boundary edited with a job open keeps the job's worked area. The view model gives the
/// coverage map the outer boundary's box at every boundary edit (an obstacle's choice, one
/// added or deleted, another outer boundary); once the grid had grown past that box, the map
/// was emptied and the next save deleted the job's tiles (a tester's sprayer job, a day's
/// passes gone after one obstacle deleted).
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageBoundaryEditTests
{
    private string _jobDir = null!;
    private ConfigurationStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _jobDir = Path.Combine(Path.GetTempPath(), $"agopenweb_covedit_{Guid.NewGuid():N}");
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
    public void The_same_box_given_again_after_the_grid_grew_keeps_the_worked_area()
    {
        // The tester's case: the field's box, a pass near its edge (the grid grows by itself),
        // then a boundary edit that leaves the outer boundary where it was.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        PaintStrip(svc, 80, 86, -20, 20); // within 50 m of the east edge: the grid grows
        Assert.That(svc.GetCoverageBounds()!.Value.MaxE, Is.GreaterThan(100), "the grid grew while painting");
        double area = svc.TotalWorkedArea;
        int cells = svc.PatchCount;

        svc.SetFieldBounds(-100, 100, -100, 100);

        Assert.That(svc.IsPointCovered(-7, 0), Is.True);
        Assert.That(svc.IsPointCovered(83, 0), Is.True);
        Assert.That(svc.IsPointCovered(30, 0), Is.False);
        Assert.That(svc.PatchCount, Is.EqualTo(cells));
        Assert.That(svc.TotalWorkedArea, Is.EqualTo(area).Within(0.01));
        Assert.That(DisplayPixelAt(svc, -7, 0), Is.Not.EqualTo(0), "the painted map is kept too");
    }

    [Test]
    public void The_job_saved_after_a_boundary_edit_still_has_its_tiles()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        PaintStrip(svc, 80, 86, -20, 20);
        svc.SaveToFile(_jobDir);
        double area = svc.TotalWorkedArea;

        svc.SetFieldBounds(-100, 100, -100, 100); // a boundary edit
        svc.SaveToFile(_jobDir);                  // the autosave after it

        var reopened = new CoverageMapService(_store);
        reopened.SetFieldBounds(-100, 100, -100, 100);
        reopened.LoadFromFile(_jobDir);
        Assert.That(reopened.IsPointCovered(-7, 0), Is.True);
        Assert.That(reopened.IsPointCovered(83, 0), Is.True);
        Assert.That(reopened.TotalWorkedArea, Is.EqualTo(area).Within(0.01));
    }

    [Test]
    public void Another_outer_boundary_keeps_the_worked_area_and_the_grid_holds_both()
    {
        // The outer boundary replaced by a small one far inside the field: nothing painted is lost.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -40, -34, -40, 40);
        double area = svc.TotalWorkedArea;

        svc.SetFieldBounds(0, 30, 0, 30);

        Assert.That(svc.IsPointCovered(-37, -30), Is.True, "painted outside the new box");
        Assert.That(svc.TotalWorkedArea, Is.EqualTo(area).Within(0.01));
        var b = svc.GetCoverageBounds()!.Value;
        Assert.That(b.MinE, Is.LessThanOrEqualTo(-100));
        Assert.That(b.MaxN, Is.GreaterThanOrEqualTo(100));
    }

    [Test]
    public void A_larger_box_grows_the_grid_and_the_new_ground_can_be_painted()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);

        svc.SetFieldBounds(-100, 400, -100, 100);

        Assert.That(svc.IsPointCovered(-7, 0), Is.True);
        Assert.That(DisplayPixelAt(svc, -7, 0), Is.Not.EqualTo(0));
        var b = svc.GetCoverageBounds()!.Value;
        Assert.That(b.MaxE, Is.GreaterThanOrEqualTo(400));
        PaintStrip(svc, 300, 306, -20, 20);
        Assert.That(svc.IsPointCovered(303, 0), Is.True);
    }

    [Test]
    public void A_box_moved_by_less_than_a_cell_keeps_the_worked_area()
    {
        // The grid keeps its size here, and an array of the same size is reused: it must not be
        // cleared while it is the one the worked area is copied from.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);
        double area = svc.TotalWorkedArea;
        int cells = svc.PatchCount;

        svc.SetFieldBounds(-100.02, 100, -100, 100);

        Assert.That(svc.IsPointCovered(-7, 0), Is.True);
        Assert.That(svc.PatchCount, Is.EqualTo(cells));
        Assert.That(svc.TotalWorkedArea, Is.EqualTo(area).Within(0.01));
        Assert.That(DisplayPixelAt(svc, -7, 0), Is.Not.EqualTo(0));
    }

    [Test]
    public void An_empty_grid_takes_the_new_box_as_it_is()
    {
        // Nothing painted: the grid is the field's box again, as before (no grid kept larger
        // than the field for nothing).
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);

        svc.SetFieldBounds(0, 30, 0, 30);

        var b = svc.GetCoverageBounds()!.Value;
        Assert.That(b.MinE, Is.EqualTo(0).Within(0.01));
        Assert.That(b.MaxE, Is.EqualTo(30).Within(0.01));
        Assert.That(b.MinN, Is.EqualTo(0).Within(0.01));
        Assert.That(b.MaxN, Is.EqualTo(30).Within(0.01));
    }

    [Test]
    public void Another_field_still_starts_empty()
    {
        // A field closing clears the bounds first: the next field's box is a new, empty grid.
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        PaintStrip(svc, -10, -4, -20, 20);

        svc.ClearFieldBounds();
        svc.ClearAll();
        svc.SetFieldBounds(-50, 150, -100, 100);

        Assert.That(svc.IsPointCovered(-7, 0), Is.False);
        Assert.That(svc.PatchCount, Is.EqualTo(0));
        Assert.That(svc.TotalWorkedArea, Is.EqualTo(0));
        var b = svc.GetCoverageBounds()!.Value;
        Assert.That(b.MinE, Is.EqualTo(-50).Within(0.01));
    }
}
