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
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Boustrophedon Cellular Decomposition sweep-line algorithm.
/// Processes events in sweep order, maintaining a list of active edges
/// (edges that intersect the current sweep line) sorted by perpendicular
/// position. Each event mutates the active list and may open/close cells
/// or create Reeb-graph adjacencies.
///
/// Outputs are a list of finished cells (each a closed polygon) and a Reeb
/// graph encoding inter-cell adjacency at split/merge events.
/// </summary>
internal sealed class BcdSweep
{
    /// <summary>
    /// One edge currently intersected by the sweep line. Tracks which cell
    /// it bounds on its lower-perpendicular side (LeftCellId) and higher
    /// side (RightCellId). One of these may be -1 when the edge faces the
    /// outside of the polygon-with-holes.
    /// </summary>
    private sealed class ActiveEdge
    {
        public Vec2 LowerEnd = default!;   // Endpoint at lower sweep coord
        public Vec2 UpperEnd = default!;   // Endpoint at higher sweep coord
        public int CellOnLeft = -1;        // Cell on the lower-perpendicular side
        public int CellOnRight = -1;       // Cell on the higher-perpendicular side
        public List<Vec2> ChainOnLeft = new();   // Boundary chain for CellOnLeft (left edge of cell, accumulated as sweep advances)
        public List<Vec2> ChainOnRight = new();  // Boundary chain for CellOnRight (right edge of cell)
    }

    private double _sweepHeading;
    private double _sx, _sy;        // Unit sweep direction
    private double _px, _py;        // Unit perpendicular direction (sweep rotated +90°)

    private readonly List<ActiveEdge> _active = new();
    private readonly List<Cell> _finished = new();
    private readonly List<ReebEdge> _reeb = new();
    /// <summary>
    /// Floor (lower-sweep boundary) of each open cell. Floors come from the
    /// virtual sweep-line segment at the cell's creation event:
    ///   - Open: single point [V_open]
    ///   - Split (left child): [leftAtSplit, V_split]
    ///   - Split (right child): [V_split, rightAtSplit]
    ///   - Merge (combined): [edgeALAtMerge, V_merge, edgeBRAtMerge]
    /// At cell close the polygon is built as: floor + leftChain + ceiling + reverse(rightChain).
    /// </summary>
    private readonly Dictionary<int, List<Vec2>> _cellFloor = new();
    private int _nextCellId;

    public ReebGraph Run(BoundaryPolygon outer, List<BoundaryPolygon> inners, double sweepHeading)
    {
        _active.Clear();
        _finished.Clear();
        _reeb.Clear();
        _cellFloor.Clear();
        _nextCellId = 0;

        if (outer.Points.Count < 3) return new ReebGraph();

        var outerVec2 = outer.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var outerCcw = PolygonOrientation.Ensure(outerVec2, wantCcw: true);

        var innersCw = new List<List<Vec2>>();
        foreach (var inner in inners)
        {
            if (inner.Points.Count < 3) continue;
            var pts = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            innersCw.Add(PolygonOrientation.Ensure(pts, wantCcw: false));
        }

        // Perturb sweep heading to avoid degenerate edges.
        _sweepHeading = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            sweepHeading, outerCcw, innersCw);
        _sx = Math.Sin(_sweepHeading);
        _sy = Math.Cos(_sweepHeading);
        // Perpendicular direction = sweep rotated +90° CCW: (-sy, sx).
        _px = -_sy;
        _py = _sx;

        // Build merged event list across all polygons.
        var events = new List<SweepEvent>();
        AddEvents(events, outerCcw, isInnerHole: false);
        foreach (var inner in innersCw)
            AddEvents(events, inner, isInnerHole: true);

        events.Sort((a, b) => a.SweepCoord.CompareTo(b.SweepCoord));

        foreach (var ev in events)
            ProcessEvent(ev);

