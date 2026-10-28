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
/// <para>A pass is where the tool runs. With a tool offset the tractor steers beside it, by the
/// <see cref="ToolShift"/>: the tool to the right going A→B is to the left coming back, so the
/// line the tractor steers moves to the other side of the pass (AgOpenGPS, #446). Where a pass
/// lies on the ground (boundary checks, coverage) takes no tool shift.</para>
/// </summary>
public static class PassGeometry
{
    /// <summary>Distance from the reference line to pass 0's centre (without the nudge).</summary>
    public static double EdgeOffset(Track? track, double widthMinusOverlap) =>
        track is { NoPassOffset: true } ? 0 : 0.5 * widthMinusOverlap;

    /// <summary>Where the tool runs beside the tractor (m, + = right of A→B): the tool offset
    /// (+ = right of the tractor) as seen along the track.</summary>
    public static double ToolShift(double toolOffset, bool isHeadingSameWay) =>
        isHeadingSameWay ? toolOffset : -toolOffset;

    /// <summary>Signed distance (m, + = right of A→B) from the reference line to pass
    /// <paramref name="pass"/>; with a <paramref name="toolShift"/>, to the line the tractor
    /// steers so that the tool runs on the pass.</summary>
    public static double Offset(Track? track, int pass, double widthMinusOverlap, double nudge = 0, double toolShift = 0) =>
        widthMinusOverlap * pass + EdgeOffset(track, widthMinusOverlap) + nudge - toolShift;

    /// <summary>The pass whose centre is nearest a point <paramref name="distFromRef"/> from the
    /// reference line (+ = right); with a <paramref name="toolShift"/>, the pass nearest the tool
    /// of a tractor there. Halves round away from zero, as AgOpenGPS does.</summary>
    public static int Nearest(Track? track, double distFromRef, double widthMinusOverlap, double nudge = 0, double toolShift = 0)
    {
        double r = (distFromRef + toolShift - nudge - EdgeOffset(track, widthMinusOverlap)) / widthMinusOverlap;
        return r < 0 ? (int)(r - 0.5) : (int)(r + 0.5);
    }
}
