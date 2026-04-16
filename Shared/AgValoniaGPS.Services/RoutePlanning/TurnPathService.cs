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
/// Generates boundary-validated Dubins turn paths between consecutive swaths.
///
/// Algorithm:
/// 1. Extend exit/entry points outward along heading into the headland zone
/// 2. Generate all 6 Dubins path types between the extended points
/// 3. Build full turn path for each: exit leg + Dubins arc + entry leg
/// 4. Check every point against the outer boundary
/// 5. Return the shortest path where all points are inside
/// 6. If none fit, try progressively shorter leg lengths
/// 7. If still none fit, return shortest path with IsValid = false
/// </summary>
public class TurnPathService : ITurnPathService
{
    public TurnPathResult GenerateTurn(TurnPathInput input)
    {
        var boundaryVec2 = input.Boundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        var innerBoundariesVec2 = input.InnerBoundaries
            .Where(b => b.Points.Count >= 3)
            .Select(b => b.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList())
            .ToList();

        // Try with progressively shorter leg lengths.
        // Start with full headland-based leg, shrink by 25% each attempt, min 2m.
        double baseLeg = Math.Max(input.HeadlandWidth - 1.5 * input.TurningRadius, 2.0);
        TurnPathResult? bestInvalid = null;

        for (double legScale = 1.0; legScale >= 0.25; legScale -= 0.25)
        {
            double legLength = Math.Max(baseLeg * legScale, 2.0);
            var result = TryGenerateTurn(input, legLength, boundaryVec2, innerBoundariesVec2);

            if (result.IsValid)
                return result;

            // Keep track of shortest invalid path as fallback
            if (bestInvalid == null || result.Length < bestInvalid.Length)
                bestInvalid = result;
        }

        // No valid path found at any leg length — return best-effort
        return bestInvalid ?? new TurnPathResult { IsValid = false };
    }

    private static TurnPathResult TryGenerateTurn(
        TurnPathInput input, double legLength, List<Vec2> boundaryVec2,
        List<List<Vec2>> innerBoundariesVec2)
    {
        // Extend exit point outward along exit heading
        double exitDirE = Math.Sin(input.ExitHeading);
        double exitDirN = Math.Cos(input.ExitHeading);
        var dubinsStart = new Vec3(
            input.ExitPoint.Easting + exitDirE * legLength,
            input.ExitPoint.Northing + exitDirN * legLength,
            input.ExitHeading);

        // Extend entry point outward along reversed entry heading (approach from opposite direction)
        double entryDirE = Math.Sin(input.EntryHeading + Math.PI);
        double entryDirN = Math.Cos(input.EntryHeading + Math.PI);
        var dubinsGoal = new Vec3(
            input.EntryPoint.Easting + entryDirE * legLength,
            input.EntryPoint.Northing + entryDirN * legLength,
            input.EntryHeading);

        // Generate all 6 Dubins path types
        var dubins = new DubinsPathService(input.TurningRadius);
        var allPaths = dubins.GenerateAllPaths(dubinsStart, dubinsGoal);

        if (allPaths.Count == 0)
            return new TurnPathResult { IsValid = false };

        // For each candidate, build full turn path and check boundary containment
        TurnPathResult? bestValid = null;
        TurnPathResult? bestAny = null;

        foreach (var (arcPath, pathType, arcLength) in allPaths)
        {
            if (arcPath.Count == 0) continue;

            // Build full turn: exit leg + Dubins arc + entry leg
            var fullPath = new List<Vec3>();
            fullPath.Add(new Vec3(input.ExitPoint.Easting, input.ExitPoint.Northing, input.ExitHeading));
            fullPath.AddRange(arcPath);
            fullPath.Add(new Vec3(input.EntryPoint.Easting, input.EntryPoint.Northing, input.EntryHeading));

            // Calculate total length
            double totalLength = 0;
            for (int j = 1; j < fullPath.Count; j++)
            {
                double dx = fullPath[j].Easting - fullPath[j - 1].Easting;
                double dy = fullPath[j].Northing - fullPath[j - 1].Northing;
                totalLength += Math.Sqrt(dx * dx + dy * dy);
            }

            bool insideOuter = AllPointsInsideBoundary(fullPath, boundaryVec2);
            bool crossesInner = AnyPointsInsideAnyInner(fullPath, innerBoundariesVec2);

            var result = new TurnPathResult
            {
                Waypoints = fullPath,
                Length = totalLength,
                PathType = pathType,
                IsValid = insideOuter && !crossesInner
            };

            if (result.IsValid)
            {
                if (bestValid == null || result.Length < bestValid.Length)
                    bestValid = result;
            }

            if (bestAny == null || result.Length < bestAny.Length)
                bestAny = result;
        }

        // Return shortest valid path, or shortest overall if none valid
        if (bestValid != null)
            return bestValid;

        return bestAny ?? new TurnPathResult { IsValid = false };
    }

    private static bool AllPointsInsideBoundary(List<Vec3> path, List<Vec2> boundary)
    {
        foreach (var pt in path)
        {
            if (!GeometryMath.IsPointInPolygon(boundary, new Vec2(pt.Easting, pt.Northing)))
                return false;
        }
        return true;
    }

    private static bool AnyPointsInsideAnyInner(List<Vec3> path, List<List<Vec2>> innerBoundaries)
    {
        if (innerBoundaries.Count == 0) return false;
        foreach (var pt in path)
        {
            var test = new Vec2(pt.Easting, pt.Northing);
            foreach (var inner in innerBoundaries)
            {
                if (GeometryMath.IsPointInPolygon(inner, test))
                    return true;
            }
        }
        return false;
    }
}