        return new ReebGraph
        {
            Cells = _finished,
            Edges = _reeb,
        };
    }

    private void AddEvents(List<SweepEvent> events, List<Vec2> polygon, bool isInnerHole)
    {
        var classified = CriticalPointClassifier.Classify(polygon, _sweepHeading, isInnerHole);
        for (int i = 0; i < polygon.Count; i++)
        {
            int prev = (i - 1 + polygon.Count) % polygon.Count;
            int next = (i + 1) % polygon.Count;
            events.Add(new SweepEvent
            {
                Vertex = polygon[i],
                SweepCoord = classified[i].SweepCoordinate,
                Type = classified[i].Type,
                IsInnerHole = isInnerHole,
                PrevVertex = polygon[prev],
                NextVertex = polygon[next],
            });
        }
    }

    private double SweepCoord(Vec2 p) => p.Easting * _sx + p.Northing * _sy;
    private double PerpCoord(Vec2 p) => p.Easting * _px + p.Northing * _py;

    /// <summary>
    /// Perpendicular position of an active edge at the given sweep coord
    /// (linear interpolation along the edge).
    /// </summary>
    private double PerpAt(ActiveEdge e, double sweepCoord)
    {
        var p = PointAt(e, sweepCoord);
        return p.Easting * _px + p.Northing * _py;
    }

    /// <summary>2D point on an active edge at the given sweep coord.</summary>
    private Vec2 PointAt(ActiveEdge e, double sweepCoord)
    {
        double sLo = SweepCoord(e.LowerEnd);
        double sHi = SweepCoord(e.UpperEnd);
        double range = sHi - sLo;
        if (Math.Abs(range) < 1e-12) return e.LowerEnd;
        double t = (sweepCoord - sLo) / range;
        return new Vec2(
            e.LowerEnd.Easting + t * (e.UpperEnd.Easting - e.LowerEnd.Easting),
            e.LowerEnd.Northing + t * (e.UpperEnd.Northing - e.LowerEnd.Northing));
    }

    private void ProcessEvent(SweepEvent ev)
    {
        // Identify which adjacent edges are FORWARD (other endpoint has higher
        // sweep coord) and BACKWARD. There are exactly two adjacent edges per
        // vertex; combinations encode the event class.
        bool prevForward = SweepCoord(ev.PrevVertex) > ev.SweepCoord;
        bool nextForward = SweepCoord(ev.NextVertex) > ev.SweepCoord;

        if (ev.Type == CriticalPointType.Regular)
        {
            HandleRegular(ev, prevForward, nextForward);
        }
        else if (ev.Type == CriticalPointType.Open)
        {
            HandleOpen(ev);
        }
        else if (ev.Type == CriticalPointType.Close)
        {
            HandleClose(ev);
        }
        else if (ev.Type == CriticalPointType.Split)
        {
            HandleSplit(ev);
        }
        else if (ev.Type == CriticalPointType.Merge)
        {
            HandleMerge(ev);
        }
    }

    private ActiveEdge MakeEdge(Vec2 v, Vec2 other)
    {
        // Return an ActiveEdge oriented LowerEnd → UpperEnd by sweep coord.
        if (SweepCoord(v) <= SweepCoord(other))
            return new ActiveEdge { LowerEnd = v, UpperEnd = other };
        return new ActiveEdge { LowerEnd = other, UpperEnd = v };
    }

    private void InsertActiveEdge(ActiveEdge edge, double sweepCoord)
    {
        double perp = PerpAt(edge, sweepCoord);
        int idx = 0;
        while (idx < _active.Count && PerpAt(_active[idx], sweepCoord) < perp) idx++;
        _active.Insert(idx, edge);
    }

    private void RemoveActiveEdgeContaining(Vec2 v)
    {
        // Remove the active edge whose UpperEnd matches v (i.e., the edge ends at v).
        for (int i = 0; i < _active.Count; i++)
        {
            if (Vec2Equal(_active[i].UpperEnd, v))
            {
                _active.RemoveAt(i);
                return;
            }
        }
    }

    private static bool Vec2Equal(Vec2 a, Vec2 b)
        => Math.Abs(a.Easting - b.Easting) < 1e-9 && Math.Abs(a.Northing - b.Northing) < 1e-9;

    // ===== Event handlers =====
    // Phase C scaffolding — Open/Close cover the no-obstacle convex case.
    // Split/Merge for inner-hole and outer-notch cases land in Phase C.2.

    private void HandleOpen(SweepEvent ev)
    {
        var leftEdge = MakeEdge(ev.Vertex, ev.PrevVertex);
        var rightEdge = MakeEdge(ev.Vertex, ev.NextVertex);

        double slightlyAbove = ev.SweepCoord + 1e-6;
        if (PerpAt(leftEdge, slightlyAbove) > PerpAt(rightEdge, slightlyAbove))
            (leftEdge, rightEdge) = (rightEdge, leftEdge);

        int cellId = _nextCellId++;
        leftEdge.CellOnRight = cellId;
        rightEdge.CellOnLeft = cellId;
        // Floor is just the open vertex (cell narrows to a point at the bottom).
        _cellFloor[cellId] = new List<Vec2> { ev.Vertex };

        InsertActiveEdge(leftEdge, ev.SweepCoord);
        InsertActiveEdge(rightEdge, ev.SweepCoord);
    }

    private void HandleClose(SweepEvent ev)
    {
        ActiveEdge? leftIncoming = null, rightIncoming = null;
        foreach (var e in _active)
        {
            if (!Vec2Equal(e.UpperEnd, ev.Vertex)) continue;
            if (leftIncoming == null) leftIncoming = e;
            else { rightIncoming = e; break; }
        }
        if (leftIncoming == null || rightIncoming == null) return;

        if (PerpAt(leftIncoming, ev.SweepCoord) > PerpAt(rightIncoming, ev.SweepCoord))
            (leftIncoming, rightIncoming) = (rightIncoming, leftIncoming);

        int cellId = leftIncoming.CellOnRight;

        FinishCellPolygon(cellId, leftIncoming.ChainOnRight, rightIncoming.ChainOnLeft,
            ceiling: new List<Vec2> { ev.Vertex });

        _active.Remove(leftIncoming);
        _active.Remove(rightIncoming);
    }

    private void HandleRegular(SweepEvent ev, bool prevForward, bool nextForward)
    {
        Vec2 forward = prevForward ? ev.PrevVertex : ev.NextVertex;

        ActiveEdge? old = null;
        foreach (var e in _active)
        {
            if (Vec2Equal(e.UpperEnd, ev.Vertex)) { old = e; break; }
        }
        if (old == null) return;

        var fresh = MakeEdge(ev.Vertex, forward);
        fresh.CellOnLeft = old.CellOnLeft;
        fresh.CellOnRight = old.CellOnRight;
        fresh.ChainOnLeft = old.ChainOnLeft;
        fresh.ChainOnRight = old.ChainOnRight;
        // Regular vertex IS on a polygon edge — append to the appropriate chain
        // (the side that bounds an active cell).
        if (fresh.CellOnRight >= 0) fresh.ChainOnRight.Add(ev.Vertex);
        if (fresh.CellOnLeft >= 0) fresh.ChainOnLeft.Add(ev.Vertex);

        _active.Remove(old);
        InsertActiveEdge(fresh, ev.SweepCoord);
    }

    private void HandleSplit(SweepEvent ev)
    {
        // Two new edges enter the active list (both adjacent edges go forward).
        var aFwd = MakeEdge(ev.Vertex, ev.PrevVertex);
        var bFwd = MakeEdge(ev.Vertex, ev.NextVertex);

        // Order so leftFwd is on the lower perp side at slightly-above sweep.
        double slightlyAbove = ev.SweepCoord + 1e-7;
        ActiveEdge leftFwd, rightFwd;
        if (PerpAt(aFwd, slightlyAbove) <= PerpAt(bFwd, slightlyAbove))
        {
            leftFwd = aFwd; rightFwd = bFwd;
        }
        else
        {
            leftFwd = bFwd; rightFwd = aFwd;
        }

        // Find existing active edges immediately surrounding V's perpendicular.
        double vPerp = PerpCoord(ev.Vertex);
        ActiveEdge? leftBound = null, rightBound = null;
        double bestLeftPerp = double.NegativeInfinity;
        double bestRightPerp = double.PositiveInfinity;
        foreach (var e in _active)
        {
            double p = PerpAt(e, ev.SweepCoord);
            if (p < vPerp && p > bestLeftPerp) { bestLeftPerp = p; leftBound = e; }
            else if (p > vPerp && p < bestRightPerp) { bestRightPerp = p; rightBound = e; }
        }

        if (leftBound == null || rightBound == null
            || leftBound.CellOnRight == -1
            || leftBound.CellOnRight != rightBound.CellOnLeft)
        {
            // V isn't inside an active cell — fall back to an Open (rare; treats
            // disconnected polygon-with-holes input as a separate cell).
            HandleOpen(ev);
            return;
        }

        int parentId = leftBound.CellOnRight;

        Vec2 leftAtSplit = PointAt(leftBound, ev.SweepCoord);
        Vec2 rightAtSplit = PointAt(rightBound, ev.SweepCoord);

        // Close parent. Ceiling = [leftAtSplit, V_split, rightAtSplit].
        FinishCellPolygon(parentId, leftBound.ChainOnRight, rightBound.ChainOnLeft,
            ceiling: new List<Vec2> { leftAtSplit, ev.Vertex, rightAtSplit });

        // Open new cells. Floors:
        //   L (lower perp half): [leftAtSplit, V_split]
        //   R (higher perp half): [V_split, rightAtSplit]
        int leftCellId = _nextCellId++;
        int rightCellId = _nextCellId++;
        _cellFloor[leftCellId] = new List<Vec2> { leftAtSplit, ev.Vertex };
        _cellFloor[rightCellId] = new List<Vec2> { ev.Vertex, rightAtSplit };

        leftBound.CellOnRight = leftCellId;
        leftBound.ChainOnRight = new List<Vec2>();   // chain of regulars only

        rightBound.CellOnLeft = rightCellId;
        rightBound.ChainOnLeft = new List<Vec2>();

        leftFwd.CellOnLeft = leftCellId;
        leftFwd.CellOnRight = -1;
        leftFwd.ChainOnLeft = new List<Vec2>();

        rightFwd.CellOnLeft = -1;
        rightFwd.CellOnRight = rightCellId;
        rightFwd.ChainOnRight = new List<Vec2>();

        InsertActiveEdge(leftFwd, ev.SweepCoord);
        InsertActiveEdge(rightFwd, ev.SweepCoord);

        _reeb.Add(new ReebEdge { FromCellId = parentId, ToCellId = leftCellId, CriticalPoint = ev.Vertex });
        _reeb.Add(new ReebEdge { FromCellId = parentId, ToCellId = rightCellId, CriticalPoint = ev.Vertex });
    }

    private void HandleMerge(SweepEvent ev)
    {
        // Find the two active edges ending at V (UpperEnd == ev.Vertex).
        ActiveEdge? leftIn = null, rightIn = null;
        foreach (var e in _active)
        {
            if (!Vec2Equal(e.UpperEnd, ev.Vertex)) continue;
            if (leftIn == null) leftIn = e;
            else if (rightIn == null) { rightIn = e; break; }
        }
        if (leftIn == null || rightIn == null) return;

        // Order by perp at slightly-below sweep coord (where edges still exist).
        double slightlyBelow = ev.SweepCoord - 1e-7;
        if (PerpAt(leftIn, slightlyBelow) > PerpAt(rightIn, slightlyBelow))
            (leftIn, rightIn) = (rightIn, leftIn);

        int leftIdx = _active.IndexOf(leftIn);
        int rightIdx = _active.IndexOf(rightIn);
        if (leftIdx < 0 || rightIdx < 0) return;

        // The cells outside leftIn (on its left) and outside rightIn (on its right).
        ActiveEdge? edgeAL = (leftIdx > 0) ? _active[leftIdx - 1] : null;
        ActiveEdge? edgeBR = (rightIdx < _active.Count - 1) ? _active[rightIdx + 1] : null;

        if (edgeAL == null || edgeBR == null
            || leftIn.CellOnLeft == -1 || rightIn.CellOnRight == -1
            || edgeAL.CellOnRight != leftIn.CellOnLeft
            || edgeBR.CellOnLeft != rightIn.CellOnRight)
        {
            // Inconsistent state — degrade to a Close (collapse the inner pair).
            HandleClose(ev);
            return;
        }

        int cellAId = leftIn.CellOnLeft;
        int cellBId = rightIn.CellOnRight;

        Vec2 edgeALAtMerge = PointAt(edgeAL, ev.SweepCoord);
        Vec2 edgeBRAtMerge = PointAt(edgeBR, ev.SweepCoord);

        // Close A. Ceiling = [edgeALAtMerge, V_merge].
        FinishCellPolygon(cellAId, edgeAL.ChainOnRight, leftIn.ChainOnLeft,
            ceiling: new List<Vec2> { edgeALAtMerge, ev.Vertex });

        // Close B. Ceiling = [V_merge, edgeBRAtMerge].
        FinishCellPolygon(cellBId, rightIn.ChainOnRight, edgeBR.ChainOnLeft,
            ceiling: new List<Vec2> { ev.Vertex, edgeBRAtMerge });

        // Open new cell C. Floor = [edgeALAtMerge, V_merge, edgeBRAtMerge].
        int cellCId = _nextCellId++;
        _cellFloor[cellCId] = new List<Vec2> { edgeALAtMerge, ev.Vertex, edgeBRAtMerge };

        edgeAL.CellOnRight = cellCId;
        edgeAL.ChainOnRight = new List<Vec2>();
        edgeBR.CellOnLeft = cellCId;
        edgeBR.ChainOnLeft = new List<Vec2>();

        _active.Remove(leftIn);
        _active.Remove(rightIn);

        _reeb.Add(new ReebEdge { FromCellId = cellAId, ToCellId = cellCId, CriticalPoint = ev.Vertex });
        _reeb.Add(new ReebEdge { FromCellId = cellBId, ToCellId = cellCId, CriticalPoint = ev.Vertex });
    }

    /// <summary>
    /// Assemble a cell polygon from its stored floor + accumulated left chain
    /// of polygon-edge regulars + provided ceiling + reversed right chain.
    /// All chains contain only REGULAR-event vertices (no virtual sweep-line corners).
    /// Floor is provided at cell creation; ceiling at cell close.
    /// </summary>
    private void FinishCellPolygon(int cellId, List<Vec2> leftChainAscending,
        List<Vec2> rightChainAscending, List<Vec2> ceiling)
    {
        var poly = new List<Vec2>();
        if (_cellFloor.TryGetValue(cellId, out var floor)) poly.AddRange(floor);
        poly.AddRange(leftChainAscending);
        poly.AddRange(ceiling);
        for (int i = rightChainAscending.Count - 1; i >= 0; i--)
            poly.Add(rightChainAscending[i]);
        FinishCell(cellId, poly);
    }

    private void FinishCell(int cellId, List<Vec2> polygon)
    {
        if (polygon.Count < 3) return;
        // Compute sweep extents.
        double sMin = double.PositiveInfinity, sMax = double.NegativeInfinity;
        foreach (var p in polygon)
        {
            double s = SweepCoord(p);
            if (s < sMin) sMin = s;
            if (s > sMax) sMax = s;
        }
        _finished.Add(new Cell
        {
            Id = cellId,
            Polygon = polygon,
            SweepStart = sMin,
            SweepEnd = sMax,
        });
    }

    private sealed class SweepEvent
    {
        public Vec2 Vertex;
        public double SweepCoord;
        public CriticalPointType Type;
        public bool IsInnerHole;
        public Vec2 PrevVertex;
        public Vec2 NextVertex;
    }
}
