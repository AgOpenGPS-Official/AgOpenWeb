// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using AgOpenWeb.VehicleSimulator.Models;
using AgOpenWeb.VehicleSimulator.Modules;

namespace AgOpenWeb.VehicleSimulator.ViewModels;

public enum DriveMode
{
    // Physics sim: speed + wheel angle drive heading & position; IMU heading
    // follows the vehicle, WAS follows the wheel angle.
    Vehicle,
    // Every sensor output is a decoupled slider (heading, roll, pitch, WAS) so
    // each pipeline stage can be exercised in isolation.
    Individual
}

public class MainWindowViewModel : INotifyPropertyChanged
{
    private VirtualModuleHub? _hub;
    private DispatcherTimer? _simTimer;
    private long _packetCount;
    private readonly VehiclePhysicsModel _vehicle = new();
    // Persisted starting GPS pose (loaded in the ctor; saved on operator edits while stopped).
    private readonly SimSettings _settings = SimSettings.Load();

    // Vehicle
    private double _speed; // km/h
    private double _heading; // degrees
    private double _latitude = 42.0308;
    private double _longitude = -93.6319;
    // Bicycle-model axle length. Operator-adjustable so the host's wizard
    // belief about wheelbase can be tested against a configurable truth, the
    // same pattern as the virtual CPD / WAS offset knobs.
    private double _wheelbase = 2.5; // meters; matches VehiclePhysicsModel default

    // Sensors
    private double _wasAngle; // degrees
    private double _maxPhysicalWheelAngle = 35.0; // degrees, ± clamp
    private double _rollAngle; // degrees (IMU / dual-antenna roll)
    private double _pitchAngle; // degrees (IMU pitch)
    private bool _imuValid = true; // PANDA: false → 65535 no-IMU sentinel
    private bool _isDualGps; // false = $PANDA (single), true = $PAOGI (dual)
    private int _fixQuality = 4; // RTK Fixed
    private int _satelliteCount = 12;

    // Status
    private bool _isRunning;
    private double _commandedAngle;
    private byte _pwmDisplay;
    private bool _steerSwitchOn;
    private bool _autoSteerEngaged;
    private DriveMode _driveMode = DriveMode.Vehicle;
    private string _statusText = "Stopped";

    // Sent / Received data panes (newest line first). Freeze-frame via IsPaused.
    private const int MaxLogLines = 200;
    private readonly object _logSync = new();
    private readonly List<string> _sentLines = new();
    private readonly List<string> _receivedLines = new();
    private string _sentLog = "";
    private string _receivedLog = "";
    private bool _sentDirty;
    private bool _receivedDirty;
    private bool _isPaused;

    // Applied module settings (mirrored from VirtualSteerModule for UI display)
    private byte _appliedKp;
    private byte _appliedCountsPerDegree;
    private short _appliedWasOffset;
    private byte _appliedHighPwm;
    private bool _appliedInvertWas;
    private bool _appliedIsDanfoss;
    private bool _appliedSteerSwitchEnabled;
    private bool _appliedWorkSwitchEnabled;
    private double _reportedSteerAngle;

    // Virtual WAS hardware truth — operator-adjustable knobs that simulate the
    // real CPD and off-center of the pretend WAS sensor. Persisted on the
    // ViewModel so values survive Stop/Start cycles of the module.
    private double _virtualCountsPerDegree = 100.0;
    private short _virtualWasOffset;
    private double _truthRawCounts;

