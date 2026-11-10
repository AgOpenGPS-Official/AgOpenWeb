// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Contour;

/// <summary>Tool and steering inputs for contour guidance, read from the config each cycle.</summary>
public readonly record struct ContourParams(
    double ToolWidth, double ToolOverlap, double ToolOffset,
    bool UseStanley, double StanleyHeadingErrorGain, double StanleyDistanceErrorGain,
    double Wheelbase, double MaxSteerAngle, double PurePursuitIntegralGain,
    double SideHillCompFactor, double GoalPointDistance);

/// <summary>Steering result of <see cref="ContourGuidance.DistanceFromContourLine"/>.</summary>
public readonly record struct ContourSteer(double SteerAngle, double CrossTrackError, Vec2 GoalPoint);

/// <summary>
/// Contour guidance, ported from AgOpenGPS CContour (+ Core ContourPurePursuitGuidanceService)
/// (#110). While any section paints, the pivot (shifted by the tool offset) is recorded as a
/// strip. With the contour button on, the nearest strip ahead becomes the reference and a
/// guidance line one tool width beside it (or on it) is built every 2 s; steering follows it.
/// Lock keeps the reference strip. Not thread-safe: the pipeline owns it under its lock.
/// </summary>
public sealed class ContourGuidance
{
    private const double PIBy2 = Math.PI / 2.0;
    private const double TwoPI = Math.PI * 2.0;

    /// <summary>All strips: the field's saved ones plus the ones recorded this session.</summary>
    public List<List<Vec3>> Strips { get; } = new();
    /// <summary>The guidance line (AgOpenGPS ctList).</summary>
    public List<Vec3> Line { get; } = new(128);
    /// <summary>Finished strips not yet saved with the field (AgOpenGPS contourSaveList).</summary>
    public List<List<Vec3>> PendingSave { get; } = new();

    public bool IsRecording { get; private set; }  // isContourOn
    public bool IsLocked { get; private set; }
    /// <summary>Index of the reference strip in <see cref="Strips"/>, or -1.</summary>
    public int StripNum { get; private set; } = -1;

    private List<Vec3> _ptList = new(16);
    private int _pt, _lastLockPt = int.MaxValue;
    private double _lastSecond = double.NegativeInfinity;
    private double _lastXte; // modeActualXTE: last contour XTE, for the look-ahead
    private double _inty, _pivotDistanceError, _pivotDistanceErrorLast;
    private int _counter2;
    private bool _wasAutoSteerOn;
    // Held strip (AutoSteer on or locked): which way it is driven and on which side the line is,
    // decided when the strip was taken. Judged again at every rebuild, both flip at a corner
    // sharper than 90° (the tractor turns before the strip does) and the contour turns back.
    private bool _hasHeld, _heldSameWay;
    private double _heldPaths;
    // The line's direction for steering while the strip is held; null: judged per cycle (AgOpenGPS).
    private bool? _lineSameWay;

    public double LastCrossTrackError => _lastXte;
    /// <summary>Bumped whenever <see cref="Line"/> is rebuilt or cleared, so callers can cache.</summary>
    public int LineVersion { get; private set; }

    /// <summary>Lock button: toggles the lock once there's a line to lock to.</summary>
    public bool SetLockToLine()
    {
        if (Line.Count > 5) IsLocked = !IsLocked;
        return IsLocked;
    }

    public void Unlock() { IsLocked = false; }

    /// <summary>
    /// AgOpenGPS btnAutoSteer_Click "handle contour lock" (#1170): engaging AutoSteer locks
    /// to the current line, disengaging unlocks. Call every cycle with the engaged state
    /// after all kickouts, so every engage/disengage path is followed, not only the button.
    /// </summary>
    public void FollowAutoSteer(bool isAutoSteerOn)
    {
        if (isAutoSteerOn && !_wasAutoSteerOn && !IsLocked) SetLockToLine();
        else if (!isAutoSteerOn && _wasAutoSteerOn) IsLocked = false;
        _wasAutoSteerOn = isAutoSteerOn;
    }

    /// <summary>Contour button toggled: drop the line and the reference strip.</summary>
    public void ClearReference()
    {
        ClearLine();
        StripNum = -1;
        _lastLockPt = int.MaxValue;
        _hasHeld = false;
    }

    // ── Recording ────────────────────────────────────────────────────────

