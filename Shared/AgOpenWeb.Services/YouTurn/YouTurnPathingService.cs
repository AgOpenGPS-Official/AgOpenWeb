// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
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
        double nextDistAway = PassGeometry.Offset(referenceTrack, nextPathsAway, widthMinusOverlap, guidance.NudgeOffset);

        // Authoritative perpendicular width for the U-turn arc — direction lives in IsTurnLeft.
        turn.NextTrackTurnOffset = Math.Abs(pathsToMove * widthMinusOverlap);

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
            var offsetPoints = CurveProcessing.ExtendCurveEnds(
                CurveProcessing.CreateOffsetCurve(referenceTrack.Points, nextDistAway));
            nextTrack = Models.Track.Track.FromCurve($"Path {nextPathsAway}", offsetPoints, referenceTrack.IsClosed);
        }
        nextTrack.IsActive = false;
        turn.NextTrack = nextTrack;

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
    /// offset direction has cultivated area.
    /// </summary>
    public (bool isInside, bool positiveDirection) WouldNextLineBeInsideBoundary(
        Models.Track.Track currentTrack,
        double abHeading,
        GuidanceWorkingState guidance,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        int uTurnSkipRows)
    {
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid)
            return (true, false);
        if (currentTrack.Points.Count < 2)
            return (true, false);

        var config = _configStore;
        int pathsToMove = uTurnSkipRows + 1;
        int nextPathsAwayNeg = guidance.HowManyPathsAway - pathsToMove;
        int nextPathsAwayPos = guidance.HowManyPathsAway + pathsToMove;

        bool negInside = CheckOffsetLineInsideCultivated(currentTrack, abHeading, nextPathsAwayNeg,
                boundary, headlandLine, config);
        bool posInside = CheckOffsetLineInsideCultivated(currentTrack, abHeading, nextPathsAwayPos,
                boundary, headlandLine, config);

        if (negInside && posInside)
        {
            // Both directions work — prefer the one that continues advancing
            // (same direction as the current pass offset from 0).
            // If on pass 0, prefer negative (original convention).
            bool preferPositive = guidance.HowManyPathsAway > 0;
            return (true, preferPositive);
        }
        if (negInside) return (true, false);
        if (posInside) return (true, true);
        return (false, false);
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
    /// Build the snake-pattern sequence for the cultivated area and rotate it so the
    /// tractor's current pass comes first. Stored on the state object.
    /// </summary>
    public void BuildSnakeSequence(
        Models.Track.Track referenceTrack,
        double abHeading,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine)
    {
        var config = _configStore;
        double widthMinusOverlap = config.ActualToolWidth - config.Tool.Overlap;
        if (widthMinusOverlap < 0.5) return;

        // Walk outwards perpendicular to the reference track to find the pass-number range
        // that fits inside the cultivated area (tested at the track's middle).
        var pointA = referenceTrack.Points[0];
        var pointB = referenceTrack.Points[referenceTrack.Points.Count - 1];
        double perpAngle = abHeading + Math.PI / 2;
        double midE = (pointA.Easting + pointB.Easting) / 2;
        double midN = (pointA.Northing + pointB.Northing) / 2;

        bool Inside(int pass)
        {
            double offsetDist = PassGeometry.Offset(referenceTrack, pass, widthMinusOverlap);
            return IsPointInsideCultivatedArea(
                midE + Math.Sin(perpAngle) * offsetDist, midN + Math.Cos(perpAngle) * offsetDist,
                boundary, headlandLine);
        }

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

        var fullSequence = AgOpenWeb.Services.Track.SwathOrderingService.GeneratePathSequence(
            minPath, maxPath, AgOpenWeb.Services.Track.SwathPattern.Snake);

        // Rotate so the sequence starts at the current pass — the tractor may start anywhere in the field.
        // A pass outside the order (its middle is outside the cultivated area) is put in front of
        // it, so the plan still starts from the pass the tractor is on and goes on to the first
        // one; otherwise the plan would be made again on every cycle.
        int currentIdx = fullSequence.IndexOf(guidance.HowManyPathsAway);
        turn.SnakeSequence = currentIdx > 0
            ? fullSequence.Skip(currentIdx).Concat(fullSequence.Take(currentIdx)).ToList()
            : currentIdx < 0
                ? fullSequence.Prepend(guidance.HowManyPathsAway).ToList()
                : fullSequence;
        turn.SnakeIndex = 0;

        _logger.LogDebug("[YouTurn] Built snake sequence (rotated from path {Cur}): [{Seq}]",
            guidance.HowManyPathsAway, string.Join(", ", turn.SnakeSequence));
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
