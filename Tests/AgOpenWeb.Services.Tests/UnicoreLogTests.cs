// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Unicore / NovAtel ASCII logs: the CRC-32 frame (long <c>#</c> and short <c>%</c>
/// headers) and the INSPVAX decoder for the UM981's one-fix-per-epoch INS output.
/// </summary>
[TestFixture]
public class UnicoreLogTests
{
    // UM981 manual R1.0, section 2.3.4, "Message Output" — CRC as printed there.
    private const string ManualExample =
        "#INSPVAXA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562;"
        + "INS_SOLUTION_GOOD,INS_PSRSP,51.11637873403,-114.03825114994,1063.6093,-16.9000,-0.0845,-0.0464,-0.0127,"
        + "0.138023492,0.069459386,90.000923268,0.9428,0.6688,1.4746,0.0430,0.0518,0.0521,0.944295466,0.944567084,1.000131845,3,0*e877c178";

    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>A log with its CRC computed, for lines the manual does not print.</summary>
    private static byte[] Framed(string withoutCrc)
    {
        uint crc = UnicoreCrc32.Compute(B(withoutCrc)[1..]);
        return B(withoutCrc + "*" + crc.ToString("x8"));
    }

    private static NmeaParseResult Parse(byte[] line, out VehicleState state, ConfigurationStore? config = null)
    {
        state = new VehicleState();
        NmeaParserServiceFast.TryParseIntoState(line, ref state, config ?? new ConfigurationStore(), out var result);
        return result;
    }

    [Test]
    public void The_CRC_matches_the_manuals_example()
    {
        var text = ManualExample;
        int star = text.LastIndexOf('*');
        Assert.That(UnicoreCrc32.Compute(B(text[1..star])), Is.EqualTo(0xe877c178u));
    }

    [Test]
    public void The_manuals_INSPVAXA_decodes_to_one_fix()
    {
        Assert.That(Parse(B(ManualExample), out var s), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.Multiple(() =>
        {
            Assert.That(s.SentenceType, Is.EqualTo(GpsSentenceType.Inspvax));
            Assert.That(s.Latitude, Is.EqualTo(51.11637873403));
            Assert.That(s.Longitude, Is.EqualTo(-114.03825114994));
            Assert.That(s.Altitude, Is.EqualTo(1063.6093));
            Assert.That(s.Heading, Is.EqualTo(90.000923268), "the INS azimuth");
            Assert.That(s.HasDualHeading, Is.True, "the receiver's own heading, no IMU fusion");
            Assert.That(s.ImuValid, Is.False);
            Assert.That(s.Roll, Is.EqualTo(0.138023492));
            Assert.That(s.Pitch, Is.EqualTo(0.069459386));
            Assert.That(s.Speed, Is.EqualTo(Math.Sqrt(0.0845 * 0.0845 + 0.0464 * 0.0464)).Within(1e-12), "from the north and east velocities, not the undulation");
            Assert.That(s.FixQuality, Is.EqualTo(1), "INS_PSRSP");
            Assert.That(s.Satellites, Is.Zero);
            Assert.That(s.Hdop, Is.Zero);
        });
    }

    [Test]
    public void A_wrong_CRC_is_BadChecksum()
    {
        var bytes = B(ManualExample);
        bytes[^1] = (byte)'9';
        Assert.That(Parse(bytes, out _), Is.EqualTo(NmeaParseResult.BadChecksum));
    }

    [Test]
    public void The_short_header_is_framed_the_same_way()
    {
        var line = Framed("%INSPVAXSA,1695,309428000;INS_SOLUTION_GOOD,INS_RTKFIXED,51.1,-114.0,1063.6,-16.9,1.0,0.0,0.0,0.5,0.2,45.0,0.9,0.6,1.4,0.04,0.05,0.05,0.9,0.9,1.0,3,0");
        Assert.That(Parse(line, out var s), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(s.FixQuality, Is.EqualTo(4));
        Assert.That(s.Speed, Is.EqualTo(1.0));
        Assert.That(s.Heading, Is.EqualTo(45.0));
    }

    [Test]
    public void INSPVAA_without_the_deviations_decodes_too()
    {
        var line = Framed("#INSPVAA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562;INS_ALIGNMENT_COMPLETE,INS_RTKFLOAT,51.1,-114.0,1063.6,-16.9,0.0,2.0,0.0,0.5,0.2,180.0");
        Assert.That(Parse(line, out var s), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(s.FixQuality, Is.EqualTo(5));
        Assert.That(s.Speed, Is.EqualTo(2.0));
        Assert.That(s.Heading, Is.EqualTo(180.0));
    }

    [Test]
    public void An_inactive_INS_is_no_fix()
    {
        var line = Framed("#INSPVAXA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562;INS_INACTIVE,INS_RTKFIXED,51.1,-114.0,1063.6,-16.9,0.0,0.0,0.0,0.0,0.0,0.0,0.9,0.6,1.4,0.04,0.05,0.05,0.9,0.9,1.0,0,0");
        Assert.That(Parse(line, out var s), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(s.FixQuality, Is.Zero);
    }

    [TestCase("INS_RTKFIXED", 4)]
    [TestCase("INS_RTKFLOAT", 5)]
    [TestCase("INS_PSRDIFF", 2)]
    [TestCase("INS_PSRSP", 1)]
    [TestCase("INS", 1)]
    [TestCase("NONE", 0)]
    public void Position_types_map_to_the_GGA_scale(string posType, int fix) =>
        Assert.That(NmeaParserServiceFast.InsFixQuality(B(posType)), Is.EqualTo(fix));

    [Test]
    public void Roll_goes_through_the_AHRS_calibration()
    {
        var config = new ConfigurationStore();
        config.Ahrs.RollZero = 0.1;
        Assert.That(Parse(B(ManualExample), out var s, config), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(s.Roll, Is.EqualTo(0.038023492).Within(1e-12));
    }

    [Test]
    public void Another_log_is_unknown_and_a_broken_frame_is_a_bad_frame()
    {
        Assert.That(Parse(Framed("#BESTNAVA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562;SOL_COMPUTED,NARROW_INT,51.1,-114.0"), out _),
            Is.EqualTo(NmeaParseResult.UnknownSentence));
        Assert.That(Parse(B("#INSPVAXA,COM1,0;INS_SOLUTION_GOOD*12"), out _), Is.EqualTo(NmeaParseResult.BadFrame));
        Assert.That(Parse(B("#INSPVAXA,COM1,0,73.5,FINESTEERING,1695,309428.000,00000040,4e77,43562,INS_SOLUTION_GOOD*e877c178"), out _),
            Is.Not.EqualTo(NmeaParseResult.Accepted), "no ';' means no body");
    }

    [Test]
    public void The_INSPVAX_path_allocates_nothing()
    {
        var line = B(ManualExample);
        var config = new ConfigurationStore();
        var state = new VehicleState();
        for (int i = 0; i < 100; i++) NmeaParserServiceFast.TryParseIntoState(line, ref state, config, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) NmeaParserServiceFast.TryParseIntoState(line, ref state, config, out _);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
    }
}
