// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #175: changing the display quality (Ultra → High) repaints the display layer from the
/// 0.1 m detection bits. The repaint passed bitmap-local cell indices where absolute ones
/// were expected, so every pixel was shifted by the field's min corner: the worked area
/// vanished and only the edge strokes remained.
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageResolutionChangeTests
{
    [Test]
    public void Quality_change_keeps_painted_coverage_where_it_was()
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Display.DisplayResolutionMultiplier = 1.0;           // Ultra
        var svc = new CoverageMapService(store);
        svc.SetFieldBounds(-320, 0, -200, 0);                      // negative min corner, like the report

        // Paint a 6 m wide strip from N=-150 to N=-100 at E=-160..-154.
        svc.StartMapping(0, new Vec2(-160, -150), new Vec2(-154, -150));
        for (double n = -149; n <= -100; n += 1)
            svc.AddCoveragePoint(0, new Vec2(-160, n), new Vec2(-154, n));
        svc.StopMapping(0);

        var before = WorldBox(svc);
        Assume.That(before.count, Is.GreaterThan(0), "strip painted");

        store.Display.DisplayResolutionMultiplier = 1.5;           // High
        svc.RebuildDisplayForResolutionChange();
        var after = WorldBox(svc);

        Assert.Multiple(() =>
        {
            Assert.That(after.count, Is.GreaterThan(0), "coverage must still be painted after the quality change");
            double tol = svc.DisplayDimensions!.Value.CellSize * 2;
            Assert.That(after.minE, Is.EqualTo(before.minE).Within(tol));
            Assert.That(after.maxE, Is.EqualTo(before.maxE).Within(tol));
            Assert.That(after.minN, Is.EqualTo(before.minN).Within(tol));
            Assert.That(after.maxN, Is.EqualTo(before.maxN).Within(tol));
        });
    }

    /// <summary>World-coordinate bounding box of display cells that are painted AND visible.</summary>
    private static (int count, double minE, double maxE, double minN, double maxN) WorldBox(CoverageMapService svc)
    {
        var d = svc.DisplayDimensions!.Value;
        var o = svc.DisplayBoundsWorld!.Value;
        int count = 0; double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var (x, y, _, alpha) in svc.GetPaintedDisplayCells())
        {
            if (alpha < 128) continue;
            double e = o.MinE + (x + 0.5) * d.CellSize, n = o.MinN + (y + 0.5) * d.CellSize;
            count++; minE = Math.Min(minE, e); maxE = Math.Max(maxE, e); minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
        }
        return (count, minE, maxE, minN, maxN);
    }
}
