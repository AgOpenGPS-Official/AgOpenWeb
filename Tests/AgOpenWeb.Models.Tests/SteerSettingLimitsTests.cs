// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// The steer wizard scenarios: Kp 0 or 999, and a min steer speed above the max, were written to
/// the live settings (and the module) before the wizard's Next refused them. They are refused at
/// config.set now, with AgOpenGPS's ranges (FormSteer).
/// </summary>
[TestFixture]
public class SteerSettingLimitsTests
{
    private static ConfigurationStore Store()
    {
        var s = new ConfigurationStore();
        s.AutoSteer.MinSteerSpeed = 0;
        s.AutoSteer.MaxSteerSpeed = 15;
        return s;
    }

    [TestCase("autosteer.proportionalGain", "0", false)]
    [TestCase("autosteer.proportionalGain", "999", false)]
    [TestCase("autosteer.proportionalGain", "12.5", false)]
    [TestCase("autosteer.proportionalGain", "1", true)]
    [TestCase("autosteer.proportionalGain", "200", true)]
    [TestCase("autosteer.integralGain", "9", false)]
    [TestCase("autosteer.integralGain", "0.5", true)]
    [TestCase("autosteer.minSteerSpeed", "25", false)]
    [TestCase("autosteer.minSteerSpeed", "3", true)]
    [TestCase("autosteer.maxSteerSpeed", "60", false)]
    [TestCase("autosteer.maxSteerSpeed", "25", true)]
    public void Steer_settings_take_AgOpenGPSs_ranges(string key, string value, bool ok)
    {
        var why = VehicleToolLimits.Check(key, value, Store());
        Assert.That(why == null, Is.EqualTo(ok), why);
    }

    [Test]
    public void The_min_steer_speed_must_stay_below_the_max()
    {
        var s = Store();
        s.AutoSteer.MaxSteerSpeed = 5;
        Assert.That(VehicleToolLimits.Check("autosteer.minSteerSpeed", "8", s), Does.StartWith("Min steer speed must be below the max steer speed"));

        s.AutoSteer.MinSteerSpeed = 3;
        Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "3", s), Does.StartWith("Max steer speed must be above the min steer speed"));
        Assert.That(VehicleToolLimits.Check("autosteer.maxSteerSpeed", "4", s), Is.Null);
    }
}
