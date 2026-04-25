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
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.PathPlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Optimal cell visit order via Held-Karp dynamic programming. Per cell, four
/// entry-corner options (the two ends of the first swath, the two ends of the
/// last swath); each entry implies an exit corner determined by swath-count
/// parity. State space: 2^N · N · 4 — tractable for N ≤ ~20.
///
/// Inter-cell transit cost = shortest Dubins between exit pose of previous
/// cell and entry pose of next. Internal cost is constant per cell (sum of
/// swath lengths + inter-swath turns), so it doesn't affect ordering — only
/// sliver-rounds the total.
/// </summary>
public class CellTraversalPlanner
{
    public class CellVisit
    {
        public int CellId;
        public int EntryCorner;   // 0..3
        public int ExitCorner;
        public Vec2 EntryPosition;
        public Vec2 ExitPosition;
        public double EntryHeading;
        public double ExitHeading;
        public List<Models.Track.Track> SwathsInTraversalOrder = new();
    }

    /// <summary>
    /// Plan an optimal visit order through the cells given precomputed swaths
    /// per cell. Returns a list of CellVisit in traversal order.
    /// </summary>
    public List<CellVisit> Plan(
        List<Cell> cells,
        IReadOnlyDictionary<int, List<Models.Track.Track>> cellSwaths,
        Vec2 startPosition,
        double startHeading,
        double swathHeading,
        double turningRadius)
    {
        int n = cells.Count;
        if (n == 0) return new List<CellVisit>();

        // Build per-cell info.
        var info = new CellInfo[n];
        for (int i = 0; i < n; i++)
        {
            var swaths = cellSwaths.TryGetValue(cells[i].Id, out var s) ? s : new List<Models.Track.Track>();
            info[i] = new CellInfo(cells[i].Id, swaths, swathHeading);
        }

        // Held-Karp DP.
        // dp[mask][cell][exitCorner] = min cost ending at this state.
        // To simplify storage, we index exit corner 0..3.
        int totalStates = (1 << n) * n * 4;
        var dp = new double[totalStates];
        var parent = new int[totalStates];
        for (int i = 0; i < totalStates; i++) { dp[i] = double.PositiveInfinity; parent[i] = -1; }

        int Idx(int mask, int cell, int exit) => (mask * n + cell) * 4 + exit;

        // Base case: visit a single cell from the start position.
        for (int i = 0; i < n; i++)
        {
            if (!info[i].IsValid) continue;
            int singleMask = 1 << i;
            for (int entry = 0; entry < 4; entry++)
            {
                int exit = info[i].ExitForEntry(entry);
                double startCost = DubinsLength(
                    startPosition, startHeading,
                    info[i].EntryPos(entry), info[i].EntryHeading(entry),
                    turningRadius);
                if (double.IsInfinity(startCost)) continue;
                double cost = startCost; // internal cost is uniform across entries; ignore for ordering.
                int idx = Idx(singleMask, i, exit);
                if (cost < dp[idx])
                {
                    dp[idx] = cost;
                    parent[idx] = entry; // record entry choice for reconstruction
                }
            }
        }

        // Transitions: extend from one cell to another.
        for (int mask = 1; mask < (1 << n); mask++)
        {
            for (int prev = 0; prev < n; prev++)
            {
                if ((mask & (1 << prev)) == 0) continue;
                for (int prevExit = 0; prevExit < 4; prevExit++)
                {
                    int prevIdx = Idx(mask, prev, prevExit);
                    if (double.IsInfinity(dp[prevIdx])) continue;

                    for (int next = 0; next < n; next++)
                    {
                        if ((mask & (1 << next)) != 0) continue;
                        if (!info[next].IsValid) continue;
                        for (int nextEntry = 0; nextEntry < 4; nextEntry++)
                        {
                            double transit = DubinsLength(
                                info[prev].ExitPos(prevExit), info[prev].ExitHeading(prevExit),
                                info[next].EntryPos(nextEntry), info[next].EntryHeading(nextEntry),
                                turningRadius);
                            if (double.IsInfinity(transit)) continue;
                            int nextExit = info[next].ExitForEntry(nextEntry);
                            int newMask = mask | (1 << next);
                            int newIdx = Idx(newMask, next, nextExit);
                            double newCost = dp[prevIdx] + transit;
                            if (newCost < dp[newIdx])
                            {
                                dp[newIdx] = newCost;
                                // Pack (prev cell, prev exit, next entry) into parent slot.
                                parent[newIdx] = (prev * 4 + prevExit) * 4 + nextEntry;
                            }
                        }
                    }
                }
            }
        }

        // Find best final state covering all cells.
        int fullMask = (1 << n) - 1;
        double bestCost = double.PositiveInfinity;
        int bestCell = -1, bestExit = -1;
        for (int c = 0; c < n; c++)
        {
            if (!info[c].IsValid) continue;
            for (int e = 0; e < 4; e++)
            {
                double v = dp[Idx(fullMask, c, e)];
                if (v < bestCost) { bestCost = v; bestCell = c; bestExit = e; }
            }
        }

        if (bestCell < 0) return new List<CellVisit>();

        // Reconstruct path by walking parents back to the base case.
        var trail = new List<(int cell, int entry, int exit)>();
        int curMask = fullMask;
        int curCell = bestCell;
        int curExit = bestExit;
        while (curCell != -1)
        {
            int pIdx = Idx(curMask, curCell, curExit);
            int p = parent[pIdx];
            if (curMask == (1 << curCell))
            {
                // Base case — p is the entry corner directly.
                int entry = p;
                trail.Add((curCell, entry, curExit));
                break;
            }
            // Decode (prev_cell * 4 + prev_exit) * 4 + next_entry.
            int nextEntry = p % 4;
            int prevExit = (p / 4) % 4;
            int prevCell = p / 16;
            trail.Add((curCell, nextEntry, curExit));
            curMask ^= (1 << curCell);
            curCell = prevCell;
            curExit = prevExit;
        }

        trail.Reverse();
        var visits = new List<CellVisit>(trail.Count);
        foreach (var (cellIdx, entry, exit) in trail)
        {
            visits.Add(new CellVisit
            {
                CellId = info[cellIdx].CellId,
                EntryCorner = entry,
                ExitCorner = exit,
                EntryPosition = info[cellIdx].EntryPos(entry),
                ExitPosition = info[cellIdx].ExitPos(exit),
                EntryHeading = info[cellIdx].EntryHeading(entry),
                ExitHeading = info[cellIdx].ExitHeading(exit),
                SwathsInTraversalOrder = info[cellIdx].SwathOrder(entry),
            });
        }
        return visits;
    }

