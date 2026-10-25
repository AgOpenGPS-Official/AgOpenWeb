// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.Models.Base;

/// <summary>
/// A polygon's edges sorted into 5 m strips of easting, for point-in-polygon tests that run on
/// every control tick. A point's test needs only the edges whose easting span holds it (the
/// crossing rule of <see cref="GeometryMath.CrossesSouth"/>): a dozen or so on a field
/// boundary with a point every metre, instead of every edge. The answers are those of
/// <see cref="GeometryMath.IsPointInPolygon{T}"/>. Immutable: build a new one when the polygon
/// changes.
/// </summary>
public sealed class PolygonSlabIndex
{
    private const double SlabWidth = 5.0;

    // Edge k runs from (E1[k], N1[k]) to (E2[k], N2[k]).
    private readonly double[] _e1, _n1, _e2, _n2;
    private readonly int[][] _slabs;
    private readonly double _minE, _maxE, _minN, _maxN;

    private PolygonSlabIndex(double[] e1, double[] n1, double[] e2, double[] n2, int[][] slabs,
        double minE, double maxE, double minN, double maxN)
    {
        _e1 = e1; _n1 = n1; _e2 = e2; _n2 = n2; _slabs = slabs;
        _minE = minE; _maxE = maxE; _minN = minN; _maxN = maxN;
    }

    /// <summary>The index of a polygon, or null when it has fewer than three points.</summary>
    public static PolygonSlabIndex? Build<T>(IReadOnlyList<T> polygon) where T : IPlanarPoint
    {
        int n = polygon.Count;
        if (n < 3) return null;

        var e1 = new double[n]; var n1 = new double[n]; var e2 = new double[n]; var n2 = new double[n];
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            e1[i] = polygon[i].Easting; n1[i] = polygon[i].Northing;
            e2[i] = polygon[j].Easting; n2[i] = polygon[j].Northing;
            minE = Math.Min(minE, e1[i]); maxE = Math.Max(maxE, e1[i]);
            minN = Math.Min(minN, n1[i]); maxN = Math.Max(maxN, n1[i]);
        }

        int slabCount = (int)((maxE - minE) / SlabWidth) + 1;
        var lists = new List<int>?[slabCount];
        for (int k = 0; k < n; k++)
        {
            // Every strip the edge's easting span touches (a superset of where it can count).
            int s0 = Slab(Math.Min(e1[k], e2[k]), minE, slabCount);
            int s1 = Slab(Math.Max(e1[k], e2[k]), minE, slabCount);
            for (int s = s0; s <= s1; s++) (lists[s] ??= new List<int>()).Add(k);
        }
        var slabs = new int[slabCount][];
        for (int s = 0; s < slabCount; s++) slabs[s] = lists[s]?.ToArray() ?? Array.Empty<int>();
        return new PolygonSlabIndex(e1, n1, e2, n2, slabs, minE, maxE, minN, maxN);
    }

    private static int Slab(double easting, double minE, int slabCount) =>
        Math.Clamp((int)((easting - minE) / SlabWidth), 0, slabCount - 1);

    public double MinEasting => _minE;
    public double MaxEasting => _maxE;
    public double MinNorthing => _minN;
    public double MaxNorthing => _maxN;

    /// <summary>Is the point inside the polygon?</summary>
    public bool Contains(double easting, double northing)
    {
        // Outside the bounding box no edge, or every edge pair, lies south of the point.
        if (easting < _minE || easting > _maxE || northing < _minN || northing > _maxN) return false;

        var edges = _slabs[Slab(easting, _minE, _slabs.Length)];
        WorkCounters.PolygonTests++;
        WorkCounters.PolygonEdges += edges.Length;
        bool inside = false;
        foreach (int k in edges)
            if (GeometryMath.CrossesSouth(_e1[k], _n1[k], _e2[k], _n2[k], easting, northing))
                inside = !inside;
        return inside;
    }
}
