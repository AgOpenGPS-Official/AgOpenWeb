// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;

using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// Owns the pass / next-track book-keeping for the YouTurn state machine:
/// - choosing the offset to the next pass (normal / skip / snake modes),
/// - building the offset track that represents the next pass,
/// - validating that the next pass fits inside the cultivated area,
/// - building and advancing the pre-computed snake-pattern sequence,
/// - computing skip distances around already-worked passes.
///
/// The service mutates the provided <see cref="YouTurnState"/> and
/// <see cref="GuidanceState"/> but never touches UI services — map updates
/// stay on the ViewModel side.
/// </summary>
public sealed class YouTurnPathingService
{
    private readonly ILogger<YouTurnPathingService> _logger;
    private readonly ConfigurationStore _configStore;
    private readonly PolygonIndexCache _headlandIndex = new(); // the cultivated-area test of every offset point

    public YouTurnPathingService(ILogger<YouTurnPathingService> logger, ConfigurationStore configStore)
    {
        _logger = logger;
        _configStore = configStore;
    }

    /// <summary>
    /// Compute the next track offset perpendicular to the current line and stash
    /// it on <see cref="YouTurnState.NextTrack"/> / <see cref="YouTurnState.NextTrackTurnOffset"/>.
    /// </summary>
    public void ComputeNextTrack(
        Models.Track.Track referenceTrack,
        double abHeading,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        int uTurnSkipRows,
        bool isSkipWorkedMode,
        Models.Track.Track? selectedTrack)
    {
        if (referenceTrack.Points.Count < 2) return;

        var refPointA = referenceTrack.Points[0];
        var refPointB = referenceTrack.Points[referenceTrack.Points.Count - 1];
        var config = _configStore;

        // Offset direction via XOR of turn direction and travel direction:
        //   turnLeft=true,  sameWay=true  -> negative
        //   turnLeft=true,  sameWay=false -> positive
        //   turnLeft=false, sameWay=true  -> positive
        //   turnLeft=false, sameWay=false -> negative
        int pathsToMove = uTurnSkipRows + 1; // skip=0 moves 1 pass, skip=1 moves 2, etc.
        bool positiveOffset = turn.IsTurnLeft ^ guidance.IsHeadingSameWay;

        // Skip-worked mode: find next unworked path instead of fixed skip.
        if (isSkipWorkedMode && selectedTrack != null)
        {
            pathsToMove = GetNextUnworkedPathSkip(selectedTrack, guidance.HowManyPathsAway, positiveOffset, pathsToMove);
        }

        int offsetChange = positiveOffset ? pathsToMove : -pathsToMove;
        int nextPathsAway = guidance.HowManyPathsAway + offsetChange;

        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        // Include the nudge so the cyan next-track line matches the U-turn exit leg and the
        // line the tractor steers after completion exactly (all use PassGeometry.Offset).
        // The next pass is driven the other way, so its tool shift has the other sign (#446).
        double toolShift = PassGeometry.ToolShift(config.Tool.Offset, guidance.IsHeadingSameWay);
        double nextDistAway = PassGeometry.Offset(referenceTrack, nextPathsAway, widthMinusOverlap, guidance.NudgeOffset, -toolShift);
        double currentDistAway = PassGeometry.Offset(referenceTrack, guidance.HowManyPathsAway, widthMinusOverlap, guidance.NudgeOffset, toolShift);

        // Authoritative perpendicular width for the U-turn arc: from the line steered now to the
        // next, the passes apart ± twice the tool offset, signed in the turn direction as
        // AgOpenGPS's turnOffset. A tool offset over half the pass spacing makes it negative:
        // the tractor's next line is on the other side, and the arc goes round that way.
        turn.NextTrackTurnOffset = (nextDistAway - currentDistAway) * (positiveOffset ? 1 : -1);

        Models.Track.Track nextTrack;
        if (referenceTrack.Points.Count == 2)
        {
            double perpAngle = abHeading + Math.PI / 2;
            double offsetEasting = Math.Sin(perpAngle) * nextDistAway;
            double offsetNorthing = Math.Cos(perpAngle) * nextDistAway;

            nextTrack = Models.Track.Track.FromABLine(
                $"Path {nextPathsAway}",
                new Vec3(refPointA.Easting + offsetEasting, refPointA.Northing + offsetNorthing, abHeading),
                new Vec3(refPointB.Easting + offsetEasting, refPointB.Northing + offsetNorthing, abHeading));
        }
        else
        {
            // Offset then EXTEND the ends — same order as the U-turn exit leg
            // (BuildNewOffsetCurveList) and the post-turn active line — so the cyan
            // next-track curve reaches the exit leg's end (no gap) and is the same
            // length as the magenta line that replaces it after the turn completes.
            // ExtendCurveEnds is a no-op on closed loops.
            var offsetPoints = CurveProcessing.ExtendPass(CurveProcessing.CurvePass(referenceTrack.Points, nextDistAway));
            nextTrack = Models.Track.Track.FromCurve($"Path {nextPathsAway}", offsetPoints, referenceTrack.IsClosed);
        }
        nextTrack.IsActive = false;
        turn.NextTrack = nextTrack;
        turn.PlannedNextPath = nextPathsAway; // the turn lands here, whatever the skip is by then (#585)

        _logger.LogDebug("[YouTurn] Turn {Dir}, heading {Way} way",
            turn.IsTurnLeft ? "LEFT" : "RIGHT",
            guidance.IsHeadingSameWay ? "SAME" : "OPPOSITE");
        _logger.LogDebug("[YouTurn] Offset {Sign}: path {Cur} -> {Next} ({Dist:F1}m)",
            positiveOffset ? "positive" : "negative",
            guidance.HowManyPathsAway, nextPathsAway, nextDistAway);
    }

