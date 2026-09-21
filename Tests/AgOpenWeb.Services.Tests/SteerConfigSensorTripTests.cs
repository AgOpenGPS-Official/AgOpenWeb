// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #105: PGN 251 byte 6 is the firmware's sensor kickout threshold (AiO v4 Autosteer.ino,
/// steerConfig.PulseCountMax): it disengages when encoder pulses, or the 0-255 pressure /
/// current reading, is >= this. It used to be hard-coded 0, so any enabled sensor tripped on
/// every loop.
/// </summary>
[TestFixture]
public class SteerConfigSensorTripTests
{
    // The firmware's check, verbatim in spirit: reading >= PulseCountMax → kick out.
    private static bool FirmwareKicksOut(byte threshold, int reading) => reading >= threshold;

    [Test]
    public void TurnSensor_SendsTheEncoderCount()
    {
        var c = new AutoSteerConfig { TurnSensorEnabled = true, TurnSensorCounts = 12 };

        byte b = PgnBuilder.BuildSteerConfigPgn(c)[6];

        Assert.That(b, Is.EqualTo(12));
        Assert.That(FirmwareKicksOut(b, 3), Is.False, "a few pulses mustn't kick out");
        Assert.That(FirmwareKicksOut(b, 12), Is.True);
    }

    [TestCase(40, 102)]  // 40 % → raw 102 (AgOpenGPS shows raw × 0.392 as %)
    [TestCase(99, 252)]
    public void PressureSensor_SendsTheTripPointAsRaw(int pct, int raw)
    {
        var c = new AutoSteerConfig { PressureSensorEnabled = true, PressureTripPoint = pct, TurnSensorCounts = 5 };
        Assert.That(PgnBuilder.BuildSteerConfigPgn(c)[6], Is.EqualTo(raw));
    }

    [Test]
    public void CurrentSensor_SendsItsOwnTripPoint()
    {
        var c = new AutoSteerConfig { CurrentSensorEnabled = true, CurrentTripPoint = 50 };
        Assert.That(PgnBuilder.BuildSteerConfigPgn(c)[6], Is.EqualTo(128));
    }

    [Test]
    public void ZeroPercentTripPoint_IsOff_NotAlwaysTripping()
    {
        // 0 % is the default and means "off" host-side; raw 0 would trip on every loop.
        var c = new AutoSteerConfig { PressureSensorEnabled = true, PressureTripPoint = 0 };
        byte b = PgnBuilder.BuildSteerConfigPgn(c)[6];

        Assert.That(b, Is.EqualTo(255));
        Assert.That(FirmwareKicksOut(b, 0), Is.False);
    }
}
