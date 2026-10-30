// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Tram;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// An AB line's tram lines are clipped to the fence where they cross its edges, not by testing
/// each of their 2 m points against it: the same lines, without a field's worth of polygon tests
/// per track select (seconds on a slow tablet, on the host loop).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class TramABClipTests
{
    // A star-shaped fence: convex or concave, so a line can cross it several times.
    private static List<Vec3> RandomFence(Random rng)
    {
        int n = rng.Next(3, 40);
        double cx = rng.NextDouble() * 200 - 100, cy = rng.NextDouble() * 200 - 100;
        var pts = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n, r = 50 + rng.NextDouble() * 400;
            pts.Add(new Vec3(cx + r * Math.Sin(a), cy + r * Math.Cos(a), 0));
        }
        return pts;
    }

    [Test]
    public void ClippedLines_MatchTheOffsetPoints_KeptInsideTheFence()
    {
        var rng = new Random(446);
        for (int run = 0; run < 5000; run++)
        {
            var fence = RandomFence(rng);
            var ab = new AgOpenWeb.Models.Track.Track
            {
                Points = new List<Vec3>
                {
                    new(rng.NextDouble() * 600 - 300, rng.NextDouble() * 600 - 300, 0),
                    new(rng.NextDouble() * 600 - 300, rng.NextDouble() * 600 - 300, 0),
                }
            };
            double offset = rng.NextDouble() * 800 - 400;

            var expected = TramLineService.OffsetPointsSegmented(ab, offset, fence);
            var actual = TramLineService.OffsetTrackLaterallySegmented(ab, offset, fence);

            // The same points in the same order. The clip may also split a line where it dips out
            // of the fence between two of its 2 m points, which the point test runs straight across:
            // the only extra points are the ends of the pieces either side of such a dip.
            var want = expected.SelectMany(x => x).ToList();
            int w = 0, splits = 0;
            for (int s = 0; s < actual.Count; s++)
                for (int k = 0; k < actual[s].Count; k++)
                {
                    var p = actual[s][k];
                    if (w < want.Count && Near(p, want[w])) { w++; continue; }
                    bool end = k == 0 || k == actual[s].Count - 1;
                    Assert.That(end, Is.True, $"run {run}: piece {s} point {k} is not in the point-test lines");
                    splits++;
                }
            Assert.That(w, Is.EqualTo(want.Count), $"run {run}: every point-test point is kept");
            Assert.That(actual.Count - expected.Count, Is.LessThanOrEqualTo(splits), $"run {run}: pieces");
        }
    }

    private static bool Near(Vec2 a, Vec2 b) =>
        Math.Abs(a.Easting - b.Easting) < 1e-6 && Math.Abs(a.Northing - b.Northing) < 1e-6;

    // The FZ-G1 report: a 600 m field, 12 m trams, a 590 m AB line. Selecting the line built
    // about 400k polygon tests before; it needs none now.
    [Test]
    public void ParallelTramLines_ForAnABLine_TestNoPointsAgainstTheFence()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Tram.TramWidth = 12;
        var service = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, config);
        service.SetBoundaryFence(new List<Vec3> { new(0, -100, 0), new(250, -100, 0), new(250, 530, 0), new(0, 530, 0) });
        var ab = new AgOpenWeb.Models.Track.Track { Points = new List<Vec3> { new(67.7, -75.59, 0), new(163.71, 513.52, 0) } };

        var before = WorkCounters.Take();
        service.GenerateParallelTramLines(ab, 1517);
        var work = WorkCounters.Take() - before;

        Assert.That(service.ParallelTramLines, Is.Not.Empty);
        Assert.That(work.PolygonTests, Is.Zero);
    }
}