    /// <summary>
    /// True if *any portion* of the next pass line (as determined by the normal turn rule —
    /// turnLeft XOR sameWay — with no skip-worked adjustment) crosses the cultivated area.
    /// </summary>
    /// <remarks>
    /// Fixes #289 F1. The previous implementation tested only the midpoint of the AB segment
    /// offset perpendicular — for non-rectangular fields the AB midpoint is not the center of
    /// every offset pass's usable span, so a midpoint can fall outside the polygon even when
    /// the offset line still crosses the field somewhere along its 2000 m rendered extent.
    /// That false-negative stopped U-turns early, leaving unworked strips on the far side of
    /// non-rectangular fields. New implementation walks the full offset line at fixed spacing
    /// and returns true if any sample lies inside the cultivated area.
    /// </remarks>
    /// <summary>
    /// Check if the next pass line (in either direction) falls inside the cultivated area.
    /// Returns (isInside, positiveDirection) where positiveDirection indicates which
    /// offset direction has cultivated area. With both sides inside, <paramref name="keepPositive"/>
    /// (the way the last turn stepped) wins; without it, the side away from the reference line.
    /// Given the tractor's <paramref name="position"/> and its distance to the headland ahead, a
    /// side only counts when its pass can be landed on from here (<see cref="IsPathLandable"/>).
    /// Without the position, with <paramref name="minInside"/> a side counts only when its pass has
    /// that much inside the area in all (<see cref="InsideLength"/>): the pass along the edge the
    /// operator started beside has a sliver inside (15 m of pass -15 on Olival, J20), which made
    /// both sides count and the tie-break sent the first turn at the edge.
    /// </summary>
    public (bool isInside, bool positiveDirection) WouldNextLineBeInsideBoundary(
        Models.Track.Track currentTrack,
        double abHeading,
        GuidanceWorkingState guidance,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        int uTurnSkipRows,
        bool? keepPositive = null,
        Position? position = null,
        double headlandAhead = 0,
        double minInside = 0)
    {
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid)
            return (true, false);
        if (currentTrack.Points.Count < 2)
            return (true, false);

        var config = _configStore;
        int pathsToMove = uTurnSkipRows + 1;
        int nextPathsAwayNeg = guidance.HowManyPathsAway - pathsToMove;
        int nextPathsAwayPos = guidance.HowManyPathsAway + pathsToMove;

        // With the tractor's position, a pass must also be long enough inside at this end to
        // land a turn on (IsPathLandable); without it, inside anywhere.
        bool Inside(int pathsAway) => minInside > 0
            ? InsideLength(currentTrack, abHeading, pathsAway, boundary, headlandLine, stopAt: minInside) >= minInside
            : CheckOffsetLineInsideCultivated(currentTrack, abHeading, pathsAway, boundary, headlandLine, config);
        bool negInside = position is { } pn
            ? IsPathLandable(currentTrack, abHeading, nextPathsAwayNeg, boundary, headlandLine, pn, guidance.IsHeadingSameWay, headlandAhead, guidance.HowManyPathsAway)
            : Inside(nextPathsAwayNeg);
        bool posInside = position is { } pp
            ? IsPathLandable(currentTrack, abHeading, nextPathsAwayPos, boundary, headlandLine, pp, guidance.IsHeadingSameWay, headlandAhead, guidance.HowManyPathsAway)
            : Inside(nextPathsAwayPos);

