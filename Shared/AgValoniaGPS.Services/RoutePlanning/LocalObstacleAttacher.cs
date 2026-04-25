// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Attach local (non-topological) obstacles to the cells produced by
/// <see cref="BoustrophedonDecomp"/> / <see cref="TrapezoidalDecomp"/>.
/// Each obstacle is added to the InnerRings of whichever cell contains its
/// centroid; obstacles whose centroid falls outside every cell are dropped.
///
/// Topological obstacles must be passed to the decomposer instead — they
/// cause cell splits, not inner rings.
/// </summary>
public static class LocalObstacleAttacher
{
    public static void Attach(List<Cell> cells, List<List<Vec2>> localObstacles)
    {
        if (cells == null || cells.Count == 0 || localObstacles == null) return;

        foreach (var obstacle in localObstacles)
        {
            if (obstacle == null || obstacle.Count < 3) continue;
            var centroid = Centroid(obstacle);
            foreach (var cell in cells)
            {
                if (GeometryMath.IsPointInPolygon(cell.Polygon, centroid))
                {
                    cell.InnerRings.Add(obstacle);
                    break;
                }
            }
        }
    }

    private static Vec2 Centroid(List<Vec2> poly)
    {
        double cx = 0, cy = 0;
        foreach (var p in poly) { cx += p.Easting; cy += p.Northing; }
        return new Vec2(cx / poly.Count, cy / poly.Count);
    }
}
