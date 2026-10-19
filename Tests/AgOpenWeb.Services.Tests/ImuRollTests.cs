// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Roll from the steer module's IMU (PGN 253) or an external IMU (PGN 211) when the GPS
/// sentence carries none, as AgOpenGPS does (#353): 8888 = no IMU, roll invert and roll
/// zero applied, PGN 212 drops it, and a fix with its own roll keeps it.
/// </summary>
[TestFixture]
public class ImuRollTests
{
    private IUdpCommunicationService _udp = null!;
    private ConfigurationStore _config = null!;
    private AutoSteerService _steer = null!;
    private List<double> _rolls = null!;

    [SetUp]
    public void SetUp()
    {
        _udp = Substitute.For<IUdpCommunicationService>();
        var gps = Substitute.For<IGpsService>();
        _rolls = new List<double>();
        gps.When(g => g.UpdateGpsData(Arg.Any<GpsData>())).Do(ci => _rolls.Add(ci.Arg<GpsData>().ImuRoll));
        _config = new ConfigurationStore();
        _steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), _udp, gps, _config);
        _steer.Start();
    }

    [TearDown]
    public void TearDown() => _steer.Stop();

    // $PANDA with IMU heading 65535 (no IMU on the GPS side, so no roll), or with roll ×10.
    private void FeedPanda(int? rollX10 = null)
    {
        string imu = rollX10 is int r ? "900," + r.ToString(CultureInfo.InvariantCulture) : "65535,0";
        string body = "PANDA,123456.00,4806.0000,N,01131.0000,E,4,12,0.9,100.0,0.0,5.5," + imu + ",0,0.00";
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        _steer.ProcessGpsDatagram(Encoding.ASCII.GetBytes($"${body}*{checksum:X2}"), GpsSource.ModulePort);
    }

    private static byte[] Packet(byte pgn, short a, short b)
    {
        var p = new byte[14];
        p[0] = 0x80; p[1] = 0x81; p[2] = 0x7F; p[3] = pgn; p[4] = 8;
        if (pgn == PgnNumbers.IMU_DATA) { p[5] = (byte)a; p[6] = (byte)(a >> 8); p[7] = (byte)b; p[8] = (byte)(b >> 8); }
        else { p[9] = (byte)b; p[10] = (byte)(b >> 8); p[11] = 0x01; } // PGN 253: roll at 9-10, work switch off
        for (int i = 2; i <= 12; i++) p[13] += p[i];
        return p;
    }

    private void Raise(byte[] packet) =>
        _udp.DataReceived += NSubstitute.Raise.Event<EventHandler<UdpDataReceivedEventArgs>>(
            _udp, new UdpDataReceivedEventArgs { Data = packet, PGN = packet[3] });

    private void SteerModuleRoll(short rollX10) => Raise(Packet(PgnNumbers.AUTOSTEER_DATA, 0, rollX10));

    [Test]
    public void Steer_module_roll_is_used_when_the_sentence_has_none()
    {
        FeedPanda();
        SteerModuleRoll(25);
        FeedPanda();
        Assert.That(_rolls, Is.EqualTo(new[] { 0.0, 2.5 }).Within(1e-9));
    }

    [Test]
    public void Steer_module_roll_gets_roll_invert_and_roll_zero()
    {
        _config.Ahrs.IsRollInvert = true;
        _config.Ahrs.RollZero = 0.5;
        SteerModuleRoll(25);
        FeedPanda();
        Assert.That(_rolls[^1], Is.EqualTo(-3.0).Within(1e-9));
    }

    [Test]
    public void Roll_8888_means_no_imu()
    {
        SteerModuleRoll(8888);
        FeedPanda();
        Assert.That(_rolls[^1], Is.Zero);
    }

    [Test]
    public void Sentence_roll_wins_over_the_steer_module()
    {
        SteerModuleRoll(25);
        FeedPanda(rollX10: 12);
        Assert.That(_rolls[^1], Is.EqualTo(1.2).Within(1e-9));
    }

    [Test]
    public void External_imu_roll_is_used_until_it_disconnects()
    {
        Raise(Packet(PgnNumbers.IMU_DATA, 900, -31));
        FeedPanda();
        Raise(new byte[] { 0x80, 0x81, 0x7F, PgnNumbers.IMU_DISCONNECT, 1, 1, 0 });
        FeedPanda();
        Assert.That(_rolls, Is.EqualTo(new[] { -3.1, 0.0 }).Within(1e-9));
    }

    [Test]
    public void Imu_roll_goes_stale_when_the_module_stops_sending_it()
    {
        SteerModuleRoll(25);
        Thread.Sleep((int)AutoSteerService.ImuRollStaleMs + 100);
        FeedPanda();
        Assert.That(_rolls[^1], Is.Zero);
    }
}