    /// <summary>AgOpenGPS AddContourPoints: record while any section paints, end the strip when none do.</summary>
    public void Record(bool anySectionPainting, Vec3 pivot, double toolOffset)
    {
        if (anySectionPainting)
        {
            if (!IsRecording) StartContourLine();
            AddPoint(pivot, toolOffset);
        }
        else if (IsRecording)
        {
            StopContourLine();
        }
    }

    public void StartContourLine()
    {
        _ptList = new List<Vec3>(16);
        Strips.Add(_ptList);
        IsRecording = true;
    }

    public void AddPoint(Vec3 pivot, double toolOffset)
        => _ptList.Add(new Vec3(
            pivot.Easting + Math.Cos(pivot.Heading) * toolOffset,
            pivot.Northing - Math.Sin(pivot.Heading) * toolOffset,
            pivot.Heading));

    public void StopContourLine()
    {
        // Long enough to keep → save it; otherwise drop its points (AgOpenGPS keeps the
        // empty list in stripList; the build skips strips under 4 points).
        if (_ptList.Count > 5) PendingSave.Add(_ptList);
        else _ptList.Clear();
        IsRecording = false;
    }

    /// <summary>Replace the strips with the field's saved ones.</summary>
    public void Load(IEnumerable<List<Vec3>> strips)
    {
        Reset();
        foreach (var s in strips) Strips.Add(new List<Vec3>(s));
    }

    /// <summary>AgOpenGPS ResetContour / delete contour paths: forget every strip and the line.</summary>
    public void Reset()
    {
        Strips.Clear();
        _ptList = new List<Vec3>(16);
        ClearLine();
        PendingSave.Clear();
        IsRecording = false;
        IsLocked = false;
        StripNum = -1;
        _lastLockPt = int.MaxValue;
        _hasHeld = false;
        _lastSecond = double.NegativeInfinity;
        _lastXte = 0;
    }

    // ── Guidance line ────────────────────────────────────────────────────

    /// <summary>AgOpenGPS BuildContourGuidanceLine. Rebuilds at most every 0.3 s while there's
    /// no line, every 2 s once there is. While AutoSteer is on the reference strip is kept
    /// even when unlocked (#1171), so steering never jumps to another strip; a new one is
    /// only searched for when there's none yet.</summary>
    public void BuildContourGuidanceLine(Vec3 pivot, double fixHeading, in ContourParams p, double nowSeconds,
        bool isAutoSteerOn = false)
    {
        if (nowSeconds - _lastSecond < (Line.Count == 0 ? 0.3 : 2)) return;
        _lastSecond = nowSeconds;

        double toolContourDistance = p.ToolWidth * 3 + Math.Abs(p.ToolOffset);
        int stripCount = Strips.Count;
        if (stripCount < 1) return;

        double sinH = Math.Sin(pivot.Heading) * 0.2;
        double cosH = Math.Cos(pivot.Heading) * 0.2;
        double sin2HL = Math.Sin(pivot.Heading + PIBy2);
        double cos2HL = Math.Cos(pivot.Heading + PIBy2);
        var boxA = new Vec2(pivot.Easting - sin2HL + sinH, pivot.Northing - cos2HL + cosH);
        var boxB = new Vec2(pivot.Easting + sin2HL + sinH, pivot.Northing + cos2HL + cosH);

        double minDistance = double.MaxValue;
        int ptCount;
        bool keepStrip = (IsLocked || isAutoSteerOn) && StripNum >= 0 && StripNum < stripCount;
        if (!keepStrip)
        {
            StripNum = -1;
            _hasHeld = false;
            for (int s = 0; s < stripCount; s++)
            {
                var strip = Strips[s];
                for (int q = 0; q < strip.Count; q += 3)
                {
                    // Only points ahead of the line across the pivot.
                    if (((boxA.Easting - boxB.Easting) * (strip[q].Northing - boxB.Northing))
                        - ((boxA.Northing - boxB.Northing) * (strip[q].Easting - boxB.Easting)) > 0)
                        continue;

                    double dist = Sq(pivot.Easting - strip[q].Easting) + Sq(pivot.Northing - strip[q].Northing);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        StripNum = s;
                        _pt = _lastLockPt = q;
                    }
                }
            }
            minDistance = Math.Sqrt(minDistance);

            if (StripNum < 0 || minDistance > toolContourDistance || Strips[StripNum].Count < 4)
            {
                StripNum = -1; // not a reference: AutoSteer mustn't hold on to it
                ClearLine();
                return;
            }
        }
        else
        {
            ptCount = Strips[StripNum].Count;
            if (ptCount < 2) { ClearLine(); return; }

            // Near the last point; the whole strip after an unlock at the line's end
            // (_lastLockPt is int.MaxValue then, and + 20 would overflow).
            bool anyPt = _lastLockPt == int.MaxValue;
            int from = anyPt ? 0 : Math.Max(_lastLockPt - 20, 0);
            int to = anyPt ? ptCount : Math.Min(_lastLockPt + 20, ptCount);
            for (int i = from; i < to; i += 3)
            {
                var sp = Strips[StripNum][i];
                double dist = Sq(pivot.Easting - sp.Easting) + Sq(pivot.Northing - sp.Northing);
                if (minDistance >= dist)
                {
                    minDistance = dist;
                    _pt = _lastLockPt = i;
                }
            }
            minDistance = Math.Sqrt(minDistance);

            // Near the end of the held strip and nearer the strip that carries on from it (the
            // line already runs on along it, see below): follow that one from here (ContinuationOf).
            var held = Strips[StripNum];
            bool heldSameWay = _hasHeld ? _heldSameWay : SameWay(fixHeading, held[_pt].Heading);
            if (heldSameWay ? _pt + 70 > ptCount : _pt - 70 < 0)
            {
                int next = ContinuationOf(StripNum, heldSameWay, toolContourDistance);
                if (next >= 0)
                {
                    // Only its first stretch (its last, driven against it): the strip carrying on may be
                    // the one being recorded, whose newest points are under the tractor.
                    var c = Strips[next];
                    double best = double.MaxValue; int nearest = 0;
                    int cFrom = heldSameWay ? 0 : Math.Max(c.Count - 90, 0), cTo = heldSameWay ? Math.Min(90, c.Count) : c.Count;
                    for (int i = cFrom; i < cTo; i++)
                    {
                        double d = Sq(pivot.Easting - c[i].Easting) + Sq(pivot.Northing - c[i].Northing);
                        if (d < best) { best = d; nearest = i; }
                    }
                    if (Math.Sqrt(best) < minDistance)
                    {
                        StripNum = next;
                        _pt = _lastLockPt = nearest;
                        minDistance = Math.Sqrt(best);
                    }
                }
            }
            if (minDistance > toolContourDistance) { ClearLine(); return; }
        }

