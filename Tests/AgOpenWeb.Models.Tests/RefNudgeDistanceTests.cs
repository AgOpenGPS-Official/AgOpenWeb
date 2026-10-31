// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>Ref Nudge step (#489): its own value in the vehicle profile's AutoSteer settings,
/// apart from the nudge distance of the guidance line.</summary>
[TestFixture]
public class RefNudgeDistanceTests
{
    [Test]
    public void RefNudgeDistance_DefaultsTo5()
    {
        Assert.That(new AutoSteerConfig().RefNudgeDistance, Is.EqualTo(5));
    }

    [TestCase(20, 20)]
    [TestCase(0, 1)]       // a broken saved value
    [TestCase(-3, 1)]
    [TestCase(9999, 500)]
    public void RefNudgeDistance_IsKeptInRange(int set, int expected)
    {
        var a = new AutoSteerConfig { RefNudgeDistance = set };
        Assert.That(a.RefNudgeDistance, Is.EqualTo(expected));
    }

    [Test]
    public void RefNudgeDistance_RoundTripsThroughTheDto_AndLeavesNudgeDistanceAlone()
    {
        var a = new AutoSteerConfig { RefNudgeDistance = 12, NudgeDistance = 30 };

        var b = new AutoSteerConfig();
        b.ApplyFromDto(a.ToDto());

        Assert.That(b.RefNudgeDistance, Is.EqualTo(12));
        Assert.That(b.NudgeDistance, Is.EqualTo(30));
    }

    [Test]
    public void RefNudgeDistance_MissingInASavedProfile_DefaultsTo5()
    {
        var dto = JsonSerializer.Deserialize<AutoSteerConfigDto>("{}");
        Assert.That(dto!.RefNudgeDistance, Is.EqualTo(5));
    }
}
