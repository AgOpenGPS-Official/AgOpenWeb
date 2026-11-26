// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Base;

/// <summary>
/// Where a polygon's inside is clear of its outline, on a 10 m grid: each cell inside the
/// polygon holds how many whole cells lie between it and the nearest cell an edge runs through.
/// A point in a cell holding n is at least n cells from every edge, so anything within that
/// distance of it is inside the polygon too: the quick answer for a section away from the
/// fence, read on every control tick. It follows the polygon's own shape, so a notch in the
/// fence, or the missing corner of an L, is never "inside" as it is by the bounding box.
/// Immutable: build a new one when the polygon changes.
/// </summary>
public sealed class PolygonClearGrid
{
    private const double CellSize = 10.0;

    // 5 x 5 km. A larger polygon (a point typed 50 km away) gets no grid, and no quick answer.
    private const int MaxCells = 250_000;

    private readonly double _minE, _minN;
    private readonly int _cols, _rows;
    private readonly byte[] _clear; // 0: outside, or an edge in the cell or in one beside it

    private PolygonClearGrid(double minE, double minN, int cols, int rows, byte[] clear)
    {
        _minE = minE; _minN = minN; _cols = cols; _rows = rows; _clear = clear;
    }

    /// <summary>The grid of the polygon <paramref name="slabs"/> was built from (its edges and its
    /// point-in-polygon test, so the two are always one polygon's), or null when there is no index
    /// (fewer than three points) or the polygon is too large.</summary>
    public static PolygonClearGrid? Build(PolygonSlabIndex? slabs)
    {
        if (slabs == null) return null;

        double minE = slabs.MinEasting, minN = slabs.MinNorthing;
        double spanE = slabs.MaxEasting - minE, spanN = slabs.MaxNorthing - minN;
        if (!(spanE >= 0 && spanN >= 0) || (spanE / CellSize + 1) * (spanN / CellSize + 1) > MaxCells) return null;
        int cols = (int)(spanE / CellSize) + 1, rows = (int)(spanN / CellSize) + 1;

        // The cells each edge runs through.
        var edge = new bool[cols * rows];
        for (int k = 0; k < slabs.EdgeCount; k++)
        {
            var (e1, n1, e2, n2) = slabs.Edge(k);
            double x0 = (e1 - minE) / CellSize, y0 = (n1 - minN) / CellSize;
            double x1 = (e2 - minE) / CellSize, y1 = (n2 - minN) / CellSize;
            if (x0 > x1) (x0, y0, x1, y1) = (x1, y1, x0, y0);
            int c0 = Math.Clamp((int)x0, 0, cols - 1), c1 = Math.Clamp((int)x1, 0, cols - 1);
            for (int c = c0; c <= c1; c++)
            {
                // The edge's stretch in this column, and the rows it spans.
                double xa = Math.Max(x0, c), xb = Math.Min(x1, c + 1);
                double ya = y0, yb = y1;
                if (x1 - x0 > 1e-12)
                {
                    ya = y0 + (y1 - y0) * (xa - x0) / (x1 - x0);
                    yb = y0 + (y1 - y0) * (xb - x0) / (x1 - x0);
                }
                int r0 = Math.Clamp((int)Math.Min(ya, yb), 0, rows - 1), r1 = Math.Clamp((int)Math.Max(ya, yb), 0, rows - 1);
                for (int r = r0; r <= r1; r++) edge[r * cols + c] = true;
            }
        }

        // Cells to the nearest edge cell, counted the chessboard way (two passes).
        var clear = new byte[cols * rows];
        for (int k = 0; k < clear.Length; k++) clear[k] = edge[k] ? (byte)0 : byte.MaxValue;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                Lower(clear, cols, rows, r, c, r, c - 1, r - 1, c - 1, r - 1, c, r - 1, c + 1);
        for (int r = rows - 1; r >= 0; r--)
            for (int c = cols - 1; c >= 0; c--)
                Lower(clear, cols, rows, r, c, r, c + 1, r + 1, c + 1, r + 1, c, r + 1, c - 1);

        // Keep the cells inside the polygon, each as the whole cells between it and an edge cell.
        // Cells side by side with no edge in them are on one side of the outline, so one
        // point-in-polygon test serves each run of them along a row.
        for (int r = 0; r < rows; r++)
        {
            bool inside = false;
            for (int c = 0; c < cols; c++)
            {
                int k = r * cols + c;
                if (edge[k]) continue;
                if (c == 0 || edge[k - 1])
                    inside = slabs.Contains(minE + (c + 0.5) * CellSize, minN + (r + 0.5) * CellSize);
                clear[k] = inside ? (byte)(clear[k] - 1) : (byte)0;
            }
        }
        return new PolygonClearGrid(minE, minN, cols, rows, clear);
    }

    private static void Lower(byte[] d, int cols, int rows, int r, int c, int r1, int c1, int r2, int c2, int r3, int c3, int r4, int c4)
    {
        int best = d[r * cols + c];
        if (best == 0) return;
        best = Math.Min(best, At(d, cols, rows, r1, c1) + 1);
        best = Math.Min(best, At(d, cols, rows, r2, c2) + 1);
        best = Math.Min(best, At(d, cols, rows, r3, c3) + 1);
        best = Math.Min(best, At(d, cols, rows, r4, c4) + 1);
        d[r * cols + c] = (byte)best;
    }

    private static int At(byte[] d, int cols, int rows, int r, int c) =>
        r < 0 || r >= rows || c < 0 || c >= cols ? byte.MaxValue : d[r * cols + c];

    /// <summary>Is everything within <paramref name="reach"/> metres of the point inside the
    /// polygon? False also means "not known from here": take the full test.</summary>
    public bool IsClearInside(double easting, double northing, double reach)
    {
        double x = (easting - _minE) / CellSize, y = (northing - _minN) / CellSize;
        if (!(x >= 0 && y >= 0 && x < _cols && y < _rows)) return false;
        int cells = _clear[(int)y * _cols + (int)x];
        return cells > 0 && cells * CellSize >= reach;
    }
}
