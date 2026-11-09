// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Communication;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// A steer module and the steering it drives, as the AgOpenGPS AiO v4 firmware
/// (AgOpenGPS-Official/Boards, Autosteer.ino) behaves, so the wizard is tested against what a
/// tractor does rather than against a model that already agrees with the host:
/// <list type="bullet">
/// <item>PGN 252 / 251 are applied byte by byte as the firmware reads them (Kp, high/low/min PWM,
/// counts per degree, WAS offset, Ackermann fix; invert WAS, motor direction, steer switch or
/// button).</item>
/// <item>PGN 254: status, speed and set point, read as Autosteer.ino reads them: every packet
/// sets <c>guidanceStatusChanged = (status != previous status)</c>, and the control block, every
/// LOOP_TIME = 25 ms, engages on <c>guidanceStatusChanged &amp;&amp; guidanceStatus == 1</c>
/// without clearing the flag, so the next packet overwrites it. That is deliberately no more
/// forgiving than the reference board: a host that sends an unchanged packet inside the 25 ms
/// wipes the edge and the board never steers (AgOpenWeb at 100 Hz, fixed by holding the
/// unchanged packets 30 ms, AioV4EngageEdgeTests). The motor runs only while the watchdog is
/// fresh: status bit 0, speed ≥ 0.1 km/h, switch on.</item>
/// <item>The WAS counts come from the real wheel angle through the sensor's real counts per
/// degree, centre offset and mounting direction; the reported angle is the firmware's formula
/// on the host's settings, Ackermann fix applied to left (negative) readings.</item>
/// <item>The motor: P-only PID with min PWM and the low/high limit as the firmware, then the
/// real wiring direction; the wheel moves only above the break-away PWM, stops at the
/// mechanical locks, and follows the operator's hands when they hold the wheel.</item>
/// </list>
/// Every loop is recorded (<see cref="Samples"/>) for the safety checks.
/// </summary>
public sealed class EmulatedSteerModule : IDisposable
{
    // ------------------------------------------------------------ the real tractor (truth)

    /// <summary>Counts per degree of the real sensor (what CPD should come out near).</summary>
    public double TruthCpd { get; set; } = 110;
    /// <summary>Raw counts minus 6805 with the wheels straight: what Zero WAS must cancel.</summary>
    public int TruthCentreCounts { get; set; } = 260;
    /// <summary>The sensor is mounted so a right turn lowers the counts (needs Invert WAS).</summary>
    public bool TruthWasReversed { get; set; }
    /// <summary>The motor is wired so it needs Invert Motor (PGN 251 bit 2).</summary>
    public bool TruthMotorReversed { get; set; }
    /// <summary>Left-turn sensor reading × this / 100 = the real left angle (the right Ackermann).</summary>
    public double TruthAckermann { get; set; } = 90;
    public double LockRightDeg { get; set; } = 38;
    public double LockLeftDeg { get; set; } = 38;
    /// <summary>Below this PWM the motor/valve doesn't move the wheels.</summary>
    public int BreakawayPwm { get; set; } = 28;
    public double DegPerSecPerPwm { get; set; } = 0.25;
    /// <summary>Real roll of the tractor (+ = right side down).</summary>
    public double TruthRollDeg { get; set; }
    /// <summary>IMU bolted the other way round: reports −roll.</summary>
    public bool ImuReversed { get; set; }
    /// <summary>IMU bolted crooked by this much.</summary>
    public double ImuMountErrorDeg { get; set; } = 1.7;
    public bool HasImu { get; set; } = true;

    /// <summary>The operator's hands on the wheel: the wheel goes there and stays (the operator
    /// is stronger than the motor). Null = hands off.</summary>
    public double? HandAngleDeg { get; set; }
    /// <summary>Physical steer switch (only read with a switch configured).</summary>
    public bool SteerSwitchClosed { get; set; } = true;
    /// <summary>Module powered and on the network.</summary>
    public bool Online { get; set; } = true;

    // ------------------------------------------------------------ settings as received

    public byte Kp { get; private set; } = 40;
    public byte HighPwm { get; private set; } = 180;
    public byte LowPwm { get; private set; } = 30;
    public byte MinPwm { get; private set; } = 25;
    public byte Cpd { get; private set; } = 100;
    public short WasOffset { get; private set; }
    public double AckermanFix { get; private set; } = 1.0;
    public bool InvertWas { get; private set; }
    public bool MotorDriveDirection { get; private set; }
    public bool SwitchConfigured { get; private set; }
    public bool ButtonConfigured { get; private set; }
    public int SettingsReceived { get; private set; }
    public int ConfigReceived { get; private set; }

    // ------------------------------------------------------------ live state

