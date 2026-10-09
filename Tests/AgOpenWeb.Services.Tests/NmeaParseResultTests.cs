// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services.Tests;

/// <summary>Why the parser refused a line, for the System Data card's counters.</summary>
[TestFixture]
public class NmeaParseResultTests
{
    private static byte[] Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return Encoding.ASCII.GetBytes($"${body}*{checksum:X2}");
    }

    private static NmeaParseResult Parse(byte[] line)
    {
        var state = new VehicleState();
        NmeaParserServiceFast.TryParseIntoState(line, ref state, new ConfigurationStore(), out var result);
        return result;
    }

    [Test]
    public void A_good_PANDA_is_accepted() =>
        Assert.That(Parse(Sentence("PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00")), Is.EqualTo(NmeaParseResult.Accepted));

    [Test]
    public void A_wrong_checksum_is_BadChecksum()
    {
        var line = Sentence("PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00");
        line[^1] = (byte)(line[^1] == '0' ? '1' : '0');
        Assert.That(Parse(line), Is.EqualTo(NmeaParseResult.BadChecksum));
    }

    [Test]
    public void A_sentence_this_build_does_not_decode_is_UnknownSentence() =>
        Assert.That(Parse(Sentence("GNGGA,123519.00,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,1.0,0000")), Is.EqualTo(NmeaParseResult.UnknownSentence));

    [Test]
    public void A_hash_log_this_build_does_not_decode_is_UnknownSentence_and_a_bad_CRC_is_BadChecksum()
    {
        const string bestnav = "#BESTNAVA,COM1,0,55.0,FINESTEERING,2000,10.0,00000040,4e77,43562;SOL_COMPUTED,NARROW_INT,51.1,-114.0";
        uint crc = AgOpenWeb.Services.Gps.UnicoreCrc32.Compute(Encoding.ASCII.GetBytes(bestnav[1..]));
        Assert.That(Parse(Encoding.ASCII.GetBytes(bestnav + "*" + crc.ToString("x8"))), Is.EqualTo(NmeaParseResult.UnknownSentence));
        Assert.That(Parse(Encoding.ASCII.GetBytes(bestnav + "*1a2b3c4d")), Is.EqualTo(NmeaParseResult.BadChecksum));
    }

    [Test]
    public void Too_short_or_no_checksum_marker_is_BadFrame()
    {
        Assert.That(Parse(Encoding.ASCII.GetBytes("$PANDA,1")), Is.EqualTo(NmeaParseResult.BadFrame));
        Assert.That(Parse(Encoding.ASCII.GetBytes("$PANDA,123456.00,4807.038,N,01131.000,E,4,12")), Is.EqualTo(NmeaParseResult.BadFrame));
    }

    [Test]
    public void A_known_sentence_with_too_few_fields_is_BadFields() =>
        Assert.That(Parse(Sentence("PANDA,123456.00,4807.038,N,01131.000,E,4,12")), Is.EqualTo(NmeaParseResult.BadFields));
}
