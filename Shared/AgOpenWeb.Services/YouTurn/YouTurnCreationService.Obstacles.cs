// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.YouTurn;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The obstacles the automatic U-turn is made at: every valid inner boundary whose choice is
/// <see cref="ObstacleAction.Turn"/> gets a turn line, the obstacle offset outward by the U-turn
/// distance from boundary, as AgOpenGPS builds a turn line round every inner boundary that is
/// not drive-through (CTurn.BuildTurnLines). A pass that runs into one turns there, before the
/// obstacle, the same way it turns at the outer boundary's turn line.
/// </summary>
public partial class YouTurnCreationService
{
    /// <param name="InnerIndex">The obstacle's index in <see cref="Boundary.InnerBoundaries"/>.</param>
    /// <param name="TurnLine">The turn line, with headings (the generator walks it).</param>
    /// <param name="Area">Inside the turn line: no part of a turn may be there.</param>
    /// <param name="Fence">The obstacle itself, for the hard-boundary clearance.</param>
    private sealed record TurnObstacle(int InnerIndex, List<Vec3> TurnLine, PolygonSlabIndex Area,
        List<Vec2> Fence, bool IsHard);

    // Built once per boundary, choice and distance: a turn is planned again every cycle while it
    // fails, and a field may have dozens of obstacles.
    private (Boundary? Boundary, double Distance, long Key, List<TurnObstacle> List) _turnObstacles =
        (null, 0, 0, new List<TurnObstacle>());

    private List<TurnObstacle> TurnObstacles(Boundary boundary, double distance)
    {
        if (boundary.InnerBoundaries.Count == 0) return EmptyObstacles;
        long key = ObstacleKey(boundary);
        var cached = _turnObstacles;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Distance == distance && cached.Key == key)
            return cached.List;

        var list = new List<TurnObstacle>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.Turn) continue;

            var fence = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            // > 0: the turn stays that far from the obstacle; < 0: it may reach that far into it.
            List<Vec2>? line = distance > 0.1 ? _polygonOffsetService.CreateOutwardOffset(fence, distance)
                : distance < -0.1 ? _polygonOffsetService.CreateInwardOffset(fence, -distance)
                : fence;
            if (line == null || line.Count < 3) continue; // shrunk away: nothing left to turn at

            var withHeadings = _polygonOffsetService.CalculatePointHeadings(line);
            var area = PolygonSlabIndex.Build(withHeadings);
            if (area == null) continue;
            list.Add(new TurnObstacle(i, withHeadings, area, fence, inner.IsHard));
        }

        _turnObstacles = (boundary, distance, key, list);
        return list;
    }

    private static readonly List<TurnObstacle> EmptyObstacles = new();

    // A choice changed on the same boundary object, or an obstacle was redrawn.
    private static long ObstacleKey(Boundary boundary)
    {
        long h = boundary.InnerBoundaries.Count;
        foreach (var inner in boundary.InnerBoundaries)
            h = ((h * 31 + inner.Points.Count) * 31 + (int)inner.AtObstacle) * 31 + (inner.IsHard ? 1 : 0);
        return h;
    }

    /// <summary>
    /// The turn area test with the obstacles, AgOpenGPS's IsPointInsideTurnArea: -1 outside the
    /// outer turn line, 0 in the workable area, k inside obstacle <paramref name="obstacles"/>[k - 1]'s
    /// turn line (turn line k of the input, the outer one being 0).
    /// </summary>
    private static Func<Vec3, int> TurnAreaTest(PolygonSlabIndex? outerArea, List<TurnObstacle> obstacles)
    {
        if (obstacles.Count == 0)
            return point => outerArea != null && outerArea.Contains(point.Easting, point.Northing) ? 0 : -1;
        return point =>
        {
            if (outerArea == null || !outerArea.Contains(point.Easting, point.Northing)) return -1;
            for (int k = 0; k < obstacles.Count; k++)
                if (obstacles[k].Area.Contains(point.Easting, point.Northing)) return k + 1;
            return 0;
        };
    }

    /// <summary>
    /// The obstacle the current pass runs into before anything else, walking it from the tractor
    /// the way the generator looks for the turn (FindCurveTurnPoint), or null when the first
    /// thing ahead is the outer turn line or nothing.
    /// </summary>
    private static TurnObstacle? ObstacleAhead(YouTurnCreationInput input, List<TurnObstacle> obstacles)
    {
        if (obstacles.Count == 0) return null;
        int step = input.IsHeadingSameWay ? 1 : -1;
        var points = input.GuidancePoints;
        for (int j = input.CurrentLocationIndex; j > 0 && j < points.Count; j += step)
        {
            int area = input.IsPointInsideTurnArea(points[j]);
            if (area == 0) continue;
            return area > 0 && area <= obstacles.Count ? obstacles[area - 1] : null;
        }
        return null;
    }
}
