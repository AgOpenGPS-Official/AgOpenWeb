// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A steer module and the steering it drives, on a clock the test advances (no real time):
/// the firmware's loop every <see cref="LoopMs"/> (P gain, minimum PWM, the limit within a few
/// degrees of the set point: AutosteerPID.ino), settings arriving a moment after they change in
/// the store (the host's 150 ms debounce), and wheels that move above a break-away PWM, each
/// side its own, after a dead time and through a lag, with play and sensor noise when asked for.
/// For the wheel tuning step's tests (<see cref="SteerWizardTuneTests"/>).
/// </summary>
internal sealed class FakeSteering
{
    private readonly AutoSteerConfig _config;
    private readonly Random _random = new(12345);

    public FakeSteering(AutoSteerConfig config) => _config = config;

    // ------------------------------------------------------------ the tractor

    /// <summary>PWM from which the wheels move to the right / to the left.</summary>
    public int BreakawayRight = 30, BreakawayLeft = 30;
    /// <summary>Degrees per second for each PWM above the break-away.</summary>
    public double DegPerSecPerPwm = 0.25;
    /// <summary>Speed at the break-away PWM itself (a valve cracks open, a motor jumps free).</summary>
    public double RateAtBreakaway = 0.5;
    /// <summary>Top speed: the valve fully open, the motor at its limit.</summary>
    public double MaxRate = 1000;
    /// <summary>The valve or motor answers this long after the PWM changes.</summary>
    public int DeadTimeMs;
    /// <summary>First-order lag of the wheel speed (0 = none).</summary>
    public double LagMs;
    /// <summary>Play between the motor and the wheels.</summary>
    public double BacklashDeg;
    /// <summary>Sensor noise, ± degrees.</summary>
    public double NoiseDeg;
    /// <summary>The motor is wired the other way (needs Invert Motor).</summary>
    public bool MotorReversed;
    /// <summary>The firmware's LOW_HIGH_DEGREES: 3.0 in the AiO v4 firmware, 1.5 in others.</summary>
    public double LowHighDeg = 3.0;
    public int LoopMs = 25;
    /// <summary>The module only steers with its switch on.</summary>
    public bool SwitchOn = true;
    /// <summary>A kickout sensor is fitted: the module lets go when the motor works against the hands.</summary>
    public bool KickoutSensor;
    /// <summary>Hands on the wheel: the wheels go there and stay. Null = hands off.</summary>
    public double? Hands;
    /// <summary>Called every firmware loop: the operator's doings (switch, hands, the Stop button).</summary>
    public Action<FakeSteering>? Script;

    // ------------------------------------------------------------ state

    public double NowMs { get; private set; }
    public double Angle { get; private set; }
    public bool FreeDriveOn { get; private set; }
    public double SetPoint { get; private set; }
    public int Pwm { get; private set; }
    public bool Steering { get; private set; }
    /// <summary>Furthest the wheels were from <see cref="WatchFrom"/> since it was set.</summary>
    public double MaxExcursion { get; private set; }
    public double WatchFrom { get; set; }
    /// <summary>Highest PWM the module drove at.</summary>
    public int PeakPwm { get; private set; }
    /// <summary>Milliseconds the motor drove while the module said it was not steering (must stay 0).</summary>
    public int DroveWhileOffMs { get; private set; }

    private int _kp = 50, _high = 180, _min = 25;
    private bool _invert;
    private (int Kp, int High, int Min, bool Invert) _seen;
    private double _seenAt;
    private bool _kicked;
    private double _againstHandsMs;
    private double _noise;
    private double _rate, _motor;
    private readonly Queue<double> _driveLine = new();
    private int _sinceLoop;

    public void EnableFreeDrive() { if (!FreeDriveOn) _kicked = false; FreeDriveOn = true; }
    public void DisableFreeDrive() => FreeDriveOn = false;
    public void SetFreeDriveAngle(double deg) => SetPoint = Math.Clamp(deg, -60, 60);
    public void SetWheel(double deg) { Angle = deg; _motor = deg; }

