// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The display grid stays within the largest texture the web client reported. A field with no
/// boundary starts at 500 x 500 m, which at Ultra (0.1 m) is 5000 x 5000 pixels: more than a
/// 4096 texture, so the web client fell back to a 2D canvas copied whole several times over and
/// the tablet's web view was killed for memory.
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageDisplayTextureLimitTests
{
    private static CoverageMapService NewService(double multiplier)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Display.DisplayResolutionMultiplier = multiplier;
        return new CoverageMapService(store);
    }

    [Test]
    public void No_limit_keeps_the_ultra_grid()
    {
        var svc = NewService(1.0);
        svc.SetFieldBoundsFromPosition(0, 0); // 500 x 500 m, as for a field with no boundary
        var d = svc.DisplayDimensions!.Value;
        Assert.That(d.CellSize, Is.EqualTo(0.1).Within(1e-9));
        Assert.That(Math.Max(d.Width, d.Height), Is.GreaterThan(4096));
    }

    [Test]
    public void A_field_opened_after_the_limit_fits_the_texture()
    {
        var svc = NewService(1.0);
        svc.LimitDisplaySide(4096);
        svc.SetFieldBoundsFromPosition(0, 0);
        var d = svc.DisplayDimensions!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(d.Width, Is.LessThanOrEqualTo(4096));
            Assert.That(d.Height, Is.LessThanOrEqualTo(4096));
            Assert.That(d.CellSize, Is.EqualTo(0.15).Within(1e-9), "the next step up from 0.1 m");
        });
    }

    [Test]
    public void An_open_field_is_regridded_and_keeps_its_coverage()
    {
        var svc = NewService(1.0);
        svc.SetFieldBoundsFromPosition(0, 0);
        svc.StartMapping(0, new Vec2(-3, -50), new Vec2(3, -50));
        for (double n = -49; n <= 50; n += 1)
            svc.AddCoveragePoint(0, new Vec2(-3, n), new Vec2(3, n));
        svc.StopMapping(0);
        Assume.That(svc.GetPaintedDisplayCells().Count, Is.GreaterThan(0));

        svc.LimitDisplaySide(4096);

        var d = svc.DisplayDimensions!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(Math.Max(d.Width, d.Height), Is.LessThanOrEqualTo(4096));
            Assert.That(svc.GetPaintedDisplayCells().Count, Is.GreaterThan(0), "repainted from the detection bits");
        });
    }

    [Test]
    public void A_bigger_limit_later_does_not_lift_a_smaller_one()
    {
        var svc = NewService(1.0);
        svc.LimitDisplaySide(4096);
        svc.LimitDisplaySide(16384); // a desktop browser connecting afterwards
        svc.SetFieldBoundsFromPosition(0, 0);
        Assert.That(Math.Max(svc.DisplayDimensions!.Value.Width, svc.DisplayDimensions!.Value.Height),
            Is.LessThanOrEqualTo(4096));
    }

    [Test]
    public void A_grid_already_within_the_limit_is_left_alone()
    {
        var svc = NewService(2.5); // Medium: 0.25 m, 2000 x 2000
        svc.SetFieldBoundsFromPosition(0, 0);
        double before = svc.DisplayDimensions!.Value.CellSize;
        svc.LimitDisplaySide(4096);
        Assert.That(svc.DisplayDimensions!.Value.CellSize, Is.EqualTo(before).Within(1e-12));
    }

    [Test]
    public void Bounds_growing_past_the_limit_take_a_coarser_cell()
    {
        var svc = NewService(1.0);
        svc.LimitDisplaySide(4096);
        svc.SetFieldBounds(0, 400, 0, 1500); // a long field: 1500 m needs about 0.37 m cells
        var d = svc.DisplayDimensions!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(d.Height, Is.LessThanOrEqualTo(4096));
            Assert.That(d.CellSize, Is.EqualTo(0.5).Within(1e-9));
        });
    }
}
