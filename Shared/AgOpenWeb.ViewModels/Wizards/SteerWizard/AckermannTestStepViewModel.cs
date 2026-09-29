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
/// Ackermann Calibration step.
/// The user drives in a steady LEFT circle while the system tracks
/// GPS positions to measure the turning diameter, then auto-calculates Ackermann.
/// Ackermann compensates for the difference between inner and outer wheel angles during turning.
/// </summary>
public class AckermannTestStepViewModel : WizardStepViewModel
{
    private readonly IConfigurationService _configService;
    private readonly IAutoSteerService? _autoSteerService;
    private HardwareInstalledStepViewModel? _hardwareStep;

    private double _startEasting;
    private double _startNorthing;
    private double _startAngle;
    private int _stableCounter;
    private bool _sawNonRtkWhileRecording;

    public override string Title => "Ackermann Calibration";

    public override string Description =>
        "Ackermann must be at 100 (neutral) for this test. Turn the steering wheel to the LEFT " +
        "about 20 degrees. While driving in a steady circle, press Record and wait. " +
        "The system will measure the turning diameter and calculate Ackermann automatically. " +
        "Use RTK Fixed if you can — with a lower fix quality the result may be off.";

    public override bool CanSkip => true;

    public override bool ShouldSkip => _hardwareStep?.HardwareLevel == 0;

    public void SetHardwareStep(HardwareInstalledStepViewModel step) => _hardwareStep = step;

    private int _ackermann;
    /// <summary>Ackermann value (0-200, 100 = neutral).</summary>
    public int Ackermann
    {
        get => _ackermann;
        set => SetProperty(ref _ackermann, value);
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
    /// <summary>True when the GPS fix is RTK Fixed (FixQuality == 4).</summary>
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
    public int FixQuality
    {
        get => _fixQuality;
        set
        {
            if (SetProperty(ref _fixQuality, value))
                OnPropertyChanged(nameof(FixQualityLabel));
        }
    }

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

    /// <summary>The WAS angle captured at the start of recording.</summary>
    public double CapturedStartAngle => _startAngle;

    /// <summary>
    /// The module's Ackermann must be neutral (100) while measuring: the WAS reading it
    /// reports for a left turn already has Ackermann applied, so any other value skews the
    /// ratio. AgOpenGPS disables its Ackermann test the same way.
    /// </summary>
    public bool NeedsNeutralAckermann => _configService.Store.AutoSteer.Ackermann != 100;

    /// <summary>
    /// True when recording can start: moving, Ackermann neutral, and not already recording.
    /// RTK Fixed is recommended but not required (AgOpenGPS parity) — <see cref="RecordHint"/>
    /// warns when it's missing (#154).
    /// </summary>
    public bool CanRecord => Speed > 0.5 && !IsRecording && !NeedsNeutralAckermann;

    /// <summary>
    /// Why Record can't start, or a warning about the measurement, for the wizard to show
    /// next to the button. Empty when ready with RTK Fixed.
    /// </summary>
    public string RecordHint =>
        IsRecording ? ""
        : NeedsNeutralAckermann ? (TestResult.StartsWith("Ackermann set to")
            ? "To measure again, set Ackermann back to 100 (neutral) first."
            : $"Ackermann is {_configService.Store.AutoSteer.Ackermann} — set it to 100 (neutral) before measuring.")
        : Speed <= 0.5 ? "Start driving in a steady circle to enable Record."
        : !IsRtkFixed ? $"{FixQualityLabel}, not RTK Fixed — the result may be inaccurate."
        : "";

    /// <summary>Live progress line while recording.</summary>
    public string PhaseDescription =>
        IsRecording ? $"Drive steady — measuring the circle… {Diameter:F1} m (started at {_startAngle:F1}°)" : "";

    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    /// <summary>One tap to put Ackermann at neutral so the test can run.</summary>
    public ICommand SetNeutralAckermannCommand { get; }

    public AckermannTestStepViewModel(IConfigurationService configService,
        IAutoSteerService? autoSteerService = null)
    {
        _configService = configService;
        _autoSteerService = autoSteerService;

        StartRecordingCommand = new RelayCommand(StartRecording, () => CanRecord);
        StopRecordingCommand = new RelayCommand(StopRecording, () => IsRecording);
        SetNeutralAckermannCommand = new RelayCommand(SetNeutralAckermann, () => !IsRecording);
    }

    private void SetNeutralAckermann()
    {
        // Straight to the store: AutoSteerService sends it to the module (PGN 252).
        _configService.Store.AutoSteer.Ackermann = 100;
        SetUntouched(() => Ackermann = 100);
        TestResult = "";
    }

    /// <summary>
    /// Start recording from the current GPS position, capturing the current WAS angle.
    /// </summary>
    private void StartRecording()
    {
        var snapshot = _autoSteerService?.LatestSnapshot;
        if (snapshot == null) return;

        double currentAngle = _autoSteerService!.LastSteerData.ActualSteerAngle;
        StartRecordingAt(snapshot.Value.Easting, snapshot.Value.Northing, currentAngle);
    }

    /// <summary>
    /// Start recording from specified coordinates with a given start angle. Exposed for testing.
    /// </summary>
    public void StartRecordingAt(double easting, double northing, double startAngle)
    {
        _startEasting = easting;
        _startNorthing = northing;
        _startAngle = startAngle;
        Diameter = 0;
        _stableCounter = 0;
        _sawNonRtkWhileRecording = !IsRtkFixed;
        CalculatedSteerAngle = 0;
        TestResult = "";
        IsRecording = true;
    }

    /// <summary>
    /// Stop recording manually without calculating Ackermann.
    /// </summary>
    private void StopRecording()
    {
        IsRecording = false;
        TestResult = "Recording stopped — Ackermann not changed.";
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
            // Diameter stabilized - calculate Ackermann
            IsRecording = false;
            double wheelbase = _configService.Store.Vehicle.Wheelbase;
            double trackWidth = _configService.Store.Vehicle.TrackWidth;

            if (CpdCircleTestStepViewModel.MeasurementError(Diameter, trackWidth, Math.Abs(_startAngle)) is { } error)
            {
                TestResult = error + " Ackermann not changed.";
                return;
            }

            int newAckermann = CalculateAckermann(wheelbase, trackWidth, Diameter, _startAngle);

            double calcAngle = Math.Atan(wheelbase / ((Diameter - trackWidth * 0.5) / 2)) * 180.0 / Math.PI;
            CalculatedSteerAngle = Math.Round(calcAngle, 1);

            // Apply straight to the store (see CpdCircleTestStepViewModel): the wizard shows
            // it at once and a later edit isn't overwritten on leaving the step.
            _configService.Store.AutoSteer.Ackermann = newAckermann;
            SetUntouched(() => Ackermann = newAckermann);

            TestResult = $"Ackermann set to {newAckermann} — circle {Diameter:F1} m, calculated angle {calcAngle:F1}°, "
                + $"sensor {Math.Abs(_startAngle):F1}°."
                + (newAckermann is <= 0 or >= 200 ? " That's the limit (0–200), so the real value is off the scale — check the steer angle sensor and the circle, then test again." : "")
                + (_sawNonRtkWhileRecording ? " Measured without RTK Fixed — check the result." : "");
        }
    }

