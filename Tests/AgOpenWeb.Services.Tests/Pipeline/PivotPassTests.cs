// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Pipeline;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// A water pivot's passes, guidance line and guidelines all come from the pass cache, as circles
/// about its centre: offsetting its points like a curve's would collapse the inner passes.
/// </summary>
[TestFixture]
public class PivotPassTests
{
    [Test]
    public void Pivot_passes_are_circles_about_its_centre()
    {
        var c = new Vec3(-30, 45, 0);
        var pivot = AgOpenWeb.Models.Track.Track.FromPivot("P", c, 50);
        var cache = new OffsetPassCache();

        foreach (double d in new[] { 3.0, 9.0, 45.0, -3.0, -33.0 })
        {
            var (pts, removed) = cache.Get(pivot, d);
            Assert.That(removed, Is.Zero);
            Assert.That(pts.All(p => Math.Abs(Math.Sqrt(Math.Pow(p.Easting - c.Easting, 2) + Math.Pow(p.Northing - c.Northing, 2)) - Math.Abs(50 - d)) < 1e-6),
                $"offset {d}");
        }
    }
}
