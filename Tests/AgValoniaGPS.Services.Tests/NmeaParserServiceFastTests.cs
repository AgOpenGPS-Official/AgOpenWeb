// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Globalization;
using System.Text;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Unit tests for <see cref="NmeaParserServiceFast"/>, the sole NMEA parser
/// after Phase B C5. Ported from the deleted <c>NmeaParserServiceTests</c>;
/// tests that exercised heading fusion, fix-quality filtering, or the
/// ConsecutiveBadFixes counter were dropped because those responsibilities
/// moved to <see cref="Gps.GpsHeadingFusionService"/> and
/// <see cref="Gps.GpsFixQualityValidator"/> (Phase B C2) and are covered
/// by those services' own test suites.
/// </summary>
[TestFixture]
[NonParallelizable]
public class NmeaParserServiceFastTests
{
    private IGpsService _gpsService = null!;
    private NmeaParserServiceFast _parser = null!;
    private GpsData? _lastGpsData;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());

        _gpsService = Substitute.For<IGpsService>();
        _gpsService.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
                   .Do(ci => _lastGpsData = ci.Arg<GpsData>());
        _lastGpsData = null;

        _parser = new NmeaParserServiceFast(_gpsService);
    }

    // ── Checksum validation ─────────────────────────────────────────────

    [Test]
    public void ParseSpan_ValidChecksum_Parses()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        _gpsService.Received(1).UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSpan_InvalidChecksum_DoesNotParse()
    {
        byte[] sentence = Encoding.ASCII.GetBytes(
            "$PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100,0,5.5,90.0,0,0,0*FF");

        _parser.ParseSpan(sentence);

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSpan_TooShort_DoesNotParse()
    {
        byte[] sentence = Encoding.ASCII.GetBytes("$PANDA,1");

        _parser.ParseSpan(sentence);

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSpan_NoDollar_DoesNotParse()
    {
        byte[] sentence = Encoding.ASCII.GetBytes(
            "PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100,0,5.5,90.0,0,0,0*48");

        _parser.ParseSpan(sentence);

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSpan_UnknownSentenceType_DoesNotParse()
    {
        byte[] sentence = BuildSentence("GPGGA,123456.00,4807.038,N,01131.000,E,1,12,0.9,100,M,0,M,,");

        _parser.ParseSpan(sentence);

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    // ── Latitude / Longitude parsing ───────────────────────────────────

    [Test]
    public void ParseSpan_NorthernLatitude_Positive()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    [Test]
    public void ParseSpan_SouthernLatitude_Negative()
    {
        byte[] sentence = BuildPandaBytes(3352.128, "S", 15112.556, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.LessThan(0));
    }

    [Test]
    public void ParseSpan_WesternLongitude_Negative()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 07400.360, "W", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Longitude, Is.LessThan(0));
    }

    [Test]
    public void ParseSpan_EasternLongitude_Positive()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Longitude, Is.GreaterThan(0));
    }

    // ── Field passthrough ──────────────────────────────────────────────

    [Test]
    public void ParseSpan_FixQuality_PassesThrough()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.FixQuality, Is.EqualTo(4));
        Assert.That(_lastGpsData.SatellitesInUse, Is.EqualTo(12));
    }

    [Test]
    public void ParseSpan_SpeedInKnots_ConvertedToMs()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 10.0, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Speed, Is.EqualTo(5.14444).Within(0.01));
    }

    // ── Sentence-type coverage ─────────────────────────────────────────

    [Test]
    public void ParseSpan_PAOGI_ParsesLikePANDA()
    {
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        // Swap the 5 bytes of "PANDA" for "PAOGI" (same length) and recompute the checksum.
        byte[] paogi = new byte[sentence.Length];
        sentence.CopyTo(paogi, 0);
        Encoding.ASCII.GetBytes("PAOGI").CopyTo(paogi, 1);
        RecomputeChecksumInPlace(paogi);

        _parser.ParseSpan(paogi);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static byte[] BuildPandaBytes(
        double lat, string latDir, double lon, string lonDir,
        int fixQuality, int sats, double hdop, double alt,
        double diffAge, double speedKnots, double heading,
        double roll, double pitch, double yawRate)
    {
        string body = string.Format(CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},{10:F1},{11:F1},{12:F1},{13:F1}",
            lat, latDir, lon, lonDir, fixQuality, sats, hdop, alt, diffAge, speedKnots, heading, roll, pitch, yawRate);
        return BuildSentence(body);
    }

    private static byte[] BuildSentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return Encoding.ASCII.GetBytes($"${body}*{checksum:X2}");
    }

    private static void RecomputeChecksumInPlace(byte[] sentence)
    {
        int asterisk = System.Array.IndexOf(sentence, (byte)'*');
        if (asterisk < 0) return;

        byte checksum = 0;
        for (int i = 1; i < asterisk; i++) checksum ^= sentence[i];

        string hex = checksum.ToString("X2");
        sentence[asterisk + 1] = (byte)hex[0];
        sentence[asterisk + 2] = (byte)hex[1];
    }
}
