// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// Block clustering per Hameed et al. 2013 / Höffmann 2024 review §5.7.
//
// Generates parallel guidance tracks across the WHOLE field (outer boundary
// with inner rings as holes), then groups the resulting track segments into
// "blocks" — contiguous obstacle-free regions that can each be covered by a
// simple back-and-forth boustrophedon. This sidesteps cellular decomposition
// entirely: no "cell with hole" problem, no sub-decomposition, no slivers.
//
// Algorithm:
//   1. Treat the whole field as one polygon-with-holes.
//   2. Generate swaths (multi-segment when an inner ring splits a track).
//   3. Walk swaths in sweep order. Each swath has m segments. When m
//      changes between consecutive swaths, close the current set of blocks
//      and open a new set sized m. Otherwise append seg[i] to block[i].
//   4. Tag each block with the inner-ring index it borders (if any), so
//      the stitcher can trigger inner-headland coverage when first
//      entering a ring-adjacent block.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

public static class BlockClusterer
{
    /// <summary>5cm tolerance for "vertex on inner-ring edge" — same as elsewhere in the pipeline.</summary>
    private const double OnRingTolerance = 0.05;

    /// <summary>
    /// Cluster the field into blocks ready for boustrophedon coverage.
    /// </summary>
    /// <param name="outerBoundary">Outer field boundary (inward-offset clip-boundary).</param>
    /// <param name="expandedInnerRings">Inner rings already outward-offset by HeadlandDistance.</param>
    /// <param name="swathHeading">Swath direction (radians, +N CW).</param>
    /// <param name="opWidth">Effective swath spacing.</param>
    /// <returns>Blocks in sweep order. Empty list if inputs are degenerate.</returns>
    public static List<Block> Cluster(
        List<Vec2> outerBoundary,
        IReadOnlyList<List<Vec2>> expandedInnerRings,
        double swathHeading,
        double opWidth)
    {
        var blocks = new List<Block>();
        if (outerBoundary == null || outerBoundary.Count < 3 || opWidth <= 0)
            return blocks;

        // 1. Generate all field-wide swaths in one shot by treating the whole
        //    field as a single virtual cell with the inner rings as holes.
        var virtualCell = new Cell
        {
            Polygon = outerBoundary,
            InnerRings = expandedInnerRings != null
                ? new List<List<Vec2>>(expandedInnerRings)
                : new List<List<Vec2>>(),
        };
        var swaths = new RotationalSwathGenerator().Generate(virtualCell, swathHeading, opWidth);
        if (swaths.Count == 0) return blocks;

        // 2. Compute sweep-coord (perpendicular to swath direction) for each
        //    swath so we can sort and walk them in sweep order. Sweep direction
        //    in the original frame: rotate (1, 0) by 90° around swath heading
        //    → sweep unit vector = (cos h, -sin h).
        double sx = Math.Cos(swathHeading);
        double sy = -Math.Sin(swathHeading);
        var ordered = new List<(GeneratedSwath swath, double sweepCoord)>();
        foreach (var sw in swaths)
        {
            if (sw.Segments.Count == 0) continue;
            var any = sw.Segments[0].Start;
            double sc = any.Easting * sx + any.Northing * sy;
            ordered.Add((sw, sc));
        }
        ordered.Sort((a, b) => a.sweepCoord.CompareTo(b.sweepCoord));

        // 3. Walk ordered swaths, clustering by segment count. When count
        //    changes, close the current block set and open a fresh one.
        List<Block> currentBlocks = new();
        int prevCount = -1;
        foreach (var (sw, sweepCoord) in ordered)
        {
            int count = sw.Segments.Count;
            if (count == 0) continue;

            if (count != prevCount)
            {
                currentBlocks = new List<Block>(count);
                for (int i = 0; i < count; i++)
                {
                    var b = new Block { Id = blocks.Count };
                    currentBlocks.Add(b);
                    blocks.Add(b);
                }
                prevCount = count;
            }

            for (int i = 0; i < count; i++)
            {
                var seg = sw.Segments[i];
                currentBlocks[i].Tracks.Add(new BlockTrack
                {
                    SwathIndex = sw.Index,
                    Start = seg.Start,
                    End = seg.End,
                    SweepCoord = sweepCoord,
                });
            }
        }

        // 4. Tag inner-ring touch. A block touches inner ring r if any of its
        //    track endpoints lies within OnRingTolerance of any edge of ring r.
        if (expandedInnerRings != null)
        {
            foreach (var b in blocks)
            {
                int hit = -1;
                for (int r = 0; r < expandedInnerRings.Count && hit < 0; r++)
                {
                    var ring = expandedInnerRings[r];
                    if (ring == null || ring.Count < 3) continue;
                    foreach (var t in b.Tracks)
                    {
                        if (PointTouchesRing(t.Start, ring) || PointTouchesRing(t.End, ring))
                        {
                            hit = r;
                            break;
                        }
                    }
                }
                b.InnerRingIndex = hit;
            }
        }

        return blocks;
    }

    private static bool PointTouchesRing(Vec2 p, List<Vec2> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            if (GeometryMath.PointToSegmentDistance(p, a, b) <= OnRingTolerance) return true;
        }
        return false;
    }
}
