// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>
/// A contiguous set of parallel track segments that can be covered as a
/// single back-and-forth boustrophedon. Per Hameed et al. 2013 §2.1.2, a
/// block is the natural unit of coverage when the field is treated as a
/// non-decomposed region: parallel tracks are generated across the whole
/// field, clipped at obstacles into segments, and segments at the same
/// "split index" across consecutive tracks form a block.
///
/// For a field with one central obstacle and N-S swaths going W→E:
///   - Tracks left of obstacle: 1 segment each → block A
///   - Tracks at obstacle x-range: 2 segments each → blocks B (south of
///     obstacle) and C (north of obstacle)
///   - Tracks right of obstacle: 1 segment each → block D
/// </summary>
public class Block
{
    /// <summary>Stable id assigned in clustering order (first encountered = 0).</summary>
    public int Id { get; set; }

    /// <summary>
    /// One segment per track in the block, ordered by SweepCoord (perpendicular
    /// to swath direction). Each segment is one swath's contribution to this
    /// block — its start/end are the points where the parallel track entered
    /// and exited the obstacle-free region this block represents.
    /// </summary>
    public List<BlockTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Index of the inner ring (in the original ring list) this block borders,
    /// or -1 if the block doesn't touch any inner ring. Used to trigger
    /// inner-headland coverage when the planner first enters a ring-adjacent
    /// block.
    /// </summary>
    public int InnerRingIndex { get; set; } = -1;
}

/// <summary>One track's segment in a <see cref="Block"/>.</summary>
public class BlockTrack
{
    public int SwathIndex { get; set; }

    /// <summary>Track segment start (one endpoint where the parallel track entered the block's region).</summary>
    public Vec2 Start { get; set; }

    /// <summary>Track segment end (the other endpoint).</summary>
    public Vec2 End { get; set; }

    /// <summary>Sweep-coord (perpendicular to swath direction). Used to sort tracks within a block.</summary>
    public double SweepCoord { get; set; }
}
