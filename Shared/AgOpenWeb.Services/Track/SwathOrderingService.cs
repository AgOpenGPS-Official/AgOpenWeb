// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// Swath ordering patterns for field coverage path planning.
/// Ported from Fields2Cover (https://github.com/Fields2Cover/Fields2Cover).
///
/// These algorithms take a number of parallel track offsets and return
/// the optimal order to traverse them for a given pattern.
/// </summary>
public enum SwathPattern
{
    /// <summary>Sequential back-and-forth: 0, 1, 2, 3, 4... (tight turns)</summary>
    Boustrophedon,

    /// <summary>Skip-and-fill: 0, 2, 4, 6, 8, 7, 5, 3, 1 (wide turns)</summary>
    Snake,

    /// <summary>Grouped spiral: works clusters of N tracks from outside in</summary>
    Spiral
}

/// <summary>
/// Generates track traversal sequences for different field coverage patterns.
/// </summary>
public static class SwathOrderingService
{
    /// <summary>
    /// Generate the track traversal sequence for a given pattern.
    /// </summary>
    /// <param name="trackCount">Total number of parallel tracks</param>
    /// <param name="pattern">The coverage pattern to use</param>
    /// <param name="spiralSize">Cluster size for Spiral pattern (default 2, min 2)</param>
    /// <returns>List of track indices in traversal order</returns>
    public static List<int> GenerateSequence(int trackCount, SwathPattern pattern, int spiralSize = 2)
    {
        if (trackCount <= 0) return new List<int>();
        if (trackCount == 1) return new List<int> { 0 };

        var indices = Enumerable.Range(0, trackCount).ToList();

        switch (pattern)
        {
            case SwathPattern.Boustrophedon:
                // Sequential: 0, 1, 2, 3, 4...
                return indices;

            case SwathPattern.Snake:
                return SnakeOrder(indices);

            case SwathPattern.Spiral:
                return SpiralOrder(indices, Math.Max(2, spiralSize));

            default:
                return indices;
        }
    }

    /// <summary>
    /// Generate the track traversal sequence for a given pattern,
    /// offset by a starting path number (e.g., if the tractor starts on path 3).
    /// </summary>
    /// <param name="minPath">Minimum path number in the cultivated area</param>
    /// <param name="maxPath">Maximum path number in the cultivated area</param>
    /// <param name="pattern">The coverage pattern to use</param>
    /// <param name="spiralSize">Cluster size for Spiral pattern</param>
    /// <returns>List of actual path numbers in traversal order</returns>
    public static List<int> GeneratePathSequence(int minPath, int maxPath, SwathPattern pattern, int spiralSize = 2)
    {
        int count = maxPath - minPath + 1;
        if (count <= 0) return new List<int>();

        var sequence = GenerateSequence(count, pattern, spiralSize);
        // Offset indices to actual path numbers
        return sequence.Select(i => i + minPath).ToList();
    }

    /// <summary>
    /// The skip-and-fill order of the passes <paramref name="minPath"/> to <paramref name="maxPath"/>
    /// with <paramref name="step"/> passes from one to the next (the rows skipped plus one), starting
    /// on <paramref name="current"/>. The passes fall into <paramref name="step"/> lanes (every
    /// step-th pass); the order works the tractor's lane from its pass to one end of the field, then
    /// each other lane end to end, out and back, at each end taking the lane whose first pass is
    /// nearest, and last the rest of the tractor's lane. With step 2 it is the <see cref="SwathPattern.Snake"/>
    /// order (0, 2, 4, 6, 8, 7, 5, 3, 1) turned to start on <paramref name="current"/>, as before.
    /// The way to start is the one that leaves the shorter moves across the field between lanes
    /// (with an odd step the rest of the tractor's lane is reached across the field unless it
    /// starts at an end); with no difference, as the snake: up on the lanes of even rank, down on
    /// the others. A pass outside the range is put first, followed by the order from
    /// <paramref name="minPath"/> up. Each pass of the range is in it once. The snake order
    /// skipped one row whatever the operator chose (a tester's field, 25 Nov 2026).
    /// </summary>
    public static List<int> SkipAndFillFrom(int minPath, int maxPath, int current, int step)
    {
        if (maxPath < minPath) return new List<int> { current };
        // A step as long as the range or longer puts each pass in a lane of its own: the same
        // order with the step held to the range, and no lane looked at that has no pass.
        step = Math.Clamp(step, 1, maxPath - minPath + 1);
        if (current < minPath || current > maxPath)
        {
            var fromStart = SkipAndFill(minPath, maxPath, minPath, +1, step);
            fromStart.Insert(0, current);
            return fromStart;
        }

        var up = SkipAndFill(minPath, maxPath, current, +1, step);
        var down = SkipAndFill(minPath, maxPath, current, -1, step);
        int upCost = CrossingCost(up, step), downCost = CrossingCost(down, step);
        if (upCost != downCost) return upCost < downCost ? up : down;
        return ((current - minPath) % step) % 2 == 0 ? up : down;
    }

