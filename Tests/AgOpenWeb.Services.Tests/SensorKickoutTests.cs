// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Communication;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Track;
using AgOpenWeb.VirtualModules;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A pressure or current reading (PGN 250) at the trip point stops steering in the service.
/// The app must hear about it once, with the reason, or the AutoSteer button stays on while
/// nothing steers and nothing says why.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SensorKickoutTests
{
    private ConfigurationStore _config = null!;
    private IUdpCommunicationService _udp = null!;
    private GpsService _gps = null!;
    private AutoSteerService _svc = null!;
    private List<string> _kickouts = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _udp = Substitute.For<IUdpCommunicationService>();
        _gps = new GpsService();
        _svc = new AutoSteerService(new TrackGuidanceService(), _udp, _gps, _config);
        _svc.Start();
        _kickouts = new List<string>();
        _svc.SensorKickout += (_, reason) => _kickouts.Add(reason);
    }

    [TearDown]
    public void TearDown() { _svc.Stop(); _gps.Stop(); }

    private void Sensor(byte value)
    {
        var data = new byte[8];
        data[0] = value;
        _udp.DataReceived += Raise.EventWith(_udp, new UdpDataReceivedEventArgs
        {
            Data = PgnProtocol.BuildPacket(PgnProtocol.PGN_SENSOR_DATA, data),
            PGN = PgnNumbers.SENSOR_DATA,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8888),
            Timestamp = DateTime.Now,
        });
    }

    [Test]
    public void Pressure_at_the_trip_point_stops_steering_and_says_why_once()
    {
        _config.AutoSteer.PressureSensorEnabled = true;
        _config.AutoSteer.PressureTripPoint = 36;
        _svc.Engage();

        Sensor(80);  // 31 %
        Assert.That(_svc.IsEngaged, Is.True);

        Sensor(105); // 41 %
        Sensor(110); // still over: the app already knows
        Assert.That(_svc.IsEngaged, Is.False);
        Assert.That(_kickouts, Is.EqualTo(new[] { "pressure sensor 41% reached the trip point (36%)" }));
    }

    [Test]
    public void Current_at_the_trip_point_says_current()
    {
        _config.AutoSteer.CurrentSensorEnabled = true;
        _config.AutoSteer.CurrentTripPoint = 50;
        _svc.Engage();

        Sensor(200); // 78 %

        Assert.That(_kickouts, Is.EqualTo(new[] { "current sensor 78% reached the trip point (50%)" }));
    }

    [Test]
    public void Over_the_trip_point_while_off_raises_nothing()
    {
        _config.AutoSteer.PressureSensorEnabled = true;
        _config.AutoSteer.PressureTripPoint = 36;

        Sensor(200);

        Assert.That(_kickouts, Is.Empty);
    }
}
