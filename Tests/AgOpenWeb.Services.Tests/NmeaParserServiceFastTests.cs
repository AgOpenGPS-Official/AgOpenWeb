// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Globalization;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

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

        _parser = new NmeaParserServiceFast(_gpsService, ConfigurationStore.Instance);
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
        byte[] sentence = BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSpan(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    // ── IMU heading + roll wire-format scaling (zero-copy ParseIntoState) ─

    [Test]
    public void ParseIntoState_PANDA_DividesHeadingBy10()
    {
        // Wire "905" represents 90.5° on the AiO PANDA encoding.
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 90.5, roll: 0, pitch: 0, yawRate: 0);
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.True);
        Assert.That(state.ImuValid, Is.True);
        Assert.That(state.ImuHeading, Is.EqualTo(90.5).Within(1e-6));
        // Primary heading is seeded from IMU at parse time (pipeline overrides at speed).
        Assert.That(state.Heading, Is.EqualTo(90.5).Within(1e-6));
    }

    [Test]
    public void ParseIntoState_PANDA_DividesRollBy10()
    {
        // Wire "57" represents 5.7° on PANDA encoding.
        byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 90.0, roll: 5.7, pitch: 0, yawRate: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(state.Roll, Is.EqualTo(5.7).Within(1e-6));
    }

    [Test]
    public void ParseIntoState_PANDA_65535SentinelMarksImuInvalid()
    {
        byte[] sentence = BuildPandaBytesNoImu(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5);
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.True);
        Assert.That(state.ImuValid, Is.False);
        Assert.That(state.ImuHeading, Is.EqualTo(0));
        Assert.That(state.Roll, Is.EqualTo(0));
        Assert.That(state.Heading, Is.EqualTo(0),
            "primary heading should fall to 0 when IMU is invalid; pipeline picks up fix-to-fix");
    }

    [Test]
    public void ParseIntoState_PANDA_AppliesAhrsIsRollInvert()
    {
        // The Roll-calibration wizard step's IsRollInvert toggle must
        // take effect on live NMEA roll, not just on the values the
        // wizard captures via OnLeaving. Without this post-process the
        // operator's "Invert Roll" toggle has no visible effect on the
        // gauge.
        var prior = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance;
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(
            new AgOpenWeb.Models.Configuration.ConfigurationStore());
        try
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.IsRollInvert = true;

            byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
                heading: 90.0, roll: 5.7, pitch: 0, yawRate: 0);
            var state = new VehicleState();
            NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

            Assert.That(state.Roll, Is.EqualTo(-5.7).Within(1e-6),
                "IsRollInvert flips the sign of the parsed roll before downstream consumers.");
        }
        finally
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(prior);
        }
    }

    [Test]
    public void ParseIntoState_PANDA_SubtractsAhrsRollZero()
    {
        // Zero Roll calibration captures the current reading as the
        // zero point; subsequent parses subtract it so the gauge centres.
        var prior = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance;
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(
            new AgOpenWeb.Models.Configuration.ConfigurationStore());
        try
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.RollZero = 2.0;

            byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
                heading: 90.0, roll: 5.7, pitch: 0, yawRate: 0);
            var state = new VehicleState();
            NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

            Assert.That(state.Roll, Is.EqualTo(3.7).Within(1e-6),
                "RollZero offset is subtracted from the raw IMU roll.");
        }
        finally
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(prior);
        }
    }

    [Test]
    public void ParseIntoState_PANDA_InvertAndZero_CompositeOrder()
    {
        // Order matters: invert first, then subtract zero. Mirrors the
        // legacy AiO firmware pipeline so the operator's calibration
        // gives the same readings as the original hardware path.
        var prior = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance;
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(
            new AgOpenWeb.Models.Configuration.ConfigurationStore());
        try
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.IsRollInvert = true;
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.RollZero = 2.0;

            byte[] sentence = BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
                heading: 90.0, roll: 5.7, pitch: 0, yawRate: 0);
            var state = new VehicleState();
            NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

            // -5.7 - 2.0 = -7.7
            Assert.That(state.Roll, Is.EqualTo(-7.7).Within(1e-6),
                "Composite: invert first (-5.7), then subtract zero (-7.7).");
        }
        finally
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(prior);
        }
    }

    [Test]
    public void ParseIntoState_PAOGI_AlsoAppliesAhrsCalibration()
    {
        // Same post-process must mirror in the PAOGI branch — its roll
        // field is float decimal degrees but the calibration semantics
        // are identical.
        var prior = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance;
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(
            new AgOpenWeb.Models.Configuration.ConfigurationStore());
        try
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.IsRollInvert = true;
            AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Ahrs.RollZero = 1.0;

            byte[] sentence = BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
                heading: 90.5, roll: 1.25, pitch: 0, yawRate: 0);
            var state = new VehicleState();
            NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

            // -1.25 - 1.0 = -2.25
            Assert.That(state.Roll, Is.EqualTo(-2.25).Within(1e-6));
        }
        finally
        {
            AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(prior);
        }
    }

    [Test]
    public void ParseIntoState_PAOGI_HeadingAsFloatNoScaling_ImuStaysInvalid()
    {
        // PAOGI emits heading as float decimal degrees — no ×10 scaling.
        byte[] sentence = BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 90.5, roll: 1.25, pitch: 0, yawRate: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(state.Heading, Is.EqualTo(90.5).Within(1e-6));
        Assert.That(state.Roll, Is.EqualTo(1.25).Within(1e-6));
        Assert.That(state.ImuValid, Is.False,
            "PAOGI is dual-antenna ground truth — fusion isn't needed, ImuValid stays false");
        Assert.That(state.ImuHeading, Is.EqualTo(0));
    }

    // #157: only PAOGI's heading is a dual-antenna heading (AgIO: PAOGI field 12 →
    // headingTrueDual, PANDA field 12 → imuHeading).
    [Test]
    public void ParseIntoState_DualHeadingFlag_OnlyForPaogi()
    {
        var state = new VehicleState();
        NmeaParserServiceFast.ParseIntoState(BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 95.0, roll: 0, pitch: 0, yawRate: 0), ref state, ConfigurationStore.Instance);
        Assert.That(state.HasDualHeading, Is.False, "PANDA's heading is the IMU's");
        Assert.That(state.SentenceType, Is.EqualTo(GpsSentenceType.Panda));

        NmeaParserServiceFast.ParseIntoState(BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 95.0, roll: 0, pitch: 0, yawRate: 0), ref state, ConfigurationStore.Instance);
        Assert.That(state.HasDualHeading, Is.True);
        Assert.That(state.SentenceType, Is.EqualTo(GpsSentenceType.Paogi));
    }

    [Test]
    public void ParseSpan_DualHeadingFlag_OnlyForPaogi()
    {
        _parser.ParseSpan(BuildPandaBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: 95.0, roll: 0, pitch: 0, yawRate: 0));
        Assert.That(_lastGpsData!.HasDualHeading, Is.False);
        Assert.That(_lastGpsData.SentenceType, Is.EqualTo(GpsSentenceType.Panda));

        _parser.ParseSpan(BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0));
        Assert.That(_lastGpsData!.HasDualHeading, Is.True);
        Assert.That(_lastGpsData.SentenceType, Is.EqualTo(GpsSentenceType.Paogi));
    }

    // Upstream AgValoniaGPS issue #486: variable-width (no leading-zero) $PAOGI
    // heading/roll fields like "3.7" / "-0.65" displayed as 0 — only 3-integer-digit
    // values (>= 100) parsed. Our parser splits on commas (GetField) and uses
    // Utf8Parser, so field width is irrelevant; this guards that small values in the
    // reported failing ranges round-trip (heading 0–99.99°, roll -9.99–99.99°).
    [TestCase(3.7, -0.65)]
    [TestCase(1.5, 0.1)]
    [TestCase(99.9, 99.99)]
    [TestCase(9.5, -9.99)]
    public void ParseIntoState_PAOGI_VariableWidthSmallHeadingRoll_ParsedNotZero(double heading, double roll)
    {
        byte[] sentence = BuildPaogiBytes(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5,
            heading: heading, roll: roll, pitch: 0, yawRate: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        // F1 heading / F2 roll formatting in the builder, matched here.
        Assert.That(state.Heading, Is.EqualTo(System.Math.Round(heading, 1)).Within(1e-6));
        Assert.That(state.Roll, Is.EqualTo(System.Math.Round(roll, 2)).Within(1e-6));
    }

    // ── $KSXT (Bynav / Unicore, straight from the receiver) ─────────────
    // Unicore N4 manual table 7-131, 0-based: lon 2, lat 3, height 4, heading 5, pitch 6
    // (= vehicle roll, antennas across), track 7, vel km/h 8, roll 9, pos qual 10, heading
    // qual 11, slave sats 12, master sats 13, ENU 14-16, ENU vel 17-19, 20-21 reserved.

    [Test]
    public void ParseIntoState_KSXT_ParsesTheUnicoreManualExample()
    {
        // Verbatim from the manual (7.3.59), checksum as printed.
        byte[] sentence = Encoding.ASCII.GetBytes(
            "$KSXT,20190909084745.00,116.23662400,40.07897925,68.3830,299.22,-67.03,190.28,0.022,,1,3,46,28,,,,-0.004,-0.021,-0.020,,*27");
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.True);
        Assert.That(state.Latitude, Is.EqualTo(40.07897925).Within(1e-9));
        Assert.That(state.Longitude, Is.EqualTo(116.23662400).Within(1e-9));
        Assert.That(state.Altitude, Is.EqualTo(68.3830).Within(1e-6));
        Assert.That(state.Heading, Is.EqualTo(299.22).Within(1e-6));
        Assert.That(state.Speed, Is.EqualTo(0.022 / 3.6).Within(1e-9));
        Assert.That(state.FixQuality, Is.EqualTo(1), "position quality 1 = single");
        Assert.That(state.Roll, Is.EqualTo(-67.03).Within(1e-6), "heading quality 3, so the baseline pitch is taken as roll");
        Assert.That(state.Satellites, Is.EqualTo(28), "master antenna's count");
        Assert.That(state.DifferentialAge, Is.EqualTo(0), "field 20 is reserved and empty");
    }

    [Test]
    public void ParseIntoState_KSXT_DecodesTheWholeFix()
    {
        byte[] sentence = BuildKsxtBytes(lon: -74.006000, lat: 43.712800, alt: 68.38, heading: 271.5,
            roll: -2.25, speedKmh: 18.0, posQuality: 3, headingQuality: 3, sats: 27, age: 1.2);
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.True);
        Assert.That(state.SentenceType, Is.EqualTo(GpsSentenceType.Ksxt));
        Assert.That(state.Latitude, Is.EqualTo(43.712800).Within(1e-7), "signed decimal degrees, no DDMM");
        Assert.That(state.Longitude, Is.EqualTo(-74.006000).Within(1e-7));
        Assert.That(state.Altitude, Is.EqualTo(68.38).Within(1e-6));
        Assert.That(state.Heading, Is.EqualTo(271.5).Within(1e-6));
        Assert.That(state.HeadingRadians, Is.EqualTo(271.5 * System.Math.PI / 180).Within(1e-9));
        Assert.That(state.HasDualHeading, Is.True, "the receiver's heading is a dual-antenna heading");
        Assert.That(state.ImuValid, Is.False);
        Assert.That(state.Roll, Is.EqualTo(-2.25).Within(1e-6));
        Assert.That(state.Speed, Is.EqualTo(5.0).Within(1e-6), "18 km/h is 5 m/s");
        Assert.That(state.FixQuality, Is.EqualTo(4), "KSXT quality 3 is RTK fixed");
        Assert.That(state.Satellites, Is.EqualTo(27));
        Assert.That(state.DifferentialAge, Is.EqualTo(1.2).Within(1e-6));
        Assert.That(state.Hdop, Is.EqualTo(0), "KSXT carries no HDOP");
    }

    [Test]
    public void ParseIntoState_KSXT_SpeedIsKmhNotKnots()
    {
        byte[] sentence = BuildKsxtBytes(lon: 1, lat: 1, alt: 0, heading: 0, roll: 0, speedKmh: 36.0,
            posQuality: 3, headingQuality: 3, sats: 20, age: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(state.Speed, Is.EqualTo(10.0).Within(1e-6));
    }

    // Receiver codes: 0 none, 1 single, 2 float, 3 fixed. Anything else is not guessed at.
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 5)]
    [TestCase(3, 4)]
    [TestCase(4, 0)]
    [TestCase(9, 0)]
    public void ParseIntoState_KSXT_MapsPositionQualityToGgaFixQuality(int posQuality, int expectedFix)
    {
        byte[] sentence = BuildKsxtBytes(lon: 1, lat: 1, alt: 0, heading: 0, roll: 0, speedKmh: 0,
            posQuality: posQuality, headingQuality: 3, sats: 20, age: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(state.FixQuality, Is.EqualTo(expectedFix));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ParseIntoState_KSXT_RollIsZeroUnlessHeadingIsRtkFixed(int headingQuality)
    {
        byte[] sentence = BuildKsxtBytes(lon: 1, lat: 1, alt: 0, heading: 90, roll: 3.5, speedKmh: 0,
            posQuality: 3, headingQuality: headingQuality, sats: 20, age: 0);
        var state = new VehicleState { Roll = 7 };

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, ConfigurationStore.Instance);

        Assert.That(state.Roll, Is.EqualTo(0), "AgIO sends 'no roll' when the heading solution isn't fixed");
        Assert.That(state.Heading, Is.EqualTo(90).Within(1e-6), "the heading itself is still taken");
    }

    [Test]
    public void ParseIntoState_KSXT_AppliesAhrsRollCalibration()
    {
        var cfg = new ConfigurationStore();
        cfg.Ahrs.IsRollInvert = true;
        cfg.Ahrs.RollZero = 0.5;
        byte[] sentence = BuildKsxtBytes(lon: 1, lat: 1, alt: 0, heading: 0, roll: 2.0, speedKmh: 0,
            posQuality: 3, headingQuality: 3, sats: 20, age: 0);
        var state = new VehicleState();

        NmeaParserServiceFast.ParseIntoState(sentence, ref state, cfg);

        Assert.That(state.Roll, Is.EqualTo(-2.5).Within(1e-6), "invert, then subtract zero — same as PAOGI");
    }

    [Test]
    public void ParseIntoState_KSXT_RejectsASentenceMissingPositionOrHeading()
    {
        // AgIO ignores the sentence unless time, lon, lat, height and heading are all present.
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(BuildSentence(
            "KSXT,20190909084745.00,,40.07897925,68.3837,2.5,-1.1,0.00,0.00,0.00,0.00,3,3,0,20,0.000,0.000,0.000,0.000,0.000,0.000,0.000"),
            ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void ParseIntoState_KSXT_RejectsATruncatedSentence()
    {
        var state = new VehicleState();

        bool ok = NmeaParserServiceFast.ParseIntoState(BuildSentence(
            "KSXT,20190909084745.00,116.23662400,40.07897925,68.3837,2.5,-1.1,0.00,0.00,0.00,0.00,3,3"),
            ref state, ConfigurationStore.Instance);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void ParseSpan_KSXT_ReachesTheGpsServiceAsADualHeadingFix()
    {
        _parser.ParseSpan(BuildKsxtBytes(lon: 116.236624, lat: 40.078979, alt: 68.4, heading: 2.5, roll: 0,
            speedKmh: 0, posQuality: 1, headingQuality: 0, sats: 12, age: 0));

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SentenceType, Is.EqualTo(GpsSentenceType.Ksxt));
        Assert.That(_lastGpsData.HasDualHeading, Is.True);
        Assert.That(_lastGpsData.CurrentPosition.Latitude, Is.EqualTo(40.078979).Within(1e-6));
        Assert.That(_lastGpsData.FixQuality, Is.EqualTo(1));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static byte[] BuildPandaBytes(
        double lat, string latDir, double lon, string lonDir,
        int fixQuality, int sats, double hdop, double alt,
        double diffAge, double speedKnots, double heading,
        double roll, double pitch, double yawRate)
    {
        // PANDA wire format: heading and roll are int scaled ×10 (with 65535
        // sentinel for "no IMU" on heading); pitch is rounded int; yawRate
        // is float. Mirrors AiO firmware's NAVProcessor::formatPANDAMessage.
        int headingX10 = (int)System.Math.Round(heading * 10.0);
        int rollX10 = (int)System.Math.Round(roll * 10.0);
        int pitchInt = (int)System.Math.Round(pitch);
        string body = string.Format(CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},{10},{11},{12},{13:F2}",
            lat, latDir, lon, lonDir, fixQuality, sats, hdop, alt, diffAge, speedKnots,
            headingX10, rollX10, pitchInt, yawRate);
        return BuildSentence(body);
    }

    /// <summary>
    /// Builds a PANDA sentence with the "no IMU" sentinel (65535) in the
    /// heading field. Used to test that parser detects the sentinel and
    /// leaves <c>state.ImuValid = false</c>.
    /// </summary>
    private static byte[] BuildPandaBytesNoImu(
        double lat, string latDir, double lon, string lonDir,
        int fixQuality, int sats, double hdop, double alt,
        double diffAge, double speedKnots)
    {
        string body = string.Format(CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},65535,0,0,0.00",
            lat, latDir, lon, lonDir, fixQuality, sats, hdop, alt, diffAge, speedKnots);
        return BuildSentence(body);
    }

    /// <summary>
    /// Builds a PAOGI sentence (dual antenna). Heading and roll are float
    /// decimal degrees, no scaling — matches AiO firmware's
    /// NAVProcessor::formatPAOGIMessage.
    /// </summary>
    private static byte[] BuildPaogiBytes(
        double lat, string latDir, double lon, string lonDir,
        int fixQuality, int sats, double hdop, double alt,
        double diffAge, double speedKnots, double heading,
        double roll, double pitch, double yawRate)
    {
        int pitchInt = (int)System.Math.Round(pitch);
        string body = string.Format(CultureInfo.InvariantCulture,
            "PAOGI,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},{10:F1},{11:F2},{12},{13:F2}",
            lat, latDir, lon, lonDir, fixQuality, sats, hdop, alt, diffAge, speedKnots,
            heading, roll, pitchInt, yawRate);
        return BuildSentence(body);
    }

    /// <summary>
    /// Builds a $KSXT sentence in the Unicore table 7-131 layout. <paramref name="roll"/>
    /// goes in the pitch field (6), which is what the decoder reads as roll; the
    /// receiver's own roll field (9) and the ENU fields are left empty like the manual's
    /// example. <paramref name="age"/> goes in reserved field 20, where AgIO reads it.
    /// </summary>
    private static byte[] BuildKsxtBytes(double lon, double lat, double alt, double heading, double roll,
        double speedKmh, int posQuality, int headingQuality, int sats, double age)
    {
        string body = string.Format(CultureInfo.InvariantCulture,
            "KSXT,20190909084745.00,{0:F8},{1:F8},{2:F4},{3:F2},{4:F2},0.00,{5:F3},,{6},{7},{8},{8},,,,,,,{9:F3},",
            lon, lat, alt, heading, roll, speedKmh, posQuality, headingQuality, sats, age);
        return BuildSentence(body);
    }

    private static byte[] BuildSentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return Encoding.ASCII.GetBytes($"${body}*{checksum:X2}");
    }
}
