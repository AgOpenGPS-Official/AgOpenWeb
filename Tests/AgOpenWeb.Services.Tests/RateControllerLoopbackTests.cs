// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Communication;
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
/// PGN 235 (section dimensions): AgOpenGPS's layout (CPGN_EB), widths of sections 1-16 in
/// cm and the section count. Rate controllers match their sections to it.
/// </summary>
[TestFixture]
public class SectionDimensionsPgnLayoutTests
{
    [Test]
    public void Layout_MatchesAgOpenGps()
    {
        var tool = new ToolConfig();
        tool.SetSectionWidth(0, 300);
        tool.SetSectionWidth(1, 250.4);
        tool.SetSectionWidth(2, 600);

        var pgn = PgnBuilder.BuildSectionDimensionsPgn(tool, numSections: 3);

        Assert.That(pgn.Length, Is.EqualTo(39));
        Assert.That(pgn[..5], Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xEB, 33 }));
        Assert.That(pgn[5] | pgn[6] << 8, Is.EqualTo(300));
        Assert.That(pgn[7] | pgn[8] << 8, Is.EqualTo(250));
        Assert.That(pgn[9] | pgn[10] << 8, Is.EqualTo(600));
        Assert.That(pgn[11..37].All(b => b == 0), Is.True, "past the last section");
        Assert.That(pgn[37], Is.EqualTo(3));
        Assert.That(PgnProtocol.IsValidPacket(pgn, pgn.Length), Is.True);
    }

    [Test]
    public void MoreThan16Sections_Sends16Widths_AndTheCount()
    {
        var tool = new ToolConfig();
        for (int i = 0; i < 20; i++) tool.SetSectionWidth(i, 100 + i);

        var pgn = PgnBuilder.BuildSectionDimensionsPgn(tool, numSections: 20);

        Assert.That(pgn[35] | pgn[36] << 8, Is.EqualTo(115), "section 16");
        Assert.That(pgn[37], Is.EqualTo(20));
        Assert.That(PgnProtocol.IsValidPacket(pgn, pgn.Length), Is.True);
    }
}

/// <summary>
/// PGN 235 and 238 reach AgIO's loopback port, where rate controllers listen. 235 goes out at
/// start and on profile save or load, as AgOpenGPS sends it on saving the tool settings, and
/// only when the layout changed: a rate controller asks the operator to accept each change, so
/// neither the edits on the way to a layout nor a save of other settings may repeat it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SectionDimensionsEmitTests
{
    private IUdpCommunicationService _udp = null!;
    private AutoSteerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.NumSections = 2;
        ConfigurationStore.Instance.Tool.SetSectionWidth(0, 300);
        ConfigurationStore.Instance.Tool.SetSectionWidth(1, 300);

        _udp = Substitute.For<IUdpCommunicationService>();
        _service = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), _udp,
            Substitute.For<IGpsService>(), ConfigurationStore.Instance);
        _service.ConfigEmitDebounceMilliseconds = 20;
    }

    [TearDown]
    public void TearDown()
    {
        _service.Stop();
    }

    private static bool Is235(byte[] b) => b.Length > 4 && b[3] == 0xEB;

    private static void WaitForEmit() => Thread.Sleep(200);

    [Test]
    public void Start_Sends235_ToModulesAndLoopback()
    {
        _service.Start();

        _udp.Received(1).SendToModules(Arg.Is<byte[]>(b => Is235(b)));
        _udp.Received(1).SendToLoopback(Arg.Is<byte[]>(b => Is235(b) && b[37] == 2));
    }

    [Test]
    public void Start_Sends238_ToLoopback()
    {
        _service.Start();

        _udp.Received(1).SendToLoopback(Arg.Is<byte[]>(b => b.Length > 4 && b[3] == 0xEE));
    }

    [Test]
    public void SteerSettingChange_DoesNotResend235()
    {
        _service.Start();
        _udp.ClearReceivedCalls();

        ConfigurationStore.Instance.AutoSteer.InvertWas = true;
        WaitForEmit();

        _udp.Received(1).SendToModules(Arg.Is<byte[]>(b => b.Length > 4 && b[3] == 0xFB));
        _udp.DidNotReceive().SendToLoopback(Arg.Is<byte[]>(b => Is235(b)));
    }

    [Test]
    public void SectionEdits_NotSentUntilSave_ThenTheWholeLayoutOnce()
    {
        _service.Start();
        _udp.ClearReceivedCalls();

        var store = ConfigurationStore.Instance;
        store.NumSections = 3;
        store.Tool.SetSectionWidth(1, 450);
        store.Tool.SetSectionWidth(2, 200);
        WaitForEmit();

        _udp.DidNotReceive().SendToLoopback(Arg.Is<byte[]>(b => Is235(b)));
        _udp.DidNotReceive().SendToModules(Arg.Is<byte[]>(b => Is235(b)));

        store.OnProfileSaved();

        _udp.Received(1).SendToLoopback(Arg.Is<byte[]>(b =>
            Is235(b) && b[37] == 3 && (b[7] | b[8] << 8) == 450 && (b[9] | b[10] << 8) == 200));
        _udp.Received(1).SendToModules(Arg.Is<byte[]>(b => Is235(b)));
    }

    [Test]
    public void SaveWithoutSectionChange_DoesNotResend235()
    {
        _service.Start();
        _udp.ClearReceivedCalls();

        ConfigurationStore.Instance.AutoSteer.InvertWas = true;
        ConfigurationStore.Instance.OnProfileSaved();

        _udp.DidNotReceive().SendToLoopback(Arg.Is<byte[]>(b => Is235(b)));
    }

    [Test]
    public void ProfileLoad_SendsTheNewLayout()
    {
        _service.Start();
        _udp.ClearReceivedCalls();

        ConfigurationStore.Instance.NumSections = 1;
        ConfigurationStore.Instance.OnProfileLoaded();

        _udp.Received(1).SendToLoopback(Arg.Is<byte[]>(b => Is235(b) && b[37] == 1));
    }
}

