// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>Where the wheel tuning step is; the web client picks its buttons from it.</summary>
public enum TuneStage
{
    /// <summary>Nothing running: Start.</summary>
    Idle = 0,
    /// <summary>The stop check or the measurements are running: Stop.</summary>
    Running = 1,
    /// <summary>The stop check passed: Continue (the wheels move by themselves from here).</summary>
    StopChecked = 2,
    /// <summary>Values found, not in use yet: Use these values / Keep the old values.</summary>
    Result = 3,
    /// <summary>The values found are in use: Run again.</summary>
    Accepted = 4,
}

/// <summary>How the operator can stop the steering at the wheel, for the stop check.</summary>
internal enum TuneStopKind { Screen, Switch, Sensor, SwitchOrSensor }

/// <summary>
/// Finds the three settings of the steer module's own loop (the wheels following the angle
/// asked for) by moving the wheels through Free Drive and measuring how they answer:
/// <list type="number">
/// <item><b>Stop check.</b> Before anything moves by itself, the operator stops the module
/// once at the wheel (kickout sensor, steer switch or button) and the step sees it stop. With
/// no stop at the wheel set up, the check is the Stop button on the page, and the page says
/// that this is the only stop.</item>
/// <item><b>Minimum To Move.</b> With the module's gain fixed and its minimum at 1, the set
/// point is moved slowly away from the wheels, so the module's PWM rises slowly; the PWM at
/// which the wheels first move is read from the module. Right and left: a quick rise, a
/// slow one, then the PWM just under what was read is held and must leave the wheels still
/// (a steering that answers late reads high on any rise). The lower side, less a small
/// margin, is the setting (the firmware has one value for both sides).</item>
/// <item><b>Max Limit.</b> The wheels are driven a few degrees at a fixed PWM (the Max Limit
/// under test, with a gain that saturates it and the set point kept just ahead of the
/// wheels), then let go; the speed is the slope of the angle. The PWM rises level by level
/// until the wheels turn as fast as wanted, or more PWM turns them no faster.</item>
/// <item><b>Proportional Gain.</b> Small steps of the set point at rising gain; the step
/// stops the module at once when the wheels swing. Of the gains that leave the wheels about
/// where they were asked (not past the target, not short of it), the setting is the lowest
/// that settles about as fast as the best one. A pair of larger steps checks it.</item>
/// </list>
/// The firmware's rule (Autosteer.ino / AutosteerPID.ino, the same for a valve, a Keya and a
/// motor on the steering wheel): <c>pwm = Kp × error ± minPWM</c>, limited to highPWM, and to
/// less within a few degrees of the set point.
/// <para>Safety: the operator's own three values are put back on every way out, and the
/// values found go into the settings only on <see cref="AcceptCommand"/>. Every sample
/// checks that the module is still steering, that the wheels are within
/// <see cref="WindowDeg"/> of where they started and turn the right way; any of those, Stop,
/// leaving the step, closing the wizard or losing the browser stops the module.</para>
/// </summary>
public class TuneSteeringStepViewModel : SwitchGatedWizardStep
{
    // ------------------------------------------------------------ limits that hold in every part

    /// <summary>The wheels must be about straight to start: the test swings them either way.</summary>
    internal const double MaxStartAngleDeg = 8.0;
    /// <summary>No faster than this to start (walking pace is the advice).</summary>
    internal const double MaxStartSpeedKmh = 6.0;
    /// <summary>Faster than this during the test stops it.</summary>
    internal const double MaxTestSpeedKmh = 10.0;
    /// <summary>The wheels never go further than this from where they started; past it the test stops.</summary>
    internal const double WindowDeg = 22.0;
    /// <summary>Wheels this far the wrong way stop the test: the motor direction is wrong.</summary>
    internal const double WrongWayDeg = 3.0;
    internal const int SampleMs = 20;
    /// <summary>After a settings change: the 150 ms debounce before PGN 252 goes out, and the module taking it.</summary>
    internal const int SettingsSettleMs = 400;
    internal const int StopCheckTimeoutMs = 30000;
    /// <summary>Stop check: wheels the module only holds may not move further than this by themselves.</summary>
    internal const double HoldDriftDeg = 3.0;
    /// <summary>Stop check with a kickout sensor: the operator turns the wheel by hand, up to this far.</summary>
    internal const double HoldByHandDeg = 12.0;

    // ------------------------------------------------------------ Minimum To Move

    /// <summary>The module's gain while looking for the first movement: PWM = 20 × error + 1.</summary>
    internal const int ProbeKp = 20;
    /// <summary>The search stops here (the setting itself can't be more than 200).</summary>
    internal const int ProbeLimitPwm = 200;
    internal const double CoarsePwmPerSec = 80;
    /// <summary>The setting is this share (2 PWM at least) under the PWM that moves the easier side, so
    /// that the Minimum To Move alone never moves the wheels.</summary>
    internal const double MinMarginShare = 0.04;
    /// <summary>How long a PWM is held to see that it leaves the wheels still.</summary>
    internal const int HoldMs = 1200;
    /// <summary>Times the PWM held is lowered while the wheels still move at it.</summary>
    internal const int HoldTries = 8;
    /// <summary>The quick rise on each side: this share of the first find per second (never under
    /// <see cref="QuickPwmPerSecMin"/>); the slow rises after it are a third of that.</summary>
    internal const double QuickRateShare = 0.4;
    /// <summary>The quick look at one side stops at this share of the PWM that moved the wheels before, plus 8.</summary>
    internal const double QuickLimitShare = 1.3;
    /// <summary>The slow look rises at this share of the quick one; holding a PWM then settles what a rise reads high.</summary>
    internal const double SlowRateShare = 0.7;
    /// <summary>Wheels that creep after the set point stay (their PWM - 1) / gain behind it, so the
    /// search goes this much PWM further to see them move <see cref="MoveDetectDeg"/>.</summary>
    internal static readonly int CreepPwm = (int)Math.Ceiling(ProbeKp * MoveDetectDeg) + 2;
    internal const double QuickPwmPerSecMin = 12;
    /// <summary>Below this the wheels count as not having moved yet (sensor noise).</summary>
    internal const double MoveNoiseDeg = 0.15;
    /// <summary>The wheels have moved: this far, two samples running.</summary>
    internal const double MoveDetectDeg = 0.4;

    // ------------------------------------------------------------ Max Limit

    /// <summary>The module's gain gives the whole PWM under test from this error up.</summary>
    internal const double SaturationDeg = 5.0;
    /// <summary>The set point is kept this far ahead of the wheels while they are driven.</summary>
    internal const double CarrotDeg = 7.0;
    /// <summary>
    /// Each run drives the wheels across where they started and lets go so that they stop about
    /// this far on the other side (their speed times the time they take to react is allowed for).
    /// </summary>
    internal const double DriveReachDeg = 4.0;
    /// <summary>A run that starts closer than this to where the wheels started is too short to measure.</summary>
    internal const double DriveShortStartDeg = 2.5;
    /// <summary>The speed of a run is its fastest stretch of this length.</summary>
    internal const double SpeedWindowSeconds = 0.15;
    internal const int DriveMaxMs = 1800;
    internal const double LevelRatio = 1.25;
    /// <summary>A level that turns the wheels less than this much faster than the one before adds nothing.</summary>
    internal const double WorthwhileGain = 1.05;

    // ------------------------------------------------------------ Proportional Gain

