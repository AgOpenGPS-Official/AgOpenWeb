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
    private int _nextCellId;

    public ReebGraph Run(BoundaryPolygon outer, List<BoundaryPolygon> inners, double sweepHeading)
    {
        _active.Clear();
        _finished.Clear();
        _reeb.Clear();
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
        double sLo = SweepCoord(e.LowerEnd);
        double sHi = SweepCoord(e.UpperEnd);
        double range = sHi - sLo;
        if (Math.Abs(range) < 1e-12) return PerpCoord(e.LowerEnd);
        double t = (sweepCoord - sLo) / range;
        double easting = e.LowerEnd.Easting + t * (e.UpperEnd.Easting - e.LowerEnd.Easting);
        double northing = e.LowerEnd.Northing + t * (e.UpperEnd.Northing - e.LowerEnd.Northing);
        return easting * _px + northing * _py;
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
        // Two new edges enter the active list, one going to PrevVertex, one to NextVertex.
        // After perturbation, a true OPEN has both neighbors at higher sweep coord.
        var leftEdge = MakeEdge(ev.Vertex, ev.PrevVertex);
        var rightEdge = MakeEdge(ev.Vertex, ev.NextVertex);

        // Determine which edge is on the left (lower perp) and which on the right.
        // After this event, both edges bound a single new cell: cell-on-right of
        // leftEdge == cell-on-left of rightEdge == new cell.
        // For an outer CCW vertex at OPEN, the LEFT edge is the one going to PrevVertex
        // (incoming edge from polygon walk) — the polygon interior is to its right.
        // The RIGHT edge is to NextVertex (outgoing) — interior to its left.
        // After perturbation we double-check by perp position.
        double slightlyAbove = ev.SweepCoord + 1e-6;
        double leftPerp = PerpAt(leftEdge, slightlyAbove);
        double rightPerp = PerpAt(rightEdge, slightlyAbove);
        if (leftPerp > rightPerp)
        {
            (leftEdge, rightEdge) = (rightEdge, leftEdge);
        }

        int cellId = _nextCellId++;
        leftEdge.CellOnRight = cellId;
        leftEdge.ChainOnRight.Add(ev.Vertex);
        rightEdge.CellOnLeft = cellId;
        rightEdge.ChainOnLeft.Add(ev.Vertex);

        InsertActiveEdge(leftEdge, ev.SweepCoord);
        InsertActiveEdge(rightEdge, ev.SweepCoord);
    }

    private void HandleClose(SweepEvent ev)
    {
        // Two existing active edges meet at this vertex. Both have UpperEnd == ev.Vertex.
        // Find them in the active list.
        ActiveEdge? leftIncoming = null, rightIncoming = null;
        for (int i = 0; i < _active.Count; i++)
        {
            if (Vec2Equal(_active[i].UpperEnd, ev.Vertex))
            {
                if (leftIncoming == null) leftIncoming = _active[i];
                else { rightIncoming = _active[i]; break; }
            }
        }
        if (leftIncoming == null || rightIncoming == null) return;

        // Order by perp.
        if (PerpAt(leftIncoming, ev.SweepCoord) > PerpAt(rightIncoming, ev.SweepCoord))
            (leftIncoming, rightIncoming) = (rightIncoming, leftIncoming);

        // The cell ending here is leftIncoming.CellOnRight == rightIncoming.CellOnLeft.
        int cellId = leftIncoming.CellOnRight;

        // Build cell polygon: leftIncoming.ChainOnRight (forward) + ev.Vertex + reversed rightIncoming.ChainOnLeft.
        var poly = new List<Vec2>(leftIncoming.ChainOnRight);
        poly.Add(ev.Vertex);
        for (int i = rightIncoming.ChainOnLeft.Count - 1; i >= 0; i--)
            poly.Add(rightIncoming.ChainOnLeft[i]);

        FinishCell(cellId, poly);

        _active.Remove(leftIncoming);
        _active.Remove(rightIncoming);
    }

    private void HandleRegular(SweepEvent ev, bool prevForward, bool nextForward)
    {
        // One backward edge ends here; one forward edge begins. Replace the
        // backward edge with the forward one, preserving the cell binding.
        Vec2 backward = prevForward ? ev.NextVertex : ev.PrevVertex;
        Vec2 forward = prevForward ? ev.PrevVertex : ev.NextVertex;

        ActiveEdge? old = null;
        for (int i = 0; i < _active.Count; i++)
        {
            if (Vec2Equal(_active[i].UpperEnd, ev.Vertex))
            {
                old = _active[i];
                break;
            }
        }
        if (old == null) return;

        var fresh = MakeEdge(ev.Vertex, forward);
        fresh.CellOnLeft = old.CellOnLeft;
        fresh.CellOnRight = old.CellOnRight;
        fresh.ChainOnLeft = old.ChainOnLeft;
        fresh.ChainOnRight = old.ChainOnRight;
        // Append vertex to whichever chain bounds an active cell.
        if (fresh.CellOnLeft >= 0) fresh.ChainOnLeft.Add(ev.Vertex);
        if (fresh.CellOnRight >= 0) fresh.ChainOnRight.Add(ev.Vertex);

        _active.Remove(old);
        InsertActiveEdge(fresh, ev.SweepCoord);
    }

    private void HandleSplit(SweepEvent ev)
    {
        // Phase C.2 placeholder — for now treat as a no-op so simple-rectangle
        // tests pass. Will be implemented when we extend to inner-hole and
        // outer-notch fixtures.
        // TODO Phase C.2: identify the parent cell at this perp, finish it,
        // open two new cells with the two forward edges as their inner walls.
    }

    private void HandleMerge(SweepEvent ev)
    {
        // Phase C.2 placeholder — see HandleSplit.
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