        var ref_ = Strips[StripNum];
        double refX = ref_[_pt].Easting, refZ = ref_[_pt].Northing;
        double dx, dz, distanceFromRefLine;
        if (_pt < ref_.Count - 1)
        {
            dx = ref_[_pt + 1].Easting - refX;
            dz = ref_[_pt + 1].Northing - refZ;
            distanceFromRefLine = ((dz * pivot.Easting) - (dx * pivot.Northing)
                + (ref_[_pt + 1].Easting * refZ) - (ref_[_pt + 1].Northing * refX))
                / Math.Sqrt((dz * dz) + (dx * dx));
        }
        else if (_pt > 0)
        {
            dx = refX - ref_[_pt - 1].Easting;
            dz = refZ - ref_[_pt - 1].Northing;
            distanceFromRefLine = ((dz * pivot.Easting) - (dx * pivot.Northing)
                + (refX * ref_[_pt - 1].Northing) - (refZ * ref_[_pt - 1].Easting))
                / Math.Sqrt((dz * dz) + (dx * dx));
        }
        else return;

        // Driving the same way the strip was made?
        bool isSameWay = keepStrip && _hasHeld ? _heldSameWay : SameWay(fixHeading, ref_[_pt].Heading);
        double widthNoOverlap = p.ToolWidth - p.ToolOverlap;
        double refDist = (distanceFromRefLine + (isSameWay ? p.ToolOffset : -p.ToolOffset)) / widthNoOverlap;
        double halfWidth = p.ToolWidth / 2;

        // Beside what is done → one pass over; on it → follow it.
        double howManyPathsAway =
            Math.Abs(distanceFromRefLine) > halfWidth || Math.Abs(p.ToolOffset) > halfWidth
                ? (refDist < 0 ? -1 : 1)
                : 0;
        if (keepStrip && _hasHeld) howManyPathsAway = _heldPaths;
        else { _heldSameWay = isSameWay; _heldPaths = howManyPathsAway; _hasHeld = true; }