    internal static readonly int[] GainLadder = { 10, 14, 20, 27, 38, 54, 75, 105, 147, 200 };
    /// <summary>Tried only when the wheels already swing at the first gain of the ladder.</summary>
    internal static readonly int[] GainLadderDown = { 7, 5, 3 };
    internal const double SmallStepDeg = 4.0;
    internal const double BigStepDeg = 8.0;
    /// <summary>Past the target by more than this on a small step: the gain is too high.</summary>
    internal const double OvershootLimitDeg = 0.5;
    /// <summary>The gain picked goes past the target by no more than this, when one does.</summary>
    internal const double CleanOvershootDeg = 0.3;
    internal const double BigOvershootLimitDeg = 1.0;
    /// <summary>Wheels left further than this from a turn's target get a line in the result.</summary>
    internal const double ShortNoteDeg = 0.5;
    internal const int StepWindowMs = 2500;
    /// <summary>Wheels that have not moved this long after the start of a run are held.</summary>
    internal const double StepStartSeconds = 1.0;
    /// <summary>The same for a turn of the set point, once the steering's reaction time is known:
    /// twice that plus 0.3 s, between these two.</summary>
    internal const double StepStartMinSeconds = 0.5;
    internal const double StepStartMaxSeconds = 1.5;
    /// <summary>A turn this much longer than the wheels can stop short of it has to move them
    /// (or, at a low gain, long enough to ask for <see cref="MustMovePwm"/> more than moves them).</summary>
    internal const double MustMoveDeg = 0.8;
    internal const double MustMovePwm = 6;
    /// <summary>Wheels at rest this much further from the target than they can stop short of it are
    /// held (at a low gain: far enough to be asked for <see cref="HeldShortPwm"/> more than moves them).</summary>
    internal const double HeldShortDeg = 1.2;
    internal const double HeldShortPwm = 12;
    /// <summary>Wheels that were moving and stand still this long while still driven are held.</summary>
    internal const double StallSeconds = 0.4;
    /// <summary>The widest LOW_HIGH_DEGREES of the firmwares (3.0 in the AiO v4 firmware, 1.5 in
    /// others): assumed where the module's PWM is worked out, so the figure is never over the module's.</summary>
    internal const double WidestLowHighDeg = 5.0;

    internal const string WrongWayText =
        "The wheels turned the wrong way, so the test stopped and left them where they are. " +
        "Run the motor test first (Back): it finds the motor direction.";
    internal const string HoldMovedText =
        "The wheels moved while the module was only asked to hold them, so the test stopped. " +
        "Run the motor test first (Back): the motor direction may be wrong.";
    internal const string ModuleStoppedText =
        "The steer module stopped steering (steer switch off, or a kickout), so the test stopped.";
    internal const string StuckText =
        "The wheels stopped answering, so the test stopped. A hand on the wheel, or the wheels against a stop?";
    internal const string NothingChangedText = " Nothing was changed.";
    /// <summary>After this line of the result come the measurements, one a line (the page folds them away).</summary>
    internal const string MeasuredHeader = "\nMeasurements:\n";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private HardwareInstalledStepViewModel? _hardwareStep;
    private volatile bool _armed;
    private volatile bool _stopLatched;
    // While the module arms it only holds the wheels: watched from the control loop.
    private volatile bool _holdWatch;
    private volatile bool _holdTripped;
    private double _holdAt;
    // The PWM that moves each side (from the first part): a turn that asks for more has to move the wheels.
    private int _rightPwm, _leftPwm;
    /// <summary>Since when (ms) the module has been asking for clearly more than moves the wheels
    /// with the wheels still at <see cref="_pushAngle"/>; NaN when it is not.</summary>
    private double _pushSinceMs = double.NaN;
    private double _pushAngle;
    private bool _screenStop;
    private bool _inStopCheck;
    private TuneStopKind _stopKind;
    private double _anchor;
    private double _speedKmh;
    private int _origKp, _origMax, _origMin;
    private readonly List<double> _reactionMs = new();
    private readonly List<string> _measured = new();

    /// <summary>A test stopped for a reason the operator has to read.</summary>
    private sealed class TuneAbort(string message) : Exception(message);

    private readonly record struct Sample(double T, double Angle, int Pwm);
    /// <summary>
    /// One gain tried. <c>ShortDeg</c>: how far short of the target the wheels came to rest (the
    /// PWM asked for so near the target no longer moves that side).
    /// </summary>
    private readonly record struct LevelResult(int Kp, bool Bad, double SettleSeconds, double OvershootDeg, double ShortDeg)
    {
        /// <summary>How far from where they were asked the wheels went or stayed: the worse of the two.</summary>
        public double OffDeg => Math.Max(OvershootDeg, ShortDeg);
    }

    /// <summary>Injectable feedback reader for tests. Production reads <see cref="IAutoSteerService.LastSteerData"/>.</summary>
    internal Func<SteerModuleData>? ReadModuleData { get; set; }

    /// <summary>Injectable clock (milliseconds) for tests; they advance it from <c>DelayFunc</c>.</summary>
    internal Func<double> NowMs { get; set; } = () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    protected override SteerModuleData CurrentModuleData =>
        ReadModuleData != null ? ReadModuleData() : base.CurrentModuleData;

    public override string Title => "Tune the Wheel Response";

    public override string Description =>
        "Finds Minimum To Move, Max Limit and Proportional Gain by moving the wheels and measuring how they answer. " +
        "Engine at working speed, oil warm, wheels about straight, nobody near the front wheels; best while rolling " +
        "slowly on open ground. The wheels move by themselves for one to two minutes: small moves first, then swings " +
        "of up to 15°. Nothing is kept until you press Use these values. You can also leave this step out with Next.";

    public override bool ShouldSkip => _hardwareStep?.HardwareLevel == 0;

    public void SetHardwareStep(HardwareInstalledStepViewModel step) => _hardwareStep = step;

    private AutoSteerConfig Config => ConfigService.Store.AutoSteer;

    // ------------------------------------------------------------ state the page shows

