// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A browser connecting gets the coverage snapshot (<see cref="CoverageMapService.GetPaintedDisplayCells"/>),
/// while the 100 Hz control tick paints coverage under the same lock. The walk took the lock band
/// by band and let go of it between bands only for an instant; the lock is not fair, so the
/// painter, woken each time just too late, waited the whole walk: 101 ms control ticks on a 22 MB
/// seed (Working Day scenarios, CONTROL-TICK-OVER-10-MS, measured on the harness host). Under the
/// lock the walk now only copies a band's pixels.
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageSnapshotLockTests
{
    [Test]
    public void Painting_DuringASnapshot_WaitsAtMostABand()
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Display.DisplayResolutionMultiplier = 1.0;
        var svc = new CoverageMapService(store);
        svc.SetFieldBoundsFromPosition(0, 0);
        // 40 passes of 6 m, 240 m long: about 5.8 million display cells at 0.1 m.
        for (int k = 0; k < 40; k++)
        {
            double e = -120 + k * 6;
            svc.StartMapping(0, new Vec2(e, -120), new Vec2(e + 6, -120));
            for (double y = -119; y <= 120; y += 1)
                svc.AddCoveragePoint(0, new Vec2(e, y), new Vec2(e + 6, y));
            svc.StopMapping(0);
        }
        svc.GetPaintedDisplayCells();   // warm up

        svc.StartMapping(1, new Vec2(100, -100), new Vec2(106, -100));
        double longest = 0;
        int paints = 0;
        var walk = Stopwatch.StartNew();
        var snapshot = Task.Run(() => svc.GetPaintedDisplayCells().Count);
        double n = -100;
        while (!snapshot.IsCompleted)
        {
            n += 0.03;
            long t0 = Stopwatch.GetTimestamp();
            svc.AddCoveragePoint(1, new Vec2(100, n), new Vec2(106, n));
            longest = Math.Max(longest, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            paints++;
            Thread.Sleep(1);
        }
        double walkMs = walk.Elapsed.TotalMilliseconds;
        svc.StopMapping(1);

        TestContext.Out.WriteLine($"snapshot {walkMs:0} ms, {snapshot.Result:N0} cells, {paints} paints, longest paint {longest:0.0} ms");
        Assume.That(walkMs, Is.GreaterThan(30), "the snapshot must take long enough to show the wait");
        Assert.That(snapshot.Result, Is.GreaterThan(1_000_000));
        Assert.That(longest, Is.LessThan(10),
            $"a paint waited {longest:0.0} ms during a {walkMs:0} ms snapshot ({paints} paints)");
    }
}
