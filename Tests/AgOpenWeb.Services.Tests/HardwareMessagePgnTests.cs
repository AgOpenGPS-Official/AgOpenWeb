using System.Text;
using AgOpenWeb.Services.AutoSteer;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>#110: PGN 221 hardware messages, read like AgOpenGPS UDPComm case 221.</summary>
[TestFixture]
public class HardwareMessagePgnTests
{
    private static byte[] Pgn(string text, byte seconds, byte colour)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var d = new byte[8 + t.Length];
        d[0] = 0x80; d[1] = 0x81; d[2] = 0x7F; d[3] = 221;
        d[4] = (byte)(t.Length + 2); d[5] = seconds; d[6] = colour;
        t.CopyTo(d, 7);
        d[^1] = 0xCC; // CRC
        return d;
    }

    [Test]
    public void Parses_TextSecondsAndColour()
    {
        Assert.That(AutoSteerService.TryParseHardwareMessage(Pgn("Steer motor fault", 5, 0), out var text, out int secs, out bool warn), Is.True);
        Assert.That(text, Is.EqualTo("Steer motor fault"));
        Assert.That(secs, Is.EqualTo(5));
        Assert.That(warn, Is.True, "colour byte 0 = warning (AgOpenGPS salmon)");

        AutoSteerService.TryParseHardwareMessage(Pgn("Ready", 3, 1), out _, out _, out warn);
        Assert.That(warn, Is.False);
    }

    [Test]
    public void TooShort_IsIgnored()
        => Assert.That(AutoSteerService.TryParseHardwareMessage(new byte[] { 0x80, 0x81, 0x7F, 221, 2, 1, 0, 0 }, out _, out _, out _), Is.False);
}
