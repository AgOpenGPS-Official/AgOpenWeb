// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

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
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.When(u => u.SendToModules(Arg.Any<byte[]>()))
            .Do(c => { var d = c.Arg<byte[]>(); if (d[3] == 0x64) _toModules.Add((byte[])d.Clone()); });
        udp.When(u => u.SendToLoopback(Arg.Any<byte[]>()))
            .Do(c => _toLoopback.Add((byte[])c.Arg<byte[]>().Clone()));

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
    }

    [Test]
    public void NoFix_NothingSent()
    {
        _gps.UpdateGpsData(Fix(heading: 90, roll: 0, fixQuality: 0));

        Assert.That(_toModules, Is.Empty);
        Assert.That(_toLoopback, Is.Empty);
    }
}
