// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Polygon orientation utilities for BCD. The decomposition needs outer
/// polygons in CCW order and inner holes in CW order so that walking either
/// keeps the polygon-with-holes interior on the LEFT.
/// </summary>
internal static class PolygonOrientation
{
    /// <summary>Signed area; positive means CCW, negative means CW.</summary>
    public static double SignedArea(List<Vec2> polygon)
    {
        double a = 0;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % n];
            a += (q.Easting - p.Easting) * (q.Northing + p.Northing);
        }
        // Sum of (x2-x1)(y2+y1) is twice the signed area, NEGATED for CCW.
        // So negative = CCW, positive = CW. Flip sign for the convention here.
        return -a / 2.0;
    }

    public static bool IsCcw(List<Vec2> polygon) => SignedArea(polygon) > 0;

    /// <summary>Return a copy with the requested orientation.</summary>
    public static List<Vec2> Ensure(List<Vec2> polygon, bool wantCcw)
    {
        if (IsCcw(polygon) == wantCcw)
            return new List<Vec2>(polygon);
        var rev = new List<Vec2>(polygon.Count);
        for (int i = polygon.Count - 1; i >= 0; i--) rev.Add(polygon[i]);
        return rev;
    }
}
