// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// PGN 100 (corrected position, #337): AgOpenGPS's 24-byte layout, sent once per fix, when ticked on Network IO, to
/// the module subnet and to AgIO's loopback port, carrying the antenna position with
/// antenna offset and roll applied but not moved back to the pivot (AgOpenGPS pn.fix).
/// </summary>
[TestFixture]
public class CorrectedPositionPgnLayoutTests
{
    [Test]
    public void Layout_MatchesAgOpenGps24ByteVersion()
    {
        var pgn = PgnBuilder.BuildCorrectedPositionPgn(latitude: 52.123456789, longitude: 5.987654321, headingDegrees: 271.5);

        Assert.That(pgn.Length, Is.EqualTo(30));
        Assert.That(pgn[..5], Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0x64, 24 }));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(pgn.AsSpan(5, 8)), Is.EqualTo(5.987654321));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(pgn.AsSpan(13, 8)), Is.EqualTo(52.123456789));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(pgn.AsSpan(21, 8)), Is.EqualTo(271.5));

        int sum = 0;
        for (int i = 2; i < pgn.Length - 1; i++) sum += pgn[i];
        Assert.That(pgn[^1], Is.EqualTo((byte)sum));
    }
}

/// <summary>
/// PGN 214 (#364): AgIO's GPS data, the 57-byte layout GPS_Out parses (PGN54908.cs) to
/// build its NMEA sentences. Fields a sentence doesn't carry hold MaxValue, as in AgIO.
/// </summary>
[TestFixture]
public class GpsDataPgnLayoutTests
{
    private static float F(byte[] p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at, 4));
    private static ushort U(byte[] p, int at) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at, 2));
    private static short S(byte[] p, int at) => BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(at, 2));

    private static GpsData Data(GpsSentenceType type, bool dual) => new()
    {
        CurrentPosition = new Position
        {
            Latitude = 51.1565491, Longitude = -100.7603537, Altitude = 512.25,
            Heading = 271.5, Speed = 2.5, // m/s
        },
        FixQuality = 4, SatellitesInUse = 17, Hdop = 0.64, DifferentialAge = 1.2,
        ImuRoll = -3.5, ImuPitch = 12, ImuYawRate = -7, ImuHeading = 271.5, ImuValid = true,
        HasDualHeading = dual, SentenceType = type,
    };

    [Test]
    public void Simulator_FillsWhatGpsOutShows()
    {
        var p = PgnBuilder.BuildGpsDataPgn(Data(GpsSentenceType.Simulator, dual: true));

        Assert.That(p.Length, Is.EqualTo(57));
        Assert.That(p[..5], Is.EqualTo(new byte[] { 0x80, 0x81, 0x7C, 0xD6, 0x33 }));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(5, 8)), Is.EqualTo(-100.7603537));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(13, 8)), Is.EqualTo(51.1565491));
        Assert.That(F(p, 21), Is.EqualTo(271.5f), "dual heading: the sim stands in for a dual antenna");
        Assert.That(F(p, 25), Is.EqualTo(float.MaxValue));
        Assert.That(F(p, 29), Is.EqualTo(9.0f).Within(1e-4), "km/h");
        Assert.That(F(p, 33), Is.EqualTo(-3.5f));
        Assert.That(F(p, 37), Is.EqualTo(512.25f));
        Assert.That(U(p, 41), Is.EqualTo(17));
        Assert.That(p[43], Is.EqualTo(4));
        Assert.That(U(p, 44), Is.EqualTo(64));
        Assert.That(U(p, 46), Is.EqualTo(120));
        Assert.That(U(p, 48), Is.EqualTo(ushort.MaxValue), "no IMU block outside $PANDA");
        Assert.That(S(p, 50), Is.EqualTo(short.MaxValue));

        // GPS_Out's GoodCRC: the byte sum from index 2 up to the CRC.
        int sum = 0;
        for (int i = 2; i < p.Length - 1; i++) sum += p[i];
        Assert.That(p[^1], Is.EqualTo((byte)sum));
    }

    [Test]
    public void Panda_HeadingAndRollGoToTheImuFields()
    {
        // AgIO: $PANDA's heading is the IMU heading and its roll the IMU roll, both × 10.
        var p = PgnBuilder.BuildGpsDataPgn(Data(GpsSentenceType.Panda, dual: false));

        Assert.That(F(p, 21), Is.EqualTo(float.MaxValue));
        Assert.That(F(p, 25), Is.EqualTo(float.MaxValue));
        Assert.That(F(p, 33), Is.EqualTo(float.MaxValue));
        Assert.That(U(p, 48), Is.EqualTo(2715));
        Assert.That(S(p, 50), Is.EqualTo(-35));
        Assert.That(S(p, 52), Is.EqualTo(12));
        Assert.That(S(p, 54), Is.EqualTo(-7));
    }

    [Test]
    public void SingleAntennaNmea_HeadingIsCourseOverGround()
    {
        var p = PgnBuilder.BuildGpsDataPgn(Data(GpsSentenceType.NmeaEpoch, dual: false));

        Assert.That(F(p, 21), Is.EqualTo(float.MaxValue));
        Assert.That(F(p, 25), Is.EqualTo(271.5f));
    }
}

