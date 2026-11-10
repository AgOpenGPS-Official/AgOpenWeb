// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>How the wheels answered one step of the set point (<see cref="SteerTuneMath.StepResponse"/>).</summary>
/// <param name="OvershootDeg">How far past the target the wheels went (0 when they stopped short).</param>
/// <param name="Crossings">Times the wheels crossed the target (1 = went past once; 3 or more = swinging).</param>
/// <param name="SettleSeconds">From the step until the wheels stayed at their final angle.</param>
/// <param name="FinalErrorDeg">Target minus where the wheels came to rest (+ = short of it).</param>
/// <param name="Reached">The wheels covered at least 80 % of the step.</param>
internal readonly record struct StepResponse(double OvershootDeg, int Crossings, double SettleSeconds,
    double FinalErrorDeg, bool Reached);

/// <summary>The arithmetic of the wheel tuning step, kept apart from the wizard so it can be tested on its own.</summary>
internal static class SteerTuneMath
{
    /// <summary>A wheel angle counts as past the target only this far beyond it (sensor noise is not a crossing).</summary>
    internal const double CrossBandDeg = 0.25;
    /// <summary>The wheels have settled once they stay this close to their final angle.</summary>
    internal const double SettleBandDeg = 0.3;

    /// <summary>
    /// The PWM the module's own loop asks for this far from the target (the firmware's rule:
    /// gain × error plus Minimum To Move, never over Max Limit and, within
    /// <paramref name="lowHighDeg"/> of the target, never over a share of it rising from 1.2 ×
    /// Minimum To Move).
    /// </summary>
    internal static double ModulePwm(double errDeg, int kp, int min, int max, double lowHighDeg)
    {
        double e = Math.Abs(errDeg);
        double low = Math.Floor(min * 1.2);
        double cap = e < lowHighDeg ? Math.Floor(e * (max - low) / lowHighDeg + low) : max;
        return e <= 0 ? 0 : Math.Min(Math.Floor(kp * e) + min, cap);
    }

    internal static int Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }

    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Least-squares slope of y over x (0 with fewer than two different x).</summary>
    internal static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        int n = points.Count;
        if (n < 2) return 0;
        double mx = 0, my = 0;
        foreach (var (x, y) in points) { mx += x; my += y; }
        mx /= n; my /= n;
        double sxx = 0, sxy = 0;
        foreach (var (x, y) in points) { sxx += (x - mx) * (x - mx); sxy += (x - mx) * (y - my); }
        return sxx < 1e-12 ? 0 : sxy / sxx;
    }

    /// <summary>
    /// The fastest stretch of <paramref name="windowSeconds"/> in a run (x seconds, y degrees
    /// moved), as a slope: the wheels' speed once they are up to it, whatever the run's start
    /// and end looked like. A run shorter than that: its whole slope.
    /// </summary>
    internal static double TopSpeed(IReadOnlyList<(double X, double Y)> run, double windowSeconds)
    {
        double top = 0;
        bool any = false;
        var window = new List<(double X, double Y)>();
        for (int i = 0; i < run.Count; i++)
        {
            window.Clear();
            for (int j = i; j < run.Count; j++)
            {
                window.Add(run[j]);
                if (run[j].X - run[i].X >= windowSeconds) break;
            }
            if (window.Count < 3 || window[^1].X - window[0].X < windowSeconds) continue;
            any = true;
            top = Math.Max(top, Slope(window));
        }
        return any ? top : Math.Max(0, Slope(run));
    }

    /// <summary>
    /// What a step of <paramref name="stepDeg"/> degrees looked like. <paramref name="samples"/> are
    /// seconds since the step and degrees moved toward the target (0 = where the wheels started).
    /// </summary>
    internal static StepResponse StepResponse(IReadOnlyList<(double T, double Moved)> samples, double stepDeg)
    {
        if (samples.Count == 0) return new StepResponse(0, 0, 0, stepDeg, false);
        double peak = samples.Max(s => s.Moved);
        int crossings = Crossings(samples, stepDeg);
        int tail = Math.Min(4, samples.Count);
        double final = samples.Skip(samples.Count - tail).Average(s => s.Moved);
        double settle = 0;
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            if (Math.Abs(samples[i].Moved - final) > SettleBandDeg)
            {
                settle = i + 1 < samples.Count ? samples[i + 1].T : samples[i].T;
                break;
            }
        }
        return new StepResponse(Math.Max(0, peak - stepDeg), crossings, settle, stepDeg - final, peak >= 0.8 * stepDeg);
    }

    /// <summary>Times the wheels went from one side of the target to the other, <see cref="CrossBandDeg"/> clear of it.</summary>
    internal static int Crossings(IReadOnlyList<(double T, double Moved)> samples, double stepDeg)
    {
        int crossings = 0;
        bool above = false;
        foreach (var (_, moved) in samples)
        {
            if (!above && moved > stepDeg + CrossBandDeg) { above = true; crossings++; }
            else if (above && moved < stepDeg - CrossBandDeg) { above = false; crossings++; }
        }
        return crossings;
    }

    /// <summary>
    /// The Max Limit that gives the wanted wheel speed, from the levels measured (PWM, degrees per
    /// second; rising PWM): between the two levels either side of it, or, when the first level is
    /// already fast enough, on the line from the PWM that starts the wheels moving to that level.
    /// </summary>
    internal static int PwmForSpeed(IReadOnlyList<(int Pwm, double Speed)> levels, double wantedSpeed, int startsMovingPwm)
    {
        if (levels.Count == 0) return 255;
        var (p1, s1) = levels[^1];
        if (levels.Count == 1 || s1 < wantedSpeed)
        {
            if (s1 <= 0 || s1 < wantedSpeed) return p1;
            double p = startsMovingPwm + (p1 - startsMovingPwm) * wantedSpeed / s1;
            return (int)Math.Ceiling(Math.Min(p, p1));
        }
        var (p0, s0) = levels[^2];
        if (s1 - s0 < 1e-6) return p1;
        double between = p0 + (wantedSpeed - s0) / (s1 - s0) * (p1 - p0);
        return (int)Math.Ceiling(Math.Clamp(between, p0, p1));
    }
}