    private readonly object _lock = new();
    private double _angle;          // real wheel angle, + right
    private int _pwmOut;            // signed, after the wiring direction
    private byte _pwmDisplay;
    private byte _steerSwitch = 1;  // firmware variable: 1 = not steering
    private byte _previous;
    private byte _currentState = 1;
    private int _switchCounter;
    private byte _status, _prevStatus;
    private bool _statusChanged;
    private double _setPoint;
    private double _gpsSpeed;
    private int _watchdog = WatchdogForce;
    private const int LoopTimeMs = 25;          // Autosteer.ino LOOP_TIME
    private const int WatchdogThreshold = 100;  // WATCHDOG_THRESHOLD: loops of 25 ms = 2.5 s
    private const int WatchdogForce = WatchdogThreshold + 2;
    private const double LoopDt = LoopTimeMs / 1000.0;
    private readonly long _t0 = Environment.TickCount64;

    public double WheelAngleDeg { get { lock (_lock) return _angle; } }
    public double SetPointDeg { get { lock (_lock) return _setPoint; } }
    public bool MotorEnabled { get { lock (_lock) return _watchdog < WatchdogThreshold; } }
    public int PwmOut { get { lock (_lock) return _pwmOut; } }

    /// <summary>The angle the firmware reports in PGN 253 right now.</summary>
    public double ReportedAngleDeg { get { lock (_lock) return Reported(); } }

    public readonly record struct Sample(long Ms, double Angle, double SetPoint, int Pwm, bool Enabled,
        byte Status, double Speed, bool AtLock, double? Hand);

    private readonly List<Sample> _samples = new();
    public List<Sample> SamplesSince(long ms)
    {
        lock (_lock) return _samples.Where(s => s.Ms >= ms).ToList();
    }
    public long NowMs => Environment.TickCount64 - _t0;

    /// <summary>Packets to the host (PGN 253), as the firmware answers every PGN 254.</summary>
    public Action<byte[]>? Send { get; set; }

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void SetWheel(double deg) { lock (_lock) _angle = Math.Clamp(deg, -LockLeftDeg, LockRightDeg); }

    /// <summary>A packet from the host (PGN 254 / 252 / 251; others ignored).</summary>
    public void Receive(byte[] p)
    {
        if (p.Length < 6 || !Online) return;
        byte[]? reply = null;
        lock (_lock)
        {
            switch (p[3])
            {
                case PgnProtocol.PGN_AUTOSTEER_TO_MODULE when p.Length >= 14:
                    _gpsSpeed = (p[5] | p[6] << 8) * 0.1;
                    // ReceiveUdp, as Autosteer.ino: the flag is set from every packet, not
                    // latched, so an unchanged packet arriving before the next 25 ms pass wipes
                    // the edge exactly as it does on an AIO v4 board. Without the host's 30 ms
                    // hold after a status change (#758) that is a missed engage, and this harness
                    // must show it rather than forgive it.
                    _prevStatus = _status;
                    _status = p[7];
                    _statusChanged = _status != _prevStatus;
                    _setPoint = (short)(p[8] | p[9] << 8) * 0.01;
                    if ((_status & 1) == 0 || _gpsSpeed < 0.1 || _steerSwitch == 1)
                        _watchdog = WatchdogForce;
                    else
                        _watchdog = 0;
                    reply = BuildPgn253();
                    break;
                case PgnProtocol.PGN_STEER_SETTINGS when p.Length >= 14:
                    Kp = p[5];
                    HighPwm = p[6];
                    MinPwm = p[8];
                    LowPwm = (byte)(MinPwm * 1.2);
                    Cpd = p[9];
                    WasOffset = (short)(p[10] | ((sbyte)p[11]) << 8);
                    AckermanFix = p[12] * 0.01;
                    SettingsReceived++;
                    break;
                case PgnProtocol.PGN_STEER_CONFIG when p.Length >= 14:
                    byte s = p[5];
                    InvertWas = (s & 0x01) != 0;
                    MotorDriveDirection = (s & 0x04) != 0;
                    SwitchConfigured = (s & 0x20) != 0;
                    ButtonConfigured = (s & 0x40) != 0;
                    ConfigReceived++;
                    break;
            }
        }
        if (reply != null) Send?.Invoke(reply);
    }

    private double SensorDeg(double wheel) =>
        wheel >= 0 ? wheel : wheel * 100.0 / TruthAckermann;

    private double RawCounts()
    {
        double s = SensorDeg(_angle) * TruthCpd * (TruthWasReversed ? -1 : 1);
        return 6805 + TruthCentreCounts + s;
    }

    private double Reported()
    {
        double cpd = Cpd == 0 ? 1 : Cpd; // a Teensy divides by zero; the host never sends 0
        double pos = RawCounts();
        double a = InvertWas
            ? (pos - 6805 - WasOffset) / -cpd
            : (pos - 6805 + WasOffset) / cpd;
        if (a < 0) a *= AckermanFix;
        return a;
    }

