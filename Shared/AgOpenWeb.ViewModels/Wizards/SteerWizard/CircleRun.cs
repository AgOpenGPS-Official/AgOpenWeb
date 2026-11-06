// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// What the CPD and Ackermann circle tests saw while recording: how far the tractor went and
/// how the wheel was held. The tests take the farthest point from the start as the circle's
/// diameter once it stops growing, which a quarter circle and a stop also gives (a chord of
/// 0.71 diameters: CPD 110 became 68), and they read the steer angle once (CPD at the end,
/// Ackermann at Record), so a wheel eased off while circling gave CPD 57 and Ackermann 39.
/// A circle counts when the tractor drove at least half of it, kept moving, held the wheel
/// still, and turned the way the test asks.
/// </summary>
internal sealed class CircleRun
{
    /// <summary>A GPS update this far from the last one is moving: ~1.8 km/h at 10 Hz.</summary>
    public const double MovingStepM = 0.05;

    /// <summary>Half a circle is π/2 ≈ 1.57 diameters of path; a quarter and a stop is 1.1.</summary>
    public const double MinPathPerDiameter = 1.5;

    /// <summary>How far the steer angle may wander while circling.</summary>
    public const double MaxAngleSpreadDeg = 3.0;

    private double _lastE, _lastN;
    private double _sum;
    private int _count;

    public double PathM { get; private set; }
    public double MinAngle { get; private set; } = double.MaxValue;
    public double MaxAngle { get; private set; } = double.MinValue;
    public double MeanAngle => _count == 0 ? 0 : _sum / _count;

    public void Start(double easting, double northing, double? angle = null)
    {
        _lastE = easting;
        _lastN = northing;
        PathM = 0;
        _sum = 0;
        _count = 0;
        MinAngle = double.MaxValue;
        MaxAngle = double.MinValue;
        if (angle is { } a) AddAngle(a);
    }

    /// <summary>One GPS update and the steer angle then. True when the tractor moved.</summary>
    public bool Add(double easting, double northing, double angle)
    {
        double step = Math.Sqrt(Math.Pow(easting - _lastE, 2) + Math.Pow(northing - _lastN, 2));
        _lastE = easting;
        _lastN = northing;
        PathM += step;
        AddAngle(angle);
        return step >= MovingStepM;
    }

    private void AddAngle(double a)
    {
        _sum += a;
        _count++;
        MinAngle = Math.Min(MinAngle, a);
        MaxAngle = Math.Max(MaxAngle, a);
    }

    /// <summary>Why this run can't give a result, or null when it can.</summary>
    public string? Error(double diameter, bool rightHand)
    {
        if (PathM < MinPathPerDiameter * diameter)
            return $"Couldn't calculate: the circle wasn't finished ({PathM:F0} m driven for a {diameter:F1} m circle). "
                 + "Drive at least half a circle without stopping.";
        if (MaxAngle - MinAngle > MaxAngleSpreadDeg)
            return $"Couldn't calculate: the steer angle moved from {MinAngle:F1}° to {MaxAngle:F1}° during the circle. "
                 + "Hold the steering wheel still.";
        if (rightHand ? MeanAngle < 0 : MeanAngle > 0)
            return rightHand
                ? "Couldn't calculate: that was a left-hand circle. This test needs a right-hand one."
                : "Couldn't calculate: that was a right-hand circle. This test needs a left-hand one.";
        return null;
    }
}
