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
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A steering runaway: the wheels turning away from the set point while the motor drives
/// (motor or WAS the wrong way round, the steer wizard skipped). The steer wizard harness's
/// careless operator who presses Next through every step with the sensor mounted the other way
/// round drove the wheels to the lock with AutoSteer on. AutoSteer must go off at once and say
/// why; a loop that closes the gap, an overshoot or a set point that moves must not trip it.
/// </summary>
[TestFixture]
public class SteerRunawayDetectorTests
{
    private static bool Feed(SteerRunawayDetector d, params (double T, double Actual, double SetPoint)[] samples)
    {
        bool hit = false;
        foreach (var s in samples) hit |= d.Add(s.T, s.Actual, s.SetPoint);
        return hit;
    }

    [Test]
    public void WheelsDrivingAwayFromASteadySetPoint_IsARunaway()
    {
        // Wrong motor direction at 44°/s (the harness's motor at full PWM), PGN 253 at 100 Hz.
        var d = new SteerRunawayDetector();
        bool hit = false;
        double at = double.NaN;
        for (int i = 0; i <= 30 && !hit; i++)
        {
            double t = i * 0.01, actual = 0.5 + 44 * t;
            hit = d.Add(t, actual, 0.5 - 2);  // set point 2° left of where the wheels start
            if (hit) at = actual;
        }
        Assert.That(hit, Is.True);
        Assert.That(at - 0.5, Is.LessThan(5.0), "found within a few degrees of travel, before the wheels reach the lock");
    }

    [Test]
    public void ASlowRunaway_IsFoundToo()
    {
        // A slow hydraulic valve: 8°/s away.
        var d = new SteerRunawayDetector();
        bool hit = false;
        for (int i = 0; i <= 200 && !hit; i++) hit = d.Add(i * 0.01, -i * 0.08, 1.0);
        Assert.That(hit, Is.True);
    }

    [Test]
    public void WheelsClosingTheGap_AreNoRunaway()
    {
        // Engaged with the wheels 30° from the set point: they drive towards it.
        var d = new SteerRunawayDetector();
        bool hit = false;
        for (int i = 0; i <= 100; i++) hit |= d.Add(i * 0.01, Math.Min(-30 + 44 * i * 0.01, 0), 0);
        Assert.That(hit, Is.False);
    }

    [Test]
    public void AnOvershoot_IsNoRunaway()
    {
        // Through the set point at speed, 5° past it, and back.
        var d = new SteerRunawayDetector();
        var s = new List<(double, double, double)>();
        double a = -10;
        for (int i = 0; i < 40; i++) { a = Math.Min(a + 0.4, 15); s.Add((i * 0.01, a, 10)); }   // -10 → 5.6
        for (int i = 40; i < 50; i++) { a += 0.5; s.Add((i * 0.01, a, 10)); }                    // → 10.6
        for (int i = 50; i < 60; i++) { a += 0.45; s.Add((i * 0.01, a, 10)); }                   // → 15.1 (5° past)
        for (int i = 60; i < 120; i++) { a = Math.Max(a - 0.2, 10); s.Add((i * 0.01, a, 10)); }   // back
        Assert.That(Feed(d, s.ToArray()), Is.False);
    }

    [Test]
    public void ASetPointMovingAwayFromStillWheels_IsNoRunaway()
    {
        // Guidance asks for more and more lock; the wheels haven't started yet.
        var d = new SteerRunawayDetector();
        bool hit = false;
        for (int i = 0; i <= 50; i++) hit |= d.Add(i * 0.01, 0, 20 * i * 0.01);
        Assert.That(hit, Is.False);
    }

    [Test]
    public void ASetPointJumpingAcrossTheWheels_IsNoRunaway()
    {
        // The wheels still moving right towards +10 when the set point jumps to -10.
        var d = new SteerRunawayDetector();
        var s = new List<(double, double, double)>();
        for (int i = 0; i < 20; i++) s.Add((i * 0.01, 2 + i * 0.3, 10));
        for (int i = 20; i < 30; i++) s.Add((i * 0.01, 8 + (i - 20) * 0.1, -10));  // coasting on
        for (int i = 30; i < 80; i++) s.Add((i * 0.01, 9 - (i - 30) * 0.3, -10));  // and back
        Assert.That(Feed(d, s.ToArray()), Is.False);
    }

    [Test]
    public void WasNoise_IsNoRunaway()
    {
        var d = new SteerRunawayDetector();
        var rnd = new Random(1);
        bool hit = false;
        for (int i = 0; i <= 1000; i++) hit |= d.Add(i * 0.01, 5 + (rnd.NextDouble() - 0.5) * 0.6, 0);
        Assert.That(hit, Is.False);
    }
}

/// <summary>The detector inside the service: PGN 253 with AutoSteer engaged.</summary>
[TestFixture]
[NonParallelizable]
public class SteerRunawayKickoutTests
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

    /// <summary>PGN 253 as the AIO firmware sends it: angle ×100, the steer switch bit clear
    /// (steering), the PWM.</summary>
    private void SteerData(double angleDeg, byte pwm)
    {
        var data = new byte[8];
        short raw = (short)Math.Round(angleDeg * 100);
        data[0] = (byte)(raw & 0xFF);
        data[1] = (byte)((raw >> 8) & 0xFF);
        data[2] = 0x0F; data[3] = 0x27;  // heading 9999: no IMU
        data[4] = 0xB8; data[5] = 0x22;  // roll 8888: no IMU
        data[6] = 0x01;                  // work switch not wired, steer switch on
        data[7] = pwm;
        _udp.DataReceived += Raise.EventWith(_udp, new UdpDataReceivedEventArgs
        {
            Data = PgnProtocol.BuildPacket(PgnProtocol.PGN_STEER_DATA, data),
            PGN = PgnNumbers.AUTOSTEER_DATA,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8888),
            Timestamp = DateTime.Now,
        });
    }

    [Test]
    public void ARunaway_TakesAutoSteerOff_AndSaysWhyOnce()
    {
        _svc.Engage();
        // Set point 0 (no line); the wheels drive away to the right.
        foreach (double a in new[] { 0.5, 2.0, 3.5, 5.0, 6.5, 8.0, 9.5 }) SteerData(a, 200);

        Assert.That(_svc.IsEngaged, Is.False);
        Assert.That(_kickouts, Has.Count.EqualTo(1));
        Assert.That(_kickouts[0], Does.StartWith("steering runaway"));
        Assert.That(_kickouts[0], Does.Contain("Invert Motor"));
    }

    [Test]
    public void TheSameMovementWithTheMotorStill_IsNotJudged()
    {
        // PWM 0: the module isn't driving (the wheels turned by hand, AutoSteer on but the
        // module's switch off).
        _svc.Engage();
        foreach (double a in new[] { 0.5, 2.0, 3.5, 5.0, 6.5, 8.0, 9.5 }) SteerData(a, 0);

        Assert.That(_svc.IsEngaged, Is.True);
        Assert.That(_kickouts, Is.Empty);
    }

    [Test]
    public void TheSameMovementWithAutoSteerOff_RaisesNothing()
    {
        foreach (double a in new[] { 0.5, 2.0, 3.5, 5.0, 6.5, 8.0, 9.5 }) SteerData(a, 200);
        Assert.That(_kickouts, Is.Empty);
    }
}
