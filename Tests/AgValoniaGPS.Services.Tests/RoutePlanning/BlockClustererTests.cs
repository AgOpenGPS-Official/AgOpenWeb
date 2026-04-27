// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class BlockClustererTests
{
    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    [Test]
    public void Rectangle_NoObstacle_OneBlock()
    {
        // Plain 100×100 field, swaths north-south, opWidth 5. All tracks have
        // exactly 1 segment → all in one block.
        var outer = Rect(0, 100, 0, 100);
        var blocks = BlockClusterer.Cluster(outer, new List<List<Vec2>>(), swathHeading: 0, opWidth: 5);

        Assert.That(blocks.Count, Is.EqualTo(1));
        Assert.That(blocks[0].Tracks.Count, Is.EqualTo(20));
        Assert.That(blocks[0].InnerRingIndex, Is.EqualTo(-1));
    }

    [Test]
    public void RectangleWithCenterPond_FourBlocks()
    {
        // 100×100 field with a pond in the middle (40-60 in both dimensions).
        // Swaths north-south. Tracks at x∈[0,40] and x∈[60,100] each have
        // 1 segment. Tracks at x∈[40,60] are split by the pond into 2
        // segments (south half, north half).
        // Expected: 4 blocks
        //   block 0: tracks x∈[0,40]   (single segment, no ring touch)
        //   block 1: south halves of split tracks (touches ring 0)
        //   block 2: north halves of split tracks (touches ring 0)
        //   block 3: tracks x∈[60,100] (single segment, no ring touch)
        var outer = Rect(0, 100, 0, 100);
        var pond = Rect(40, 60, 40, 60);

        var blocks = BlockClusterer.Cluster(outer, new List<List<Vec2>> { pond },
            swathHeading: 0, opWidth: 5);

        Assert.That(blocks.Count, Is.EqualTo(4),
            $"expected 4 blocks (left, south-of-pond, north-of-pond, right); got {blocks.Count}");

        // Two blocks should touch the inner ring; two shouldn't.
        int touchCount = blocks.Count(b => b.InnerRingIndex == 0);
        int noTouchCount = blocks.Count(b => b.InnerRingIndex == -1);
        Assert.That(touchCount, Is.EqualTo(2));
        Assert.That(noTouchCount, Is.EqualTo(2));
    }

    [Test]
    public void Pond_BlocksAreContiguousByTrackIndex()
    {
        // The four blocks should partition the 20 tracks contiguously by
        // sweep order. Concretely: block 0 has tracks[0..7] (left of pond),
        // blocks 1+2 share tracks[8..11] (split by pond), block 3 has
        // tracks[12..19] (right of pond). Inner-ring blocks both have the
        // same track indices.
        var outer = Rect(0, 100, 0, 100);
        var pond = Rect(40, 60, 40, 60);
        var blocks = BlockClusterer.Cluster(outer, new List<List<Vec2>> { pond },
            swathHeading: 0, opWidth: 5);

        var leftBlock = blocks.First(b => b.InnerRingIndex == -1 && b.Tracks[0].SweepCoord < 40);
        var rightBlock = blocks.First(b => b.InnerRingIndex == -1 && b.Tracks[0].SweepCoord > 60);
        var ringBlocks = blocks.Where(b => b.InnerRingIndex == 0).ToList();

        Assert.That(ringBlocks.Count, Is.EqualTo(2));
        // Ring blocks should have identical track indices (each split track contributes both)
        var leftTracks = ringBlocks[0].Tracks.Select(t => t.SwathIndex).OrderBy(x => x).ToList();
        var rightTracks = ringBlocks[1].Tracks.Select(t => t.SwathIndex).OrderBy(x => x).ToList();
        Assert.That(leftTracks, Is.EqualTo(rightTracks),
            "south-half and north-half blocks should share the same track set");
    }
}
