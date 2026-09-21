// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// #103: Zero WAS must make the FIRMWARE's reported angle read 0, for normal and inverted
/// sensors. The firmware model below is copied from AgOpenGPS-Official/Boards
/// (TeensyModules/AIO v4 Firmware/AIO_v4_Firmware/Autosteer.ino, identical in v2.5 and the
/// Arduino UDP/USB v5 modules).
/// </summary>
[TestFixture]
public class WasCalibrationTests
{
    private static double FirmwareAngle(int raw, int wasOffset, double cpd, bool invert) =>
        invert
            ? (raw - 6805 - wasOffset) / -cpd
            : (raw - 6805 + wasOffset) / cpd;

    [TestCase(false, 7300, 0, 100.0)]   // wheels straight but sensor reads +4.95°
    [TestCase(false, 6300, 150, 85.0)]  // reads negative, with a prior offset
    [TestCase(true, 7300, 0, 100.0)]
    [TestCase(true, 6300, -150, 85.0)]
    public void ZeroedOffset_MakesTheFirmwareReadZero(bool invert, int raw, int offset, double cpd)
    {
        double before = FirmwareAngle(raw, offset, cpd, invert);
        Assert.That(System.Math.Abs(before), Is.GreaterThan(1), "precondition: sensor not zeroed");

        int zeroed = WasCalibration.ZeroedOffset(offset, before, cpd);

        Assert.That(FirmwareAngle(raw, zeroed, cpd, invert), Is.EqualTo(0).Within(0.5 / cpd),
            "After Zero WAS the module must report ~0° (within one count)");
    }

    [Test]
    public void TryZero_RefusesOutOfRange_LikeAgOpenGPS()
    {
        Assert.That(WasCalibration.TryZero(0, 45, 100, out _), Is.False, "45° × 100 = 4500 counts > 3900");
        Assert.That(WasCalibration.TryZero(0, 5, 100, out int ok), Is.True);
        Assert.That(ok, Is.EqualTo(-500));
    }
}
