// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// IMU heading from the steer module (PGN 253) or an external IMU (PGN 211) when the GPS
/// sentence has none, as AgOpenGPS does: 9999 = no IMU, PGN 212 drops it, and a sentence
/// IMU heading wins. The fix carries it to the heading fusion as ImuHeading/ImuValid.
/// </summary>
[TestFixture]
public class ImuHeadingTests
{
    private IUdpCommunicationService _udp = null!;
    private AutoSteerService _steer = null!;
    private List<(bool valid, double heading)> _fixes = null!;

    [SetUp]
    public void SetUp()
    {
        _udp = Substitute.For<IUdpCommunicationService>();
        var gps = Substitute.For<IGpsService>();
        _fixes = new List<(bool, double)>();
        gps.When(g => g.UpdateGpsData(Arg.Any<GpsData>()))
           .Do(ci => _fixes.Add((ci.Arg<GpsData>().ImuValid, ci.Arg<GpsData>().ImuHeading)));
        _steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), _udp, gps, new ConfigurationStore());
        _steer.Start();
    }

    [TearDown]
    public void TearDown() => _steer.Stop();

    // $PANDA without IMU (heading 65535), or with an IMU heading ×10.
    private void FeedPanda(int headingX10 = 65535)
    {
        string body = "PANDA,123456.00,4806.0000,N,01131.0000,E,4,12,0.9,100.0,0.0,5.5,"
            + headingX10.ToString(CultureInfo.InvariantCulture) + ",0,0,0.00";
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        _steer.ProcessGpsDatagram(Encoding.ASCII.GetBytes($"${body}*{checksum:X2}"), GpsSource.ModulePort);
    }

    private void Raise(byte pgn, short headingX10)
    {
        var p = new byte[14];
        p[0] = 0x80; p[1] = 0x81; p[2] = 0x7F; p[3] = pgn; p[4] = 8;
        int at = pgn == PgnNumbers.IMU_DATA ? 5 : 7; // heading: PGN 211 bytes 5-6, PGN 253 bytes 7-8
        p[at] = (byte)headingX10; p[at + 1] = (byte)(headingX10 >> 8);
        if (pgn == PgnNumbers.AUTOSTEER_DATA) { p[9] = 0xB8; p[10] = 0x22; p[11] = 0x01; } // roll 8888
        for (int i = 2; i <= 12; i++) p[13] += p[i];
        _udp.DataReceived += NSubstitute.Raise.Event<EventHandler<UdpDataReceivedEventArgs>>(
            _udp, new UdpDataReceivedEventArgs { Data = p, PGN = pgn });
    }

    [Test]
    public void Steer_module_heading_is_used_when_the_sentence_has_none()
    {
        FeedPanda();
        Raise(PgnNumbers.AUTOSTEER_DATA, 1234);
        FeedPanda();
        Assert.That(_fixes[0].valid, Is.False);
        Assert.That(_fixes[1].valid, Is.True);
        Assert.That(_fixes[1].heading, Is.EqualTo(123.4).Within(1e-9));
    }

    [Test]
    public void Heading_9999_means_no_imu()
    {
        Raise(PgnNumbers.AUTOSTEER_DATA, 9999);
        FeedPanda();
        Assert.That(_fixes[^1].valid, Is.False);
    }

    [Test]
    public void Sentence_imu_heading_wins_over_the_steer_module()
    {
        Raise(PgnNumbers.AUTOSTEER_DATA, 1234);
        FeedPanda(headingX10: 900);
        Assert.That(_fixes[^1].heading, Is.EqualTo(90.0).Within(1e-9));
    }

    [Test]
    public void External_imu_heading_is_used_until_it_disconnects()
    {
        Raise(PgnNumbers.IMU_DATA, 2705);
        FeedPanda();
        _udp.DataReceived += NSubstitute.Raise.Event<EventHandler<UdpDataReceivedEventArgs>>(_udp,
            new UdpDataReceivedEventArgs { Data = new byte[] { 0x80, 0x81, 0x7F, PgnNumbers.IMU_DISCONNECT, 1, 1, 0 }, PGN = PgnNumbers.IMU_DISCONNECT });
        FeedPanda();
        Assert.That(_fixes[0], Is.EqualTo((true, 270.5)).Using<(bool, double)>((a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1e-9));
        Assert.That(_fixes[1].valid, Is.False);
    }
}
