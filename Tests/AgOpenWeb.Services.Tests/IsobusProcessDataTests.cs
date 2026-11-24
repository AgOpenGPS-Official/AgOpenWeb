// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Isobus;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// PGN 242 to the ISOBUS task controller (#921): AgOpenGPS sends the speed, the distance and
/// the distance off the line, and AOG-TaskController's tractor ECU has the speed from nowhere
/// else. With AgOpenWeb an implement that takes its speed from the tractor applied nothing.
/// </summary>
[TestFixture]
public class IsobusProcessDataTests
{
    private IUdpCommunicationService _udp = null!;
    private readonly List<byte[]> _sent = new();

    [SetUp]
    public void SetUp()
    {
        _sent.Clear();
        _udp = Substitute.For<IUdpCommunicationService>();
        _udp.IsTaskControllerHeard.Returns(true);
        // The sender reuses its buffer: keep what was in it at each send.
        _udp.When(u => u.SendToTaskController(Arg.Any<byte[]>())).Do(c => _sent.Add((byte[])c.Arg<byte[]>().Clone()));
    }

    private static (ushort Ddi, int Value) Read(byte[] pgn) =>
        ((ushort)(pgn[5] | pgn[6] << 8), BitConverter.ToInt32(pgn, 7));

    private Dictionary<ushort, int> Values() => _sent.Select(Read).ToDictionary(v => v.Ddi, v => v.Value);

    [Test]
    public void TheFrame_IsAgOpenGpss()
    {
        // CISOBUS.SendProcessData: 80 81 7F F2 06, DDI low, high, the value as int32
        // little-endian, and SendPgnToLoop's CRC: the sum of bytes 2 to 10.
        var pgn = PgnBuilder.WriteIsobusProcessDataPgn(new byte[12], 397, -2500);

        Assert.That(pgn, Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xF2, 0x06, 0x8D, 0x01, 0x3C, 0xF6, 0xFF, 0xFF, 0x35 }));
        byte crc = 0;
        for (int i = 2; i <= 10; i++) crc += pgn[i];
        Assert.That(pgn[11], Is.EqualTo(crc));
    }

    [Test]
    public void SpeedIsMillimetresPerSecond_NegativeInReverse()
    {
        // AgOpenGPS: (int)(avgSpeed * 1000 / 3.6), negated when isReverse. 9 km/h = 2.5 m/s.
        var sender = new IsobusProcessDataSender(_udp);

        sender.Send(2.5, reverse: false, 0, null, nowMs: 1000);
        Assert.That(Values()[PgnBuilder.DDI_ACTUAL_SPEED], Is.EqualTo(2500));

        _sent.Clear();
        sender.Send(2.5, reverse: true, 0, null, nowMs: 1100);
        Assert.That(Values()[PgnBuilder.DDI_ACTUAL_SPEED], Is.EqualTo(-2500));
    }

    [Test]
    public void DistanceIsMillimetres()
    {
        // AgOpenGPS: (int)(fd.distanceUser * 1000).
        var sender = new IsobusProcessDataSender(_udp);

        sender.Send(2.5, false, distanceMeters: 1234.5678, null, nowMs: 1000);

        Assert.That(Values()[PgnBuilder.DDI_TOTAL_DISTANCE], Is.EqualTo(1234567));
    }

    [Test]
    public void DeviationIsTheDistanceOffTheLineTimes100()
    {
        // AgOpenGPS: guidanceLineDistanceOff (mm) * 100; AOG-TaskController divides by 1000
        // into NMEA 2000's 0.01 m. 35 mm left of the line.
        var sender = new IsobusProcessDataSender(_udp);

        sender.Send(2.5, false, 0, distanceOffLineMm: -35, nowMs: 1000);

        Assert.That(Values()[PgnBuilder.DDI_GUIDANCE_LINE_DEVIATION], Is.EqualTo(-3500));
    }

    [Test]
    public void WithNoLine_NoDeviationIsSent()
    {
        var sender = new IsobusProcessDataSender(_udp);

        sender.Send(2.5, false, 10, distanceOffLineMm: null, nowMs: 1000);

        Assert.That(Values().Keys, Is.EquivalentTo(new[] { PgnBuilder.DDI_ACTUAL_SPEED, PgnBuilder.DDI_TOTAL_DISTANCE }));
    }

    [Test]
    public void ASteadySpeedIsSentWithEveryFix()
    {
        // AgOpenGPS sends a value only when it changes: a task controller restarted while the
        // speed is steady reads 0 until it moves, and the implement applies nothing (#921).
        var sender = new IsobusProcessDataSender(_udp);

        for (int fix = 0; fix < 10; fix++) sender.Send(2.5, false, 100, null, nowMs: 1000 + fix * 100);

        Assert.That(_sent.Select(Read).Count(v => v.Ddi == PgnBuilder.DDI_ACTUAL_SPEED && v.Value == 2500), Is.EqualTo(10));
    }

    [Test]
    public void A20HzReceiver_SendsEveryOtherFix()
    {
        // At most every 100 ms, as AgOpenGPS.
        var sender = new IsobusProcessDataSender(_udp);

        for (int fix = 0; fix < 20; fix++) sender.Send(2.5, false, 100, null, nowMs: 1000 + fix * 50);

        Assert.That(_sent.Select(Read).Count(v => v.Ddi == PgnBuilder.DDI_ACTUAL_SPEED), Is.EqualTo(10));
    }

    [Test]
    public void WithNoTaskControllerHeard_NothingIsSent()
    {
        _udp.IsTaskControllerHeard.Returns(false);
        var sender = new IsobusProcessDataSender(_udp);

        sender.Send(2.5, false, 100, 20, nowMs: 1000);

        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public void ATaskControllerThatStarts_GetsTheSpeedWithTheNextFix()
    {
        var sender = new IsobusProcessDataSender(_udp);
        _udp.IsTaskControllerHeard.Returns(false);
        sender.Send(2.5, false, 100, null, nowMs: 1000);

        _udp.IsTaskControllerHeard.Returns(true);
        sender.Send(2.5, false, 100, null, nowMs: 1100);

        Assert.That(Values()[PgnBuilder.DDI_ACTUAL_SPEED], Is.EqualTo(2500));
    }
}
