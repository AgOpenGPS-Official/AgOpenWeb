// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using System.Windows.Input;

using AgOpenWeb.Services.Interfaces;

using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// CPD (Counts Per Degree) Circle Test step.
/// The user drives in a steady right-turn circle while the system tracks
/// GPS positions to measure the turning diameter, then auto-calculates CPD.
/// </summary>
public class CpdCircleTestStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;
    private readonly IAutoSteerService? _autoSteerService;
    private HardwareInstalledStepViewModel? _hardwareStep;

    private double _startEasting;
    private double _startNorthing;
    private int _stableCounter;
    private bool _sawNonRtkWhileRecording;
    private double _startAngle;

    public override string Title => "CPD Circle Test";

    public override string Description =>
        "Turn the steering wheel to the RIGHT about 20 degrees and drive in a steady circle " +
        "at roughly 5 km/h. Press Record and keep the turn consistent — the system will measure " +
        "the turning diameter and calculate CPD automatically. Use RTK Fixed if you can — " +
        "with a lower fix quality the measured circle, and so the CPD, may be off.";

    public override bool CanSkip => true;

    public override bool ShouldSkip => _hardwareStep?.HardwareLevel == 0;

    public void SetHardwareStep(HardwareInstalledStepViewModel step) => _hardwareStep = step;

    private double _countsPerDegree;
    /// <summary>WAS counts per degree of steering angle.</summary>
    public double CountsPerDegree
    {
        get => _countsPerDegree;
        set => SetProperty(ref _countsPerDegree, value);
    }

    private bool _isRecording;
    /// <summary>True while recording GPS positions for circle measurement.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
                OnPropertyChanged(nameof(CanRecord));
        }
    }

    private double _diameter;
    /// <summary>Maximum measured diameter during recording (meters).</summary>
    public double Diameter
    {
        get => _diameter;
        set => SetProperty(ref _diameter, value);
    }

    private double _calculatedSteerAngle;
    /// <summary>Calculated steer angle from the measured circle diameter.</summary>
    public double CalculatedSteerAngle
    {
        get => _calculatedSteerAngle;
        set => SetProperty(ref _calculatedSteerAngle, value);
    }

    private string _testResult = "";
    /// <summary>Result summary text after recording completes.</summary>
    public string TestResult
    {
        get => _testResult;
        set => SetProperty(ref _testResult, value);
    }

    private bool _isRtkFixed;
    /// <summary>
    /// True only when GPS fix is RTK Fixed (FixQuality == 4). RTK Float
    /// (FixQuality == 5) reports centimeter-class precision but is still
    /// drifting; the circle test relies on absolute accuracy of the
    /// recorded loop, so Float is excluded here.
    /// </summary>
    public bool IsRtkFixed
    {
        get => _isRtkFixed;
        set
        {
            if (SetProperty(ref _isRtkFixed, value))
                OnPropertyChanged(nameof(CanRecord));
        }
    }

    private int _fixQuality;
    /// <summary>Raw NMEA GGA fix-quality value from the last GPS update.</summary>
    public int FixQuality
    {
        get => _fixQuality;
        set
        {
            if (SetProperty(ref _fixQuality, value))
                OnPropertyChanged(nameof(FixQualityLabel));
        }
    }

    /// <summary>
    /// Human-readable label for <see cref="FixQuality"/>, so the wizard
    /// shows operators *why* recording is blocked (e.g. "RTK Float" vs.
    /// "No Fix") instead of a generic "No RTK Fix" indicator.
    /// </summary>
    public string FixQualityLabel => FixQuality switch
    {
        0 => "No Fix",
        1 => "GPS Fix",
        2 => "DGPS",
        3 => "PPS",
        4 => "RTK Fixed",
        5 => "RTK Float",
        6 => "Dead Reckoning",
        7 => "Manual",
        8 => "Simulator",
        _ => $"Unknown ({FixQuality})"
    };

    private bool _isAtRecommendedSpeed;
    /// <summary>
    /// True when current speed is roughly the recommended 5 km/h
    /// (between ~3 and ~7 km/h). Lower speeds give the WAS more time to
    /// settle without drifting too far; higher speeds risk understeer
    /// changing the actual circle radius.
    /// </summary>
    public bool IsAtRecommendedSpeed
    {
        get => _isAtRecommendedSpeed;
        set => SetProperty(ref _isAtRecommendedSpeed, value);
    }

    private double _speed;
    /// <summary>Current vehicle speed in km/h.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (SetProperty(ref _speed, value))
                OnPropertyChanged(nameof(CanRecord));
        }
    }

    private double _liveSteerAngle;
    /// <summary>Live actual steer angle from PGN 253.</summary>
    public double LiveSteerAngle
    {
        get => _liveSteerAngle;
        set => SetProperty(ref _liveSteerAngle, value);
    }

    /// <summary>
    /// True when recording can start: moving and not already recording. RTK Fixed is
    /// recommended but not required (AgOpenGPS has no fix gate) — <see cref="RecordHint"/>
    /// warns when it's missing (#154).
    /// </summary>
    public bool CanRecord => Speed > 0.5 && !IsRecording;

    /// <summary>The WAS angle captured when recording started.</summary>
    public double CapturedStartAngle => _startAngle;

    /// <summary>
    /// Why Record can't start, or a warning about the measurement, for the wizard to show
    /// next to the button. Empty when ready with RTK Fixed (#154: Record used to do nothing
    /// with no explanation).
    /// </summary>
    public string RecordHint =>
        IsRecording ? ""
        : Speed <= 0.5 ? "Start driving in a steady circle to enable Record."
        : !IsRtkFixed ? $"{FixQualityLabel}, not RTK Fixed — the result may be inaccurate."
        : "";

    /// <summary>Live progress line while recording.</summary>
    public string PhaseDescription =>
        IsRecording ? $"Drive steady — measuring the circle… {Diameter:F1} m (started at {_startAngle:F1}°)" : "";

    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }

    public CpdCircleTestStepViewModel(IConfigurationService configService,
        IAutoSteerService? autoSteerService = null)
    {
        _configService = configService;
        _autoSteerService = autoSteerService;

        StartRecordingCommand = new RelayCommand(StartRecording, () => CanRecord);
        StopRecordingCommand = new RelayCommand(StopRecording, () => IsRecording);
    }

    /// <summary>
    /// Start recording from the current GPS position.
    /// </summary>
    private void StartRecording()
    {
        var snapshot = _autoSteerService?.LatestSnapshot;
        if (snapshot == null) return;

        _startAngle = _autoSteerService!.LastSteerData.ActualSteerAngle;
        StartRecordingAt(snapshot.Value.Easting, snapshot.Value.Northing);
    }

    /// <summary>
    /// Start recording from specified coordinates. Exposed for testing.
    /// </summary>
    public void StartRecordingAt(double easting, double northing)
    {
        _startEasting = easting;
        _startNorthing = northing;
        Diameter = 0;
        _stableCounter = 0;
        _sawNonRtkWhileRecording = !IsRtkFixed;
        CalculatedSteerAngle = 0;
        TestResult = "";
        IsRecording = true;
    }

    /// <summary>
    /// Stop recording manually without calculating CPD.
    /// </summary>
    private void StopRecording()
    {
        IsRecording = false;
        TestResult = "Recording stopped — CPD not changed.";
    }

    /// <summary>
    /// Process a GPS position update during recording.
    /// Exposed for testing.
    /// </summary>
    public void ProcessGpsUpdate(double easting, double northing)
    {
        if (!IsRecording) return;

        double dist = Math.Sqrt(
            Math.Pow(easting - _startEasting, 2) +
            Math.Pow(northing - _startNorthing, 2));

        if (dist > Diameter)
        {
            Diameter = dist;
            _stableCounter = 0;
        }
        else
        {
            _stableCounter++;
        }

        if (_stableCounter > 9)
        {
            // Diameter stabilized - calculate CPD
            IsRecording = false;
            double wheelbase = _configService.Store.Vehicle.Wheelbase;
            double trackWidth = _configService.Store.Vehicle.TrackWidth;
            double actualAngle = Math.Abs(_autoSteerService?.LastSteerData.ActualSteerAngle ?? 0);

            if (MeasurementError(Diameter, trackWidth, actualAngle) is { } error)
            {
                TestResult = error + " CPD not changed.";
                return;
            }

            // Scale from the CPD the module is using now (the store — the web edits it
            // directly, so the step's entry-time copy can be stale).
            double currentCpd = _configService.Store.AutoSteer.CountsPerDegree;
            double newCpd = CalculateCpdFromCircle(wheelbase, trackWidth, Diameter, actualAngle, currentCpd);

            double calcAngle = Math.Atan(wheelbase / ((Diameter - trackWidth * 0.5) / 2)) * 180.0 / Math.PI;
            CalculatedSteerAngle = Math.Round(calcAngle, 1);

            // Apply straight to the store, like AgOpenGPS sets the slider: the wizard shows
            // the store value and AutoSteerService sends it to the module. Mirror it into the
            // step without marking it touched so OnLeaving can't later overwrite a value the
            // operator edits after the test.
            _configService.Store.AutoSteer.CountsPerDegree = newCpd;
            SetUntouched(() => CountsPerDegree = newCpd);

            TestResult = $"CPD set to {newCpd:F0} — circle {Diameter:F1} m, calculated angle {calcAngle:F1}°, "
                + $"sensor {actualAngle:F1}°."
                + (newCpd is <= 1 or >= 255 ? " That's the limit (1–255), so the real value is off the scale — check the steer angle sensor and the circle, then test again." : "")
                + (_sawNonRtkWhileRecording ? " Measured without RTK Fixed — check the result." : "");
        }
    }

    /// <summary>
    /// Pure math function for CPD calculation from circle test results.
    /// Uses the legacy AgOpenGPS algorithm with 0.9 conservative factor.
    /// </summary>
    /// <param name="wheelbase">Vehicle wheelbase in meters</param>
    /// <param name="trackWidth">Vehicle track width in meters</param>
    /// <param name="diameter">Measured turning circle diameter in meters</param>
    /// <param name="actualAngle">Actual steer angle from WAS sensor (degrees, absolute)</param>
    /// <param name="currentCpd">Current counts per degree setting</param>
    /// <returns>New CPD value, clamped to 1-255</returns>
    /// <summary>
    /// Why a finished circle can't give a CPD, or null when it can: the circle must be
    /// larger than half the track width (the formula divides by their difference) and the
    /// WAS must read a real angle (a ~0° reading would drive CPD to its minimum).
    /// </summary>
    public static string? MeasurementError(double diameter, double trackWidth, double sensorAngle)
    {
        if (diameter <= trackWidth * 0.5 + 1.0)
            return $"Couldn't calculate: the measured circle ({diameter:F1} m) is too small — drive a full, steady circle.";
        if (sensorAngle < 2.0)
            return $"Couldn't calculate: the steer angle sensor reads {sensorAngle:F1}°. Hold the wheel at about 20° while circling, and check the WAS.";
        return null;
    }

    public static double CalculateCpdFromCircle(double wheelbase, double trackWidth,
        double diameter, double actualAngle, double currentCpd)
    {
        double calcAngle = Math.Atan(wheelbase / ((diameter - trackWidth * 0.5) / 2));
        calcAngle = calcAngle * 180.0 / Math.PI;

        double newCpd = (actualAngle / calcAngle) * currentCpd * 0.9;
        return Math.Clamp((int)newCpd, 1, 255);
    }

    protected override void OnEntering()
    {
        CountsPerDegree = _configService.Store.AutoSteer.CountsPerDegree;

        if (_autoSteerService != null)
        {
            _autoSteerService.StateUpdated += OnStateUpdated;

            // Seed the gate inputs from the cached latest snapshot so the
            // Record button reflects the current GPS state immediately on
            // entry, instead of staying greyed for up to ~100 ms while
            // waiting for the next StateUpdated publish. Without this the
            // operator sees a disabled button on entry even when RTK fix
            // and speed already meet the gate conditions.
            var cached = _autoSteerService.LatestSnapshot;
            if (cached.HasValue)
                ApplySnapshot(cached.Value);
        }
    }

    protected override void OnLeaving()
    {
        // Stop recording if still in progress
        if (IsRecording)
        {
            IsRecording = false;
        }

        if (_autoSteerService != null)
            _autoSteerService.StateUpdated -= OnStateUpdated;

        if (Touched(nameof(CountsPerDegree))) _configService.Store.AutoSteer.CountsPerDegree = CountsPerDegree;
    }

    private void OnStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        ApplySnapshot(snapshot);

        if (IsRecording)
        {
            ProcessGpsUpdate(snapshot.Easting, snapshot.Northing);
        }
    }

    /// <summary>
    /// Copy the gate-relevant fields out of a snapshot. Pulled out of
    /// <see cref="OnStateUpdated"/> so <see cref="OnEntering"/> can
    /// seed from <see cref="IAutoSteerService.LatestSnapshot"/> without
    /// triggering the recording branch.
    /// </summary>
    private void ApplySnapshot(VehicleStateSnapshot snapshot)
    {
        FixQuality = snapshot.FixQuality;
        if (IsRecording && snapshot.FixQuality != 4) _sawNonRtkWhileRecording = true;
        // Only RTK Fixed (4) is accurate enough for the circle test; RTK
        // Float (5) still drifts at the centimeter scale and would
        // skew the measured diameter. See FixQualityLabel comment.
        IsRtkFixed = snapshot.FixQuality == 4;
        Speed = Math.Round(snapshot.SpeedKmh, 1);
        IsAtRecommendedSpeed = Speed >= 3.0 && Speed <= 7.0;
        LiveSteerAngle = Math.Round(_autoSteerService!.LastSteerData.ActualSteerAngle, 1);
    }

    public override Task<bool> ValidateAsync()
    {
        if (CountsPerDegree < 1 || CountsPerDegree > 255)
        {
            SetValidationError("Counts Per Degree must be between 1 and 255");
            return Task.FromResult(false);
        }

        ClearValidation();
        return Task.FromResult(true);
    }
}