    private byte[] BuildPgn253()
    {
        var d = new byte[8];
        short sa = (short)Math.Clamp(Reported() * 100, short.MinValue, short.MaxValue);
        d[0] = (byte)sa; d[1] = (byte)(sa >> 8);
        short hd = 9999;
        d[2] = (byte)hd; d[3] = (byte)(hd >> 8);
        short roll = HasImu ? (short)Math.Round(((ImuReversed ? -1 : 1) * TruthRollDeg + ImuMountErrorDeg) * 10) : (short)8888;
        d[4] = (byte)roll; d[5] = (byte)(roll >> 8);
        byte sw = 0;
        sw |= (byte)(_steerSwitch << 1);
        sw |= 1; // work switch: not wired (bit 0 = 1 is "off" in AgOpenGPS)
        d[6] = sw;
        d[7] = _pwmDisplay;
        return PgnProtocol.BuildPacket(PgnProtocol.PGN_STEER_DATA, d);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        while (!ct.IsCancellationRequested)
        {
            next += LoopTimeMs;
            Step();
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, ct); } catch (OperationCanceledException) { break; } }
        }
    }

    /// <summary>One LOOP_TIME (25 ms) pass of the firmware's control block plus the physics of the wheels.</summary>
    private void Step()
    {
        lock (_lock)
        {
            if (_watchdog < WatchdogForce) _watchdog++;
            if (!Online) _watchdog = WatchdogForce;

            // Switches (Autosteer.ino).
            if (SwitchConfigured)
                _steerSwitch = (byte)(SteerSwitchClosed ? 0 : 1);
            else if (ButtonConfigured)
            {
                byte reading = (byte)(SteerSwitchClosed ? 0 : 1);
                if (reading == 0 && _previous == 1)
                {
                    if (_currentState == 1) { _currentState = 0; _steerSwitch = 0; }
                    else { _currentState = 1; _steerSwitch = 1; }
                }
                _previous = reading;
            }
            else
            {
                // No switch and no button: the tablet engages (Autosteer.ino, "2 Has tablet
                // button been pressed?"). The flag is not cleared here; the next packet does it.
                if (_statusChanged && _status == 1) _steerSwitch = 0;
                // If AOG has stopped steering, wait then turn off steerswitch ready for next engage.
                if (_steerSwitch == 0 && _status == 0)
                {
                    if (_switchCounter++ > 30) _steerSwitch = 1;
                }
                else _switchCounter = 0;
            }

            double actual = Reported();
            bool enabled = _watchdog < WatchdogThreshold;
            int pwm = 0;
            if (enabled)
            {
                double err = actual - _setPoint;
                double p = Kp * err;
                pwm = (int)p;
                double errAbs = Math.Abs(err);
                const double lowHighDeg = 3.0;
                double highLowPerDeg = (HighPwm - LowPwm) / lowHighDeg;
                int newMax = errAbs < lowHighDeg ? (int)(errAbs * highLowPerDeg + LowPwm) : HighPwm;
                if (pwm < 0) pwm -= MinPwm; else if (pwm > 0) pwm += MinPwm;
                pwm = Math.Clamp(pwm, -newMax, newMax);
                if (MotorDriveDirection) pwm = -pwm;
            }
            _pwmOut = pwm;
            _pwmDisplay = (byte)Math.Min(255, Math.Abs(pwm));

            // Physics: positive pwm out moves the wheel left (it corrects "actual > set point")
            // when the motor is wired the normal way.
            if (HandAngleDeg is { } hand)
            {
                _angle = Math.Clamp(hand, -LockLeftDeg, LockRightDeg);
            }
            else if (Math.Abs(pwm) >= BreakawayPwm)
            {
                double dir = -Math.Sign(pwm) * (TruthMotorReversed ? -1 : 1);
                double rate = (Math.Abs(pwm) - BreakawayPwm + 4) * DegPerSecPerPwm;
                _angle = Math.Clamp(_angle + dir * rate * LoopDt, -LockLeftDeg, LockRightDeg);
            }

            bool atLock = (_angle >= LockRightDeg - 0.05 || _angle <= -LockLeftDeg + 0.05)
                          && Math.Abs(pwm) >= BreakawayPwm
                          && Math.Sign(-pwm * (TruthMotorReversed ? -1 : 1)) == Math.Sign(_angle);
            _samples.Add(new Sample(NowMs, _angle, _setPoint, pwm, enabled, _status, _gpsSpeed, atLock, HandAngleDeg));
            if (_samples.Count > 60000) _samples.RemoveRange(0, 10000);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(1000); } catch { }
    }
}
