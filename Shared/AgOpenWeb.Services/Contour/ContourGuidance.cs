// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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

    /// <summary>All strips: loaded from Contour.txt plus the ones recorded this session.</summary>
    public List<List<Vec3>> Strips { get; } = new();
    /// <summary>The guidance line (AgOpenGPS ctList).</summary>
    public List<Vec3> Line { get; } = new(128);
    /// <summary>Finished strips not yet appended to Contour.txt (AgOpenGPS contourSaveList).</summary>
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

    /// <summary>Replace the strips with ones loaded from Contour.txt.</summary>
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
        _lastSecond = double.NegativeInfinity;
        _lastXte = 0;
    }

    // ── Guidance line ────────────────────────────────────────────────────

    /// <summary>AgOpenGPS BuildContourGuidanceLine. Rebuilds at most every 0.3 s while there's
    /// no line, every 2 s once there is.</summary>
    public void BuildContourGuidanceLine(Vec3 pivot, double fixHeading, in ContourParams p, double nowSeconds)
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
        if (!IsLocked)
        {
            StripNum = -1;
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
                ClearLine();
                return;
            }
        }
        else
        {
            ptCount = Strips[StripNum].Count;
            if (ptCount < 2) { ClearLine(); return; }

            int from = Math.Max(_lastLockPt - 20, 0);
            int to = Math.Min(_lastLockPt + 20, ptCount);
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
        bool isSameWay = Math.PI - Math.Abs(Math.Abs(fixHeading - ref_[_pt].Heading) - Math.PI) < 1.57;
        double widthNoOverlap = p.ToolWidth - p.ToolOverlap;
        double refDist = (distanceFromRefLine + (isSameWay ? p.ToolOffset : -p.ToolOffset)) / widthNoOverlap;
        double halfWidth = p.ToolWidth / 2;

        // Beside what is done → one pass over; on it → follow it.
        double howManyPathsAway =
            Math.Abs(distanceFromRefLine) > halfWidth || Math.Abs(p.ToolOffset) > halfWidth
                ? (refDist < 0 ? -1 : 1)
                : 0;

        Line.Clear();
        ptCount = ref_.Count;
        int start, stop;
        if (isSameWay) { start = Math.Max(_pt - 20, 0); stop = Math.Min(_pt + 70, ptCount); } // shorter behind you
        else { start = Math.Max(_pt - 70, 0); stop = Math.Min(_pt + 20, ptCount); }

        double distAway = widthNoOverlap * howManyPathsAway + (isSameWay ? -p.ToolOffset : p.ToolOffset);
        double distSqAway = distAway * distAway * 0.97;

        for (int i = start; i < stop; i++)
        {
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

        if (Line.Count < 5) ClearLine();
        LineVersion++;
    }

    public void ClearLine()
    {
        if (Line.Count > 0) LineVersion++;
        Line.Clear();
        IsLocked = false;
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
            bool sameWay = Math.PI - Math.Abs(Math.Abs(pivot.Heading - abHeading) - Math.PI) < PIBy2;

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

            bool sameWay = Math.PI - Math.Abs(Math.Abs(pivot.Heading - Line[a].Heading) - Math.PI) < PIBy2;
            if (!sameWay) xte *= -1.0;

            double u = (((pivot.Easting - Line[a].Easting) * dx) + ((pivot.Northing - Line[a].Northing) * dy))
                / ((dx * dx) + (dy * dy));
            var start = new Vec3(Line[a].Easting + u * dx, Line[a].Northing + u * dy, 0);

            // Walk along the line to the look-ahead goal point.
            bool forward = isReverse ? !sameWay : sameWay;
            int step = forward ? 1 : -1;
            double distSoFar = 0;
            for (int i = forward ? b : a; i < ptCount && i >= 0; i += step)
            {
                double tempDist = Math.Sqrt(Sq(start.Easting - Line[i].Easting) + Sq(start.Northing - Line[i].Northing));
                if (tempDist + distSoFar > p.GoalPointDistance)
                {
                    double j = (p.GoalPointDistance - distSoFar) / tempDist;
                    goal = new Vec2((1 - j) * start.Easting + j * Line[i].Easting,
                                    (1 - j) * start.Northing + j * Line[i].Northing);
                    break;
                }
                distSoFar += tempDist;
                start = Line[i];
            }

            double goalDistSq = Sq(goal.Northing - pivot.Northing) + Sq(goal.Easting - pivot.Easting);
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
