// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// #99: the AutoSteer panel edits GuidanceConfig's steering-tuning values directly.
/// </summary>
[TestFixture]
public class GuidanceConfigTests
{
    [TestCase(1.0, 0.0)]
    [TestCase(0.2, -80.0)]  // AgOpenGPS slider minimum
    [TestCase(2.0, 100.0)]  // AgOpenGPS slider maximum
    [TestCase(1.25, 25.0)]
    public void UTurnCompensation_PercentRoundTrips(double multiplier, double percent)
    {
        Assert.That(GuidanceConfig.UTurnCompensationToPercent(multiplier), Is.EqualTo(percent).Within(1e-9));
        Assert.That(GuidanceConfig.UTurnCompensationFromPercent(percent), Is.EqualTo(multiplier).Within(1e-9));
    }

    [Test]
    public void ResetSteeringTuning_RestoresNewProfileDefaults_AndLeavesUTurnGeometry()
    {
        var g = new GuidanceConfig
        {
            IsPurePursuit = false,
            GoalPointLookAheadHold = 9,
            GoalPointLookAheadMult = 2.5,
            PurePursuitIntegralGain = 0.4,
            StanleyDistanceErrorGain = 3,
            StanleyHeadingErrorGain = 3,
            UTurnCompensation = 1.8,
            UTurnRadius = 12,
        };

        g.ResetSteeringTuning();

        Assert.Multiple(() =>
        {
            Assert.That(g.IsPurePursuit, Is.True);
            Assert.That(g.GoalPointLookAheadHold, Is.EqualTo(4.0));
            Assert.That(g.GoalPointLookAheadMult, Is.EqualTo(1.4));
            Assert.That(g.PurePursuitIntegralGain, Is.EqualTo(0.0));
            Assert.That(g.StanleyDistanceErrorGain, Is.EqualTo(0.8));
            Assert.That(g.StanleyHeadingErrorGain, Is.EqualTo(1.0));
            Assert.That(g.UTurnCompensation, Is.EqualTo(1.0));
            Assert.That(g.UTurnRadius, Is.EqualTo(12), "Not a steering-tuning value — must be left alone");
        });
    }
}
