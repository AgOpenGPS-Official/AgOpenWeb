// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Track;

/// <summary>
/// Where the passes of a track lie, AgOpenGPS's model (CABLine.BuildCurrentABLineList): the
/// reference line is the edge between two passes, so pass n is
/// <c>(n + ½) × (width − overlap) + nudge</c> from it, positive to the right of A→B. A line
/// drawn on the fence then has its first pass half a width inside; a driven line is moved
/// half a width to the chosen side when it's made, so a pass runs where the tractor drove.
/// A boundary-follow track (<see cref="Track.NoPassOffset"/>) is driven on the line itself.
/// </summary>
public static class PassGeometry
{
    /// <summary>Distance from the reference line to pass 0's centre (without the nudge).</summary>
    public static double EdgeOffset(Track? track, double widthMinusOverlap) =>
        track is { NoPassOffset: true } ? 0 : 0.5 * widthMinusOverlap;

    /// <summary>Signed distance (m, + = right of A→B) from the reference line to pass <paramref name="pass"/>.</summary>
    public static double Offset(Track? track, int pass, double widthMinusOverlap, double nudge = 0) =>
        widthMinusOverlap * pass + EdgeOffset(track, widthMinusOverlap) + nudge;

    /// <summary>The pass whose centre is nearest a point <paramref name="distFromRef"/> from the
    /// reference line (+ = right). Halves round away from zero, as AgOpenGPS does.</summary>
    public static int Nearest(Track? track, double distFromRef, double widthMinusOverlap, double nudge = 0)
    {
        double r = (distFromRef - nudge - EdgeOffset(track, widthMinusOverlap)) / widthMinusOverlap;
        return r < 0 ? (int)(r - 0.5) : (int)(r + 0.5);
    }
}