    private TuneStage _stage = TuneStage.Idle;
    public TuneStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(TestStage));
                OnPropertyChanged(nameof(IsMeasuring));
            }
        }
    }

    /// <summary><see cref="Stage"/> as a number, for the Wizard frame.</summary>
    public int TestStage => (int)Stage;

    /// <summary>A part is running (the Wizard frame's test flag).</summary>
    public bool IsMeasuring => Stage == TuneStage.Running;

    private string _phaseDescription = "";
    /// <summary>What is being done now.</summary>
    public string PhaseDescription
    {
        get => _phaseDescription;
        private set => SetProperty(ref _phaseDescription, value);
    }

    private string _phaseResult = "";
    /// <summary>What the last part found, or why it stopped.</summary>
    public string PhaseResult
    {
        get => _phaseResult;
        private set => SetProperty(ref _phaseResult, value);
    }

    private double _liveSteerAngle;
    public double LiveSteerAngle
    {
        get => _liveSteerAngle;
        set => SetProperty(ref _liveSteerAngle, value);
    }

    private double _targetWheelSpeed = 25;
    /// <summary>How fast the wheels should turn at the Max Limit, degrees per second (Gentle 15, Normal 25, Fast 35).</summary>
    public double TargetWheelSpeed
    {
        get => _targetWheelSpeed;
        set
        {
            if (IsMeasuring) return; // not in the middle of a run
            if (SetProperty(ref _targetWheelSpeed, Math.Clamp(value, 10, 60)))
                OnPropertyChanged(nameof(TestChoice));
        }
    }

    /// <summary><see cref="TargetWheelSpeed"/>, for the Wizard frame (the page marks the choice).</summary>
    public double TestChoice => TargetWheelSpeed;

    // ------------------------------------------------------------ what the last run found

    /// <summary>PWM at which the wheels first move to the right / to the left.</summary>
    public int MinRightPwm { get; private set; }
    public int MinLeftPwm { get; private set; }
    public int FoundMinPwm { get; private set; }
    public int FoundMaxPwm { get; private set; }
    public int FoundKp { get; private set; }
    /// <summary>Wheel speed at <see cref="FoundMaxPwm"/>, degrees per second.</summary>
    public double FoundWheelSpeed { get; private set; }
    /// <summary>A run went through to its values (they may not be in use: see <see cref="Stage"/>).</summary>
    public bool TuneCompleted { get; private set; }
    /// <summary>Settings changes sent to the module by the last run (each one is saved by the module).</summary>
    public int SettingsWrites { get; private set; }

    // ------------------------------------------------------------ commands

    public ICommand StartTestCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand StopTestCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand DiscardCommand { get; }

    public TuneSteeringStepViewModel(IConfigurationService configService,
        IUiDispatcher dispatcher, IAutoSteerService? autoSteerService = null)
        : base(configService, autoSteerService, dispatcher)
    {
        StartTestCommand = new AsyncRelayCommand(RunStopCheckAsync, () => CanStartTest);
        ContinueCommand = new AsyncRelayCommand(RunTuningAsync);
        StopTestCommand = new RelayCommand(StopTest);
        AcceptCommand = new RelayCommand(Accept);
        DiscardCommand = new RelayCommand(Discard);
    }

    // ============================================================ part 0: the stop check

    internal static TuneStopKind StopKindFor(AutoSteerConfig a)
    {
        // A pressure or current sensor with no trip point never lets go (PgnBuilder.SensorTripByte).
        bool sensor = a.TurnSensorEnabled
            || (a.PressureSensorEnabled && a.PressureTripPoint > 0)
            || (a.CurrentSensorEnabled && a.CurrentTripPoint > 0);
        bool sw = a.ExternalEnable != 0;
        return sensor && sw ? TuneStopKind.SwitchOrSensor
             : sensor ? TuneStopKind.Sensor
             : sw ? TuneStopKind.Switch
             : TuneStopKind.Screen;
    }

    private static string StopCheckText(TuneStopKind kind) => kind switch
    {
        TuneStopKind.Sensor =>
            "Stop check: take the steering wheel and turn it by hand until the module lets go.",
        TuneStopKind.Switch =>
            "Stop check: switch the steer switch off now (or press the steer button).",
        TuneStopKind.SwitchOrSensor =>
            "Stop check: turn the steering wheel by hand until the module lets go, or switch the steer switch off.",
        _ =>
            "No stop at the wheel is set up (no steer switch, button or kickout sensor): the only stop is the " +
            "Stop button on this page. Stop check: press Stop now.",
    };

    private static string AfterStopText(TuneStopKind kind) => kind switch
    {
        TuneStopKind.Sensor => "The stop works. Let go of the steering wheel, then press Continue.",
        TuneStopKind.Switch => "The stop works. Switch the steer switch back on, then press Continue.",
        TuneStopKind.SwitchOrSensor =>
            "The stop works. Let go of the steering wheel and leave the steer switch on, then press Continue.",
        _ => "The Stop button works: it is the only stop, keep a finger near it. Press Continue.",
    };

    /// <summary>
    /// Start: check the wheels are straight and the tractor slow, arm Free Drive holding the
    /// wheels where they are, and wait for the operator to stop the module at the wheel.
    /// Nothing is driven on purpose here and no setting changes.
    /// </summary>
    internal async Task RunStopCheckAsync()
    {
        if (Stage == TuneStage.Running)
            return;
        PhaseResult = "";
        double angle = CurrentWasAngle;
        if (Math.Abs(angle) > MaxStartAngleDeg)
        {
            Stage = TuneStage.Idle;
            PhaseResult = string.Create(Inv,
                $"Straighten the wheels first: they read {angle:F0}° and the test swings them either way.");
            return;
        }
        if (_speedKmh > MaxStartSpeedKmh)
        {
            Stage = TuneStage.Idle;
            PhaseResult = "Too fast: slow down to walking pace, then press Start.";
            return;
        }

        _stopKind = StopKindFor(Config);
        _screenStop = false;
        var token = NewTestToken();
        Stage = TuneStage.Running;
        _inStopCheck = true;
        try
        {
            PhaseDescription = "Stop check: getting the module to hold the wheels…";
            await ArmAsync(token);
            PhaseDescription = StopCheckText(_stopKind);
            // The module only holds the wheels here. With a kickout sensor the operator turns
            // them by hand (that is the check); otherwise wheels that move are moving by
            // themselves: the motor direction is wrong and holding drives them away.
            double held = CurrentWasAngle;
            double mayMove = _stopKind is TuneStopKind.Sensor or TuneStopKind.SwitchOrSensor ? HoldByHandDeg : HoldDriftDeg;
            double t0 = NowMs();
            while (true)
            {
                await WaitAsync(SampleMs, token);
                var d = CurrentModuleData;
                LiveSteerAngle = Math.Round(d.ActualSteerAngle, 1);
                bool stopped = _stopKind == TuneStopKind.Screen
                    ? _screenStop
                    : _stopLatched || d.SteerSwitchActive;
                if (stopped)
                    break;
                if (Math.Abs(d.ActualSteerAngle - held) > mayMove)
                    throw new TuneAbort(HoldMovedText);
                if (NowMs() - t0 > StopCheckTimeoutMs)
                    throw new TuneAbort("The module was not seen to stop within 30 seconds, so the wheels were not " +
                        "moved. Check the steer switch or the kickout sensor (Speed and Sensors, two steps on), then press Start.");
            }
            Disarm();
            PhaseDescription = "";
            PhaseResult = AfterStopText(_stopKind);
            Stage = TuneStage.StopChecked;
        }
        catch (OperationCanceledException)
        {
            PhaseDescription = "";
            PhaseResult = "Stopped.";
            Stage = TuneStage.Idle;
        }
        catch (TuneAbort ex)
        {
            PhaseDescription = "";
            PhaseResult = ex.Message;
            Stage = TuneStage.Idle;
        }
        catch (Exception ex)
        {
            PhaseDescription = "";
            PhaseResult = "The test failed and stopped: " + ex.Message;
            Stage = TuneStage.Idle;
        }
        finally
        {
            _inStopCheck = false;
            Disarm();
        }
    }

    /// <summary>The Stop button: in the stop check with no stop at the wheel it is the check; otherwise it stops the run.</summary>
    private void StopTest()
    {
        if (_inStopCheck && _stopKind == TuneStopKind.Screen && _armed)
        {
            _screenStop = true;
            return;
        }
        if (Stage == TuneStage.StopChecked)
        {
            PhaseResult = "Stopped.";
            Stage = TuneStage.Idle;
            return;
        }
        StopFreeDriveTest();
    }

    // ============================================================ the run

    /// <summary>
    /// Continue: the three measurements and the check, then the values found are shown.
    /// The operator's own values are back in the settings when this returns, whatever happened.
    /// </summary>
    internal async Task RunTuningAsync()
    {
        if (Stage != TuneStage.StopChecked)
            return;
        var a = Config;
        _origKp = a.ProportionalGain;
        _origMax = a.MaxPwm;
        _origMin = a.MinPwm;
        SettingsWrites = 0;
        TuneCompleted = false;
        _reactionMs.Clear();
        _measured.Clear();
        PhaseResult = "";
        var token = NewTestToken();
        Stage = TuneStage.Running;
        var next = TuneStage.Idle;
        try
        {
            _anchor = CurrentWasAngle;
            var (right, left, notes) = await MeasureMinToMoveAsync(token);
            int lower = Math.Min(right, left);
            _rightPwm = right;
            _leftPwm = left;
            int min = Math.Clamp(lower - Math.Max(2, (int)Math.Round(lower * MinMarginShare)), 1, 200);

            var (max, speed, maxNote) = await MeasureMaxLimitAsync(lower, token);
            if (maxNote.Length > 0) notes.Add(maxNote);
            max = Math.Clamp(Math.Max(max, (int)Math.Ceiling(min * 1.3) + 5), 50, 255);

            var (kp, check, gainNote) = await FindGainAsync(min, max, token);
            if (gainNote.Length > 0) notes.Add(gainNote);
            await CentreAsync(token);

            MinRightPwm = right;
            MinLeftPwm = left;
            FoundMinPwm = min;
            FoundMaxPwm = max;
            FoundKp = kp;
            FoundWheelSpeed = speed;
            TuneCompleted = true;
            if (_reactionMs.Count > 0 && SteerTuneMath.Median(_reactionMs) >= 100)
                notes.Add(string.Create(Inv, $"The steering takes about {SteerTuneMath.Median(_reactionMs) / 1000:F2} s to react."));
            PhaseResult = string.Create(Inv,
                $"Minimum To Move: {_origMin} → {min}  (first movement at PWM {right} to the right, {left} to the left)\n" +
                $"Max Limit: {_origMax} → {max}  (the wheels turn about {speed:F0}°/s)\n" +
                $"Proportional Gain: {_origKp} → {kp}  (an {BigStepDeg:F0}° turn settles in {check.SettleSeconds:F1} s, {check.OvershootDeg:F1}° past the target)") +
                (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "") +
                "\nNot in use yet: press Use these values, or Keep the old values." +
                MeasuredHeader + string.Join("\n", _measured);
            next = TuneStage.Result;
        }
        catch (OperationCanceledException)
        {
            PhaseResult = "Stopped." + NothingChangedText;
        }
        catch (TuneAbort ex)
        {
            PhaseResult = ex.Message + NothingChangedText + (_measured.Count > 0 ? MeasuredHeader + string.Join("\n", _measured) : "");
            // The module never armed (switch still off after the stop check): Continue again.
            if (ex.Message == NotArmedText)
                next = TuneStage.StopChecked;
        }
        catch (Exception ex)
        {
            PhaseResult = "The test failed and stopped: " + ex.Message + NothingChangedText;
        }
        finally
        {
            // The module off first (nothing drives while the settings change), then the
            // operator's own values back.
            Disarm();
            a.ProportionalGain = _origKp;
            a.MaxPwm = _origMax;
            a.MinPwm = _origMin;
            PhaseDescription = "";
            Stage = next;
        }
    }

    private void Accept()
    {
        if (Stage != TuneStage.Result || !TuneCompleted)
            return;
        var a = Config;
        a.MinPwm = FoundMinPwm;
        a.MaxPwm = FoundMaxPwm;
        a.ProportionalGain = FoundKp;
        ConfigService.Store.MarkChanged();
        PhaseResult = string.Create(Inv,
            $"In use now: Minimum To Move {FoundMinPwm}, Max Limit {FoundMaxPwm}, Proportional Gain {FoundKp}. " +
            $"Finish saves them with the vehicle. Next, on a line with AutoSteer on, set Steer Response on the AutoSteer page.");
        Stage = TuneStage.Accepted;
    }

    private void Discard()
    {
        if (Stage != TuneStage.Result)
            return;
        PhaseResult = string.Create(Inv,
            $"The old values stay: Minimum To Move {Config.MinPwm}, Max Limit {Config.MaxPwm}, Proportional Gain {Config.ProportionalGain}.");
        Stage = TuneStage.Idle;
    }

    // ============================================================ part 1: Minimum To Move

    private async Task<(int Right, int Left, List<string> Notes)> MeasureMinToMoveAsync(CancellationToken token)
    {
        var notes = new List<string>();
        PhaseDescription = "Minimum To Move: looking for the first movement…";
        // Max Limit out of the way: within a few degrees of the set point the firmware limits the
        // PWM to a share of it, and at 255 that share is always above what the ramp asks for.
        await ApplyAsync(ProbeKp, 255, 1, token);
        await ArmAsync(token);

        var coarse = await RampAsync(+1, 1, CoarsePwmPerSec, ProbeLimitPwm, token);
        if (coarse.Pwm == null)
            throw new TuneAbort(string.Create(Inv,
                $"The wheels did not move up to PWM {ProbeLimitPwm}. Hands on the wheel, the wheels against a stop, or the valve or motor not driving?"));
        int guess = coarse.Pwm.Value;

        // Each side: a quick rise to find about where the wheels move, a slow one to read it
        // (the module's PWM stays at what moves the wheels while they creep after the set
        // point), then that PWM less one is held: it must leave the wheels still. A steering
        // that answers late reads high on any rise; holding takes that out.
        var sides = new Dictionary<int, int>();
        // Left first: the search to the right just moved the wheels that way.
        foreach (int dir in new[] { -1, +1 })
        {
            string side = dir > 0 ? "right" : "left";
            PhaseDescription = string.Create(Inv, $"Minimum To Move: {side}, quick look…");
            double quickRate = Math.Max(QuickPwmPerSecMin, guess * QuickRateShare);
            // No further than well past where they moved before: wheels that do not move by
            // then are held, and the motor is not left pushing against a hand.
            int quickLimit = Math.Min(ProbeLimitPwm, (int)(guess * QuickLimitShare) + 8);
            int? quick = await FindAsync(dir, (int)(guess * 0.6), quickRate, quickLimit + CreepPwm, quickLimit, token);
            if (quick == null)
                throw new TuneAbort(string.Create(Inv,
                    $"The wheels did not move to the {side} up to PWM {quickLimit}, though they moved at {guess} before. A hand on the wheel, or a valve that opens one way only?"));

            PhaseDescription = string.Create(Inv, $"Minimum To Move: {side}, slow look…");
            double slowRate = quickRate * SlowRateShare;
            // Slower, the wheels move no later than they did on the quick rise: still a little
            // past that PWM (creeping wheels take a moment to show), they are held.
            int? slow = await FindAsync(dir, (int)(quick.Value * 0.75), slowRate, (int)(quick.Value * 1.25) + 5 + CreepPwm,
                quick.Value + Math.Max(3, quick.Value / 20) + (int)Math.Ceiling(ProbeKp * MoveNoiseDeg), token);
            if (slow == null)
                throw new TuneAbort(StuckText);

            int still = Math.Max(1, slow.Value - 1);
            int holds = 0;
            for (; holds < HoldTries && still > 1; holds++)
            {
                PhaseDescription = string.Create(Inv, $"Minimum To Move: {side}, PWM {still} must leave the wheels still…");
                if (!await MovesAtAsync(dir, still, token))
                    break;
                still -= Math.Max(1, (int)Math.Round(still * 0.03));
            }
            sides[dir] = still + 1;
            _measured.Add(string.Create(Inv,
                $"first movement {side}: PWM {quick.Value} rising {quickRate:F0}/s, {slow.Value} rising {slowRate:F0}/s, still when held at {still}{(holds >= HoldTries ? " (not checked further)" : "")}"));
            guess = sides[dir];
        }
        Disarm();

        int r = sides[+1], l = sides[-1];
        int lower = Math.Min(r, l);
        if (Math.Abs(r - l) > Math.Max(6, lower / 4))
            notes.Add(string.Create(Inv,
                $"The two sides differ ({r} right, {l} left): the module has one Minimum To Move, set from the lower side, so the {(r > l ? "right" : "left")} side answers small corrections later."));
        if (lower > 120)
            notes.Add(string.Create(Inv,
                $"A first movement at PWM {lower} is high: were the wheels free to turn (rolling, engine at working speed)?"));
        return (r, l, notes);
    }

    /// <summary>
    /// One search: hold the wheels, then move the set point away from them so that the module's
    /// PWM rises by <paramref name="pwmPerSec"/> from <paramref name="fromPwm"/>, until the wheels
    /// move. Returns the PWM just before they did (null: none up to <paramref name="limitPwm"/>),
    /// and whether they moved before the ramp had risen at all.
    /// </summary>
    private async Task<(int? Pwm, bool AtOnce)> RampAsync(int dir, int fromPwm, double pwmPerSec, int limitPwm, CancellationToken token,
        int stillLimitPwm = int.MaxValue)
    {
        double start = await RestAsync(token);
        bool danfoss = Config.DanfossEnabled;
        double t0 = NowMs();
        int before = 0, hits = 0;
        int? result = null;
        bool atOnce = false;
        // Wheels that begin to move reach MoveDetectDeg while the PWM rises by CreepPwm at
        // most (they follow the set point). Begun, and not there by then: they are held.
        double stallMs = Math.Max(600, (CreepPwm + 2) / pwmPerSec * 1000);
        double begunAt = double.NaN;
        int begun = 0, notBegun = 0;
        while (true)
        {
            double now = NowMs();
            double want = fromPwm + pwmPerSec * (now - t0) / 1000.0;
            if (want > limitPwm)
                break;
            AutoSteerService?.SetFreeDriveAngle(start + dir * (want - 1) / ProbeKp);
            var s = await SampleAsync(token);
            double moved = (s.Angle - start) * dir;
            if (moved <= -WrongWayDeg)
                throw new TuneAbort(WrongWayText);
            // A Danfoss valve's PWM display is its valve position, not the drive: use what the
            // firmware's rule gives for the set point sent.
            int pwm = danfoss ? (int)want : s.Pwm;
            if (moved < MoveNoiseDeg)
            {
                before = Math.Max(before, pwm);
                // Not moving at all past this PWM: no further (a hand on the wheel is not pushed harder).
                if (want > stillLimitPwm)
                    break;
                begun = 0;
                if (++notBegun >= 3) begunAt = double.NaN;
            }
            else
            {
                notBegun = 0;
                if (++begun >= 3 && double.IsNaN(begunAt)) begunAt = now;
                if (now - begunAt >= stallMs && moved < MoveDetectDeg)
                    throw new TuneAbort(StuckText);
            }
            hits = moved >= MoveDetectDeg ? hits + 1 : 0;
            if (hits >= 2)
            {
                result = Math.Max(before, 1);
                atOnce = before <= fromPwm + 1 && fromPwm > 1;
                break;
            }
        }
        // Let go where the wheels are, and let them come to rest.
        await RestAsync(token);
        return (result, atOnce);
    }

    /// <summary>
    /// <see cref="RampAsync"/>, begun again from half the PWM while the wheels move as soon as it begins
    /// (it began above what moves this side). The PWM just before the wheels moved; null: none up to the limit.
    /// </summary>
    private async Task<int?> FindAsync(int dir, int fromPwm, double pwmPerSec, int limitPwm, int stillLimitPwm, CancellationToken token)
    {
        int from = Math.Max(1, fromPwm);
        while (true)
        {
            var found = await RampAsync(dir, from, pwmPerSec, limitPwm, token, stillLimitPwm);
            if (!found.AtOnce || from <= 1)
                return found.Pwm;
            from = Math.Max(1, from / 2);
        }
    }

    /// <summary>
    /// The module's PWM held at <paramref name="pwm"/> for <see cref="HoldMs"/> (the set point kept
    /// that much ahead of the wheels, so the PWM does not fall as they move): do the wheels move?
    /// </summary>
    private async Task<bool> MovesAtAsync(int dir, int pwm, CancellationToken token)
    {
        double start = await RestAsync(token);
        double ahead = dir * (pwm - 1) / (double)ProbeKp;
        // The mean of the last few readings: sensor noise times the gain would shake the PWM.
        var recent = new Queue<double>();
        recent.Enqueue(start);
        double t0 = NowMs();
        int hits = 0;
        bool moves = false;
        while (NowMs() - t0 < HoldMs)
        {
            AutoSteerService?.SetFreeDriveAngle(recent.Average() + ahead);
            var s = await SampleAsync(token);
            recent.Enqueue(s.Angle);
            if (recent.Count > 4) recent.Dequeue();
            double moved = (s.Angle - start) * dir;
            if (moved <= -WrongWayDeg)
                throw new TuneAbort(WrongWayText);
            hits = moved >= MoveDetectDeg ? hits + 1 : 0;
            if (hits >= 2)
            {
                moves = true;
                break;
            }
        }
        await RestAsync(token);
        return moves;
    }

    /// <summary>Set point on the wheels until they are at rest; the angle they rest at.</summary>
    private async Task<double> RestAsync(CancellationToken token)
    {
        var last = new List<double>();
        double angle = CurrentWasAngle;
        for (int i = 0; i < 40; i++)
        {
            AutoSteerService?.SetFreeDriveAngle(angle);
            var s = await SampleAsync(token);
            angle = s.Angle;
            last.Add(angle);
            if (last.Count >= 6 && last.Skip(last.Count - 6).Max() - last.Skip(last.Count - 6).Min() < 0.15)
                break;
        }
        double rest = last.Skip(Math.Max(0, last.Count - 4)).Average();
        AutoSteerService?.SetFreeDriveAngle(rest);
        return rest;
    }

    // ============================================================ part 2: Max Limit

    private async Task<(int MaxPwm, double Speed, string Note)> MeasureMaxLimitAsync(int startsMovingPwm, CancellationToken token)
    {
        double wanted = TargetWheelSpeed;
        var levels = new List<(int Pwm, double Speed)>();
        int pwm = Math.Clamp((int)Math.Round(startsMovingPwm * 1.6), 50, 255);
        while (true)
        {
            PhaseDescription = string.Create(Inv, $"Max Limit: how fast the wheels turn at PWM {pwm}…");
            // The gain that gives the whole PWM from SaturationDeg of error up; minimum 1, so the
            // module holds still on sensor noise while it arms.
            int kp = Math.Clamp((int)Math.Ceiling((pwm - 1) / SaturationDeg), 1, 200);
            await ApplyAsync(kp, pwm, 1, token);
            // Each run goes across where the wheels started, so they stay around it. From
            // close to it the run is too short to measure: one run first, only to get away.
            int first = CurrentWasAngle > _anchor ? -1 : +1;
            if (Math.Abs(CurrentWasAngle - _anchor) < DriveShortStartDeg)
            {
                await DriveAsync(first, token);
                first = -first;
            }
            double s1 = await DriveAsync(first, token);
            double s2 = await DriveAsync(-first, token);
            double speed = (s1 + s2) / 2;
            levels.Add((pwm, speed));
            _measured.Add(string.Create(Inv, $"PWM {pwm}: {s1:F1} and {s2:F1} deg/s"));
            PhaseDescription = string.Create(Inv, $"Max Limit: PWM {pwm} turns the wheels {speed:F0}°/s");

            if (speed >= wanted)
            {
                int at = SteerTuneMath.PwmForSpeed(levels, wanted, startsMovingPwm);
                return (at, wanted, "");
            }
            if (levels.Count >= 2 && levels[^2].Speed > 1 && speed < levels[^2].Speed * WorthwhileGain)
            {
                var (p, s) = levels[^2];
                return (p, s, string.Create(Inv,
                    $"More than PWM {p} does not turn the wheels faster ({s:F0}°/s, {wanted:F0}°/s was wanted): the valve is fully open or the motor at its top speed."));
            }
            if (pwm >= 255)
            {
                if (speed < 1)
                    throw new TuneAbort("The wheels hardly move even at full power (PWM 255). Hands on the wheel, the wheels against a stop, low oil pressure, or the motor slipping?");
                return (255, speed, string.Create(Inv,
                    $"At full power the wheels turn {speed:F0}°/s, less than the {wanted:F0}°/s wanted."));
            }
            pwm = Math.Min(255, (int)Math.Ceiling(pwm * LevelRatio));
        }
    }

    /// <summary>
    /// One run at the PWM in the settings: arm, keep the set point <see cref="CarrotDeg"/> ahead of
    /// the wheels (the module then drives at its Max Limit, and with the program gone it stops
    /// that far on), and let go so that the wheels stop <see cref="DriveReachDeg"/> past where they
    /// started. The wheels are sampled until they are still: a valve goes on turning for a moment
    /// after it is let go, and that is part of the run. Returns degrees per second.
    /// </summary>
    private async Task<double> DriveAsync(int dir, CancellationToken token)
    {
        await ArmAsync(token);
        double start = CurrentWasAngle, angle = start;
        double t0 = NowMs();
        var run = new List<(double X, double Y)>();
        double firstMoveMs = double.NaN;
        while (true)
        {
            double carrot = Math.Clamp(angle + dir * CarrotDeg, _anchor - (WindowDeg - 2), _anchor + (WindowDeg - 2));
            AutoSteerService?.SetFreeDriveAngle(carrot);
            var s = await SampleAsync(token);
            angle = s.Angle;
            double t = s.T - t0, moved = (angle - start) * dir;
            if (moved <= -WrongWayDeg)
                throw new TuneAbort(WrongWayText);
            run.Add((t / 1000.0, moved));
            if (moved >= 0.3 && double.IsNaN(firstMoveMs))
                firstMoveMs = t;
            // Where the wheels stop if let go now: they go on for about as long as they took to start.
            double react = double.IsNaN(firstMoveMs) ? 0 : firstMoveMs / 1000.0;
            double stopsAt = (angle - _anchor) * dir + RecentSpeed(run) * react;
            // Every run is at more PWM than moved the wheels before. Wheels that have not started
            // after a second, or that started and now stand still, are held: stop pushing.
            if (double.IsNaN(firstMoveMs) ? t >= StepStartSeconds * 1000 : Stalled(run, StallSeconds))
                throw new TuneAbort(StuckText);
            if (stopsAt >= DriveReachDeg || t >= DriveMaxMs)
                break;
        }
        Disarm();
        // Still: less than 0.1° in the last 100 ms.
        for (int waited = 0; waited < 1500; waited += SampleMs)
        {
            var s = await SampleAsync(token);
            double t = (s.T - t0) / 1000.0, moved = (s.Angle - start) * dir;
            run.Add((t, moved));
            int back = run.FindLastIndex(r => r.X <= t - 0.1);
            if (back >= 0 && Math.Abs(moved - run[back].Y) < 0.1)
                break;
        }
        if (!double.IsNaN(firstMoveMs))
            _reactionMs.Add(firstMoveMs);
        return SteerTuneMath.TopSpeed(run, SpeedWindowSeconds);
    }

    /// <summary>The wheels moved less than 0.1° over the last <paramref name="seconds"/> of a run that is that long.</summary>
    private static bool Stalled(List<(double X, double Y)> run, double seconds)
    {
        double now = run[^1].X;
        int back = run.FindLastIndex(r => r.X <= now - seconds);
        if (back < 0) return false;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = back; i < run.Count; i++)
        {
            lo = Math.Min(lo, run[i].Y);
            hi = Math.Max(hi, run[i].Y);
        }
        return hi - lo < 0.1;
    }

    /// <summary>Degrees per second over the last few samples of a run (0 until there are enough).</summary>
    private static double RecentSpeed(List<(double X, double Y)> run)
    {
        if (run.Count < 4) return 0;
        return Math.Max(0, SteerTuneMath.Slope(run.GetRange(run.Count - 4, 4)));
    }

    /// <summary>Module off: wait for the wheels to stop.</summary>
    private async Task CoastAsync(CancellationToken token)
    {
        double last = CurrentWasAngle;
        int still = 0;
        for (int waited = 0; waited < 1200 && still < 2; waited += 50)
        {
            var s = await SampleAsync(token, 50);
            still = Math.Abs(s.Angle - last) < 0.1 ? still + 1 : 0;
            last = s.Angle;
        }
    }

    // ============================================================ part 3: Proportional Gain

    private async Task<(int Kp, LevelResult Check, string Note)> FindGainAsync(int min, int max, CancellationToken token)
    {
        var tried = new List<LevelResult>();
        async Task<LevelResult> TryAsync(int kp)
        {
            PhaseDescription = string.Create(Inv, $"Proportional Gain: small turns at {kp}…");
            var r = await RunLevelAsync(kp, max, min, SmallStepDeg, OvershootLimitDeg, token);
            tried.Add(r);
            _measured.Add(Describe(r, SmallStepDeg));
            return r;
        }
        static bool PastTheTarget(LevelResult r) => r.Bad || r.OvershootDeg > CleanOvershootDeg;

        var first = await TryAsync(GainLadder[0]);
        if (PastTheTarget(first))
        {
            // Already past the target at the bottom of the ladder (a steering that answers
            // late): look below it for a gain that is not.
            foreach (int kp in GainLadderDown)
                if (!PastTheTarget(await TryAsync(kp))) break;
        }
        else
        {
            // Up the ladder until the wheels go past the target, or two levels running are no
            // quicker than the best before them (the wheels already turn at the Max Limit for
            // most of the step: more gain adds nothing).
            foreach (int kp in GainLadder.Skip(1))
            {
                if (PastTheTarget(await TryAsync(kp)))
                    break;
                if (tried.Count >= 3)
                {
                    double before = tried.Take(tried.Count - 2).Min(g => g.SettleSeconds);
                    if (tried[^1].SettleSeconds > before * 0.95 && tried[^2].SettleSeconds > before * 0.95)
                        break;
                }
            }
        }

        string note = "";
        int pick;
        var calm = tried.Where(g => !g.Bad).ToList();
        if (calm.Count > 0)
        {
            // The gains that put the wheels about where they were asked: no further past the
            // target, and no further short of it, than the best gain does (a low gain leaves the
            // wheels short on a side that needs more PWM to move, a high one takes them past).
            // Of those, the lowest that settles about as fast as the best.
            double limit = Math.Max(CleanOvershootDeg, calm.Min(g => g.OffDeg) + 0.1);
            var good = calm.Where(g => g.OffDeg <= limit).ToList();
            double best = good.Min(g => g.SettleSeconds);
            pick = good.Where(g => g.SettleSeconds <= best * 1.15 + 0.03).Min(g => g.Kp);
        }
        else
        {
            pick = tried.Min(g => g.Kp);
            note = string.Create(Inv,
                $"The wheels swing even at Proportional Gain {pick}: the steering answers late, or there is play between the motor and the wheels. Check before using these values.");
        }

        // The check: a pair of larger turns with the values found; one level down while they swing.
        LevelResult check;
        for (int attempt = 0; ; attempt++)
        {
            PhaseDescription = string.Create(Inv, $"Checking: {BigStepDeg:F0}° turns with Proportional Gain {pick}…");
            check = await RunLevelAsync(pick, max, min, BigStepDeg, BigOvershootLimitDeg, token);
            _measured.Add(Describe(check, BigStepDeg));
            int lower = NextLowerGain(pick);
            if (!check.Bad || attempt >= 2 || lower == pick)
                break;
            pick = lower;
        }
        if (check.Bad && note.Length == 0)
            note = string.Create(Inv,
                $"The wheels still go past the target on an {BigStepDeg:F0}° turn at Proportional Gain {pick}. Check for play in the steering before using these values.");
        else if (check.ShortDeg > ShortNoteDeg && note.Length == 0)
            note = string.Create(Inv,
                $"On one side the wheels stop about {check.ShortDeg:F1}° short of a turn: that side needs more PWM to move than the Minimum To Move, which is set from the other side.");
        return (pick, check, note);
    }

    private static string Describe(LevelResult r, double stepDeg) => string.Create(Inv,
        $"gain {r.Kp}, {stepDeg:F0} deg turns: {(r.Bad ? "swings or too far past" : "calm")}, {r.OvershootDeg:F2} past, {r.ShortDeg:F2} short, settles in {r.SettleSeconds:F2} s");

    internal static int NextLowerGain(int kp)
    {
        int lower = kp;
        foreach (int k in GainLadderDown.Concat(GainLadder))
            if (k < kp && (lower == kp || k > lower)) lower = k;
        return lower;
    }

    /// <summary>
    /// The settings under test, then turns of <paramref name="stepDeg"/> about where the wheels
    /// started: to the left of it (not measured, it only gets them there), to the right, back.
    /// </summary>
    private async Task<LevelResult> RunLevelAsync(int kp, int max, int min, double stepDeg, double overshootLimit, CancellationToken token)
    {
        await ApplyAsync(kp, max, min, token);
        if (!_armed)
            await ArmAsync(token);
        double half = stepDeg / 2;
        double hard = Math.Max(2.0, stepDeg / 2);

        // By the firmware's rule each side stops (its PWM - Minimum To Move) / gain short of the
        // target. A turn longer than that has to move the wheels, and wheels at rest further
        // from the target than that are held: a hand on the wheel.
        double shortRight = Math.Max(0, _rightPwm - min) / (double)kp;
        double shortLeft = Math.Max(0, _leftPwm - min) / (double)kp;

        var law = (kp, min, max);
        var (pre, preCut) = await StepAsync(_anchor - half, hard, law, shortRight, shortLeft, token, measured: false);
        if (preCut)
            return new LevelResult(kp, true, StepWindowMs / 1000.0, pre.OvershootDeg, 0);
        var (right, rightCut) = await StepAsync(_anchor + half, hard, law, shortRight, shortLeft, token);
        if (rightCut)
            return new LevelResult(kp, true, StepWindowMs / 1000.0, right.OvershootDeg, 0);
        var (left, leftCut) = await StepAsync(_anchor - half, hard, law, shortRight, shortLeft, token);

        double over = Math.Max(right.OvershootDeg, left.OvershootDeg);
        double shortOf = Math.Max(0, Math.Max(right.FinalErrorDeg, left.FinalErrorDeg));
        bool bad = leftCut || over > overshootLimit || right.Crossings >= 3 || left.Crossings >= 3;
        return new LevelResult(kp, bad, Math.Max(right.SettleSeconds, left.SettleSeconds), over, shortOf);
    }

    /// <summary>
    /// One step of the set point, sampled until the wheels are at rest or <see cref="StepWindowMs"/>.
    /// Wheels that swing across the target, or go <paramref name="hardOvershootDeg"/> past it,
    /// stop the module at once (Cut). <paramref name="shortRightDeg"/> and
    /// <paramref name="shortLeftDeg"/>: how far short of a target each side can stop by itself;
    /// wheels that do not start on a longer turn, or rest further from the target, are held by
    /// something and the test stops. So are wheels that stand still, in this turn or since an
    /// earlier one, while the module asks for clearly more than moves them
    /// (<paramref name="law"/>: the settings under test).
    /// </summary>
    private async Task<(StepResponse Response, bool Cut)> StepAsync(double target, double hardOvershootDeg,
        (int Kp, int Min, int Max) law, double shortRightDeg, double shortLeftDeg, CancellationToken token, bool measured = true)
    {
        int kp = law.Kp;
        double start = CurrentWasAngle;
        double step = Math.Abs(target - start);
        AutoSteerService?.SetFreeDriveAngle(target);
        if (step < 0.5)
        {
            await SampleAsync(token, 200);
            return (new StepResponse(0, 0, 0, 0, true), false);
        }
        int dir = target > start ? 1 : -1;
        double canStopShort = dir > 0 ? shortRightDeg : shortLeftDeg;
        double mustMove = canStopShort + Math.Max(MustMoveDeg, MustMovePwm / kp);
        double held = canStopShort + Math.Max(HeldShortDeg, HeldShortPwm / kp);
        double startBy = StepStartBySeconds();
        double startedAt = double.NaN;
        double t0 = NowMs();
        var samples = new List<(double T, double Moved)>();
        while (true)
        {
            var s = await SampleAsync(token);
            double t = (s.T - t0) / 1000.0, moved = (s.Angle - start) * dir;
            samples.Add((t, moved));
            if (moved <= -WrongWayDeg)
                throw new TuneAbort(WrongWayText);
            // The module asking for clearly more than moves this side, and the wheels still for
            // longer than they take to start: held. This clock runs on from turn to turn, so a
            // hand that stops the wheels near the end of one turn is not pushed for the next.
            double errNow = target - s.Angle;
            bool pushing = SteerTuneMath.ModulePwm(errNow, law.Kp, law.Min, law.Max, WidestLowHighDeg)
                           >= (errNow > 0 ? _rightPwm : _leftPwm) + HeldShortPwm;
            if (!pushing || double.IsNaN(_pushSinceMs) || Math.Abs(s.Angle - _pushAngle) > 0.35)
            {
                _pushSinceMs = pushing ? s.T : double.NaN;
                _pushAngle = s.Angle;
            }
            else if (s.T - _pushSinceMs >= (startBy + 0.2) * 1000)
                throw new TuneAbort(StuckText);
            // A turn longer than the wheels can stop short of asks for more PWM than moved this
            // side before: wheels that do not start, or that rest well short of where this gain
            // can take them, are held. (A shorter turn may not move a stiff side at all: that is
            // a poor gain, not a hand, and the level's result says so.)
            double most = samples.Max(x => Math.Abs(x.Moved));
            if (double.IsNaN(startedAt) && most >= 0.3)
                startedAt = t;
            if (t >= startBy && step > mustMove && double.IsNaN(startedAt))
                throw new TuneAbort(StuckText);
            // (At rest since they started, not before: a steering that answers late stands
            // still for its dead time first.)
            if (t - startedAt >= StallSeconds + 0.1 && step - moved > held && AtRest(samples, StallSeconds + 0.1))
                throw new TuneAbort(StuckText);
            if (moved - step > hardOvershootDeg || SteerTuneMath.Crossings(samples, step) >= 3)
            {
                Disarm();
                await CoastAsync(token);
                return (SteerTuneMath.StepResponse(samples, step), true);
            }
            if (t * 1000 >= StepWindowMs)
                break;
            // (A turn that only gets the wheels there is not waited for longer than they take to start.)
            if (AtRest(samples, 0.3) && (moved >= 0.8 * step || t >= startBy + (measured ? 0.5 : 0)))
                break;
        }
        return (SteerTuneMath.StepResponse(samples, step), false);
    }

    /// <summary>
    /// By when the wheels must have started after a turn of the set point: twice the time they
    /// took to start in the Max Limit runs (a turn asks for less PWM than those) plus 0.3 s.
    /// </summary>
    private double StepStartBySeconds() => _reactionMs.Count == 0
        ? StepStartSeconds
        : Math.Clamp(2 * SteerTuneMath.Median(_reactionMs) / 1000.0 + 0.3, StepStartMinSeconds, StepStartMaxSeconds);

    /// <summary>The wheels moved less than 0.35° over the last <paramref name="seconds"/>.</summary>
    private static bool AtRest(List<(double T, double Moved)> samples, double seconds)
    {
        double now = samples[^1].T;
        if (now < seconds) return false;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = samples.Count - 1; i >= 0 && samples[i].T >= now - seconds; i--)
        {
            lo = Math.Min(lo, samples[i].Moved);
            hi = Math.Max(hi, samples[i].Moved);
        }
        return hi - lo <= 0.35;
    }

    /// <summary>Back to where the wheels started, with the values found (they are known to be calm), then the module off.</summary>
    private async Task CentreAsync(CancellationToken token)
    {
        PhaseDescription = "Putting the wheels back where they were…";
        if (!_armed)
            await ArmAsync(token);
        int startByLoops = (int)Math.Ceiling(StepStartBySeconds() * 1000 / 50);
        var seen = new List<double> { CurrentWasAngle };
        AutoSteerService?.SetFreeDriveAngle(_anchor);
        for (int waited = 0; waited < 2500; waited += 50)
        {
            var s = await SampleAsync(token, 50);
            if (Math.Abs(s.Angle - _anchor) <= 1.0)
                break;
            // Wheels that do not come, or no longer come, are held: let go, the values are found already.
            seen.Add(s.Angle);
            if (seen.Count > startByLoops && Math.Abs(s.Angle - seen[seen.Count - 1 - startByLoops]) < 0.2)
                break;
        }
        Disarm();
    }

    // ============================================================ the pieces every part uses

    private async Task WaitAsync(int ms, CancellationToken token)
    {
        await DelayFunc(ms, token);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Free Drive on, holding the wheels where they are; throws when the module does not arm.
    /// Arming takes half a second or more with the module only holding the wheels: wheels that
    /// move <see cref="HoldDriftDeg"/> in that time are running away (the motor direction is
    /// wrong, so holding drives them off), and the control loop lets the module go at once.
    /// </summary>
    private async Task ArmAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _pushSinceMs = double.NaN;
        _holdAt = CurrentWasAngle;
        _holdTripped = false;
        _holdWatch = true;
        bool armed;
        try
        {
            armed = await BeginFreeDriveAsync(token);
        }
        catch (OperationCanceledException) when (_holdTripped)
        {
            throw new TuneAbort(HoldMovedText);
        }
        finally
        {
            _holdWatch = false;
        }
        if (_holdTripped)
            throw new TuneAbort(HoldMovedText);
        if (!armed)
            throw new TuneAbort(NotArmedText);
        token.ThrowIfCancellationRequested();
        _stopLatched = false;
        _armed = true;
    }

    /// <summary>Module off now (PGN 254 status 0): the motor stops driving at once.</summary>
    private void Disarm()
    {
        bool was = _armed;
        _armed = false;
        if (was || AutoSteerService?.IsInFreeDriveMode == true)
            ReleaseFreeDrive();
    }

    /// <summary>The three settings of the module's loop, then the time they take to reach it.</summary>
    private async Task ApplyAsync(int kp, int maxPwm, int minPwm, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var a = Config;
        bool changed = a.ProportionalGain != kp || a.MaxPwm != maxPwm || a.MinPwm != minPwm;
        a.ProportionalGain = kp;
        a.MaxPwm = maxPwm;
        a.MinPwm = minPwm;
        if (!changed)
            return;
        SettingsWrites++;
        await WaitAsync(SettingsSettleMs, token);
    }

    /// <summary>
    /// Wait one sample, read the module, and stop the test when anything is not as it must be:
    /// the module no longer steering (switch, kickout), the wheels outside their window, the
    /// tractor too fast.
    /// </summary>
    private async Task<Sample> SampleAsync(CancellationToken token, int ms = SampleMs)
    {
        await WaitAsync(ms, token);
        var d = CurrentModuleData;
        LiveSteerAngle = Math.Round(d.ActualSteerAngle, 1);
        if (_armed && (_stopLatched || d.SteerSwitchActive))
            throw new TuneAbort(ModuleStoppedText);
        if (Math.Abs(d.ActualSteerAngle - _anchor) > WindowDeg)
            throw new TuneAbort(string.Create(Inv,
                $"The wheels went more than {WindowDeg:F0}° from where they started, so the test stopped. Check the wheel angle sensor and the motor direction; a valve that goes on turning long after it is let go does this too."));
        if (_speedKmh > MaxTestSpeedKmh)
            throw new TuneAbort("Too fast for this test, so it stopped: keep to walking pace.");
        return new Sample(NowMs(), d.ActualSteerAngle, d.PwmDisplay);
    }

    // ============================================================ lifecycle

    protected override void OnEntering()
    {
        Stage = TuneStage.Idle;
        PhaseDescription = "";
        PhaseResult = "";
        SubscribeToSwitchGate();
        if (AutoSteerService != null)
            AutoSteerService.StateUpdated += OnStateUpdated;
    }

    protected override void OnLeaving()
    {
        // Cancel a running part and make sure Free Drive is off; its own clean-up puts the
        // operator's values back. Values found and not taken are dropped.
        StopFreeDriveTest();
        UnsubscribeFromSwitchGate();
        if (AutoSteerService != null)
            AutoSteerService.StateUpdated -= OnStateUpdated;
        if (Stage is TuneStage.StopChecked or TuneStage.Result)
            Stage = TuneStage.Idle;
    }

    private void OnStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        // Control-loop thread, 100 Hz: a kickout with a steer switch lasts one module loop
        // (25 ms) and would slip between two samples, so it is latched here.
        if (AutoSteerService == null)
            return;
        var d = CurrentModuleData;
        if (_armed && d.SteerSwitchActive)
            _stopLatched = true;
        if (_holdWatch && Math.Abs(d.ActualSteerAngle - _holdAt) > HoldDriftDeg)
        {
            // Module off from here, not a sample later: the wheels are running.
            _holdWatch = false;
            _holdTripped = true;
            StopFreeDriveTest();
        }
        _speedKmh = snapshot.SpeedKmh;
        if (!IsMeasuring)
            LiveSteerAngle = Math.Round(d.ActualSteerAngle, 1);
    }
}