    /// <summary>
    /// The skip-and-fill order from <paramref name="start"/>, its lane first worked the way of
    /// <paramref name="direction"/> (+1 to higher passes). See <see cref="SkipAndFillFrom"/>.
    /// </summary>
    private static List<int> SkipAndFill(int minPath, int maxPath, int start, int direction, int step)
    {
        var order = new List<int>(maxPath - minPath + 1);
        int Lane(int pass) => (pass - minPath) % step;
        int First(int lane) => minPath + lane;                                   // its lowest pass
        int Last(int lane) => First(lane) + (maxPath - First(lane)) / step * step; // its highest pass

        for (int p = start; p >= minPath && p <= maxPath; p += direction * step) order.Add(p);

        // The other lanes, each worked end to end, the way opposite to the one before.
        var lanes = Enumerable.Range(0, step).Where(l => l != Lane(start) && First(l) <= maxPath).ToList();
        int at = order[^1];
        int way = -direction;
        while (lanes.Count > 0)
        {
            // Its pass at this end of the field: the highest going down, the lowest going up.
            int lane = lanes.OrderBy(l => Math.Abs((way < 0 ? Last(l) : First(l)) - at)).ThenBy(l => l).First();
            lanes.Remove(lane);
            for (int p = way < 0 ? Last(lane) : First(lane); p >= minPath && p <= maxPath; p += way * step) order.Add(p);
            at = order[^1];
            way = -way;
        }

        // The rest of the tractor's lane, behind its start: from whichever end of it is nearer, the
        // field's end when both are as near (the snake: ..., 3, 1, 0, 2 from 4).
        var rest = new List<int>();
        for (int p = start - direction * step; p >= minPath && p <= maxPath; p -= direction * step) rest.Add(p);
        if (rest.Count > 0)
        {
            if (Math.Abs(rest[^1] - at) <= Math.Abs(rest[0] - at)) rest.Reverse();
            order.AddRange(rest);
        }
        return order;
    }

    /// <summary>How much further than <paramref name="step"/> passes the moves of an order go, in all.</summary>
    private static int CrossingCost(List<int> order, int step)
    {
        int cost = 0;
        for (int i = 1; i < order.Count; i++) cost += Math.Max(0, Math.Abs(order[i] - order[i - 1]) - step);
        return cost;
    }

    /// <summary>
    /// Snake ordering: alternates taking tracks from beginning and end.
    /// Produces pattern like: 0, 2, 4, 6, 8, 7, 5, 3, 1
    /// Every U-turn spans 2 track widths — no tight omega turns.
    ///
    /// Ported from Fields2Cover SnakeOrder::sortSwaths()
    /// </summary>
    private static List<int> SnakeOrder(List<int> swaths)
    {
        var result = new List<int>(swaths);
        int n = result.Count;

        int i;
        // Rotate inward from both ends
        for (i = 1; i < (n - 1) / 2 + 1; i++)
        {
            RotateLeft(result, i, n);
        }

        // Reverse the second half
        int reverseStart = i + 1;
        if (reverseStart < n)
        {
            result.Reverse(reverseStart, n - reverseStart);
        }

        // If odd number of swaths, do one more rotation
        if (n % 2 == 1)
        {
            RotateLeft(result, i, n);
        }

        return result;
    }

    /// <summary>
    /// Spiral ordering: works tracks in clusters, spiraling from outside to inside.
    /// With spiralSize=2: pairs of tracks worked from edges inward.
    /// With spiralSize=3: triples of tracks.
    ///
    /// Ported from Fields2Cover SpiralOrder::sortSwaths()
    /// </summary>
    private static List<int> SpiralOrder(List<int> swaths, int spiralSize)
    {
        var result = new List<int>(swaths);
        int n = result.Count;
        int spiralCount = n / spiralSize;

        for (int i = 0; i < spiralCount; i++)
        {
            SpiralGroup(result, i * spiralSize, spiralSize);
        }

        int remaining = n - spiralCount * spiralSize;
        if (remaining > 1)
        {
            SpiralGroup(result, spiralCount * spiralSize, remaining);
        }

        return result;
    }

    /// <summary>
    /// Spiral a single group by moving alternate elements to create
    /// an outside-in pattern within the group.
    /// </summary>
    private static void SpiralGroup(List<int> swaths, int offset, int size)
    {
        for (int j = (offset + 1) % 2; j < size; j += 2)
        {
            RotateLeft(swaths, offset + j, offset + size);
        }
    }

    /// <summary>
    /// Rotate elements left: moves element at 'start' to 'end-1',
    /// shifting everything between left by one position.
    /// Equivalent to std::rotate(begin+start, begin+start+1, begin+end)
    /// </summary>
    private static void RotateLeft(List<int> list, int start, int end)
    {
        if (start >= end - 1) return;
        int temp = list[start];
        for (int k = start; k < end - 1; k++)
        {
            list[k] = list[k + 1];
        }
        list[end - 1] = temp;
    }
}
