// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Text;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests.Pipeline;

/// <summary>
/// Per-field parity check between the two parsers. Phase B C5 deletes
/// <see cref="NmeaParserService"/>; this test confirms that before the
/// deletion happens, the raw field outputs of both parsers agree on a
/// representative PANDA sentence. Fusion and antenna transforms are
/// explicitly suppressed so only the parse is compared.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class NmeaParserParityTests
{
    private const double Tolerance = 1e-9;

    // Altitude and Speed use float.TryParse in the string parser but
    // double.TryParse in the fast parser. Float rounding introduces an ULP-scale
    // divergence (~5e-8 on the test inputs). The fast parser is the more
    // accurate one; the string parser is about to retire anyway.
    private const double FloatBackedTolerance = 1e-6;

    [SetUp]
    public void SetUp()
    {
        // Neutralize post-parse processing so the comparison isolates parsing.
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = false;
        c.HeadingFusionWeight = 1.0;   // no IMU blend
        c.MinGpsStep = 1e9;            // fix-to-fix never triggers
        c.MinFixQuality = 0;           // no rejection
        c.MaxHdop = 99.0;
        c.MaxDifferentialAge = 99.0;

        // Neutralize antenna-to-pivot transform (zero offsets).
        var v = ConfigurationStore.Instance.Vehicle;
        v.AntennaPivot = 0;
        v.AntennaOffset = 0;
        v.AntennaHeight = 0;
    }

    [Test]
    public void Both_parsers_agree_on_representative_PANDA_fields()
    {
        // $PANDA,time,lat,lat_dir,lon,lon_dir,fix,sats,hdop,alt,age,speed_knots,heading,roll,pitch,yaw*CS
        const string sentence = "$PANDA,120000,4807.038,N,01131.000,E,4,12,0.8,340.0,1.0,5.4,123.4,0.5,-0.3,1.2";
        string withChecksum = AppendChecksum(sentence);
        byte[] bytes = Encoding.ASCII.GetBytes(withChecksum);

        // ── Capture the string-parser's GpsData via a mock IGpsService ──
        GpsData? fromString = CaptureStringParserOutput(withChecksum);

        // ── Capture the fast-parser's GpsData via the same IGpsService shape ──
        GpsData? fromFast = CaptureFastParserOutput(bytes);

        Assert.That(fromString, Is.Not.Null, "string parser should emit");
        Assert.That(fromFast, Is.Not.Null, "fast parser should emit");

        Assert.Multiple(() =>
        {
            Assert.That(fromFast!.CurrentPosition.Latitude,
                Is.EqualTo(fromString!.CurrentPosition.Latitude).Within(Tolerance),
                "latitude mismatch");
            Assert.That(fromFast.CurrentPosition.Longitude,
                Is.EqualTo(fromString.CurrentPosition.Longitude).Within(Tolerance),
                "longitude mismatch");
            Assert.That(fromFast.CurrentPosition.Altitude,
                Is.EqualTo(fromString.CurrentPosition.Altitude).Within(FloatBackedTolerance),
                "altitude mismatch (float-backed in string parser)");
            Assert.That(fromFast.CurrentPosition.Speed,
                Is.EqualTo(fromString.CurrentPosition.Speed).Within(FloatBackedTolerance),
                "speed mismatch (both convert knots→m/s; string parser's knots field is float)");
            Assert.That(fromFast.CurrentPosition.Heading,
                Is.EqualTo(fromString.CurrentPosition.Heading).Within(Tolerance),
                "heading mismatch (fusion suppressed in setup)");
            Assert.That(fromFast.FixQuality,
                Is.EqualTo(fromString.FixQuality), "fix quality mismatch");
            Assert.That(fromFast.SatellitesInUse,
                Is.EqualTo(fromString.SatellitesInUse), "satellite count mismatch");
            Assert.That(fromFast.Hdop,
                Is.EqualTo(fromString.Hdop).Within(Tolerance), "HDOP mismatch");
            Assert.That(fromFast.DifferentialAge,
                Is.EqualTo(fromString.DifferentialAge).Within(Tolerance),
                "differential-age mismatch");
        });
    }

    private static GpsData? CaptureStringParserOutput(string sentence)
    {
        var captured = new GpsData[1];
        var mockGps = Substitute.For<IGpsService>();
        mockGps.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
               .Do(ci => captured[0] = CloneGpsData(ci.Arg<GpsData>()));

        var parser = new NmeaParserService(mockGps);
        parser.ParseSentence(sentence);

        return captured[0];
    }

    private static GpsData? CaptureFastParserOutput(byte[] bytes)
    {
        var captured = new GpsData[1];
        var mockGps = Substitute.For<IGpsService>();
        mockGps.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
               .Do(ci => captured[0] = CloneGpsData(ci.Arg<GpsData>()));

        var parser = new NmeaParserServiceFast(mockGps);
        parser.ParseSpan(bytes);

        return captured[0];
    }

    private static GpsData CloneGpsData(GpsData src) => new()
    {
        CurrentPosition = src.CurrentPosition with { },
        FixQuality = src.FixQuality,
        SatellitesInUse = src.SatellitesInUse,
        Hdop = src.Hdop,
        DifferentialAge = src.DifferentialAge,
        Timestamp = src.Timestamp,
    };

    private static string AppendChecksum(string sentence)
    {
        // XOR between $ and *
        byte cs = 0;
        for (int i = 1; i < sentence.Length; i++) cs ^= (byte)sentence[i];
        return $"{sentence}*{cs:X2}";
    }
}