[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class CorrectedPositionPgnPipelineTests
{
    private static readonly Wgs84 Origin = new(43.7128, -74.006);

    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private AutoSteerService _autoSteer = null!;
    private ConfigurationStore _config = null!;
    private readonly List<byte[]> _toModules = new();
    private readonly List<byte[]> _toLoopback = new();
    private readonly List<byte[]> _toGpsLoopback = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Connections.SendCorrectedPosition = true;

        _toModules.Clear();
        _toLoopback.Clear();
        _toGpsLoopback.Clear();
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.When(u => u.SendToModules(Arg.Any<byte[]>()))
            .Do(c => { var d = c.Arg<byte[]>(); if (d[3] == 0x64) _toModules.Add((byte[])d.Clone()); });
        udp.When(u => u.SendToLoopback(Arg.Any<byte[]>()))
            .Do(c => _toLoopback.Add((byte[])c.Arg<byte[]>().Clone()));
        udp.When(u => u.SendGpsDataToLoopback(Arg.Any<byte[]>()))
            .Do(c => _toGpsLoopback.Add((byte[])c.Arg<byte[]>().Clone()));

        _gps = new GpsService();
        _gps.Start();

        var toolPosition = new ToolPositionService(_config);
        var coverage = new CoverageMapService(_config);
        var sectionControl = new SectionControlService(toolPosition, coverage, _config);
        _autoSteer = new AutoSteerService(new TrackGuidanceService(), udp, _gps, _config);
        _autoSteer.Start();

        _pipeline = new GpsPipelineService(
            _gps, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, _autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), _config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _config),
                NullLogger<YouTurnStateMachine>.Instance, _config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            new GpsHeadingFusionService(_config),
            NullLogger<GpsPipelineService>.Instance,
            _config,
            new PositionEstimator());

        _pipeline.SetLocalPlane(new LocalPlane(Origin, new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _autoSteer.Stop();
        _gps.Stop();
    }

    // Standing still with an IMU, so the fused heading is the IMU heading.
    private static GpsData Fix(double heading, double roll, int fixQuality = 4) => new()
    {
        CurrentPosition = new Position
        {
            Latitude = Origin.Latitude, Longitude = Origin.Longitude,
            Heading = heading, Speed = 0,
        },
        FixQuality = fixQuality,
        ImuValid = true,
        ImuHeading = heading,
        ImuRoll = roll,
        IsValid = true,
    };

    private static GeoCoord Local(byte[] pgn)
    {
        double lon = BinaryPrimitives.ReadDoubleLittleEndian(pgn.AsSpan(5, 8));
        double lat = BinaryPrimitives.ReadDoubleLittleEndian(pgn.AsSpan(13, 8));
        return new LocalPlane(Origin, new SharedFieldProperties()).ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
    }

    [Test]
    public void SentOncePerFix_ToSubnetAndLoopback()
    {
        _gps.UpdateGpsData(Fix(heading: 90, roll: 0));
        _gps.UpdateGpsData(Fix(heading: 90, roll: 0));

        Assert.That(_toModules, Has.Count.EqualTo(2));
        Assert.That(_toLoopback, Has.Count.EqualTo(2));
        Assert.That(_toLoopback[1], Is.EqualTo(_toModules[1]));
        Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(_toModules[1].AsSpan(21, 8)), Is.EqualTo(90).Within(0.01));
    }

    [Test]
    public void AntennaPivot_IsNotApplied()
    {
        // AgOpenGPS sends pn.fix, the antenna, not the pivot it computes afterwards.
        _config.Vehicle.AntennaPivot = 2.0;
        _config.Vehicle.AntennaOffset = 0;
        _config.Vehicle.AntennaHeight = 0;

        _gps.UpdateGpsData(Fix(heading: 90, roll: 0));

        var p = Local(_toModules[^1]);
        Assert.That(p.Easting, Is.EqualTo(0).Within(0.005));
        Assert.That(p.Northing, Is.EqualTo(0).Within(0.005));
    }

    [Test]
    public void AntennaOffsetAndRoll_AreApplied()
    {
        // Heading east. Antenna 0.5 m right of centre: the centre line is 0.5 m north.
        // Tilted 10° right with the antenna 3 m up: the antenna has swung 3·sin 10° south.
        _config.Vehicle.AntennaPivot = 2.0;
        _config.Vehicle.AntennaOffset = 0.5;
        _config.Vehicle.AntennaHeight = 3.0;

        _gps.UpdateGpsData(Fix(heading: 90, roll: 10));

        var p = Local(_toModules[^1]);
        Assert.That(p.Easting, Is.EqualTo(0).Within(0.005));
        Assert.That(p.Northing, Is.EqualTo(0.5 + 3.0 * Math.Sin(10 * Math.PI / 180)).Within(0.005));
    }

    [Test]
    public void OptionOff_NothingSent()
    {
        _config.Connections.SendCorrectedPosition = false;

        _gps.UpdateGpsData(Fix(heading: 90, roll: 0));

        Assert.That(_toModules, Is.Empty);
        Assert.That(_toLoopback, Is.Empty);
        Assert.That(_toGpsLoopback, Is.Empty);
    }

    [Test]
    public void NoFix_NoCorrectedPosition_ButGpsDataShowsIt()
    {
        // GPS_Out shows the fix quality, so PGN 214 goes out without a fix too (AgIO does).
        _gps.UpdateGpsData(Fix(heading: 90, roll: 0, fixQuality: 0));

        Assert.That(_toModules, Is.Empty);
        Assert.That(_toLoopback, Is.Empty);
        Assert.That(_toGpsLoopback, Has.Count.EqualTo(1));
        Assert.That(_toGpsLoopback[0][43], Is.EqualTo(0));
    }

    [Test]
    public void GpsData_SentOncePerFix_AsItCameIn()
    {
        // #364: the simulator's fix, speed and heading reach GPS_Out.
        var fix = Fix(heading: 90, roll: 0);
        fix.CurrentPosition = fix.CurrentPosition with { Speed = 2.5 };
        fix.SatellitesInUse = 12;
        fix.HasDualHeading = true;
        fix.SentenceType = GpsSentenceType.Simulator;

        _gps.UpdateGpsData(fix);
        _gps.UpdateGpsData(fix);

        Assert.That(_toGpsLoopback, Has.Count.EqualTo(2));
        var p = _toGpsLoopback[1];
        Assert.That(p[3], Is.EqualTo(0xD6));
        Assert.That(BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(21, 4)), Is.EqualTo(90f));
        Assert.That(BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(29, 4)), Is.EqualTo(9f).Within(1e-4));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(41, 2)), Is.EqualTo(12));
        Assert.That(p[43], Is.EqualTo(4));
    }
}
