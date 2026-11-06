// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// The Vehicle &amp; Tool values no machine has are refused, with the reason (the Vehicle &amp;
/// Tool scenarios: a negative wheelbase took a Case Puma off its pass, 288 typed for
/// 2.88 m swung it 2.6 m across the pass, an overlap as wide as the boom pulled it back onto
/// the first pass, all with no word to the operator).
/// </summary>
[TestFixture]
public class VehicleToolLimitsTests
{
    private static ConfigurationStore Sprayer()
    {
        var s = new ConfigurationStore();
        s.NumSections = 8;
        for (int i = 0; i < 8; i++) s.Tool.SetSectionWidth(i, 100);
        return s;
    }

    [TestCase("vehicle.wheelbase", "0")]
    [TestCase("vehicle.wheelbase", "-2.88")]
    [TestCase("vehicle.wheelbase", "288")]
    [TestCase("vehicle.wheelbase", "NaN")]
    [TestCase("vehicle.trackWidth", "0")]
    [TestCase("vehicle.antennaHeight", "310")]
    [TestCase("vehicle.antennaHeight", "-3.1")]
    [TestCase("vehicle.antennaHeight", "Infinity")]
    [TestCase("vehicle.antennaPivot", "100")]
    [TestCase("vehicle.antennaOffset", "25")]
    [TestCase("vehicle.hitchLength", "-1.1")]
    [TestCase("vehicle.hitchLength", "110")]
    [TestCase("vehicle.type", "7")]
    [TestCase("gps.dualHeadingOffset", "400")]
    [TestCase("roll.rollFilter", "5")]
    [TestCase("roll.rollZero", "45")]
    [TestCase("tool.type", "xyz")]
    [TestCase("tool.type", "")]
    [TestCase("tool.numSections", "0")]
    [TestCase("tool.numSections", "65")]
    [TestCase("tool.numSections", "8.5")]
    [TestCase("tool.zones", "2.5")]
    [TestCase("tool.sectionWidth", "0,0")]
    [TestCase("tool.sectionWidth", "0,1")]
    [TestCase("tool.sectionWidth", "0,-100")]
    [TestCase("tool.sectionWidth", "0,100000")]
    [TestCase("tool.sectionWidth", "99,100")]
    [TestCase("tool.offset", "250")]
    [TestCase("tool.overlap", "8")]
    [TestCase("tool.overlap", "20")]
    [TestCase("tool.hitchLength", "150")]
    [TestCase("tool.trailingHitchLength", "-4")]
    [TestCase("tool.trailingHitchLength", "0")]
    [TestCase("tool.tankTrailingHitchLength", "-3")]
    [TestCase("tool.slowSpeedCutoff", "100")]
    [TestCase("tool.minCoverage", "150")]
    [TestCase("tool.turnOffDelay", "100")]
    [TestCase("tool.zones", "9")]
    public void An_impossible_value_is_refused_with_the_reason(string key, string value)
    {
        string? why = VehicleToolLimits.Check(key, value, Sprayer());
        Assert.That(why, Is.Not.Null.And.Not.Empty);
    }

    [TestCase("vehicle.wheelbase", "2.88")]
    [TestCase("vehicle.antennaHeight", "3.1")]
    [TestCase("vehicle.antennaOffset", "-0.25")]
    [TestCase("vehicle.hitchLength", "1.1")]
    [TestCase("gps.dualHeadingOffset", "90")]
    [TestCase("gps.dualHeadingOffset", "-180")]
    [TestCase("roll.rollFilter", "0.5")]
    [TestCase("tool.type", "Trailing")]
    [TestCase("tool.type", "tbt")]
    [TestCase("tool.numSections", "8")]
    [TestCase("tool.sectionWidth", "7,76.2")]
    [TestCase("tool.sectionWidth", "0,15")]
    [TestCase("tool.sectionWidth", "0,1000")]
    [TestCase("tool.offset", "-2.5")]
    [TestCase("tool.overlap", "0.2")]
    [TestCase("tool.overlap", "-1")]
    [TestCase("tool.trailingToolToPivotLength", "-0.6")]
    [TestCase("tool.slowSpeedCutoff", "0.5")]
    [TestCase("conn.ntripEnabled", "1")]          // keys with no range are not checked
    [TestCase("vehicle.wheelbase", "abc")]        // not a number: ignored by the setter, as before
    public void A_real_value_passes(string key, string value)
    {
        Assert.That(VehicleToolLimits.Check(key, value, Sprayer()), Is.Null);
    }

    [Test]
    public void The_reason_names_the_setting_its_range_and_the_value_in_the_operators_units()
    {
        var s = Sprayer();
        Assert.That(VehicleToolLimits.Check("vehicle.wheelbase", "288", s), Is.EqualTo("Wheelbase must be 0.5 to 20 m (not 288 m)"));
        Assert.That(VehicleToolLimits.Check("tool.overlap", "8", s), Is.EqualTo("Overlap must be less than the implement width (8 m)"));
        s.IsMetric = false;
        Assert.That(VehicleToolLimits.Check("vehicle.wheelbase", "-2.88", s), Is.EqualTo("Wheelbase must be 1.64 to 65.62 ft (not -9.45 ft)"));
    }

    [Test]
    public void AutoSteer_cannot_steer_with_no_wheelbase_or_passes_on_top_of_each_other()
    {
        var s = Sprayer();
        s.Vehicle.Wheelbase = 2.88;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Is.Null);

        s.Vehicle.Wheelbase = 0;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Does.Contain("wheelbase"));
        s.Vehicle.Wheelbase = -2.88;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Does.Contain("wheelbase"));
        s.Vehicle.Wheelbase = 2.88;

        s.Tool.Overlap = 8;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Does.Contain("overlap"));
        s.Tool.Overlap = 0;

        s.Vehicle.AntennaOffset = double.NaN;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Does.Contain("antenna"));
    }
}
