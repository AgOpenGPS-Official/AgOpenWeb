// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;

namespace AgOpenWeb.Models.Base;

/// <summary>
/// The <see cref="PolygonSlabIndex"/> of the polygon last asked about, for a polygon tested
/// every cycle (a headland line): kept with the list it was built from and rebuilt when the
/// list is replaced or changes length. A polygon whose points are moved in place, keeping its
/// length, needs a new list. Safe from several threads: the index and its list are one
/// immutable entry, published whole.
/// </summary>
public sealed class PolygonIndexCache
{
    private sealed record Entry(object Polygon, int Count, PolygonSlabIndex? Index);
    private Entry? _entry;

    /// <summary>The polygon's index, or null when it has fewer than three points.</summary>
    public PolygonSlabIndex? For<T>(IReadOnlyList<T> polygon) where T : IPlanarPoint
    {
        var cached = Volatile.Read(ref _entry);
        if (cached != null && ReferenceEquals(cached.Polygon, polygon) && cached.Count == polygon.Count) return cached.Index;
        var index = PolygonSlabIndex.Build(polygon);
        Volatile.Write(ref _entry, new Entry(polygon, polygon.Count, index));
        return index;
    }

    /// <summary>Is the point inside the polygon? The answer of
    /// <see cref="GeometryMath.IsPointInPolygon{T}"/>; false for fewer than three points.</summary>
    public bool Contains<T>(IReadOnlyList<T> polygon, double easting, double northing) where T : IPlanarPoint =>
        For(polygon)?.Contains(easting, northing) ?? false;
}