    public double Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, 0, 30); OnPropertyChanged(); }
    }

    public double Heading
    {
        get => _heading;
        set { _heading = ((value % 360) + 360) % 360; OnPropertyChanged(); PersistPose(); }
    }

    public double Latitude
    {
        get => _latitude;
        set { _latitude = value; OnPropertyChanged(); PersistPose(); }
    }

    public double Longitude
    {
        get => _longitude;
        set { _longitude = value; OnPropertyChanged(); PersistPose(); }
    }

    // Auto-save the starting pose when the operator edits it (stopped) — never while
    // running, so live driving drift doesn't clobber the saved start point. The
    // explicit Save Position button uses SavePose directly (saves the current pose
    // regardless of running state).
    private void PersistPose()
    {
        if (_isRunning) return;
        SavePose();
    }

    private void SavePose()
    {
        _settings.Latitude = _latitude;
        _settings.Longitude = _longitude;
        _settings.Heading = _heading;
        _settings.Save();
    }

    public double Wheelbase
    {
        get => _wheelbase;
        set
        {
            _wheelbase = Math.Clamp(value, 1.0, 5.0);
            OnPropertyChanged();
            // Push live so changes affect the next kinematic step without
            // requiring a Stop/Start cycle.
            _vehicle.Wheelbase = _wheelbase;
        }
    }

    public double WasAngle
    {
        get => _wasAngle;
        set { _wasAngle = Math.Clamp(value, -45, 45); OnPropertyChanged(); }
    }

    /// <summary>
    /// Maximum physical wheel angle (±degrees) the simulated tractor
    /// can reach. The internal steer module clamps its integrated WAS
    /// at this value so the wizard's max-steering-angle test sees a
    /// real plateau rather than a slowly-converging PID output. Default
    /// 35° approximates a typical agricultural front axle; operator can
    /// dial it up or down to test the wizard against different limits.
    /// </summary>
    public double MaxPhysicalWheelAngle
    {
        get => _maxPhysicalWheelAngle;
        set
        {
            // Allow 5..60° to cover everything from articulated tractors
            // (very tight) to forklift-style steering (very wide).
            double clamped = Math.Clamp(value, 5.0, 60.0);
            if (_maxPhysicalWheelAngle != clamped)
            {
                _maxPhysicalWheelAngle = clamped;
                _hub.Steer.MaxPhysicalWheelAngleDeg = clamped;
                OnPropertyChanged();
            }
        }
    }

    public double RollAngle
    {
        get => _rollAngle;
        set { _rollAngle = Math.Clamp(value, -45, 45); OnPropertyChanged(); }
    }

    public double PitchAngle
    {
        get => _pitchAngle;
        set { _pitchAngle = Math.Clamp(value, -45, 45); OnPropertyChanged(); }
    }

    /// <summary>PANDA only: unchecking sends the 65535 no-IMU sentinel so the
    /// host's ImuValid=false path is testable.</summary>
    public bool ImuValid
    {
        get => _imuValid;
        set { _imuValid = value; OnPropertyChanged(); }
    }

    /// <summary>false = single antenna ($PANDA), true = dual antenna ($PAOGI).</summary>
    public bool IsDualGps
    {
        get => _isDualGps;
        set { _isDualGps = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpsMessageLabel)); }
    }

    public string GpsMessageLabel => _isDualGps ? "$PAOGI (dual)" : "$PANDA (single)";

    public int FixQuality
    {
        get => _fixQuality;
        set { _fixQuality = Math.Clamp(value, 0, 5); OnPropertyChanged(); }
    }

    public int SatelliteCount
    {
        get => _satelliteCount;
        set { _satelliteCount = Math.Clamp(value, 0, 30); OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartStopLabel)); }
    }

    public double CommandedAngle
    {
        get => _commandedAngle;
        set { _commandedAngle = value; OnPropertyChanged(); }
    }

    public byte PwmDisplay
    {
        get => _pwmDisplay;
        set { _pwmDisplay = value; OnPropertyChanged(); }
    }

    public bool SteerSwitchOn
    {
        get => _steerSwitchOn;
        set { _steerSwitchOn = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Read-only indicator: autosteer engaged signal received from the host
    /// (PGN 254 IsEngaged bit).
    /// </summary>
    public bool AutoSteerEngaged
    {
        get => _autoSteerEngaged;
        private set { _autoSteerEngaged = value; OnPropertyChanged(); }
    }

    public DriveMode DriveMode
    {
        get => _driveMode;
        set { _driveMode = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // === Sent / Received data panes ===

    public string SentLog
    {
        get => _sentLog;
        private set { _sentLog = value; OnPropertyChanged(); }
    }

    public string ReceivedLog
    {
        get => _receivedLog;
        private set { _receivedLog = value; OnPropertyChanged(); }
    }

    /// <summary>Freeze-frame: when true, the data panes stop updating (the
    /// simulation keeps running underneath) so a frame can be read / copied.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayPauseLabel)); }
    }

    public string PlayPauseLabel => _isPaused ? "▶ Play" : "⏸ Pause";

    private void AppendSent(string line) => Append(_sentLines, line, sent: true);
    private void AppendReceived(string line) => Append(_receivedLines, line, sent: false);

    // Taps fire on background receive/tick threads AND the UI thread (GPS send),
    // possibly at a high host packet rate. Keep this O(1)-ish and do NO UI work
    // here — just buffer the line and mark dirty. RefreshLogs (UI thread, 10 Hz)
    // coalesces the redraw so the packet rate can't flood the dispatcher.
    private void Append(List<string> buffer, string line, bool sent)
    {
        if (_isPaused) return; // freeze-frame: drop while paused
        string stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        lock (_logSync)
        {
            buffer.Insert(0, stamped); // newest first
            if (buffer.Count > MaxLogLines)
                buffer.RemoveRange(MaxLogLines, buffer.Count - MaxLogLines);
            if (sent) _sentDirty = true; else _receivedDirty = true;
        }
    }

    /// <summary>UI-thread, called once per simulation tick (10 Hz). Rebuilds the
    /// pane text only when new lines arrived, decoupling redraw from packet rate.</summary>
    private void RefreshLogs()
    {
        string? sent = null, received = null;
        lock (_logSync)
        {
            if (_sentDirty) { sent = string.Join(Environment.NewLine, _sentLines); _sentDirty = false; }
            if (_receivedDirty) { received = string.Join(Environment.NewLine, _receivedLines); _receivedDirty = false; }
        }
        if (sent != null) SentLog = sent;
        if (received != null) ReceivedLog = received;
    }

    private void ClearLogs()
    {
        lock (_logSync)
        {
            _sentLines.Clear();
            _receivedLines.Clear();
        }
        SentLog = "";
        ReceivedLog = "";
    }

    // === Applied-settings panel (read-only mirrors of VirtualSteerModule state) ===
    // These reflect the most recent values applied from PGN 251 / 252 plus the
    // live PWM and post-calibration angle, so the operator can sanity-check
    // that the host's wizard config landed on the module.

    public byte AppliedKp
    {
        get => _appliedKp;
        private set { _appliedKp = value; OnPropertyChanged(); }
    }

    public byte AppliedCountsPerDegree
    {
        get => _appliedCountsPerDegree;
        private set { _appliedCountsPerDegree = value; OnPropertyChanged(); }
    }

    public short AppliedWasOffset
    {
        get => _appliedWasOffset;
        private set { _appliedWasOffset = value; OnPropertyChanged(); }
    }

    public byte AppliedHighPwm
    {
        get => _appliedHighPwm;
        private set { _appliedHighPwm = value; OnPropertyChanged(); }
    }

    public bool AppliedInvertWas
    {
        get => _appliedInvertWas;
        private set { _appliedInvertWas = value; OnPropertyChanged(); }
    }

    public bool AppliedIsDanfoss
    {
        get => _appliedIsDanfoss;
        private set { _appliedIsDanfoss = value; OnPropertyChanged(); }
    }

    public bool AppliedSteerSwitchEnabled
    {
        get => _appliedSteerSwitchEnabled;
        private set
        {
            _appliedSteerSwitchEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SteerSwitchToggleEnabled));
        }
    }

    public bool AppliedWorkSwitchEnabled
    {
        get => _appliedWorkSwitchEnabled;
        private set { _appliedWorkSwitchEnabled = value; OnPropertyChanged(); }
    }

    public double ReportedSteerAngle
    {
        get => _reportedSteerAngle;
        private set { _reportedSteerAngle = value; OnPropertyChanged(); }
    }

    // === Virtual WAS hardware truth (operator-adjustable knobs) ===

    public double VirtualCountsPerDegree
    {
        get => _virtualCountsPerDegree;
        set
        {
            _virtualCountsPerDegree = Math.Clamp(value, 1.0, 255.0);
            OnPropertyChanged();
            if (_hub != null) _hub.Steer.VirtualCountsPerDegree = _virtualCountsPerDegree;
        }
    }

    public short VirtualWasOffset
    {
        get => _virtualWasOffset;
        set
        {
            _virtualWasOffset = (short)Math.Clamp((int)value, -2000, 2000);
            OnPropertyChanged();
            if (_hub != null) _hub.Steer.VirtualWasOffset = _virtualWasOffset;
        }
    }

    public double TruthRawCounts
    {
        get => _truthRawCounts;
        private set { _truthRawCounts = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether the UI's Steer-Switch toggle should accept input. When the host
    /// has not configured a physical steer switch, the module reports the bit
    /// as continuously active regardless of the toggle, so the toggle is
    /// disabled in the UI to make that explicit.
    /// </summary>
    public bool SteerSwitchToggleEnabled => _appliedSteerSwitchEnabled;

    public string StartStopLabel => IsRunning ? "Stop" : "Start";

    public ICommand StartStopCommand { get; }
    // Quick-set buttons: stop motion, center the wheel, zero roll. Set the live
    // property so the next tick pushes 0 to the GPS/steer modules.
    public ICommand StopSpeedCommand { get; }
    public ICommand CenterWheelCommand { get; }
    public ICommand ZeroRollCommand { get; }
    // Fine ±0.5 nudge buttons (match the in-app sim step) for setting exact values
    // where the slider is awkward. Property setters clamp.
    public ICommand SpeedUpCommand { get; }
    public ICommand SpeedDownCommand { get; }
    public ICommand WheelUpCommand { get; }
    public ICommand WheelDownCommand { get; }
    public ICommand RollUpCommand { get; }
    public ICommand RollDownCommand { get; }
    // Heading nudge wraps through 360/0 (the Heading setter is modulo 360), so the
    // 360→0 boundary is testable — a slider can't cross a wrap.
    public ICommand HeadingUpCommand { get; }
    public ICommand HeadingDownCommand { get; }
    // Explicit "Save Position" — snapshots the CURRENT pose (works while running too,
    // so you can drive somewhere and save it as the new startup point).
    public ICommand SavePositionCommand { get; }
    // Freeze-frame the sent/received data panes (sim keeps running).
    public ICommand PlayPauseCommand { get; }

    private const double NudgeStep = 0.5;

    public MainWindowViewModel()
    {
        // Restore the operator's last starting pose (set fields directly — before any
        // binding — so PersistPose isn't triggered re-saving on construction).
        _latitude = _settings.Latitude;
        _longitude = _settings.Longitude;
        _heading = _settings.Heading;

        StartStopCommand = new RelayCommand(ToggleRunning);
        StopSpeedCommand = new RelayCommand(() => Speed = 0);
        CenterWheelCommand = new RelayCommand(() => WasAngle = 0);
        ZeroRollCommand = new RelayCommand(() => RollAngle = 0);
        SpeedUpCommand = new RelayCommand(() => Speed += NudgeStep);
        SpeedDownCommand = new RelayCommand(() => Speed -= NudgeStep);
        WheelUpCommand = new RelayCommand(() => WasAngle += NudgeStep);
        WheelDownCommand = new RelayCommand(() => WasAngle -= NudgeStep);
        RollUpCommand = new RelayCommand(() => RollAngle += NudgeStep);
        RollDownCommand = new RelayCommand(() => RollAngle -= NudgeStep);
        HeadingUpCommand = new RelayCommand(() => Heading += NudgeStep);
        HeadingDownCommand = new RelayCommand(() => Heading -= NudgeStep);
        SavePositionCommand = new RelayCommand(() => { SavePose(); StatusText = "Position saved"; });
        PlayPauseCommand = new RelayCommand(() => IsPaused = !IsPaused);

        // "Send to" interface checkboxes: loopback + every up IPv4 NIC. Each checked NIC
        // targets its subnet broadcast on :9999 so a host on that LAN (e.g. a headless SBC)
        // receives the GPS/PGN — the old sim was localhost-only.
        foreach (var opt in NetworkTargetOption.Enumerate(HostReceivePort, _settings.SelectedTargets))
        {
            opt.SelectionChanged = OnNetworkTargetsChanged;
            NetworkTargets.Add(opt);
        }
    }

    /// <summary>The UDP port the AgOpenWeb host receives GPS/PGN on (AgIO convention).</summary>
    private const int HostReceivePort = 9999;

    /// <summary>Checkable destinations shown in the "Send to" list.</summary>
    public ObservableCollection<NetworkTargetOption> NetworkTargets { get; } = new();

    private IEnumerable<IPEndPoint> SelectedEndpoints() =>
        NetworkTargets.Where(t => t.IsSelected).Select(t => t.Endpoint);

    private void OnNetworkTargetsChanged()
    {
        // Persist the selection and, if running, retarget the live modules immediately.
        _settings.SelectedTargets = NetworkTargets.Where(t => t.IsSelected).Select(t => t.Key).ToList();
        _settings.Save();
        _hub?.Targets.Set(SelectedEndpoints());
    }

    private void ToggleRunning()
    {
        if (IsRunning)
            StopSimulation();
        else
            StartSimulation();
    }

    private void StartSimulation()
    {
        try
        {
            // Initialize physics model with current UI values
            _vehicle.Latitude = Latitude;
            _vehicle.Longitude = Longitude;
            _vehicle.HeadingDeg = Heading;
            _vehicle.SpeedKmh = Speed;
            _vehicle.SteerAngleDeg = WasAngle;
            _vehicle.Wheelbase = Wheelbase;

            _hub = new VirtualModuleHub(hostReceivePort: HostReceivePort, moduleListenPort: 8888);
            _hub.Targets.Set(SelectedEndpoints()); // GPS/PGN go to the checked interfaces
            _hub.Gps.Latitude = Latitude;
            _hub.Gps.Longitude = Longitude;
            _hub.Gps.HeadingDegrees = Heading;
            _hub.Gps.SpeedKnots = Speed / 1.852;
            _hub.Gps.FixQuality = FixQuality;
            _hub.Gps.Satellites = SatelliteCount;
            _hub.Gps.RollDegrees = RollAngle;
            _hub.Gps.PitchDegrees = PitchAngle;
            _hub.Gps.ImuValid = ImuValid;
            _hub.Gps.MessageType = IsDualGps ? GpsMessageType.Paogi : GpsMessageType.Panda;
            _hub.Steer.ActualSteerAngleDeg = WasAngle;
            // Push the operator's mechanical-limit setting before Start
            // so the synthetic PWM loop clamps the simulated wheel angle
            // at the configured maximum from the first tick.
            _hub.Steer.MaxPhysicalWheelAngleDeg = MaxPhysicalWheelAngle;
            // The module's synthetic PWM tick loop drives WAS when autosteer
            // is engaged; the legacy on-receive WAS-follow stays off.
            _hub.Steer.SimulateSteerResponse = false;
            // Seed the module's virtual hardware truth from the operator-set VM
            // values, so PGN 253 picks up the correct calibration on the first
            // emit after Start (rather than the module defaults).
            _hub.Steer.VirtualCountsPerDegree = VirtualCountsPerDegree;
            _hub.Steer.VirtualWasOffset = VirtualWasOffset;

            // Tap outgoing/incoming frames into the Sent / Received panes.
            ClearLogs();
            _hub.Gps.OnSent = AppendSent;
            _hub.Steer.OnSent = AppendSent;
            _hub.Steer.OnReceived = AppendReceived;
            _hub.Machine.OnSent = AppendSent;
            _hub.Machine.OnReceived = AppendReceived;

            _hub.Start();

            _packetCount = 0;

            _simTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100) // 10 Hz
            };
            _simTimer.Tick += SimulationTick;
            _simTimer.Start();

            IsRunning = true;
            StatusText = "Running";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private void StopSimulation()
    {
        _simTimer?.Stop();
        _simTimer = null;

        _hub?.Dispose();
        _hub = null;

        IsRunning = false;
        StatusText = "Stopped";
    }

    private void SimulationTick(object? sender, EventArgs e)
    {
        if (_hub == null) return;

        // Mirror host's autosteer engagement to the read-only indicator. When
        // engaged, the module's synthetic PWM loop owns the WAS angle; reflect
        // it back into the UI slider so the operator sees the simulated motor
        // turning the wheel. When not engaged, the slider is the source of
        // truth and we push it down to the module further below.
        bool engaged = _hub.Steer.LastCommand?.IsEngaged ?? false;
        AutoSteerEngaged = engaged;
        if (engaged)
        {
            WasAngle = _hub.Steer.ActualSteerAngleDeg;
        }

        const double dt = 0.1; // 10 Hz tick
        if (DriveMode == DriveMode.Vehicle)
        {
            _vehicle.SpeedKmh = Speed;
            _vehicle.SteerAngleDeg = WasAngle;
            _vehicle.Step(dt);

            Heading = _vehicle.HeadingDeg;
            Latitude = _vehicle.Latitude;
            Longitude = _vehicle.Longitude;
        }
        else
        {
            // Individual mode: slider Heading is source of truth; just integrate position.
            double speedMs = Speed / 3.6;
            double headingRad = Heading * Math.PI / 180.0;
            double dNorth = speedMs * Math.Cos(headingRad) * dt;
            double dEast = speedMs * Math.Sin(headingRad) * dt;
            Latitude += dNorth / 111320.0;
            Longitude += dEast / (111320.0 * Math.Cos(Latitude * Math.PI / 180.0));

            // Keep the bicycle model's pose in sync so a switch back to Bicycle
            // mode resumes from the user's current pose rather than snapping back.
            _vehicle.Latitude = Latitude;
            _vehicle.Longitude = Longitude;
            _vehicle.HeadingDeg = Heading;
        }

        // Push updated values to GPS module
        _hub.Gps.Latitude = Latitude;
        _hub.Gps.Longitude = Longitude;
        _hub.Gps.HeadingDegrees = Heading;
        _hub.Gps.SpeedKnots = Speed / 1.852; // km/h to knots
        _hub.Gps.FixQuality = FixQuality;
        _hub.Gps.Satellites = SatelliteCount;
        _hub.Gps.RollDegrees = RollAngle;
        _hub.Gps.PitchDegrees = PitchAngle;
        _hub.Gps.ImuValid = ImuValid;
        _hub.Gps.MessageType = IsDualGps ? GpsMessageType.Paogi : GpsMessageType.Panda;
        // Only push slider → WAS when not engaged. When engaged the module's
        // PWM loop is the source of truth (we already pulled it into WasAngle
        // above), and pushing the slider value back would fight that loop.
        if (!engaged)
        {
            _hub.Steer.ActualSteerAngleDeg = WasAngle;
        }
        _hub.Steer.SteerSwitchActive = SteerSwitchOn;

        // Send GPS data
        _hub.Gps.SendOnce();
        _packetCount++;

        // Read back commanded angle from steer module
        CommandedAngle = _hub.Steer.CommandedSteerAngleDeg;
        PwmDisplay = _hub.Steer.PwmDisplay;

        // Refresh the applied-settings panel so operators can see PGN 251 / 252
        // values landing on the module in real time.
        AppliedKp = _hub.Steer.Kp;
        AppliedCountsPerDegree = _hub.Steer.CountsPerDegree;
        AppliedWasOffset = _hub.Steer.WasOffset;
        AppliedHighPwm = _hub.Steer.HighPWM;
        AppliedInvertWas = _hub.Steer.InvertWas;
        AppliedIsDanfoss = _hub.Steer.IsDanfoss;
        AppliedSteerSwitchEnabled = _hub.Steer.SteerSwitchEnabled;
        AppliedWorkSwitchEnabled = _hub.Steer.WorkSwitchEnabled;
        ReportedSteerAngle = _hub.Steer.ReportedSteerAngleDeg;
        TruthRawCounts = _hub.Steer.TruthRawCounts;

        StatusText = $"Running, {_packetCount} packets sent";

        // Coalesced redraw of the data panes (once per 10 Hz tick, not per packet).
        RefreshLogs();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}
