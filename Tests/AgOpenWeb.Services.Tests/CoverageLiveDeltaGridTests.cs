// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The live coverage stream (new cells, drained at 10 Hz) and the full snapshot must address
/// the same display grid. The stream indexed cells from the raw field corner while the grid,
/// the snapshot and the per-cell alpha use the corner snapped to the world grid, so a live
/// client got each cell's alpha for its neighbour. Driving south or west, the last value a
/// row received was a part-covered one: the worked area stayed see-through on screen until
/// the client reconnected.
/// </summary>
[TestFixture, NonParallelizable]
public class CoverageLiveDeltaGridTests
{
    [TestCase(-1, TestName = "Driving south")]
    [TestCase(+1, TestName = "Driving north")]
    public void Live_stream_ends_up_matching_the_snapshot(int direction)
    {
        var store = new ConfigurationStore();
        ConfigurationStore.SetInstance(store);
        store.Display.DisplayResolutionMultiplier = 2.5;           // 0.625 m cells, as a tablet uses
        var svc = new CoverageMapService(store);
        svc.SetFieldBounds(-450.904, 944.604, -152.129, 600.330);  // corner not on the cell grid
        double cell = svc.DisplayDimensions!.Value.CellSize;

        // A live client: every drained cell replaces what it had for that cell (BlendMode.Src).
        var live = new Dictionary<(int, int), int>();
        void Drain()
        {
            foreach (var (x, y, _) in svc.GetNewCoverageBitmapCellsServer(cell))
                live[(x, y)] = svc.GetDisplayCellAlpha255(x, y);
        }

        // A 16 m strip, 60 m long, laid in 0.3 m steps with a drain after each (10 Hz at ~11 km/h).
        double start = direction > 0 ? 100 : 160;
        svc.StartMapping(0, new Vec2(40, start), new Vec2(56, start));
        for (double d = 0.3; d <= 60; d += 0.3)
        {
            svc.AddCoveragePoint(0, new Vec2(40, start + direction * d), new Vec2(56, start + direction * d));
            Drain();
        }
        svc.StopMapping(0);
        Drain();

        var snapshot = svc.GetPaintedDisplayCells().ToDictionary(c => (c.X, c.Y), c => c.Alpha);
        Assume.That(snapshot.Count(c => c.Value == 255), Is.GreaterThan(1000), "strip painted");

        var wrong = snapshot.Where(c => !live.TryGetValue(c.Key, out int a) || a != c.Value).ToList();
        Assert.That(wrong, Is.Empty,
            $"{wrong.Count} of {snapshot.Count} cells differ between the live stream and the snapshot");
    }
}
