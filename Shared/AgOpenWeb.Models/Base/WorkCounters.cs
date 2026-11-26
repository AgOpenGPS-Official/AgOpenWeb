// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Base;

/// <summary>
/// Counts of the expensive geometry the guidance cycle does, per thread. The hot-path budget
/// tests read them around pipeline cycles: a count doesn't vary with the machine the way time
/// does, so CI can hold every cycle to a budget (a pass scan that ran every cycle cost 209 ms
/// on a finished field, #423). Counting costs one thread-static add per call.
/// </summary>
public static class WorkCounters
{
    /// <summary>Point-in-polygon tests (<see cref="GeometryMath.IsPointInPolygon(System.Collections.Generic.IReadOnlyList{Vec3}, Vec3)"/>).</summary>
    [ThreadStatic] public static long PolygonTests;

    /// <summary>Polygon edges those tests visited: their real cost.</summary>
    [ThreadStatic] public static long PolygonEdges;

    /// <summary>Coverage bitmap cell lookups.</summary>
    [ThreadStatic] public static long CoverageLookups;

    /// <summary>Points of the curves offset by <c>CurveProcessing.CreateOffsetCurveWithInfo</c>.</summary>
    [ThreadStatic] public static long OffsetCurvePoints;

    /// <summary>Points of the polygons offset by <c>PolygonOffsetService</c>.</summary>
    [ThreadStatic] public static long PolygonOffsetPoints;

    public readonly record struct Snapshot(
        long PolygonTests, long PolygonEdges, long CoverageLookups,
        long OffsetCurvePoints, long PolygonOffsetPoints, long AllocatedBytes)
    {
        public static Snapshot operator -(Snapshot a, Snapshot b) => new(
            a.PolygonTests - b.PolygonTests, a.PolygonEdges - b.PolygonEdges,
            a.CoverageLookups - b.CoverageLookups, a.OffsetCurvePoints - b.OffsetCurvePoints,
            a.PolygonOffsetPoints - b.PolygonOffsetPoints, a.AllocatedBytes - b.AllocatedBytes);
    }

    /// <summary>This thread's counts so far, with the bytes it has allocated.</summary>
    public static Snapshot Take() => new(
        PolygonTests, PolygonEdges, CoverageLookups, OffsetCurvePoints, PolygonOffsetPoints,
        GC.GetAllocatedBytesForCurrentThread());
}