    private static double DubinsLength(
        Vec2 fromPos, double fromHeading,
        Vec2 toPos, double toHeading,
        double turningRadius)
    {
        var dubins = new DubinsPathService(turningRadius);
        var paths = dubins.GenerateAllPaths(
            new Vec3(fromPos.Easting, fromPos.Northing, fromHeading),
            new Vec3(toPos.Easting, toPos.Northing, toHeading));
        if (paths.Count == 0) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        foreach (var (_, _, length) in paths)
        {
            if (length < best) best = length;
        }
        return best;
    }

    /// <summary>
    /// Per-cell info: 4 corner positions/headings, swath count parity,
    /// boustrophedon swath ordering for each entry option.
    ///
    /// Corner indexing:
    ///   0 = first-swath low-perp end  (low sweep, low perp)
    ///   1 = first-swath high-perp end (low sweep, high perp)
    ///   2 = last-swath low-perp end   (high sweep, low perp)
    ///   3 = last-swath high-perp end  (high sweep, high perp)
    /// </summary>
    private sealed class CellInfo
    {
        public int CellId;
        public bool IsValid;

        private readonly Vec2[] _corner = new Vec2[4];
        private readonly double[] _heading = new double[4];

        // ExitForEntry[entry] given by parity:
        //   N odd:  0→3, 1→2, 2→1, 3→0  (diagonal pairs)
        //   N even: 0→2, 1→3, 2→0, 3→1  (same-perp pairs across swath axis)
        private readonly int[] _exitForEntry = new int[4];

        // Pre-built boustrophedon swath orderings, indexed by entry corner.
        private readonly List<Models.Track.Track>[] _swathOrder = new List<Models.Track.Track>[4];

        public CellInfo(int cellId, List<Models.Track.Track> swaths, double swathHeading)
        {
            CellId = cellId;
            int n = swaths.Count;
            if (n == 0)
            {
                IsValid = false;
                return;
            }
            IsValid = true;

            // Each swath has 2 points; CellSwathGenerator already sorts them
            // so Points[0] is low-perp and Points[1] is high-perp.
            var first = swaths[0];
            var last = swaths[n - 1];

            _corner[0] = new Vec2(first.Points[0].Easting, first.Points[0].Northing);
            _corner[1] = new Vec2(first.Points[1].Easting, first.Points[1].Northing);
            _corner[2] = new Vec2(last.Points[0].Easting, last.Points[0].Northing);
            _corner[3] = new Vec2(last.Points[1].Easting, last.Points[1].Northing);

            // Entry headings: at corners 0 and 2 we head toward high-perp (swathHeading);
            // at corners 1 and 3 we head toward low-perp (swathHeading + π).
            // Swath direction is the perpendicular of the sweep — and it's the same
            // angle whether reading from low-perp or high-perp end, just sign-flipped.
            _heading[0] = swathHeading;
            _heading[1] = swathHeading + Math.PI;
            _heading[2] = swathHeading;
            _heading[3] = swathHeading + Math.PI;

            bool nOdd = (n % 2) == 1;
            if (nOdd)
            {
                _exitForEntry[0] = 3;
                _exitForEntry[1] = 2;
                _exitForEntry[2] = 1;
                _exitForEntry[3] = 0;
            }
            else
            {
                _exitForEntry[0] = 2;
                _exitForEntry[1] = 3;
                _exitForEntry[2] = 0;
                _exitForEntry[3] = 1;
            }

            // Boustrophedon orderings for each entry option.
            // Entry 0: drive swath_0 low→high perp, then alternate.
            // Entry 1: drive swath_0 high→low perp, then alternate.
            // Entry 2: drive swath_(N-1) low→high perp first (reverse swath order from then on).
            // Entry 3: drive swath_(N-1) high→low perp first.
            //
            // Within a cell we don't actually need to flip the swath waypoints
            // here — the route stitcher handles travel direction by orienting
            // the swath segment per its IsReverse flag. We just need the
            // per-cell swath SEQUENCE.
            _swathOrder[0] = new List<Models.Track.Track>(swaths);
            _swathOrder[1] = new List<Models.Track.Track>(swaths);
            _swathOrder[2] = new List<Models.Track.Track>(swaths);
            _swathOrder[3] = new List<Models.Track.Track>(swaths);
            _swathOrder[2].Reverse();
            _swathOrder[3].Reverse();
        }

        public Vec2 EntryPos(int entry) => _corner[entry];
        public Vec2 ExitPos(int exit) => _corner[exit];
        public double EntryHeading(int entry) => _heading[entry];
        public double ExitHeading(int exit) => _heading[exit];
        public int ExitForEntry(int entry) => _exitForEntry[entry];
        public List<Models.Track.Track> SwathOrder(int entry) => _swathOrder[entry];
    }
}
