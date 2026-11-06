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
    [TestCase("autosteer.proportionalGain", "0")]
    [TestCase("autosteer.proportionalGain", "999")]
    [TestCase("autosteer.integralGain", "9")]
    [TestCase("autosteer.countsPerDegree", "0")]
    [TestCase("autosteer.countsPerDegree", "300")]
    [TestCase("autosteer.wasOffset", "5000")]
    [TestCase("autosteer.ackermann", "250")]
    [TestCase("autosteer.maxSteerAngle", "5")]
    [TestCase("autosteer.maxSteerAngle", "NaN")]
    [TestCase("autosteer.speedFactor", "0")]
    [TestCase("autosteer.acquireFactor", "10")]
    [TestCase("autosteer.maxPwm", "10")]
    [TestCase("autosteer.minPwm", "0")]
    [TestCase("autosteer.minSteerSpeed", "25")]
    [TestCase("autosteer.minSteerSpeed", "-1")]
    [TestCase("autosteer.maxSteerSpeed", "60")]
    [TestCase("autosteer.manualTurnsSpeed", "30")]
    [TestCase("autosteer.sideHillCompensation", "2")]
    [TestCase("autosteer.uTurnCompensation", "-100")]
    [TestCase("autosteer.lineWidth", "0")]
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
    [TestCase("autosteer.proportionalGain", "50")]
    [TestCase("autosteer.proportionalGain", "1")]
    [TestCase("autosteer.proportionalGain", "200")]
    [TestCase("autosteer.integralGain", "0")]
    [TestCase("autosteer.countsPerDegree", "110")]
    [TestCase("autosteer.wasOffset", "-260")]
    [TestCase("autosteer.maxSteerAngle", "35")]
    [TestCase("autosteer.minSteerSpeed", "0.5")]
    [TestCase("autosteer.maxSteerSpeed", "15")]
    [TestCase("autosteer.uTurnCompensation", "-80")]
    [TestCase("autosteer.invertWas", "1")]        // toggles have no range
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

    // The Speed step took 25 km/h as the minimum and 5 km/h as the maximum (the steer wizard
    // scenarios): AutoSteer could then never engage, and nothing said so.
    [Test]
    public void The_steer_speeds_must_leave_a_band_between_them()
    {
        var s = Sprayer();
        s.AutoSteer.MinSteerSpeed = 0.5;
        s.AutoSteer.MaxSteerSpeed = 8;
        Assert.Multiple(() =>
        {
            Assert.That(VehicleToolLimits.Check("autosteer.proportionalGain", "0", s), Is.EqualTo("Proportional gain must be 1 to 200 (not 0)"));
            Assert.That(VehicleToolLimits.Check("autosteer.minSteerSpeed", "8", s), Is.EqualTo("Min steer speed must be below the max steer speed (8 km/h)"));
            Assert.That(VehicleToolLimits.Check("autosteer.minSteerSpeed", "7", s), Is.Null);
            Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "0.5", s), Is.EqualTo("Max steer speed must be above the min steer speed (0.5 km/h)"));
            Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "25", s), Is.Null);
            Assert.That(VehicleToolLimits.Check("autosteer.minSteerSpeed", "25", s), Is.EqualTo("Min steer speed must be 0 to 10 km/h (not 25 km/h)"));
        });
        s.IsMetric = false;
        Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "0.5", s), Is.EqualTo("Max steer speed must be above the min steer speed (0.31 mph)"));
        Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "60", s), Is.EqualTo("Max steer speed must be 0 to 31.07 mph (not 37.28 mph)"));
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
        s.Vehicle.AntennaOffset = 0;

        s.AutoSteer.MinSteerSpeed = s.AutoSteer.MaxSteerSpeed;
        Assert.That(VehicleToolLimits.WhyCannotSteer(s), Does.Contain("min steer speed"));
    }

    // Checked before the change: a refused section count used to be undone afterwards, and the
    // sections brought back came at the default width (8 × 76.2 cm with a 1 m overlap, set to
    // 1 section: the boom grew from 6.1 m to 7.76 m).
    [Test]
    public void Fewer_sections_than_the_overlap_needs_are_refused_before_anything_changes()
    {
        var s = new ConfigurationStore();
        s.NumSections = 8;
        for (int i = 0; i < 8; i++) s.Tool.SetSectionWidth(i, 76.2);
        s.Tool.Overlap = 1.0;

        Assert.Multiple(() =>
        {
            Assert.That(VehicleToolLimits.Check("tool.numSections", "1", s), Does.Contain("wider than the overlap"));
            Assert.That(VehicleToolLimits.Check("tool.numSections", "2", s), Is.Null, "1.52 m is wider than the 1 m overlap");
            Assert.That(VehicleToolLimits.Check("tool.numSections", "12", s), Is.Null, "more sections");
            Assert.That(s.NumSections, Is.EqualTo(8));
            Assert.That(s.ActualToolWidth, Is.EqualTo(8 * 0.762).Within(1e-9), "the widths are untouched");
        });
    }

    [Test]
    public void A_section_narrowed_below_the_overlap_is_refused_and_one_not_in_use_is_not_counted()
    {
        var s = new ConfigurationStore();
        s.NumSections = 1;
        s.Tool.SetSectionWidth(0, 300);
        s.Tool.Overlap = 1.0;

        Assert.Multiple(() =>
        {
            Assert.That(VehicleToolLimits.Check("tool.sectionWidth", "0,90", s), Does.Contain("wider than the overlap"));
            Assert.That(VehicleToolLimits.Check("tool.sectionWidth", "0,150", s), Is.Null);
            Assert.That(VehicleToolLimits.Check("tool.sectionWidth", "3,10", s), Is.Null, "section 4 is not in use");
        });
    }
}
