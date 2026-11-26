// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// After the CPD circle (right) and the Ackermann circle (left), the same wheel angle must read
/// the same both ways. The CPD step takes the CPD 10 % low on purpose (AgOpenGPS), so the right
/// side reads high; the Ackermann step matched the left side to the true angle, and the two
/// sides read 13 % apart (Steer wizard scenarios, LEFT_RIGHT_READ_DIFFERENTLY).
/// </summary>
[TestFixture]
public class SteerWizardLeftRightTests
{
    // The firmware's reading: counts / CPD, left (negative) readings × Ackermann / 100.
    private static double Read(double trueDeg, double countsPerDeg, double cpd, double ackermann) =>
        trueDeg * countsPerDeg / cpd * (trueDeg < 0 ? ackermann / 100.0 : 1);

    // The angle a circle of this diameter calls for (the wizard's formula).
    private static double CircleAngle(double wheelbase, double track, double diameter) =>
        Math.Atan(wheelbase / ((diameter - track * 0.5) / 2)) * 180.0 / Math.PI;

    [TestCase(110, 110)]   // a sensor that reads the same both ways
    [TestCase(110, 95)]    // one that gives fewer counts per degree to the left (a WAS linkage)
    [TestCase(80, 120)]
    public void After_both_circles_left_and_right_read_alike(double countsRight, double countsLeft)
    {
        const double wheelbase = 2.88, track = 1.9, diameter = 18;
        double truth = CircleAngle(wheelbase, track, diameter);

        // CPD circle to the right, from a CPD of 100.
        double cpd = CpdCircleTestStepViewModel.CalculateCpdFromCircle(
            wheelbase, track, diameter, Read(truth, countsRight, 100, 100), 100);
        // Ackermann circle to the left, with Ackermann at 100 (the step sets it).
        int ack = AckermannTestStepViewModel.CalculateAckermann(
            wheelbase, track, diameter, Read(-truth, countsLeft, cpd, 100));

        double right = Read(20, countsRight, cpd, ack), left = -Read(-20, countsLeft, cpd, ack);
        Assert.That(left / right, Is.EqualTo(1).Within(0.02),
            $"20° reads {right:F1}° right and {left:F1}° left (CPD {cpd}, Ackermann {ack})");
        Assert.That(right, Is.GreaterThan(20), "the CPD step's bias (AgOpenGPS) stays: the angle reads a little high");
    }
}