        // The held strip ends within the line's reach: build the line on along the strip that
        // carries on from it, so it goes round the corner where the strip ended.
        int at = _pt, joinAt = -1;  // joinAt: where the second strip begins in ref_
        if (keepStrip && (isSameWay ? at + 70 > ref_.Count : at - 70 < 0))
        {
            int next = ContinuationOf(StripNum, isSameWay, toolContourDistance);
            if (next >= 0)
            {
                var c = Strips[next];
                var joined = new List<Vec3>(ref_.Count + c.Count);
                if (isSameWay) { joinAt = ref_.Count; joined.AddRange(ref_); joined.AddRange(c); }
                else { joinAt = c.Count; joined.AddRange(c); joined.AddRange(ref_); at += c.Count; }
                ref_ = joined;
            }
        }

        Line.Clear();
        ptCount = ref_.Count;
        int start, stop;
        if (isSameWay) { start = Math.Max(at - 20, 0); stop = Math.Min(at + 70, ptCount); } // shorter behind you
        else { start = Math.Max(at - 70, 0); stop = Math.Min(at + 20, ptCount); }

        double distAway = widthNoOverlap * howManyPathsAway + (isSameWay ? -p.ToolOffset : p.ToolOffset);
        double distSqAway = distAway * distAway * 0.97;

        int lineJoin = -1; // where the second strip's points begin in Line
        for (int i = start; i < stop; i++)
        {
            if (i == joinAt) lineJoin = Line.Count;
            var point = new Vec3(
                ref_[i].Easting + Math.Cos(ref_[i].Heading) * distAway,
                ref_[i].Northing - Math.Sin(ref_[i].Heading) * distAway,
                ref_[i].Heading);

            // Not closer than one width to the strip (drops points inside tight bends).
            bool ok = true;
            for (int j = start; j < stop; j++)
            {
                if (Sq(point.Northing - ref_[j].Northing) + Sq(point.Easting - ref_[j].Easting) < distSqAway)
                { ok = false; break; }
            }
            if (!ok) continue;

            if (Line.Count == 0
                || Sq(point.Easting - Line[^1].Easting) + Sq(point.Northing - Line[^1].Northing) > 0.2)
                Line.Add(point);
        }