    /// <summary>
    /// Pure math function for Ackermann calculation from circle test results.
    /// Uses the ratio of calculated angle to the starting (initial) WAS angle.
    /// </summary>
    /// <param name="wheelbase">Vehicle wheelbase in meters</param>
    /// <param name="trackWidth">Vehicle track width in meters</param>
    /// <param name="diameter">Measured turning circle diameter in meters</param>
    /// <param name="startAngle">WAS angle captured when recording started (degrees)</param>
    /// <returns>Ackermann value (0-200, 100 = neutral)</returns>
    public static int CalculateAckermann(double wheelbase, double trackWidth,
        double diameter, double startAngle)
    {
        double leftAngle = Math.Atan(wheelbase / ((diameter - trackWidth * 0.5) / 2));
        leftAngle = leftAngle * 180.0 / Math.PI;

        int ackermann = (int)((leftAngle / Math.Abs(startAngle)) * 100);
        return Math.Clamp(ackermann, 0, 200);
    }

    protected override void OnEntering()
    {
        Ackermann = _configService.Store.AutoSteer.Ackermann;

        if (_autoSteerService != null)
        {
            _autoSteerService.StateUpdated += OnStateUpdated;
            // Seed from the cached snapshot so the hint/button reflect the GPS state on
            // entry (as the CPD step does).
            if (_autoSteerService.LatestSnapshot is { } cached)
                ApplySnapshot(cached);
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

        if (Touched(nameof(Ackermann))) _configService.Store.AutoSteer.Ackermann = Ackermann;
    }

    private void OnStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        ApplySnapshot(snapshot);

        if (IsRecording)
        {
            ProcessGpsUpdate(snapshot.Easting, snapshot.Northing);
        }
    }

    private void ApplySnapshot(VehicleStateSnapshot snapshot)
    {
        FixQuality = snapshot.FixQuality;
        // RTK Fixed only, as in the CPD step (was >= 4, which also passed Float/DR/manual).
        IsRtkFixed = snapshot.FixQuality == 4;
        if (IsRecording && !IsRtkFixed) _sawNonRtkWhileRecording = true;
        Speed = Math.Round(snapshot.SpeedKmh, 1);
        LiveSteerAngle = Math.Round(_autoSteerService!.LastSteerData.ActualSteerAngle, 1);
    }

    public override Task<bool> ValidateAsync()
    {
        if (Ackermann < 0 || Ackermann > 200)
        {
            SetValidationError("Ackermann must be between 0 and 200");
            return Task.FromResult(false);
        }

        ClearValidation();
        return Task.FromResult(true);
    }
}