    public double Reported => Angle + _noise;

    public SteerModuleData Read() => SteerModuleData.Empty with
    {
        ActualSteerAngle = Reported,
        SteerSwitchActive = !Steering,
        PwmDisplay = (byte)Math.Min(255, Math.Abs(Pwm)),
    };

    public void Advance(int ms)
    {
        for (int i = 0; i < ms; i++)
        {
            NowMs += 1;
            if (++_sinceLoop >= LoopMs)
            {
                _sinceLoop = 0;
                Loop();
            }
            Physics(0.001);
        }
    }

    /// <summary>One pass of the firmware's control block.</summary>
    private void Loop()
    {
        Script?.Invoke(this);

        // Settings: what the store has held unchanged for 170 ms (debounce, then the packet).
        var now = (_config.ProportionalGain, _config.MaxPwm, _config.MinPwm, _config.InvertMotor);
        if (now != _seen) { _seen = now; _seenAt = NowMs; }
        else if (NowMs - _seenAt >= 170)
        {
            _kp = Math.Clamp(now.ProportionalGain, 1, 200);
            _high = Math.Clamp(now.MaxPwm, 50, 255);
            _min = Math.Clamp(now.MinPwm, 1, 200);
            _invert = now.InvertMotor;
        }

        _noise = NoiseDeg > 0 ? (_random.NextDouble() * 2 - 1) * NoiseDeg : 0;
        Steering = FreeDriveOn && SwitchOn && !_kicked;
        int pwm = 0;
        if (Steering)
        {
            double err = Reported - SetPoint;
            pwm = (int)(_kp * err);
            double low = (byte)(_min * 1.2);
            double errAbs = Math.Abs(err);
            int newMax = errAbs < LowHighDeg ? (int)(errAbs * (_high - low) / LowHighDeg + low) : _high;
            if (pwm < 0) pwm -= _min; else if (pwm > 0) pwm += _min;
            pwm = Math.Clamp(pwm, -newMax, newMax);
            if (_invert) pwm = -pwm;
        }
        Pwm = pwm;
        PeakPwm = Math.Max(PeakPwm, Math.Abs(pwm));

        // Kickout: the motor working against the hands for a tenth of a second.
        if (KickoutSensor && Hands != null && Math.Abs(pwm) >= 10)
        {
            _againstHandsMs += LoopMs;
            if (_againstHandsMs >= 100) { _kicked = true; Pwm = 0; }
        }
        else _againstHandsMs = 0;
    }

    private void Physics(double dt)
    {
        // Positive PWM out moves the wheels left when the motor is wired the normal way.
        double drive = -Pwm * (MotorReversed ? -1 : 1);
        _driveLine.Enqueue(drive);
        double delayed = _driveLine.Count > DeadTimeMs ? _driveLine.Dequeue() : 0;
        if (!Steering && Math.Abs(Pwm) > 0) DroveWhileOffMs++;

        int breakaway = delayed > 0 ? BreakawayRight : BreakawayLeft;
        double want = Math.Abs(delayed) >= breakaway
            ? Math.Sign(delayed) * Math.Min(MaxRate, (Math.Abs(delayed) - breakaway) * DegPerSecPerPwm + RateAtBreakaway)
            : 0;
        _rate = LagMs > 0 ? _rate + (want - _rate) * Math.Min(1, dt * 1000 / LagMs) : want;

        if (Hands is { } hand)
        {
            Angle = hand;
            _motor = hand;
        }
        else
        {
            _motor += _rate * dt;
            double half = BacklashDeg / 2;
            if (_motor - Angle > half) Angle = _motor - half;
            else if (Angle - _motor > half) Angle = _motor + half;
        }
        MaxExcursion = Math.Max(MaxExcursion, Math.Abs(Angle - WatchFrom));
    }
}