        if (lineJoin > 0) JoinAtCorner(lineJoin);
        _lineSameWay = keepStrip ? isSameWay : null;
        if (Line.Count < 5) ClearLine();
        LineVersion++;
    }

    public void ClearLine()
    {
        if (Line.Count > 0) LineVersion++;
        Line.Clear();
        IsLocked = false;
        _lineSameWay = null;
    }

    // ── Steering ─────────────────────────────────────────────────────────

    /// <summary>
    /// AgOpenGPS DistanceFromContourLine: Stanley on the steer axle, or Pure Pursuit (Core
    /// ContourPurePursuitGuidanceService). Null when there's no usable line (AgOpenGPS
    /// then sends distance 0 / "no guidance"). May unlock at the line's ends.
    /// </summary>
    public ContourSteer? DistanceFromContourLine(
        Vec3 pivot, Vec3 steer, Vec2 fix, double fixHeading, in ContourParams p,
        double speedKmh, bool isReverse, bool isAutoSteerOn, double imuRoll)
    {
        int ptCount = Line.Count;
        if (ptCount <= 8) { _lastXte = 0; return null; }

        double xte, steerAngle;
        var goal = new Vec2(0, 0);

        if (p.UseStanley)
        {
            var (a, b) = ClosestTwo(steer);
            double dx = Line[b].Easting - Line[a].Easting;
            double dy = Line[b].Northing - Line[a].Northing;
            if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon) return null;

            xte = ((dy * steer.Easting) - (dx * steer.Northing)
                + (Line[b].Easting * Line[a].Northing) - (Line[b].Northing * Line[a].Easting))
                / Math.Sqrt((dy * dy) + (dx * dx));

            double abHeading = Math.Atan2(dx, dy);
            if (abHeading < 0) abHeading += TwoPI;
            bool sameWay = _lineSameWay ?? Math.PI - Math.Abs(Math.Abs(pivot.Heading - abHeading) - Math.PI) < PIBy2;

            double delta;
            if (sameWay) delta = steer.Heading - abHeading;
            else { xte *= -1.0; delta = steer.Heading - abHeading + Math.PI; }

            // AgOpenGPS's circular-error fix, as written.
            if (delta > Math.PI) delta -= Math.PI;
            else if (delta < Math.PI) delta += Math.PI;
            if (delta > PIBy2) delta -= Math.PI;
            else if (delta < -PIBy2) delta += Math.PI;

            if (isReverse) delta *= -1;
            delta = Math.Clamp(delta * p.StanleyHeadingErrorGain, -0.74, 0.74);

            double ang = Math.Atan((xte * p.StanleyDistanceErrorGain) / ((Math.Abs(speedKmh) * 0.277777) + 1));
            ang = Math.Clamp(ang, -0.74, 0.74);
            steerAngle = Math.Clamp((ang + delta) * -1.0 * 180.0 / Math.PI, -p.MaxSteerAngle, p.MaxSteerAngle);
        }
        else
        {
            var (a, b) = ClosestTwo(pivot);

            // Locked and at the line's end → unlock and drop guidance this cycle.
            if (IsLocked && (a < 2 || b > ptCount - 3))
            {
                IsLocked = false;
                _lastLockPt = int.MaxValue;
                return null;
            }

            double dx = Line[b].Easting - Line[a].Easting;
            double dy = Line[b].Northing - Line[a].Northing;
            if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon) { _lastXte = 0; return null; }

            // Distance from the segment, measured at the antenna (AgOpenGPS pn.fix).
            xte = ((dy * fix.Easting) - (dx * fix.Northing)
                + (Line[b].Easting * Line[a].Northing) - (Line[b].Northing * Line[a].Easting))
                / Math.Sqrt((dy * dy) + (dx * dx));

            if (p.PurePursuitIntegralGain != 0)
            {
                _pivotDistanceError = xte * 0.2 + _pivotDistanceError * 0.8;
                double derivative = 0;
                if (++_counter2 > 4)
                {
                    derivative = (_pivotDistanceError - _pivotDistanceErrorLast) * 2;
                    _pivotDistanceErrorLast = _pivotDistanceError;
                    _counter2 = 0;
                }

                if (isAutoSteerOn && Math.Abs(derivative) < 0.1 && speedKmh > 2.5)
                {
                    // Over the line heading the wrong way → pull the integral back fast.
                    if ((_inty < 0 && xte < 0) || (_inty > 0 && xte > 0))
                        _inty += _pivotDistanceError * p.PurePursuitIntegralGain * -0.06;
                    else if (Math.Abs(xte) > 0.02)
                        _inty = Math.Clamp(_inty + _pivotDistanceError * p.PurePursuitIntegralGain * -0.02, -0.2, 0.2);
                }
                else _inty *= 0.95;
            }
            else
            {
                _inty = 0;
                _pivotDistanceError = 0;
            }
            if (isReverse) _inty = 0;

            bool sameWay = _lineSameWay ?? Math.PI - Math.Abs(Math.Abs(pivot.Heading - Line[a].Heading) - Math.PI) < PIBy2;
            if (!sameWay) xte *= -1.0;

            double u = (((pivot.Easting - Line[a].Easting) * dx) + ((pivot.Northing - Line[a].Northing) * dy))
                / ((dx * dx) + (dy * dy));
            var start = new Vec3(Line[a].Easting + u * dx, Line[a].Northing + u * dy, 0);

            // Walk along the line to the look-ahead goal point.
            bool forward = isReverse ? !sameWay : sameWay;
            int step = forward ? 1 : -1;
            double distSoFar = 0;
            bool reached = false;
            for (int i = forward ? b : a; i < ptCount && i >= 0; i += step)
            {
                double tempDist = Math.Sqrt(Sq(start.Easting - Line[i].Easting) + Sq(start.Northing - Line[i].Northing));
                if (tempDist + distSoFar > p.GoalPointDistance)
                {
                    double j = (p.GoalPointDistance - distSoFar) / tempDist;
                    goal = new Vec2((1 - j) * start.Easting + j * Line[i].Easting,
                                    (1 - j) * start.Northing + j * Line[i].Northing);
                    reached = true;
                    break;
                }
                distSoFar += tempDist;
                start = Line[i];
            }
            // The line ends within the look-ahead: aim at its end. Left at (0,0) the goal was the
            // field's origin, and AutoSteer swung towards it (AgOpenGPS keeps the last cycle's goal).
            if (!reached)
            {
                var end = forward ? Line[ptCount - 1] : Line[0];
                goal = new Vec2(end.Easting, end.Northing);
            }

            double goalDistSq = Sq(goal.Northing - pivot.Northing) + Sq(goal.Easting - pivot.Easting);
            if (goalDistSq < 0.01) { _lastXte = xte; return null; }
            double localHeading = sameWay ? TwoPI - fixHeading + _inty : TwoPI - fixHeading - _inty;
            steerAngle = Math.Atan(2 * (((goal.Easting - pivot.Easting) * Math.Cos(localHeading))
                + ((goal.Northing - pivot.Northing) * Math.Sin(localHeading))) * p.Wheelbase / goalDistSq)
                * 180.0 / Math.PI;

            if (imuRoll != 88888) steerAngle += imuRoll * -p.SideHillCompFactor;
            steerAngle = Math.Clamp(steerAngle, -p.MaxSteerAngle, p.MaxSteerAngle);
        }

        _lastXte = xte;
        return new ContourSteer(steerAngle, xte, goal);
    }

    /// <summary>
    /// The strip that carries on from <paramref name="strip"/>'s end in the way it is driven, or -1.
    /// A strip ends wherever every section closed: at a sharp corner the boom's outer end reaches
    /// the boundary ahead, so one lap is recorded as several strips with a gap at each corner. Its
    /// continuation is the strip recorded the same way that begins nearest that end, within
    /// <paramref name="reach"/> (the distance a strip is taken up from). That may be the strip being
    /// recorded now: an inner lap never closes its sections, so the outer lap's last piece and the
    /// whole lap since are one strip. Not AgOpenGPS: there, with AutoSteer on, the line runs out at the strip's end.
    /// </summary>
    private int ContinuationOf(int strip, bool sameWay, double reach)
    {
        var s = Strips[strip];
        var end = sameWay ? s[^1] : s[0];
        int best = -1;
        double bestDist = reach * reach;
        for (int t = 0; t < Strips.Count; t++)
        {
            var c = Strips[t];
            if (t == strip || c.Count < 4) continue;
            var from = sameWay ? c[0] : c[^1];
            double d = Sq(from.Easting - end.Easting) + Sq(from.Northing - end.Northing);
            if (d <= bestDist) { bestDist = d; best = t; }
        }
        return best;
    }

    /// <summary>
    /// A line built from two strips meeting at a corner: each strip's piece, set a width in, runs on
    /// towards the corner of the lap outside (the corner itself is in neither strip, so nothing trims
    /// them), and where a hand-driven corner wobbled its last points can swing out of the line.
    /// Join the pieces where they cross, as an offset corner is cut, or else where they come
    /// closest: the lap's corner. What lies beyond on either piece goes.
    /// </summary>
    private void JoinAtCorner(int join)
    {
        double best = double.MaxValue;
        int bi = join - 1, bj = join;
        for (int i = 0; i + 1 < join; i++)
        {
            var a = Line[i]; var b = Line[i + 1];
            for (int j = join; j + 1 < Line.Count; j++)
            {
                var c = Line[j]; var d = Line[j + 1];
                double rE = b.Easting - a.Easting, rN = b.Northing - a.Northing;
                double sE = d.Easting - c.Easting, sN = d.Northing - c.Northing;
                double den = rE * sN - rN * sE;
                if (Math.Abs(den) > 1e-12)
                {
                    double qE = c.Easting - a.Easting, qN = c.Northing - a.Northing;
                    double t = (qE * sN - qN * sE) / den, u = (qE * rN - qN * rE) / den;
                    if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                    {
                        var x = new Vec3(a.Easting + rE * t, a.Northing + rN * t, a.Heading);
                        Line.RemoveRange(i + 1, j - i);  // a's piece up to the crossing, then d onwards
                        Line.Insert(i + 1, x);
                        return;
                    }
                }
                double dd = Sq(a.Easting - c.Easting) + Sq(a.Northing - c.Northing);
                if (dd < best) { best = dd; bi = i; bj = j; }
            }
        }
        // Closest points: keep the first piece up to bi and the second from bj.
        if (bj - bi > 1) Line.RemoveRange(bi + 1, bj - bi - 1);
    }

    // Within 90° of each other (AgOpenGPS: within 1.57 rad).
    private static bool SameWay(double a, double b) => Math.PI - Math.Abs(Math.Abs(a - b) - Math.PI) < 1.57;

    // The two line points nearest to pos, in list order.
    private (int a, int b) ClosestTwo(Vec3 pos)
    {
        double minA = 1000000, minB = 1000000;
        int a = 0, b = 0;
        for (int t = 0; t < Line.Count; t++)
        {
            double dist = Sq(pos.Easting - Line[t].Easting) + Sq(pos.Northing - Line[t].Northing);
            if (dist < minA) { minB = minA; b = a; minA = dist; a = t; }
            else if (dist < minB) { minB = dist; b = t; }
        }
        return a > b ? (b, a) : (a, b);
    }

    private static double Sq(double v) => v * v;
}
