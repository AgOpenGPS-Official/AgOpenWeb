// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The worked-area perimeter the web client strokes over the coverage raster
/// (CoverageMapService.GetCoveragePerimeter).
/// </summary>
[TestFixture, NonParallelizable]
public class CoveragePerimeterTests
{
    private static CoverageMapService PaintStrip(double e0, double e1, double n0, double n1)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        var svc = new CoverageMapService(store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        svc.StartMapping(0, new Vec2(e0, n0), new Vec2(e1, n0));
        for (double n = n0 + 1; n <= n1; n += 1)
            svc.AddCoveragePoint(0, new Vec2(e0, n), new Vec2(e1, n));
        svc.StopMapping(0);
        return svc;
    }

    [Test]
    public void Perimeter_line_is_half_a_stroke_inside_the_swept_edge()
    {
        // The client strokes the line 0.6 m wide: on the edge itself it painted 0.3 m past
        // each end of the tool (#325).
        var lines = PaintStrip(10, 16, 0, 20).GetCoveragePerimeter();

        Assert.That(lines, Has.Count.EqualTo(2));
        var eastings = lines.SelectMany(l => l).Select(p => p.Easting).Distinct().OrderBy(e => e).ToArray();
        Assert.That(eastings, Is.EqualTo(new[] { 10.3, 15.7 }).Within(1e-9));
    }

    [Test]
    public void Perimeter_of_a_section_narrower_than_the_stroke_runs_down_its_middle()
    {
        var lines = PaintStrip(10, 10.4, 0, 20).GetCoveragePerimeter();

        Assert.That(lines.SelectMany(l => l).Select(p => p.Easting), Is.All.EqualTo(10.2).Within(1e-9));
    }
}
