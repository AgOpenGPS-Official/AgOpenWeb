// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.AutoSteer;

/// <summary>
/// Finds a steering runaway: AutoSteer on, the motor driving, and the wheels turning AWAY from
/// the angle the module is told to steer to. A correct loop always closes the gap, so that is
/// positive feedback: the motor wired the other way round, or the wheel angle sensor reading the
/// other way (Invert Motor / Invert WAS not set, the steer wizard skipped), and the wheels go to
/// the lock in a fraction of a second. Not in AgOpenGPS, which steers on.
/// <para>
/// Judged on the module's reported angle (PGN 253) against the set point sent (PGN 254), over
/// the last <see cref="WindowS"/>: the wheels moved at least <see cref="AwayDeg"/> further from
/// the set point, the gap grew as much, and the wheels were on the same side of the set point
/// all the while, at least <see cref="MinErrorDeg"/> from it at the end. A runaway never crosses
/// the set point; an overshoot does, so after a crossing the movement away has to last the whole
/// window (an overshoot comes back before that). A set point that moves while the wheels stand
/// still does not count.
/// </para>
/// </summary>
public sealed class SteerRunawayDetector
{
    /// <summary>The time the movement is judged over.</summary>
    public const double WindowS = 0.3;
    /// <summary>How far the wheels must have moved away from the set point within the window.</summary>
    public const double AwayDeg = 1.5;
    /// <summary>How far from the set point the wheels must be.</summary>
    public const double MinErrorDeg = 4.0;
    /// <summary>A crossing of the set point this recent makes it an overshoot until the
    /// movement away has lasted a whole <see cref="WindowS"/>.</summary>
    public const double LookBackS = 0.6;

    private const int Capacity = 128;  // LookBackS at up to 200 Hz
    private readonly double[] _t = new double[Capacity];
    private readonly double[] _actual = new double[Capacity];
    private readonly double[] _setPoint = new double[Capacity];
    private int _head, _count;

    /// <summary>The angles when the runaway was found (for the message).</summary>
    public double LastActualDeg { get; private set; }
    public double LastSetPointDeg { get; private set; }

    public void Reset() => _count = 0;

    /// <summary>
    /// Adds the module's reading at <paramref name="tS"/> seconds (any steady clock). Returns
    /// true when the wheels are running away; the history then starts again.
    /// </summary>
    public bool Add(double tS, double actualDeg, double setPointDeg)
    {
        if (double.IsNaN(actualDeg) || double.IsNaN(setPointDeg)) { Reset(); return false; }
        _head = (_head + 1) % Capacity;
        _t[_head] = tS; _actual[_head] = actualDeg; _setPoint[_head] = setPointDeg;
        if (_count < Capacity) _count++;

        double err = actualDeg - setPointDeg;
        if (Math.Abs(err) < MinErrorDeg) return false;
        double side = Math.Sign(err);

        // How far back the wheels have been on this side of the set point. A runaway never
        // crosses it (it drives away from it); an overshoot comes from the other side, so after
        // a crossing in the last LookBackS the movement away must last a whole window.
        int same = 0;
        bool crossed = false;
        for (int k = 1; k < _count; k++)
        {
            int i = (_head - k + Capacity) % Capacity;
            double dt = tS - _t[i];
            if (dt < 0 || dt > LookBackS) break;
            if (Math.Sign(_actual[i] - _setPoint[i]) != side) { crossed = true; break; }
            same = k;
        }

        for (int k = 1; k <= same; k++)
        {
            int i = (_head - k + Capacity) % Capacity;
            double dt = tS - _t[i];
            if (dt > WindowS && !crossed) break;
            if (crossed && dt < WindowS) continue;
            double e = _actual[i] - _setPoint[i];
            double away = (actualDeg - _actual[i]) * side;
            double grew = Math.Abs(err) - Math.Abs(e);
            if (away >= AwayDeg && grew >= AwayDeg)
            {
                LastActualDeg = actualDeg; LastSetPointDeg = setPointDeg;
                Reset();
                return true;
            }
        }
        return false;
    }
}
