// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgValoniaGPS.Services.Track;

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
    /// Spiral ordering: alternates taking clusters of tracks from the left and right
    /// edges, working inward toward the center. This distributes compaction evenly
    /// across the headland and avoids concentrating turns on one end.
    ///
    /// spiralSize=2, N=10: 0, 1, 9, 8, 2, 3, 7, 6, 4, 5
    /// spiralSize=3, N=9:  0, 1, 2, 8, 7, 6, 3, 4, 5
    /// </summary>
    private static List<int> SpiralOrder(List<int> swaths, int spiralSize)
    {
        int n = swaths.Count;
        var result = new List<int>(n);
        int left = 0;
        int right = n - 1;
        bool takeFromLeft = true;

        while (left <= right)
        {
            if (takeFromLeft)
            {
                // Take up to spiralSize from the left edge
                for (int i = 0; i < spiralSize && left <= right; i++)
                    result.Add(swaths[left++]);
            }
            else
            {
                // Take up to spiralSize from the right edge (in descending order)
                for (int i = 0; i < spiralSize && left <= right; i++)
                    result.Add(swaths[right--]);
            }
            takeFromLeft = !takeFromLeft;
        }

        return result;
    }

    /// <summary>
    /// Rotate elements left: moves element at 'start' to 'end-1',
    /// shifting everything between left by one position.
    /// Used by SnakeOrder.
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