/// <summary>
/// AgOpenGPS sends every module PGN to AgIO's loopback port, and rate controllers read
/// speed (254) and section states (239, 229) there. Once per fix, as AgOpenGPS does (the
/// modules keep their 100 Hz stream), with 239 and 229 only while a field is open.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class LoopbackMachinePgnPipelineTests
{
    private static readonly Wgs84 Origin = new(43.7128, -74.006);

    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private AutoSteerService _autoSteer = null!;
    private ConfigurationStore _config = null!;
    private readonly List<byte[]> _toLoopback = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);

        _toLoopback.Clear();
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.When(u => u.SendToLoopback(Arg.Any<byte[]>()))
            .Do(c =>
            {
                var d = c.Arg<byte[]>();
                if (d[3] == 0xFE || d[3] == 0xEF || d[3] == 0xE5) _toLoopback.Add((byte[])d.Clone());
            });

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

    private static GpsData Fix() => new()
    {
        CurrentPosition = new Position
        {
            Latitude = Origin.Latitude, Longitude = Origin.Longitude,
            Heading = 90, Speed = 2.5,
        },
        FixQuality = 4,
        IsValid = true,
    };

    private int Count(byte pgn) => _toLoopback.Count(d => d[3] == pgn);

    [Test]
    public void NoField_Only254_OncePerFix()
    {
        _gps.UpdateGpsData(Fix());
        _gps.UpdateGpsData(Fix());

        Assert.That(Count(0xFE), Is.EqualTo(2));
        Assert.That(Count(0xEF), Is.Zero);
        Assert.That(Count(0xE5), Is.Zero);
    }

    [Test]
    public void FieldOpen_254_239_229_OncePerFix()
    {
        _pipeline.SetHasActiveField(true);

        _gps.UpdateGpsData(Fix());
        _gps.UpdateGpsData(Fix());

        Assert.That(Count(0xFE), Is.EqualTo(2));
        Assert.That(Count(0xEF), Is.EqualTo(2));
        Assert.That(Count(0xE5), Is.EqualTo(2), "229 whatever the section count, as AgOpenGPS");
    }

    [Test]
    public void Speed_ReachesTheLoopbackCopy()
    {
        // 2.5 m/s = 9.0 km/h: PGN 254 bytes 5-6, km/h × 10, little-endian.
        _gps.UpdateGpsData(Fix());

        var pgn = _toLoopback.Last(d => d[3] == 0xFE);
        Assert.That(pgn[5] | pgn[6] << 8, Is.EqualTo(90));
        Assert.That(PgnProtocol.IsValidPacket(pgn, pgn.Length), Is.True);
    }
}
