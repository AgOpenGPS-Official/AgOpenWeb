// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>#112: PGN 252 byte 9 carries counts per degree rounded, not truncated
/// (the CPD test can produce a fractional value).</summary>
[TestFixture]
public class SteerSettingsCpdTests
{
    [TestCase(110.9, 111)]
    [TestCase(110.4, 110)]
    [TestCase(110.5, 111)]
    [TestCase(0.2, 1)]      // clamped to the firmware's 1..255
    [TestCase(300.0, 255)]
    public void CountsPerDegree_IsRounded(double cpd, int expected)
    {
        var pgn = PgnBuilder.BuildSteerSettingsPgn(new AutoSteerConfig { CountsPerDegree = cpd });
        Assert.That(pgn[9], Is.EqualTo(expected));
    }
}
