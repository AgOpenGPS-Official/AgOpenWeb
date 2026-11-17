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
        // The painter starts first and the snapshot only once it paints, so every paint measured
        // overlaps the walk. A paint that a garbage collection ran in is not measured: its time
        // is the collector's, not the lock's (the snapshot's list is a few hundred MB).
        using var painting = new ManualResetEventSlim();
        var walk = new Stopwatch();
        var snapshot = Task.Run(() =>
        {
            painting.Wait();
            walk.Start();
            int count = svc.GetPaintedDisplayCells().Count;
            walk.Stop();
            return count;
        });
        double n = -100;
        while (!snapshot.IsCompleted)
        {
            n += 0.3;
            int gc = GC.CollectionCount(0);
            long t0 = Stopwatch.GetTimestamp();
            svc.AddCoveragePoint(1, new Vec2(100, n), new Vec2(106, n));
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (GC.CollectionCount(0) == gc) longest = Math.Max(longest, ms);
            if (!painting.IsSet) painting.Set();
            else if (walk.IsRunning) paints++;
            Thread.Sleep(1);
        }
        double walkMs = walk.Elapsed.TotalMilliseconds;
        svc.StopMapping(1);

        TestContext.Out.WriteLine($"snapshot {walkMs:0} ms, {snapshot.Result:N0} cells, {paints} paints, longest paint {longest:0.0} ms");
        Assume.That(walkMs, Is.GreaterThan(30), "the snapshot must take long enough to show the wait");
        Assert.That(snapshot.Result, Is.GreaterThan(1_000_000));
        Assert.That(paints, Is.GreaterThan(10), "the painter must paint while the snapshot walks");
        Assert.That(longest, Is.LessThan(10),
            $"a paint waited {longest:0.0} ms during a {walkMs:0} ms snapshot ({paints} paints)");
    }

    [Test]
    public void Snapshots_WhileTheFieldIsRegridded_NeverFail()
    {
        // The alpha is counted outside the lock: read field by field there, a regrid could pair
        // the new detection width with the old array and index past it, and the browser's seed
        // failed. It is counted from the detection grid as read together under the lock.
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Display.DisplayResolutionMultiplier = 1.0;
        var svc = new CoverageMapService(store);
        svc.SetFieldBounds(-60, 60, -60, 60);
        for (int k = 0; k < 10; k++)
        {
            double e = -60 + k * 6;
            svc.StartMapping(0, new Vec2(e, -59), new Vec2(e + 6, -59));
            for (double y = -58; y <= 59; y += 1)
                svc.AddCoveragePoint(0, new Vec2(e, y), new Vec2(e + 6, y));
            svc.StopMapping(0);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Exception? failure = null;
        int snapshots = 0;
        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested) { svc.GetPaintedDisplayCells(); snapshots++; }
            }
            catch (Exception ex) { failure = ex; }
        });
        int k2 = 0;
        while (!cts.IsCancellationRequested && failure == null)
        {
            double grow = 20 * (k2++ % 4);
            svc.SetFieldBounds(-60 - grow, 60 + grow, -60 - grow / 2, 60 + grow);
        }
        reader.Wait();

        TestContext.Out.WriteLine($"snapshots {snapshots}, regrids {k2}");
        Assert.That(failure, Is.Null, $"a snapshot failed while the field was regridded ({snapshots} taken): {failure}");
        Assert.That(snapshots, Is.GreaterThan(5));
    }
}
