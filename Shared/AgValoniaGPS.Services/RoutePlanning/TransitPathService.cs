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
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.PathPlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Generates inter-zone transit paths along perimeter circuits. See
/// <see cref="ITransitPathService"/> for context.
///
/// Algorithm per circuit:
/// 1. Snap exit and entry points to nearest indices on the closed loop
/// 2. Walk both arc directions; pick the shorter
/// 3. Build short Dubins approach: ExitPoint → loop[exitIdx]
/// 4. Append along-circuit waypoints (with direction-corrected headings)
/// 5. Build short Dubins departure: loop[entryIdx] → EntryPoint
/// 6. Validate full path against outer boundary + raw inner boundaries
///
/// Returns shortest valid result across all circuits, or shortest invalid
/// result as best-effort if none are valid.
/// </summary>
public class TransitPathService : ITransitPathService
{
    public TransitPathResult GenerateTransit(TransitPathInput input)
    {
        if (input.Circuits.Count == 0)
            return new TransitPathResult { IsValid = false, CircuitUsed = "none" };

        var outerVec2 = input.OuterBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();
        var innerVec2List = input.InnerBoundaries
            .Where(b => b.Points.Count >= 3)
            .Select(b => b.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList())
            .ToList();

        TransitPathResult? bestValid = null;
        TransitPathResult? bestAny = null;

        for (int ci = 0; ci < input.Circuits.Count; ci++)
        {
            var circuit = input.Circuits[ci];
            if (circuit.Points.Count < 3) continue;

            string label = circuit.IsInnerBoundary ? $"inner-{ci}" : "outer";

            // Try forward and reverse traversal of this circuit
            foreach (bool reverse in new[] { false, true })
            {
                var result = TryBuildTransit(input, circuit, reverse, outerVec2, innerVec2List, label);
                if (result == null) continue;

                if (result.IsValid && (bestValid == null || result.Length < bestValid.Length))
                    bestValid = result;
                if (bestAny == null || result.Length < bestAny.Length)
                    bestAny = result;
            }
        }

        return bestValid ?? bestAny ?? new TransitPathResult { IsValid = false, CircuitUsed = "none" };
    }

    private static TransitPathResult? TryBuildTransit(
        TransitPathInput input,
        TransitCircuit circuit,
        bool reverseDirection,
        List<Vec2> outerVec2,
        List<List<Vec2>> innerVec2List,
        string label)
    {
        var loop = circuit.Points;

        int exitIdx = NearestIndex(loop, input.ExitPoint.Easting, input.ExitPoint.Northing);
        int entryIdx = NearestIndex(loop, input.EntryPoint.Easting, input.EntryPoint.Northing);
        if (exitIdx < 0 || entryIdx < 0) return null;

        // Walk along circuit in chosen direction
        var arcPoints = WalkLoop(loop, exitIdx, entryIdx, reverseDirection);
        if (arcPoints.Count == 0) return null;

        // Approach leg: ExitPoint → first arc point, oriented along circuit tangent at exitIdx.
        // When reversed, tangent is opposite.
        double tangentAtExit = arcPoints[0].Heading;
        var approachStart = new Vec3(input.ExitPoint.Easting, input.ExitPoint.Northing, input.ExitHeading);
        var approachGoal = new Vec3(arcPoints[0].Easting, arcPoints[0].Northing, tangentAtExit);

        var dubins = new DubinsPathService(input.TurningRadius);
        var approachPaths = dubins.GenerateAllPaths(approachStart, approachGoal);
        var approach = approachPaths.OrderBy(p => p.Length).FirstOrDefault();
        if (approach.Path == null || approach.Path.Count == 0) return null;

        // Departure leg: last arc point → EntryPoint, oriented along circuit tangent at entryIdx.
        double tangentAtEntry = arcPoints[^1].Heading;
        var departStart = new Vec3(arcPoints[^1].Easting, arcPoints[^1].Northing, tangentAtEntry);
        var departGoal = new Vec3(input.EntryPoint.Easting, input.EntryPoint.Northing, input.EntryHeading);

        var departPaths = dubins.GenerateAllPaths(departStart, departGoal);
        var depart = departPaths.OrderBy(p => p.Length).FirstOrDefault();
        if (depart.Path == null || depart.Path.Count == 0) return null;

        // Stitch: ExitPoint + approach + arc + depart + EntryPoint
        var full = new List<Vec3>();
        full.Add(approachStart);
        full.AddRange(approach.Path);
        full.AddRange(arcPoints);
        full.AddRange(depart.Path);
        full.Add(departGoal);

        double totalLength = PathLength(full);

        bool insideOuter = AllPointsInsidePolygon(full, outerVec2);
        bool insideAnyInner = AnyPointsInsideAnyPolygon(full, innerVec2List);

        return new TransitPathResult
        {
            Waypoints = full,
            Length = totalLength,
            IsValid = insideOuter && !insideAnyInner,
            CircuitUsed = label + (reverseDirection ? "-rev" : ""),
        };
    }

    /// <summary>Index of point in <paramref name="loop"/> closest to (e,n).</summary>
    private static int NearestIndex(List<Vec3> loop, double e, double n)
    {
        if (loop.Count == 0) return -1;
        int best = 0;
        double bestSq = double.MaxValue;
        for (int i = 0; i < loop.Count; i++)
        {
            double dx = loop[i].Easting - e;
            double dy = loop[i].Northing - n;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestSq) { bestSq = d2; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Walk a closed loop from <paramref name="fromIdx"/> to <paramref name="toIdx"/>.
    /// If <paramref name="reverse"/>, walk in decreasing-index direction; otherwise increasing.
    /// In reverse mode, tangent headings are flipped 180 degrees so the vehicle's
    /// travel direction matches the walk direction.
    /// </summary>
    private static List<Vec3> WalkLoop(List<Vec3> loop, int fromIdx, int toIdx, bool reverse)
    {
        var result = new List<Vec3>();
        int n = loop.Count;
        if (n == 0) return result;

        int step = reverse ? -1 : 1;
        int i = fromIdx;
        // Cap iterations at n+1 to ensure we always terminate even on edge cases
        for (int safety = 0; safety <= n; safety++)
        {
            var pt = loop[i];
            double heading = reverse ? NormalizeHeading(pt.Heading + Math.PI) : pt.Heading;
            result.Add(new Vec3(pt.Easting, pt.Northing, heading));
            if (i == toIdx) break;
            i = (i + step + n) % n;
        }
        return result;
    }

    private static double NormalizeHeading(double h)
    {
        const double TwoPi = 2.0 * Math.PI;
        h = h % TwoPi;
        if (h < 0) h += TwoPi;
        return h;
    }

    private static double PathLength(List<Vec3> path)
    {
        double total = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double dx = path[i].Easting - path[i - 1].Easting;
            double dy = path[i].Northing - path[i - 1].Northing;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static bool AllPointsInsidePolygon(List<Vec3> path, List<Vec2> polygon)
    {
        if (polygon.Count < 3) return true;
        foreach (var pt in path)
        {
            if (!GeometryMath.IsPointInPolygon(polygon, new Vec2(pt.Easting, pt.Northing)))
                return false;
        }
        return true;
    }

    private static bool AnyPointsInsideAnyPolygon(List<Vec3> path, List<List<Vec2>> polygons)
    {
        if (polygons.Count == 0) return false;
        foreach (var pt in path)
        {
            var test = new Vec2(pt.Easting, pt.Northing);
            foreach (var poly in polygons)
            {
                if (poly.Count >= 3 && GeometryMath.IsPointInPolygon(poly, test))
                    return true;
            }
        }
        return false;
    }
}
