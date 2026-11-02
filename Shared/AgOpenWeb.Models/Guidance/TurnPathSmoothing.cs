// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Guidance;

/// <summary>
/// Smoothing pass helpers for generated turn paths.
/// </summary>
public static class TurnPathSmoothing
{
    /// <summary>
    /// AgOpenGPS's <c>CYouTurn.SmoothYouTurn</c>: one pass of a centre-weighted average over
    /// <paramref name="smPts"/> points, the first and last point dropped, the headings
    /// recomputed from the smoothed positions. The position cycle applies it with 6 points to
    /// a U turn (not a K-turn) once the turn is made.
    /// </summary>
    public static void SmoothYouTurn(List<Vec3> ytList, int smPts)
    {
        int cnt = ytList.Count;
        if (cnt < smPts + 2) return;
        var arr = new Vec3[cnt];

        for (int s = 0; s < smPts / 2; s++)
        {
            arr[s].Easting = ytList[s].Easting;
            arr[s].Northing = ytList[s].Northing;
            arr[s].Heading = ytList[s].Heading;
        }

        for (int s = cnt - (smPts / 2); s < cnt; s++)
        {
            arr[s].Easting = ytList[s].Easting;
            arr[s].Northing = ytList[s].Northing;
            arr[s].Heading = ytList[s].Heading;
        }

        for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
        {
            for (int j = -smPts / 2; j < smPts / 2; j++)
            {
                arr[i].Easting += ytList[j + i].Easting;
                arr[i].Northing += ytList[j + i].Northing;
            }
            arr[i].Easting /= smPts;
            arr[i].Northing /= smPts;
            arr[i].Heading = ytList[i].Heading;
        }

        ytList.Clear();

        for (int i = 1; i < cnt - 1; i++)
        {
            arr[i].Heading = Math.Atan2(arr[i + 1].Easting - arr[i].Easting, arr[i + 1].Northing - arr[i].Northing);
            if (arr[i].Heading < 0) arr[i].Heading += Math.PI * 2.0;
            ytList.Add(arr[i]);
        }
    }

    /// <summary>
    /// Apply an in-place 3-point averaging smooth to the interior of the path.
    /// Endpoints and the points adjacent to them are left untouched so the path
    /// still connects cleanly to its entry/exit segments. Heading is preserved
    /// per-point (only Easting/Northing are averaged).
    /// </summary>
    public static void Smooth(IList<Vec3> path, int passes)
    {
        if (passes <= 1 || path.Count <= 4) return;

        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 2; i < path.Count - 2; i++)
            {
                var prev = path[i - 1];
                var curr = path[i];
                var next = path[i + 1];

                path[i] = new Vec3
                {
                    Easting = (prev.Easting + curr.Easting + next.Easting) / 3.0,
                    Northing = (prev.Northing + curr.Northing + next.Northing) / 3.0,
                    Heading = curr.Heading,
                };
            }
        }
    }
}