        if (negInside && posInside)
        {
            // Both directions work: keep stepping the way the last turn went. The pass number's
            // sign says nothing about which side is worked: started from the field's edge on the
            // negative side, "away from 0" sent every turn back over worked passes (#523). Before
            // the first turn, away from the reference line (on pass 0, negative).
            return (true, keepPositive ?? guidance.HowManyPathsAway > 0);
        }
        if (negInside) return (true, false);
        if (posInside) return (true, true);
        return (false, false);
    }

    /// <summary>
    /// How much of pass <paramref name="pathsAway"/> lies inside the cultivated area, in metres,
    /// over its whole rendered length (a curve: its points' segments; an AB line: 5 m samples),
    /// counted up to <paramref name="stopAt"/>. Any part of a pass counts for whether the pass is
    /// in the field at all (<see cref="IsPathInsideCultivated"/>); this says whether there is a
    /// pass worth driving there, the landing length (<see cref="MinLandingLength"/>) or more.
    /// </summary>
    public double InsideLength(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, double stopAt = double.MaxValue)
    {
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid || track.Points.Count < 2) return stopAt;
        var config = _configStore;
        double distAway = PassGeometry.Offset(track, pathsAway, config.ActualToolWidth - config.Tool.Overlap);
        double length = 0;
        if (track.Points.Count > 2)
        {
            var curve = CurveProcessing.CreateOffsetCurve(track.Points, distAway);
            for (int i = 1; i < curve.Count && length < stopAt; i++)
                if (IsPointInsideCultivatedArea(curve[i].Easting, curve[i].Northing, boundary, headlandLine))
                    length += GeometryMath.Distance(curve[i - 1].Easting, curve[i - 1].Northing, curve[i].Easting, curve[i].Northing);
            return length;
        }
        var a = track.Points[0];
        var b = track.Points[track.Points.Count - 1];
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01) return 0;
        double nx = dx / len, ny = dy / len, perp = abHeading + Math.PI / 2;
        double midE = (a.Easting + b.Easting) / 2 + Math.Sin(perp) * distAway;
        double midN = (a.Northing + b.Northing) / 2 + Math.Cos(perp) * distAway;
        const double Spacing = 5.0, Extent = 2000.0;
        double total = len + 2 * Extent;
        for (double t = -total / 2; t <= total / 2 && length < stopAt; t += Spacing)
            if (IsPointInsideCultivatedArea(midE + nx * t, midN + ny * t, boundary, headlandLine))
                length += Spacing;
        return length;
    }

    /// <summary>Would pass <paramref name="pathsAway"/> lie inside the cultivated area?
    /// True when there's no usable boundary (nothing to check against).</summary>
    public bool IsPathInsideCultivated(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine)
    {
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid) return true;
        if (track.Points.Count < 2) return true;
        return CheckOffsetLineInsideCultivated(track, abHeading, pathsAway, boundary, headlandLine, _configStore);
    }

    /// <summary>
    /// The least of a pass, inside the cultivated area back from the end the turn is made at,
    /// that a U-turn may land on. A pass counts as inside when any point of it is (an L-shaped
    /// field's arm), but a turn onto a pass with only a few metres inside at THIS end drives out
    /// of the field: the tractor comes off the arc in the headland, where no turn can be made,
    /// and AutoSteer follows the line out through the boundary (an olive grove's 4 m corner
    /// pass; an hourglass field's pass clipping the far lobe's bay, 40 % of the field left; a
    /// pass entered at a corner the next pass leaves 15 m on). Twenty metres is room to settle
    /// on the line and plan the next turn (turns are created from 10 m before the headland).
    /// </summary>
    public const double MinLandingLength = 20.0;

    /// <summary>
    /// Can a U-turn made at the end the tractor is heading for (<paramref name="headlandAhead"/> m
    /// ahead of <paramref name="position"/>) land on pass <paramref name="pathsAway"/>: is it
    /// inside the cultivated area, with at least <see cref="MinLandingLength"/> of it to drive
    /// back on from there, or, given the pass the tractor is on (<paramref name="fromPath"/>), at
    /// least as much as that one has at this end? True when there is no usable boundary.
    /// </summary>
    /// <remarks>
    /// The second rule is for a field entered at a tapering corner: the first passes are short
    /// and each is longer than the last, so the next one has less than 20 m but more than the
    /// tractor is driving now. Twenty metres alone stopped the job at the first pass ("End of
    /// field reached") on an olive grove whose corner pass runs 11 m and the next 19 m. A pass
    /// shorter than both (the sliver at the far side) is still refused.
    /// </remarks>
    public bool IsPathLandable(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, Position position, bool headingSameWay, double headlandAhead,
        int? fromPath = null)
    {
        if (!IsPathInsideCultivated(track, abHeading, pathsAway, boundary, headlandLine)) return false;
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid) return true;
        if (track.Points.Count < 2) return true;
        // Only whether there is enough: stop counting there (the walk is up to 2 km of
        // point-in-polygon tests, and an end of the field asks again every cycle).
        double landing = LandingLength(track, abHeading, pathsAway, boundary, headlandLine, position, headingSameWay, headlandAhead,
            stopAt: MinLandingLength, fromPath: fromPath);
        if (landing >= MinLandingLength) return true;
        // Widening, not narrowing: the pass driven now has no more at this end than the next.
        double current = fromPath is int from && from != pathsAway
            ? LandingLength(track, abHeading, from, boundary, headlandLine, position, headingSameWay, headlandAhead, stopAt: MinLandingLength)
            : double.MaxValue;
        if (landing > 0 && landing >= current) return true;
        _logger.LogDebug("[NextTrack] nextPath={Next} has only {Len:F1}m inside the field at this end (this pass {Cur:F1}m) - no turn onto it",
            pathsAway, landing, current);
        return false;
    }

    /// <summary>
    /// Metres of pass <paramref name="pathsAway"/> a turn made at the end the tractor is heading
    /// for lands on: from the point of that pass abreast of the end (<paramref name="headlandAhead"/>
    /// m ahead of the tractor), the run of it inside the cultivated area back the way the tractor
    /// came, which is the way it drives after the turn. Inside the headland line; obstacles are
    /// not counted. When that pass ends sooner, its first run back of the end, however far (a
    /// wide turn lands in the headland and drives to it). 0 when there is none. With
    /// <paramref name="stopAt"/> the run is measured no further than that.
    /// </summary>
    public double LandingLength(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, Position position, bool headingSameWay, double headlandAhead,
        double stopAt = double.MaxValue, int? fromPath = null)
        => Landing(track, abHeading, pathsAway, boundary, headlandLine, position, headingSameWay, headlandAhead, stopAt, fromPath).Length;

    /// <summary>
    /// The landing a turn at this end has on a pass (<see cref="LandingLength"/>), and why its run
    /// ended short of <paramref name="stopAt"/>: <see cref="LandingRun.FenceGap"/> when the pass
    /// leaves the cultivated area there but not the fence and comes back into the area further on
    /// (it runs along the fence nearer than the U-turn distance: J9 and J18 on Olival, the next
    /// pass 1.0 and 0.1 m from the fence 15 and 5 m after the turn), with how near it comes;
    /// null when the pass ends there.
    /// </summary>
    public readonly record struct LandingRun(double Length, double? FenceGap);

    /// <remarks>
    /// Measured from where the turn lands. A U-turn lands abreast of the end. A wide turn (the step
    /// from pass <paramref name="fromPath"/>, the one the tractor is on, more than twice the turn
    /// radius: <see cref="YouTurnCreationService"/>) runs along the turn line and comes into the
    /// next pass where that pass first leaves the turn area going on from abreast of the tractor
    /// (from the headland, after it comes into it), which can be short of the end: a slit in the
    /// fence (a track or ditch drawn into the boundary) cutting across the passes at a slant put
    /// the point of the next pass abreast of the end on its far side; measured from there, the
    /// walk back crossed the slit (0 m), and the turn was refused with the next pass on this side
    /// of it (field job J11 on Couto Ribeira, 2026-11-16, a 42 m step and a 10 m radius; 24 % of
    /// the field left).
    /// </remarks>
    public LandingRun Landing(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, Position position, bool headingSameWay, double headlandAhead,
        double stopAt = double.MaxValue, int? fromPath = null)
    {
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        double distAway = PassGeometry.Offset(track, pathsAway, widthMinusOverlap);
        bool wideTurn = fromPath is int from && from != pathsAway
            && Math.Abs(distAway - PassGeometry.Offset(track, from, widthMinusOverlap)) > 2.0 * config.Guidance.UTurnRadius;
        const double Step = 1.0;
        const double RunBack = 2000.0;
        double runLimit = Math.Min(RunBack, stopAt);
        double ahead = Math.Clamp(double.IsFinite(headlandAhead) ? headlandAhead : 0, 0, 2000);

        if (track.Points.Count > 2)
        {
            var curve = CurveProcessing.CreateOffsetCurve(track.Points, distAway);
            if (curve.Count < 2) return default;
            int near = 0;
            double best = double.MaxValue;
            for (int i = 0; i < curve.Count; i++)
            {
                double de = curve[i].Easting - position.Easting, dn = curve[i].Northing - position.Northing;
                double d = de * de + dn * dn;
                if (d < best) { best = d; near = i; }
            }
            // Does the curve's point order run the way the tractor is heading?
            int i1 = Math.Min(near + 1, curve.Count - 1), i0 = Math.Max(near - 1, 0);
            double tdx = curve[i1].Easting - curve[i0].Easting, tdy = curve[i1].Northing - curve[i0].Northing;
            double travel = headingSameWay ? abHeading : abHeading + Math.PI;
            int fwd = tdx * Math.Sin(travel) + tdy * Math.Cos(travel) >= 0 ? 1 : -1;
            bool InsideAt(int i) => IsPointInsideCultivatedArea(curve[i].Easting, curve[i].Northing, boundary, headlandLine);
            double Dist(int a, int b)
            {
                double de = curve[a].Easting - curve[b].Easting, dn = curve[a].Northing - curve[b].Northing;
                return Math.Sqrt(de * de + dn * dn);
            }
            // The point abreast of the end ahead.
            int at = near;
            for (double walked = 0; at + fwd >= 0 && at + fwd < curve.Count && walked < ahead; at += fwd)
                walked += Dist(at, at + fwd);
            if (wideTurn)
            {
                // A wide turn lands where the pass first leaves the turn area ahead (remarks); from
                // the headland, after it comes into it.
                int first = near;
                while (first != at && !InsideAt(first)) first += fwd;
                if (first != at)
                {
                    while (first != at && InsideAt(first + fwd)) first += fwd;
                    at = first;
                }
            }
            if (!InsideAt(at))
            {
                // As for an AB line below: outside the fence at first, but not out of the field again.
                bool found = false, inField = false;
                for (double walked = 0; at - fwd >= 0 && at - fwd < curve.Count && walked <= RunBack; at -= fwd)
                {
                    walked += Dist(at, at - fwd);
                    if (InsideAt(at - fwd)) { at -= fwd; found = true; break; }
                    bool inFence = InsideFence(curve[at - fwd].Easting, curve[at - fwd].Northing, boundary);
                    if (inFence) inField = true;
                    else if (inField) return default;
                }
                if (!found) return default;
            }
            double length = 0;
            int stop = at - fwd;
            for (; stop >= 0 && stop < curve.Count && length <= runLimit && InsideAt(stop); stop -= fwd)
                length += Dist(stop, stop + fwd);
            if (length > runLimit || stop < 0 || stop >= curve.Count) return new LandingRun(length, null);
            // Why the run ended: along the fence nearer than the area allows, or the pass's end.
            double nearest = double.MaxValue;
            for (int i = stop; i >= 0 && i < curve.Count; i -= fwd)
            {
                if (InsideAt(i)) return new LandingRun(length, nearest);
                if (!InsideFence(curve[i].Easting, curve[i].Northing, boundary)) break;
                nearest = Math.Min(nearest, FenceDistance(curve[i].Easting, curve[i].Northing, boundary));
            }
            return new LandingRun(length, null);
        }

        var pointA = track.Points[0];
        var pointB = track.Points[track.Points.Count - 1];
        double abDx = pointB.Easting - pointA.Easting, abDy = pointB.Northing - pointA.Northing;
        double abLen = Math.Sqrt(abDx * abDx + abDy * abDy);
        if (abLen < 0.01) return default;
        double abNx = abDx / abLen, abNy = abDy / abLen;
        double perpAngle = abHeading + Math.PI / 2;
        double midE = (pointA.Easting + pointB.Easting) / 2 + Math.Sin(perpAngle) * distAway;
        double midN = (pointA.Northing + pointB.Northing) / 2 + Math.Cos(perpAngle) * distAway;
        // Along the line, + towards B; s is the tractor's way along it.
        double s = headingSameWay ? 1 : -1;
        double t0 = (position.Easting - midE) * abNx + (position.Northing - midN) * abNy;
        bool InsideAtT(double t) => IsPointInsideCultivatedArea(midE + abNx * t, midN + abNy * t, boundary, headlandLine);

        // The point abreast of the end ahead; when the pass ends sooner, the first inside behind it.
        double tAt = t0 + s * ahead;
        double Edge(double tIn, double tOut)
        {
            // The inside point nearest tOut, to a few centimetres.
            for (int k = 0; k < 6; k++)
            {
                double m = (tIn + tOut) / 2;
                if (InsideAtT(m)) tIn = m; else tOut = m;
            }
            return tIn;
        }
        double tFront = tAt;
        if (wideTurn)
        {
            // A wide turn lands where the pass first leaves the turn area ahead (remarks); from
            // the headland, after it comes into it.
            double t = t0;
            while (s * (tAt - t) > Step && !InsideAtT(t)) t += s * Step;
            if (s * (tAt - t) > Step)
            {
                while (s * (tAt - t) > Step && InsideAtT(t + s * Step)) t += s * Step;
                if (s * (tAt - t) > Step) { tFront = Edge(t, t + s * Step); tAt = t; }
            }
        }
        if (!InsideAtT(tAt))
        {
            // Back from the end to the first of the pass in the cultivated area: the turn lands in
            // the headland and the tractor drives there. Outside the fence at first is the angled
            // end of a field; once inside, the pass must not leave the field again on the way
            // (a bay in the fence between, W1 on Baetas: AutoSteer drove out through it).
            bool found = false, inField = false;
            for (double d = Step; d <= RunBack; d += Step)
            {
                double t = tAt - s * d;
                if (InsideAtT(t)) { tAt = t; found = true; break; }
                bool inFence = InsideFence(midE + abNx * t, midN + abNy * t, boundary);
                if (inFence) inField = true;
                else if (inField) return default;
            }
            if (!found) return default;
            tFront = Edge(tAt, tAt + s * Step);
        }
        // The run back from there.
        double back = 0;
        for (double d = Step; d <= runLimit + Step && InsideAtT(tAt - s * d); d += Step) back = d;
        double tBack = Edge(tAt - s * back, tAt - s * (back + Step));
        double run = Math.Abs(tFront - tBack);
        if (back > runLimit) return new LandingRun(run, null);
        // Why the run ended: along the fence nearer than the area allows, or the pass's end.
        double closest = double.MaxValue;
        for (double d = back + Step; d <= RunBack; d += Step)
        {
            double t = tAt - s * d, e = midE + abNx * t, n = midN + abNy * t;
            if (InsideAtT(t)) return new LandingRun(run, closest);
            if (!InsideFence(e, n, boundary)) break;
            closest = Math.Min(closest, FenceDistance(e, n, boundary));
        }
        return new LandingRun(run, null);
    }

    /// <summary>
    /// Why no turn can land on pass <paramref name="pathsAway"/> at this end, in the operator's
    /// words, with what was needed and what was there, as the other refusals say it
    /// (<see cref="YouTurnStateMachine.NoTurnFitsMessage"/>); null when it can (<see cref="IsPathLandable"/>).
    /// The operator drives the pass to its end and takes over there.
    /// </summary>
    public string? NoLandingMessage(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, Position position, bool headingSameWay, double headlandAhead,
        int? fromPath = null)
    {
        if (IsPathLandable(track, abHeading, pathsAway, boundary, headlandLine, position, headingSameWay, headlandAhead, fromPath))
            return null;
        var run = Landing(track, abHeading, pathsAway, boundary, headlandLine, position, headingSameWay, headlandAhead, MinLandingLength, fromPath);
        return NoLandingMessage(run, _configStore.Guidance.UTurnDistanceFromBoundary);
    }

    /// <summary>The operator's words for a landing too short (<see cref="NoLandingMessage(Models.Track.Track, double, int, Boundary?, IReadOnlyList{Vec3}?, Position, bool, double, int?)"/>).</summary>
    /// <remarks>Along the fence only when the pass comes nearer it than the U-turn distance: a gap
    /// in the area further in (a bay of the headland line) leaves a short piece, said as such.</remarks>
    public static string NoLandingMessage(LandingRun run, double distanceFromBoundary) => run.FenceGap is double gap && gap < distanceFromBoundary
        ? string.Format(CultureInfo.InvariantCulture,
            "No U-turn onto the next pass: {0:0} m in, it runs {1:0.0} m from the boundary, {2:0.0} m needed - take over manually at the end of this pass",
            run.Length, Math.Max(gap, 0), distanceFromBoundary)
        : run.Length < 0.5
            ? "No U-turn onto the next pass: none of it is in the field at this end - take over manually at the end of this pass"
            : string.Format(CultureInfo.InvariantCulture,
                "No U-turn onto the next pass: only {0:0} m of it at this end, {1:0} m needed - take over manually at the end of this pass",
                run.Length, MinLandingLength);

    /// <summary>How far a point is from the outer boundary.</summary>
    private static double FenceDistance(double e, double n, Boundary? boundary)
    {
        if (boundary?.OuterBoundary is not { IsValid: true } outer) return double.MaxValue;
        var pts = outer.Points;
        double best = double.MaxValue;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            best = Math.Min(best, GeometryMath.PointToSegmentDistance(e, n, pts[j].Easting, pts[j].Northing, pts[i].Easting, pts[i].Northing));
        return best;
    }

    /// <summary>
    /// Is any of pass <paramref name="pathsAway"/> in the cultivated area between
    /// <paramref name="from"/> and <paramref name="upTo"/> metres ahead of the tractor (abreast of
    /// <paramref name="position"/>, the way it is heading)? A pass that meets the headland line
    /// and comes back into the cultivated area further on (a corner of the headland cut across) has
    /// its end there; not when it leaves the field (outside the fence) on the way: the pass ends
    /// at the headland ahead. Sampled every 2 m.
    /// </summary>
    public bool CultivatedAhead(Models.Track.Track track, double abHeading, int pathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine, Position position, bool headingSameWay,
        double from, double upTo = 2000)
    {
        const double Step = 2.0;
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        double distAway = PassGeometry.Offset(track, pathsAway, widthMinusOverlap);
        if (track.Points.Count < 2) return false;

        if (track.Points.Count > 2)
        {
            var curve = CurveProcessing.CreateOffsetCurve(track.Points, distAway);
            if (curve.Count < 2) return false;
            int near = 0;
            double best = double.MaxValue;
            for (int i = 0; i < curve.Count; i++)
            {
                double de = curve[i].Easting - position.Easting, dn = curve[i].Northing - position.Northing;
                double d = de * de + dn * dn;
                if (d < best) { best = d; near = i; }
            }
            int i1 = Math.Min(near + 1, curve.Count - 1), i0 = Math.Max(near - 1, 0);
            double tdx = curve[i1].Easting - curve[i0].Easting, tdy = curve[i1].Northing - curve[i0].Northing;
            double travel = headingSameWay ? abHeading : abHeading + Math.PI;
            int fwd = tdx * Math.Sin(travel) + tdy * Math.Cos(travel) >= 0 ? 1 : -1;
            double walked = 0, lastTested = double.MinValue;
            for (int i = near; i + fwd >= 0 && i + fwd < curve.Count && walked <= upTo; i += fwd)
            {
                double de = curve[i + fwd].Easting - curve[i].Easting, dn = curve[i + fwd].Northing - curve[i].Northing;
                walked += Math.Sqrt(de * de + dn * dn);
                if (walked < from || walked - lastTested < Step) continue;
                lastTested = walked;
                if (OutsideFence(curve[i + fwd].Easting, curve[i + fwd].Northing, boundary)) return false;
                if (IsPointInsideCultivatedArea(curve[i + fwd].Easting, curve[i + fwd].Northing, boundary, headlandLine)) return true;
            }
            return false;
        }

        var pointA = track.Points[0];
        var pointB = track.Points[track.Points.Count - 1];
        double abDx = pointB.Easting - pointA.Easting, abDy = pointB.Northing - pointA.Northing;
        double abLen = Math.Sqrt(abDx * abDx + abDy * abDy);
        if (abLen < 0.01) return false;
        double abNx = abDx / abLen, abNy = abDy / abLen;
        double perpAngle = abHeading + Math.PI / 2;
        double midE = (pointA.Easting + pointB.Easting) / 2 + Math.Sin(perpAngle) * distAway;
        double midN = (pointA.Northing + pointB.Northing) / 2 + Math.Cos(perpAngle) * distAway;
        double s = headingSameWay ? 1 : -1;
        double t0 = (position.Easting - midE) * abNx + (position.Northing - midN) * abNy;
        for (double d = Math.Max(from, 0); d <= upTo; d += Step)
        {
            double t = t0 + s * d;
            if (OutsideFence(midE + abNx * t, midN + abNy * t, boundary)) return false;
            if (IsPointInsideCultivatedArea(midE + abNx * t, midN + abNy * t, boundary, headlandLine)) return true;
        }
        return false;
    }

    // The pass leaves the field here: cultivated ground beyond is not the same pass going on, it
    // can't be driven to (field job J3, pass 8 across a bay in Baetas's fence, 2026-11-15 nightly).
    private static bool OutsideFence(double easting, double northing, Boundary? boundary) =>
        boundary?.OuterBoundary is { IsValid: true } outer && !outer.IsPointInside(easting, northing);

    private static bool InsideFence(double e, double n, Boundary? boundary)
        => boundary?.OuterBoundary is { IsValid: true } outer && outer.IsPointInside(e, n);

    private bool CheckOffsetLineInsideCultivated(
        Models.Track.Track currentTrack, double abHeading, int nextPathsAway,
        Boundary? boundary, IReadOnlyList<Vec3>? headlandLine,
        ConfigurationStore config)
    {

        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        double nextDistAway = PassGeometry.Offset(currentTrack, nextPathsAway, widthMinusOverlap);

        // Curves: sample the ACTUAL offset curve, not the straight A→B chord. An irregular
        // boundary curve's chord can sit well off the real line, so the chord test picks the
        // wrong side (the outward one, outside the fence — the "return path outside boundary"
        // bug). Offsetting the real curve points is accurate for either direction.
        if (currentTrack.Points.Count > 2)
        {
            var offsetCurve = CurveProcessing.CreateOffsetCurve(currentTrack.Points, nextDistAway);
            foreach (var p in offsetCurve)
                if (IsPointInsideCultivatedArea(p.Easting, p.Northing, boundary, headlandLine))
                    return true;
            return false;
        }

        var pointA = currentTrack.Points[0];
        var pointB = currentTrack.Points[currentTrack.Points.Count - 1];
        double perpAngle = abHeading + Math.PI / 2;

        // Sample the offset pass line along the AB direction across the full rendered extent
        // (pass lines are drawn 2000 m beyond A and B in each direction). Spacing 5 m gives
        // ~800 samples across a 4 km line — trivial cost, and won't miss a field whose
        // narrowest usable span is > 5 m.
        const double SampleSpacing = 5.0;
        const double LineExtent = 2000.0;
        double abDx = pointB.Easting - pointA.Easting;
        double abDy = pointB.Northing - pointA.Northing;
        double abLen = Math.Sqrt(abDx * abDx + abDy * abDy);
        if (abLen < 0.01) return IsPointInsideCultivatedArea(
            (pointA.Easting + pointB.Easting) / 2 + Math.Sin(perpAngle) * nextDistAway,
            (pointA.Northing + pointB.Northing) / 2 + Math.Cos(perpAngle) * nextDistAway,
            boundary, headlandLine);

        double abNx = abDx / abLen;
        double abNy = abDy / abLen;

        double midEasting = (pointA.Easting + pointB.Easting) / 2 + Math.Sin(perpAngle) * nextDistAway;
        double midNorthing = (pointA.Northing + pointB.Northing) / 2 + Math.Cos(perpAngle) * nextDistAway;

        double totalLength = abLen + 2 * LineExtent;
        int sampleCount = (int)(totalLength / SampleSpacing);
        double startOffset = -(totalLength / 2.0);

        for (int i = 0; i <= sampleCount; i++)
        {
            double t = startOffset + i * SampleSpacing;
            double e = midEasting + abNx * t;
            double n = midNorthing + abNy * t;
            if (IsPointInsideCultivatedArea(e, n, boundary, headlandLine))
            {
                _logger.LogDebug("[NextTrack] nextPath={Next}, sample at t={T:F0}m hit cultivated area",
                    nextPathsAway, t);
                return true;
            }
        }

        _logger.LogDebug("[NextTrack] nextPath={Next}, no sample along {N} samples of offset line hit cultivated area",
            nextPathsAway, sampleCount + 1);
        return false;
    }

    /// <summary>
    /// Build the snake-pattern sequence for the cultivated area, starting on the tractor's
    /// current pass, with <paramref name="step"/> passes from one pass to the next (the rows the
    /// operator skips plus one, at least 2). Stored on the state object.
    /// </summary>
    public void BuildSnakeSequence(
        Models.Track.Track referenceTrack,
        double abHeading,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        int step = 2)
    {
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        if (widthMinusOverlap < 0.5) return;

        // Walk outwards perpendicular to the reference track to find the pass-number range
        // in the cultivated area: a pass counts when it is inside anywhere along its length, as
        // for the normal turn's next pass. Tested at one point abreast of the middle of A and B,
        // the passes of an L-shaped field's arm were left out, and on a curve the chord's
        // middle can be far from the curve.
        bool Inside(int pass) => IsPathInsideCultivated(referenceTrack, abHeading, pass, boundary, headlandLine);

        // Start from the nearest pass inside the area. A line drawn on the fence (All Edges,
        // a boundary edge) has pass 0 half a tool in, inside a headland wider than that: the
        // range used to stop there at once, so the snake planned no turns.
        // Twenty passes either way is far more than any headland; with none inside, the plan is
        // pass 0 alone, as before (planned once, not again on every cycle).
        int seed = 0;
        for (int k = 1; k <= 40 && !Inside(seed); k++)
            seed = (k % 2 == 1) ? (k + 1) / 2 : -(k / 2);
        if (!Inside(seed)) seed = 0;

        int minPath = seed, maxPath = seed;
        while (maxPath - seed < 200 && Inside(maxPath + 1)) maxPath++;
        while (seed - minPath < 200 && Inside(minPath - 1)) minPath--;

        // Started on the current pass: the tractor may start anywhere in the field. A pass outside
        // the order (its middle is outside the cultivated area) is put in front of it, so the plan
        // still starts from the pass the tractor is on and goes on to the first one; otherwise the
        // plan would be made again on every cycle. The step is the operator's: the order was the
        // fixed snake (one pass in two), so the row count did nothing in this mode and changing it
        // planned the same turn again (a tester's field, 25 Nov 2026).
        step = Math.Max(2, step);
        turn.SnakeSequence = AgOpenWeb.Services.Track.SwathOrderingService.SkipAndFillFrom(
            minPath, maxPath, guidance.HowManyPathsAway, step);
        turn.SnakeIndex = 0;

        _logger.LogDebug("[YouTurn] Built snake sequence (every {Step} passes, from path {Cur}): [{Seq}]",
            step, guidance.HowManyPathsAway, string.Join(", ", turn.SnakeSequence));
    }

    /// <summary>
    /// True when the snake sequence was planned for the pass the tractor is on. False before the
    /// first turn, and after the operator moved to another pass midway or a swapped turn left
    /// the order: the sequence is then planned again from this pass.
    /// </summary>
    public static bool IsSnakeOnPass(YouTurnWorkingState turn, int pass) =>
        turn.SnakeSequence is { } seq && turn.SnakeIndex >= 0 && turn.SnakeIndex < seq.Count
        && seq[turn.SnakeIndex] == pass;

    /// <summary>
    /// The next pass of the snake sequence that isn't worked yet, or null when every pass left
    /// in it is worked (the field is done). Worked passes are passed over, never turned onto.
    /// </summary>
    public int? GetNextSnakePath(YouTurnWorkingState turn, Func<int, bool> isWorked)
    {
        if (turn.SnakeSequence is not { } seq) return null;
        for (int i = Math.Max(turn.SnakeIndex + 1, 0); i < seq.Count; i++)
            if (!isWorked(seq[i])) return seq[i];
        return null;
    }

    /// <summary>
    /// Move the snake sequence cursor to <paramref name="pass"/>, the pass a turn just landed on,
    /// when it is the one <see cref="GetNextSnakePath"/> planned (the passes before it in the
    /// sequence are worked). Any other pass, such as one a swapped turn went to, drops the
    /// sequence, and the next turn plans it again from there.
    /// </summary>
    public void AdvanceSnakeSequence(YouTurnWorkingState turn, int pass, Func<int, bool> isWorked)
    {
        if (turn.SnakeSequence is not { } seq) return;
        for (int i = Math.Max(turn.SnakeIndex + 1, 0); i < seq.Count; i++)
        {
            if (seq[i] == pass)
            {
                turn.SnakeIndex = i;
                _logger.LogDebug("[YouTurn] Snake advanced to index {Idx}: path {Path}", i, pass);
                return;
            }
            if (!isWorked(seq[i])) break;
        }
        _logger.LogDebug("[YouTurn] Path {Path} is off the snake sequence - planning it again at the next turn", pass);
        turn.SnakeSequence = null;
        turn.SnakeIndex = -1;
    }

    /// <summary>
    /// Is pass <paramref name="pass"/> worked? It is when sections were on while the tractor was
    /// on it (<see cref="Models.Track.Track.WorkedPaths"/>), or when the job's coverage covers at
    /// least half of it inside the cultivated area, as the map shows it: a pass worked before the
    /// field was reopened, or while guidance was on another pass. A U-turn painted across the
    /// end of a pass leaves the pass unworked.
    /// </summary>
    public bool IsPassWorked(
        Models.Track.Track track,
        int pass,
        double nudgeOffset,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        Func<double, double, bool>? isPointCovered)
    {
        if (track.IsPathWorked(pass)) return true;
        if (isPointCovered == null || track.Points.Count < 2) return false;

        double widthMinusOverlap = _configStore.ActualToolWidth - _configStore.Tool.Overlap;
        double distAway = PassGeometry.Offset(track, pass, widthMinusOverlap, nudgeOffset);
        var area = CultivatedPolygon(boundary, headlandLine);
        int inside = 0, covered = 0;

        if (track.Points.Count > 2)
        {
            // The pass every 5 m or more. The curve is taken to 5 m before it is offset (the offset
            // compares every point with every other one), and each sample costs a point-in-polygon
            // test after a test against the area's bounding box, so the area's edge is taken to
            // 5 m as well: a curve or a headland line has a point every metre or so.
            const double CurveSampleSpacing = 5.0;
            const double SpacingSq = CurveSampleSpacing * CurveSampleSpacing;
            var reference = new List<Vec3>();
            foreach (var p in track.Points)
                if (reference.Count == 0 || GeometryMath.DistanceSquared(reference[^1], p) >= SpacingSq)
                    reference.Add(p);
            if (reference.Count < 2) reference = track.Points;

            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            if (area != null)
            {
                var coarse = new List<Vec2>();
                foreach (var p in area)
                {
                    minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
                    minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
                    if (coarse.Count == 0 || GeometryMath.DistanceSquared(coarse[^1], p) >= SpacingSq)
                        coarse.Add(p);
                }
                if (coarse.Count >= 3) area = coarse;
            }
            // Every point of the offset curve is tested: the area's strip index, built once here.
            var areaIndex = area != null ? PolygonSlabIndex.Build(area) : null;

            foreach (var p in CurveProcessing.CreateOffsetCurve(reference, distAway))
            {
                if (area != null
                    && (p.Easting < minE || p.Easting > maxE || p.Northing < minN || p.Northing > maxN
                        || !(areaIndex?.Contains(p.Easting, p.Northing) ?? false)))
                    continue;
                inside++;
                if (isPointCovered(p.Easting, p.Northing)) covered++;
            }
        }
        else
        {
            if (area == null) return false;
            var a = track.Points[0];
            var b = track.Points[1];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01) return false;
            double ux = dx / len, uy = dy / len;
            // Right of A→B is (uy, -ux): the side PassGeometry counts positive.
            double offE = a.Easting + uy * distAway, offN = a.Northing - ux * distAway;

            // Where the pass line crosses the area's edge, as distances along A→B: it is inside
            // between the 1st and 2nd crossing, the 3rd and 4th, and so on (the even-odd rule
            // of the point-in-polygon test). One pass over the edge instead of a point test per
            // sample; a sample is then only a coverage lookup.
            var crossings = new List<double>();
            for (int i = 0, j = area.Count - 1; i < area.Count; j = i++)
            {
                var p = area[j];
                var q = area[i];
                double sp = (p.Easting - offE) * uy - (p.Northing - offN) * ux; // side of the line
                double sq = (q.Easting - offE) * uy - (q.Northing - offN) * ux;
                if ((sp > 0) == (sq > 0)) continue;
                double tp = (p.Easting - offE) * ux + (p.Northing - offN) * uy;
                double tq = (q.Easting - offE) * ux + (q.Northing - offN) * uy;
                crossings.Add(tp + (tq - tp) * sp / (sp - sq));
            }
            crossings.Sort();

            // The pass line every 2 m inside the area.
            const double SampleSpacing = 2.0;
            for (int k = 0; k + 1 < crossings.Count; k += 2)
                for (double t = crossings[k] + SampleSpacing / 2; t < crossings[k + 1]; t += SampleSpacing)
                {
                    inside++;
                    if (isPointCovered(offE + ux * t, offN + uy * t)) covered++;
                }
        }

        return inside > 0 && covered * 2 >= inside;
    }

    /// <summary>The cultivated area's edge: the headland line, else the outer boundary; null when there is neither.</summary>
    private static List<Vec2>? CultivatedPolygon(Boundary? boundary, IReadOnlyList<Vec3>? headlandLine)
    {
        if (headlandLine is { Count: >= 3 })
            return headlandLine.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        if (boundary?.OuterBoundary is { IsValid: true } outer)
            return outer.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        return null;
    }

    /// <summary>
    /// Mirrors AgOpenGPS's GetNextNotWorkedTrack: start at the normal goal lane and walk outward
    /// until an unworked pass is found. Returns the actual skip distance (always positive).
    /// </summary>
    public int GetNextUnworkedPathSkip(Models.Track.Track? selectedTrack, int currentPath, bool positiveDirection, int initialSkip,
        Func<int, bool>? isWorked = null)
    {
        if (selectedTrack == null) return initialSkip;
        isWorked ??= selectedTrack.IsPathWorked;

        int goalLane = currentPath + (positiveDirection ? initialSkip : -initialSkip);
        int iterations = 0;

        while (isWorked(goalLane) && iterations < 100)
        {
            if (positiveDirection) goalLane++; else goalLane--;
            iterations++;
        }

        int actualSkip = Math.Abs(goalLane - currentPath);
        if (actualSkip < 1) actualSkip = initialSkip;

        _logger.LogDebug("[YouTurn] SkipWorked: initial skip {Initial}, actual skip {Actual} (goal lane {Goal}, {It} iterations)",
            initialSkip, actualSkip, goalLane, iterations);
        return actualSkip;
    }

    // Headland line takes precedence; fall back to the outer boundary when no headland is defined.
    private bool IsPointInsideCultivatedArea(
        double easting, double northing,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine)
    {
        if (headlandLine != null && headlandLine.Count >= 3)
            return _headlandIndex.Contains(headlandLine, easting, northing);

        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
            return boundary.OuterBoundary.IsPointInside(easting, northing);

        return true;
    }
}
