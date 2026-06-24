// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AgOpenWeb.IntegrationTests.VirtualModules;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Integration tests verifying the Vehicle Simulator's data flow:
/// VirtualGpsReceiver -> UDP -> AutoSteerService -> VehicleState
/// VirtualSteerModule -> PGN 253 -> AutoSteerService -> steer data
/// </summary>
[TestFixture]
public class SimulatorDataFlowTests
{
    private ITrackGuidanceService _mockGuidance = null!;
    private IUdpCommunicationService _mockUdp = null!;
    private IGpsService _mockGps = null!;
    private ApplicationState _appState = null!;
    private AutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        _mockGuidance = Substitute.For<ITrackGuidanceService>();
        _mockUdp = Substitute.For<IUdpCommunicationService>();
        _mockGps = Substitute.For<IGpsService>();
        _appState = new ApplicationState();
        _autoSteer = new AutoSteerService(_mockGuidance, _mockUdp, _mockGps, _appState, ConfigurationStore.Instance);
        _autoSteer.Start();
    }

    private void PresetLocalPlane(double originLat, double originLon)
    {
        _appState.Field.LocalPlane = new LocalPlane(
            new Wgs84(originLat, originLon),
            new SharedFieldProperties());
    }

    [TearDown]
    public void TearDown()
    {
        _autoSteer.Stop();
    }

    // Phase B C4 contract: AutoSteerService.ProcessGpsBuffer only parses and
    // publishes the GpsData — coordinate conversion, guidance, PGN build,
    // and snapshot emission now run on the cycle worker. Tests that relied
    // on coordinate-conversion behavior moved to the C6 pipeline-level
    // integration tests; tests that verified parse-correctness were updated
    // to capture the published GpsData via the mock IGpsService instead of
    // the (no-longer-firing) StateUpdated event.

    [Test]
    public void GpsData_FromSimulator_ParsedByAutoSteerService()
    {
        // 1. Create a VirtualGpsReceiver sending to an ephemeral port
        using var listener = new UdpClient(0);
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var gps = new VirtualGpsReceiver(targetPort: port);
        gps.Latitude = 43.712800;
        gps.Longitude = -74.006000;
        gps.HeadingDegrees = 127.5;
        gps.SpeedKnots = 5.0;
        gps.FixQuality = 4;
        gps.Satellites = 14;
        gps.Hdop = 0.7;
        gps.RollDegrees = 1.5;
        gps.PitchDegrees = -0.3;
        gps.YawRateDegPerSec = 0.2;

        // 2. Send a $PANDA sentence and capture the raw bytes
        gps.SendOnce();

        listener.Client.ReceiveTimeout = 2000;
        IPEndPoint? remote = null;
        var rawBytes = listener.Receive(ref remote);

        // 3. Capture the published GpsData from the mock IGpsService.
        //    StateUpdated no longer fires from ProcessGpsBuffer — post-C4 the
        //    sole observable side-effect is IGpsService.UpdateGpsData.
        GpsData? published = null;
        _mockGps.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
                .Do(ci => published = ci.Arg<GpsData>());

        // 4. Feed the raw GPS bytes directly to AutoSteerService (same path as UdpCommunicationService)
        _autoSteer.ProcessGpsBuffer(rawBytes, rawBytes.Length);

        // 5. Verify the state was parsed correctly
        Assert.That(published, Is.Not.Null, "UpdateGpsData should fire after parse");
        Assert.That(published!.CurrentPosition.Latitude, Is.EqualTo(43.712800).Within(0.001),
            "Latitude should match sent value");
        Assert.That(published.CurrentPosition.Longitude, Is.EqualTo(-74.006000).Within(0.001),
            "Longitude should match sent value");
        Assert.That(published.CurrentPosition.Heading, Is.EqualTo(127.5).Within(0.5),
            "Heading should match sent value");
        Assert.That(published.FixQuality, Is.EqualTo(4),
            "Fix quality should be RTK Fixed");
        Assert.That(published.SatellitesInUse, Is.EqualTo(14));
        Assert.That(published.Hdop, Is.EqualTo(0.7).Within(0.1));
    }

    [Test]
    public void SteerModule_PGN253_ProcessedByAutoSteerService()
    {
        // Build PGN 253 steer feedback data (same format VirtualSteerModule uses)
        double steerAngleDeg = 12.5;
        var data = new byte[8];

        // Bytes 0-1: Actual steer angle (degrees * 100, int16 LE)
        short angleRaw = (short)(steerAngleDeg * 100);
        data[0] = (byte)(angleRaw & 0xFF);
        data[1] = (byte)((angleRaw >> 8) & 0xFF);

        // Bytes 2-3: IMU heading
        data[2] = 0;
        data[3] = 0;

        // Bytes 4-5: IMU roll
        data[4] = 0;
        data[5] = 0;

        // Byte 6: Switch status (steer switch active)
        data[6] = 0x02;

        // Byte 7: PWM display
        data[7] = 128;

        // Wrap in full PGN packet format
        var packet = PgnProtocol.BuildPacket(PgnProtocol.PGN_STEER_DATA, data);

        // Fire the DataReceived event on the mock (simulating UdpCommunicationService receiving this)
        _mockUdp.DataReceived += Raise.EventWith(
            _mockUdp,
            new UdpDataReceivedEventArgs
            {
                Data = packet,
                PGN = PgnNumbers.AUTOSTEER_DATA, // 253
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8888),
                Timestamp = DateTime.Now
            });

        // Verify AutoSteerService processed the steer data
        var steerData = _autoSteer.LastSteerData;
        Assert.That(steerData.ActualSteerAngle, Is.EqualTo(12.5).Within(0.1),
            "Actual steer angle should match sent value");
        Assert.That(steerData.SteerSwitchActive, Is.True,
            "Steer switch should be active");
    }

    [Test]
    public void ParseStillRuns_WhenFixQualityZero()
    {
        // FixQuality=0 still parses and publishes — the cycle worker's
        // GpsFixQualityValidator is what rejects bad fixes, not AutoSteerService.
        using var listener = new UdpClient(0);
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var gps = new VirtualGpsReceiver(targetPort: port);
        gps.Latitude = 43.712800;
        gps.Longitude = -74.006000;
        gps.FixQuality = 0;
        gps.Satellites = 0;

        gps.SendOnce();
        listener.Client.ReceiveTimeout = 2000;
        IPEndPoint? remote = null;
        var rawBytes = listener.Receive(ref remote);

        GpsData? published = null;
        _mockGps.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
                .Do(ci => published = ci.Arg<GpsData>());

        _autoSteer.ProcessGpsBuffer(rawBytes, rawBytes.Length);

        Assert.That(published, Is.Not.Null, "parse + publish still runs for fix quality 0");
        Assert.That(published!.FixQuality, Is.EqualTo(0));
    }

    [Test]
    public void GpsData_MultipleFrames_AllParsedSuccessfully()
    {
        // Simulate driving: send multiple GPS frames and verify metrics
        using var listener = new UdpClient(0);
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        listener.Client.ReceiveTimeout = 2000;

        using var gps = new VirtualGpsReceiver(targetPort: port);
        gps.Latitude = 43.712800;
        gps.Longitude = -74.006000;
        gps.HeadingDegrees = 90.0; // East
        gps.SpeedKnots = 10.0;
        gps.FixQuality = 4;
        gps.Satellites = 12;

        int publishCount = 0;
        _mockGps.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
                .Do(_ => publishCount++);

        // Send 10 frames, stepping position between each
        for (int i = 0; i < 10; i++)
        {
            gps.SendOnce();
            IPEndPoint? remote = null;
            var rawBytes = listener.Receive(ref remote);
            _autoSteer.ProcessGpsBuffer(rawBytes, rawBytes.Length);
            gps.Step(0.1); // 100ms at 10Hz
        }

        // Every successful parse should publish to GpsService.
        Assert.That(publishCount, Is.EqualTo(10),
            "UpdateGpsData should fire for each parsed frame");

        var metrics = _autoSteer.GetLatencyMetrics();
        Assert.That(metrics.ParseFailures, Is.EqualTo(0),
            "No parse failures expected for valid $PANDA sentences");
        // CycleCount is now incremented by the cycle worker, not ProcessGpsBuffer —
        // pipeline-level integration tests (Phase B C6) verify it end-to-end.
    }

    [Test]
    public void SteerModule_SensorData_PGN250_ProcessedCorrectly()
    {
        // Build PGN 250 sensor data
        byte sensorValue = 128; // ~50%
        var data = new byte[8];
        data[0] = sensorValue;

        var packet = PgnProtocol.BuildPacket(PgnProtocol.PGN_SENSOR_DATA, data);

        // Fire PGN 250 via the mock event
        _mockUdp.DataReceived += Raise.EventWith(
            _mockUdp,
            new UdpDataReceivedEventArgs
            {
                Data = packet,
                PGN = PgnNumbers.SENSOR_DATA, // 250
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8888),
                Timestamp = DateTime.Now
            });

        // Verify sensor data was processed
        Assert.That(_autoSteer.SensorPercent, Is.EqualTo(128.0 / 255.0 * 100.0).Within(0.5),
            "Sensor percent should be approximately 50%");
    }

    // Coordinate-conversion tests removed in Phase B C4. Pre-C4, ProcessGpsBuffer did
    // coord conversion + guidance + PGN + snapshot emission on the receive thread; post-C4
    // it parses and publishes only. The cycle worker owns coord conversion now, and Phase B
    // C6's pipeline-level integration tests cover end-to-end. Searchable in git history:
    // LocalPlaneConversion_ProducesConsistentOffsets, TractorSkipsLocalCoords_WithoutLocalPlane,
    // TractorMoves_WithPresetPlane_AtGpsOrigin, TractorMoves_WithField_FieldOriginPlane,
    // TractorMoves_FieldOriginDifferentFromGps_CorrectOffset, TractorMoves_WithPresetPlane_AtEquator,
    // TractorMoves_AtEquator_WithField.
}
