// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>Where a U-turn hands over to the line of the pass it is for.</summary>
public static class TurnLanding
{
    /// <summary>
    /// Does the turn hand over to the line it is for beyond either end of that line? Nothing is steered there:
    /// AutoSteer goes off at once ("past end of curve") or the tractor takes the nearest pass. A
    /// pass ends short of the turn where its line stops in the field: the inner passes of an open
    /// curve whose ends face each other, whose two ends meet in a V, each further back than the
    /// last (#694: a turn skipping two rows, built at the end of the pass it left, came out 18 m
    /// past the end of the pass it was for). An AB line and a curve's run-on (200 m past an end that
    /// is not A or B) are never passed.
    /// </summary>
    public static bool PastLineEnd(List<Vec3> path, Models.Track.Track? next)
    {
        // An AB line's next pass is its A and B only, and guidance runs on past them: curves only.
        if (next == null || next.Points.Count <= 2 || next.IsClosed || path.Count == 0) return false;
        // Where the turn hands over to the line: the arc's last few metres are not driven
        // (EarlyCompletionLookahead), and a little more for the steering to settle on the line.
        double back = 0;
        int k = path.Count - 1;
        while (k > 0 && back < HandOverCheckM)
        {
            double sE = path[k].Easting - path[k - 1].Easting, sN = path[k].Northing - path[k - 1].Northing;
            back += Math.Sqrt(sE * sE + sN * sN);
            k--;
        }
        var end = path[k];
        var line = next.Points;
        // The nearest point of the line to where the turn ends: past an end when it is that end
        // itself, more than the tolerance away (in the line, it is abreast of the turn's end).
        double best = double.MaxValue;
        bool atEnd = false;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double sE = b.Easting - a.Easting, sN = b.Northing - a.Northing, l2 = sE * sE + sN * sN;
            double t = l2 < 1e-12 ? 0 : ((end.Easting - a.Easting) * sE + (end.Northing - a.Northing) * sN) / l2;
            bool clampedOut = (i == 1 && t < 0) || (i == line.Count - 1 && t > 1);
            t = Math.Clamp(t, 0, 1);
            double dE = a.Easting + sE * t - end.Easting, dN = a.Northing + sN * t - end.Northing;
            double d = dE * dE + dN * dN;
            if (d < best) { best = d; atEnd = clampedOut; }
        }
        return atEnd && Math.Sqrt(best) > LandingPastEndToleranceM;
    }

    /// <summary>A turn's end this far past its line's end is the line's own point spacing.</summary>
    public const double LandingPastEndToleranceM = 1.0;

    /// <summary>How far back from the turn's end its hand-over to the line is checked: the 4 m of
    /// arc not driven (<c>EarlyCompletionLookahead</c>) and 2 m to settle.</summary>
    public const double HandOverCheckM = 6.0;
}
