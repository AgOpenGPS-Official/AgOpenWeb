// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models.Pipeline;

namespace AgOpenWeb.ViewModels.LineTune;

/// <summary>Where a tuning run on the line is.</summary>
public enum LineTuneStage
{
    Idle = 0,
    /// <summary>Started, waiting for AutoSteer on a straight stretch at a steady speed.</summary>
    Waiting = 1,
    /// <summary>Trying a value: holding the line, then moving it out and back.</summary>
    Measuring = 2,
    /// <summary>Done: a value found, not yet taken.</summary>
    Result = 3,
    /// <summary>The value found is in the settings.</summary>
    Accepted = 4,
    /// <summary>Stopped by the operator or by something that ends the run.</summary>
    Stopped = 5,
    /// <summary>No value tried held the line well.</summary>
    Failed = 6,
}

/// <summary>One GPS cycle as the tuning run sees it (10 Hz, on the host loop).</summary>
/// <param name="TimeS">Seconds on any steady clock.</param>
/// <param name="Engaged">AutoSteer is engaged.</param>
/// <param name="Xte">Distance from the line steered (m), the guidance's cross-track error.</param>
/// <param name="NudgeOffset">The line's nudge (m), as the cycle holds it.</param>
/// <param name="HeadingSameWay">Driving the track's way (the cycle turns a nudge's sign with it).</param>
/// <param name="Track">The track steered (compared by reference) and <paramref name="Pass"/> the pass on it.</param>
/// <param name="Turning">A U-turn is being driven.</param>
/// <param name="RoomAheadM">Line left before the headland (m); <see cref="double.PositiveInfinity"/> when not known.</param>
/// <param name="FixOk">A position good enough to measure centimetres (RTK fixed, or the simulator).</param>
public readonly record struct LineTuneSample(
    double TimeS, bool Engaged, double SpeedKmh, double Xte, double NudgeOffset, bool HeadingSameWay,
    object? Track, int Pass, bool Turning, double RoomAheadM, bool FixOk, bool Reverse, bool Contour);

/// <summary>What the run does to the machine: move the line, and steer by other gains.</summary>
public interface ILineTuneActions
{
    /// <summary>Move the line (m, + right from the driver's seat), as the nudge buttons do.</summary>
    void Nudge(double meters);

    /// <summary>Steer by these gains from the next cycle; <c>null</c> = the operator's settings.</summary>
    void SetGains(GuidanceGainsOverride? gains);
}

/// <summary>How the steering answered one move of the line.</summary>
/// <param name="SizeM">How far the line moved, as the cross-track error jumped (m).</param>
/// <param name="RiseS">Seconds until 90 % of the way to the new line.</param>
/// <param name="OvershootM">How far past the new line it went (m).</param>
/// <param name="SettleS">Seconds until it stayed within the band of the new line.</param>
/// <param name="Swings">Times it swung from one side of the new line to the other, out of the band.</param>
/// <param name="OffsetM">Mean distance from the line once settled (m).</param>
public sealed record LineTuneStep(double SizeM, double RiseS, double OvershootM, double SettleS, int Swings,
    double OffsetM, bool Settled, bool Cut);

/// <summary>One value tried: the line moved out and back.</summary>
public sealed class LineTuneLevel
{
    public required GuidanceGainsOverride Gains { get; init; }
    /// <summary>The value the run tunes at this level (Steer Response, or the Stanley distance gain).</summary>
    public required double Value { get; init; }
    /// <summary>A Stanley heading gain trial (after the distance gain is chosen).</summary>
    public bool HeadingTrial { get; init; }
    public LineTuneStep? Out { get; set; }
    public LineTuneStep? Back { get; set; }
    /// <summary>The line could not be held before the move, or the run cut the level short.</summary>
    public string? Failure { get; set; }

    public bool Done => Failure != null || (Out != null && Back != null);
    public double SettleS => Math.Max(Out?.SettleS ?? double.PositiveInfinity, Back?.SettleS ?? double.PositiveInfinity);
    public double OvershootM => Math.Max(Out?.OvershootM ?? 0, Back?.OvershootM ?? 0);
    public int Swings => Math.Max(Out?.Swings ?? 0, Back?.Swings ?? 0);

    /// <summary>Back on the line both ways, little past it, at most one swing.</summary>
    public bool Good(double stepM) =>
        Failure == null && Out is { Settled: true, Cut: false } && Back is { Settled: true, Cut: false }
        && OvershootM <= LineTuneRun.GoodOvershootShare * stepM && Swings <= 1;

    /// <summary>Back on the line both ways without swinging on, however far past it: when no
    /// value is <see cref="Good"/>, the best of these is still an answer.</summary>
    public bool CameBack =>
        Failure == null && Out is { Settled: true, Cut: false } && Back is { Settled: true, Cut: false } && Swings <= 1;

    /// <summary>Not good only for being slow: it crept towards the new line and never got
    /// there in time, without swinging or going past it. A quicker value is the cure.</summary>
    public bool Sluggish(double stepM) =>
        Failure == LineTuneRun.SlowFailure && Swings == 0 && OvershootM <= LineTuneRun.GoodOvershootShare * stepM;
}

/// <summary>
/// Tuning on the line: the guidance's own gains, with the module's steering loop already set
/// (Steer Wizard). With AutoSteer holding an AB line or a
/// curve, the run moves the line <see cref="StepM"/> to the right and back, as the nudge
/// buttons do, and measures how the tractor gets onto the new line: how long it takes, how far
/// past it goes, how often it swings. It does that for a few values of the one setting that
/// sets how hard the guidance pulls to the line, Steer Response (Pure Pursuit's hold
/// look-ahead) or, with Stanley, its distance gain, going from the operator's value towards
/// the quicker side until a value swings or is no quicker, or towards the calmer side when the
/// operator's own value swings. The value it proposes is the calmest that is about as quick as
/// the quickest good one. With Stanley, a value that still goes past the line is then tried with
/// more heading gain ("overshoot reduction").
/// <para>Safety: the values tried steer only through a gains override, never the settings,
/// and the run takes them away (the operator's values steer again) at once when a move runs
/// away (the tractor further from the new line than it started, or three swings), when
/// AutoSteer goes off, the speed changes, a turn comes, the line is changed or moved by hand,
/// or the fix is lost; a move it started is moved back at once. It never engages AutoSteer.</para>
/// <para>On a tractor (17 and 18 Nov 2026, 3 to 6 km/h): no value starts while the tractor is
/// still on its way to the line, however early Start was pressed (<see cref="JoinNearM"/>,
/// <see cref="JoinHoldS"/>; a value put in while AutoSteer was still joining the line was blamed
/// for it), every wait is as long in metres at any speed (<see cref="TimeFactor"/>; in seconds,
/// nothing came back in time at walking pace), the speed is a mean of <see cref="SpeedMeanS"/>
/// (one reading dropped a value), and the calm value after a cut has only to stop the swing,
/// not reach the line. A tractor with slack in its steering rests a few centimetres from where
/// it was before a move: when the time is up and it rests steady near the line, the move
/// counts, measured against where it rests. When no value comes back cleanly, the one that
/// went past the line the least is proposed, and the result says so. The run measures at the
/// speed driven and says which; <see cref="Log"/> says what it did, for the app's log.</para>
/// <para>Driven by <see cref="OnSample"/> once per GPS cycle; no timers, so tests feed a
/// simulated tractor.</para>
/// </summary>
public sealed class LineTuneRun
{
    // ------------------------------------------------------------ limits

    /// <summary>How far the line is moved each time (m): a move the operator sees, far enough to measure.</summary>
    public const double StepM = 0.30;
    /// <summary>Speed to start: walking pace is too slow to steer by look-ahead, above this the field is too short.</summary>
    public const double MinSpeedKmh = 3.0;
    public const double MaxSpeedKmh = 20.0;
    /// <summary>The speed the waits below were made at (km/h). Slower, each of them is stretched to the
    /// same metres driven (<see cref="TimeFactor"/>): the guidance takes about as far, not as
    /// long, to come onto a line, so at walking pace no value came back in time.</summary>
    public const double FullSpeedKmh = 8.0;
    /// <summary>The speed looked at is the mean of this many seconds: a receiver's speed jumps
    /// from one reading to the next (a tractor at a steady 6 km/h: 5.0 to 7.3 km/h, 5.7 to 6.5 as
    /// the mean of a second), and one reading out of the band dropped a value.</summary>
    public const double SpeedMeanS = 1.0;
    /// <summary>Within this of the line (m) the tractor has reached it. Further off it is still on
    /// its way there, and no value starts, however long that takes and whenever Start was pressed.</summary>
    public const double JoinNearM = 0.5;
    /// <summary>Seconds the operator's own values hold the line before a value starts: twice a
    /// value's own hold (<see cref="PreHoldS"/>). A tractor still creeping onto the line passed
    /// the short one, and where it was then was taken for where it rests: the first value, back
    /// on the line in 3 s, was cut for going 15 cm past a line it was 10 cm short of.</summary>
    public const double JoinHoldS = 4.0;
    /// <summary>Seconds the operator's own values have to hold the line once the tractor has
    /// reached it (<see cref="JoinNearM"/>), before a value starts: the first one, and after
    /// every interruption. Longer, the value starts all the same and is judged for it: values
    /// that swing about a line are what the run is for.</summary>
    public const double JoinTimeoutS = 30.0;
    /// <summary>The operator's own values took the tractor this far (m) out on one side of the line,
    /// then on the other, <see cref="JoinSwingTurns"/> times running: they swing it, and waiting
    /// longer lets the swing grow. The calm value steers first. Once is not a swing: a tractor
    /// that joins the line at an angle goes past it once (one went 1.95 m past, engaged in a
    /// turn), and nothing is tried on a tractor still on its way to the line.</summary>
    public const double JoinSwingM = SteadyMaxM + RunawayM;
    public const int JoinSwingTurns = 2;
    /// <summary>Speed may differ from the run's by this much (km/h, or 15 % if more).</summary>
    public const double SpeedBandKmh = 1.0;
    /// <summary>Seconds on the line, within <see cref="SteadyBandM"/>, before a move.</summary>
    public const double PreHoldS = 2.0;
    /// <summary>Held: every sample this near the mean, and the two halves' means this near each other (not drifting).</summary>
    public const double SteadyBandM = 0.04;
    public const double SteadyDriftM = 0.02;
    /// <summary>Farther from the line than this, it is not held before a move.</summary>
    public const double SteadyMaxM = 0.10;
    /// <summary>A value that can't hold the line this long after it is put in is a bad value.</summary>
    public const double SteadyTimeoutS = 12.0;
    /// <summary>Seconds within the band of the new line that count as settled.</summary>
    public const double HoldS = 2.0;
    /// <summary>Seconds a move may take to settle; longer is a bad value.</summary>
    public const double MaxStepS = 15.0;
    /// <summary>The band round the new line (m, or 10 % of the move if more).</summary>
    public const double BandM = 0.04;
    /// <summary>Farther than the move plus this from the new line, the move runs away: cut.</summary>
    public const double RunawayM = 0.25;
    /// <summary>Past the new line by this share of the move, the move is cut.</summary>
    public const double OvershootCutShare = 0.5;
    /// <summary>Swings that cut a move.</summary>
    public const int CutSwings = 3;
    /// <summary>A good value goes no further past the line than this share of the move.</summary>
    public const double GoodOvershootShare = 0.20;
    /// <summary>The value chosen settles no slower than the quickest good one by this share, plus <see cref="ChoiceSlackS"/>.</summary>
    public const double ChoiceShare = 1.15;
    public const double ChoiceSlackS = 0.3;
    /// <summary>A value must be this much quicker than the best so far for the run to go on to a quicker one.</summary>
    public const double QuickerShare = 0.95;
    /// <summary>After a value that swung or ran away, the tractor is brought back to the line with a
    /// calm value: with Stanley the distance gain divided by this (not under
    /// <see cref="RescueMinStanley"/>, and never quicker than the operator's own, one under that
    /// floor too); with Pure Pursuit the calmest Steer Response of the ladder.</summary>
    public const double RescueShare = 2.5;
    public const double RescueMinStanley = 0.3;
    /// <summary>When no value is good, values that went this much (m) further past the line than
    /// the one that went past it the least count as the same: the quickest of them is taken.</summary>
    public const double LeastPastSlackM = 0.02;
    public const double MaxHold = 10.0;
    /// <summary>Seconds the calm value has to bring the tractor back onto the line; longer stops the run.</summary>
    public const double RecoverTimeoutS = 30.0;
    /// <summary>The calm value may let a swinging tractor go this much further than where it took
    /// over (m): it was heading away, and a calm value turns it slowly. A swing wider than the one
    /// before it ends the run sooner: the calm value is not calming it.</summary>
    public const double RecoverRunawayM = 2.0;
    /// <summary>How long a move is remembered for where the tractor is swinging (s).</summary>
    public const double RecentS = 3.0;
    internal const string SlowFailure = "too slow to get there";
    internal const string RanAway = "ran away from the line";
    /// <summary>Values tried in one run, at most.</summary>
    public const int MaxLevels = 8;
    /// <summary>Line needed ahead to start a value: this many seconds of driving plus <see cref="RoomMarginM"/>.</summary>
    public const double RoomSeconds = 20.0;
    public const double RoomMarginM = 15.0;
    /// <summary>A value is dropped when the headland is nearer than this.</summary>
    public const double RoomStopM = 10.0;
    /// <summary>Seconds after a turn before a value starts (the pass moves on as the turn ends).</summary>
    public const double AfterTurnS = 3.0;
    /// <summary>Seconds a requested move may take to show in the cycle.</summary>
    public const double NudgeTimeoutS = 1.5;
    /// <summary>With Stanley, a chosen value going further past the line than this share of the move gets heading gain trials.</summary>
    public const double HeadingTrialShare = 0.05;
    /// <summary>The settled offset that gets advice (m).</summary>
    public const double OffsetAdviceM = 0.03;

    /// <summary>Steer Response (hold look-ahead, m) tried, calm to quick.</summary>
    public static readonly double[] HoldLadder = [8.0, 6.5, 5.3, 4.4, 3.6, 3.0, 2.5, 2.1, 1.8, 1.5, 1.25, 1.0];
    /// <summary>Stanley distance gain tried, calm to quick.</summary>
    public static readonly double[] StanleyLadder = [0.2, 0.3, 0.4, 0.5, 0.6, 0.8, 1.0, 1.25, 1.6, 2.0, 2.5, 3.2, 4.0];
    /// <summary>Stanley heading gains tried after the distance gain, as shares of the operator's.</summary>
    public static readonly double[] HeadingTrials = [1.5, 2.0];
    public const double MaxHeadingGain = 4.0;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------ set up

    private readonly ILineTuneActions _act;
    private readonly GuidanceGainsOverride _operator;
    private readonly double _mult, _acquire, _minLookAhead;
    private readonly double[] _ladder;
    private readonly int _startIndex;
    private readonly Dictionary<int, LineTuneLevel> _byIndex = new();

    public bool Stanley { get; }
    public LineTuneStage Stage { get; private set; } = LineTuneStage.Idle;
    /// <summary>What is happening now, for the page.</summary>
    public string Description { get; private set; } = "";
    /// <summary>The outcome, for the page ("" until there is one).</summary>
    public string ResultText { get; private set; } = "";
    /// <summary>Every value tried, one line each.</summary>
    public string Measurements => BuildMeasurements();
    /// <summary>The values tried, in order.</summary>
    public List<LineTuneLevel> Levels { get; } = new();
    /// <summary>The gains found (the operator's where nothing better was found).</summary>
    public GuidanceGainsOverride? Found { get; private set; }
    /// <summary>The speed the run measures at (km/h; 0 until it starts measuring).</summary>
    public double RunSpeedKmh { get; private set; }
    /// <summary>What the run does, one line each (a value started, how a move went, why it waits
    /// or ended): the app writes them to its log, so a bug report shows the run.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>How much longer every wait is than at <see cref="FullSpeedKmh"/>: 1 from there up.</summary>
    public double TimeFactor => Stretch(RunSpeedKmh > 0 ? RunSpeedKmh : _speedMean);

    internal static double Stretch(double kmh) => kmh > 0 ? Math.Max(1.0, FullSpeedKmh / kmh) : 1.0;

    private void Note(string what) => Log?.Invoke(what);

    /// <param name="operatorGains">The operator's Steer Response and Stanley gains.</param>
    /// <param name="stanley">Stanley steers; else Pure Pursuit.</param>
    /// <param name="speedFactor">Look-ahead speed factor (GoalPointLookAheadMult), to skip values the minimum look-ahead makes the same.</param>
    public LineTuneRun(ILineTuneActions actions, GuidanceGainsOverride operatorGains, bool stanley,
        double speedFactor, double acquireFactor, double minLookAhead)
    {
        _act = actions;
        _operator = operatorGains;
        Stanley = stanley;
        _mult = speedFactor;
        _acquire = acquireFactor;
        _minLookAhead = minLookAhead;
        // The operator's own value takes the place of the nearest one on the ladder (the order
        // holds: it lies between that one's neighbours), so the run measures it first and the
        // result compares with what the tractor does today.
        _ladder = (double[])(stanley ? StanleyLadder : HoldLadder).Clone();
        double own = stanley ? operatorGains.StanleyDistanceGain : operatorGains.LookAheadHold;
        _startIndex = NearestIndex(own);
        if (own > 0)
            _ladder[_startIndex] = own;
    }

    // ------------------------------------------------------------ the run's state

    private enum Phase { Settle, AwaitNudge, Track, Recover }

    private Phase _phase;
    private int _index;               // ladder index of the value tried
    private int _headingTrial = -1;   // >= 0: a Stanley heading trial
    private LineTuneLevel? _level;
    private int _stepNo;              // 0 out, 1 back
    // The line's nudge as this run expects the cycle to show it, NaN before the first sample.
    // Moves are kept in the cycle's own frame (it turns a nudge's sign with the driving
    // direction), so a move back is asked with the sign the cycle uses at that moment.
    private double _expectNudge = double.NaN;
    private double _outstanding;      // this run's moves not moved back (cycle frame)
    private double _requestAt;
    private double _movingBackUntil;  // a move back asked: the cycle shows it by then
    private double _pendingShift;     // the move asked last (cycle frame): the line shows it or not yet
    private object? _track;
    private int _pass;
    private double _phaseStart;
    private readonly List<(double T, double E)> _window = new();
    private double _baseline;
    private double _entryOff;         // distance from the line when the value went in
    private bool _concludePending;    // the run is done once the tractor is back on the line
    private int _recSide;
    private double _recPeak, _recLastPeak;
    private readonly List<(double T, double E)> _recent = new();

    private double RecentMaxOff(in LineTuneSample s)
    {
        double m = Math.Abs(s.Xte);
        foreach (var r in _recent) m = Math.Max(m, Math.Abs(r.E));
        return m;
    }
    // the move being measured
    private double _t0, _e1, _risenAt, _lastOut, _overshoot;
    private int _swings, _lastSide;
    private bool _risen;
    private readonly List<(double T, double E)> _hold = new();
    private readonly List<(double T, double E)> _move = new();   // a move, from the cycle the line jumped
    private string _interrupt = "";
    private readonly List<(double T, double Kmh)> _speeds = new();
    private double _speedMean;
    private double _joinStart = double.NaN;   // since when the run waits for the operator's values to hold the line
    private double _nearSince = double.NaN;   // since when, in that wait, the tractor has been at the line
    private int _joinSide;                    // the side of the line it was last well out on, in that wait (0: none yet)
    private int _joinTurns;                   // how often it then went well out on the other side
    private bool _again;                      // a value was dropped and starts again once the line is held

    /// <summary>Start (again): waits for AutoSteer on a line, then tries the operator's value first.</summary>
    public void Start()
    {
        Levels.Clear();
        _byIndex.Clear();
        Found = null;
        ResultText = "";
        RunSpeedKmh = 0;
        _headingTrial = -1;
        _index = _startIndex;
        _level = null;
        _outstanding = 0;
        _expectNudge = double.NaN;
        _movingBackUntil = 0;
        _pendingShift = 0;
        _concludePending = false;
        _lastTurningAt = double.NegativeInfinity;
        _interrupt = "";
        _again = false;
        _joinStart = double.NaN;
        _speeds.Clear();
        _speedMean = 0;
        Stage = LineTuneStage.Waiting;
        Description = "Drive on a long straight line with AutoSteer on, at the speed you work at.";
        Note(Stanley
            ? string.Create(Inv, $"Started: Stanley, Aggressiveness {_operator.StanleyDistanceGain:0.0#}, Overshoot Reduction {_operator.StanleyHeadingGain:0.0#}.")
            : string.Create(Inv, $"Started: Pure Pursuit, Steer Response {_operator.LookAheadHold:0.0#}."));
    }

    /// <summary>Stop: the operator's values steer again, a move started is moved back.</summary>
    public void Stop(string why)
    {
        if (Stage is not (LineTuneStage.Waiting or LineTuneStage.Measuring))
            return;
        _act.SetGains(null);
        MoveBack(_lastSameWay, _lastTime);
        Stage = LineTuneStage.Stopped;
        Description = "";
        ResultText = why + " Nothing was changed: your own values steer.";
        NoteEnd();
    }

    private void NoteEnd()
    {
        Note("Ended (" + Stage + "): " + ResultText);
        string m = Measurements;
        if (m.Length > 0)
            Note("Values tried: " + m.Replace("\n", "; "));
    }

    private bool _lastSameWay = true;
    private double _lastTime;
    private double _lastTurningAt = double.NegativeInfinity;

    /// <summary>The value found was taken (or not): the page shows that.</summary>
    public void MarkAccepted(string text)
    {
        Stage = LineTuneStage.Accepted;
        ResultText = text;
        Note(text);
    }

    /// <summary>The value found no longer fits what it was measured with (a steering setting
    /// changed, another vehicle loaded): it can't be taken any more.</summary>
    public void DropResult(string why)
    {
        if (Stage != LineTuneStage.Result)
            return;
        Found = null;
        Stage = LineTuneStage.Stopped;
        Description = "";
        ResultText = why + " The value found was not used: run it again.";
        Note(ResultText);
    }

    public bool Active => Stage is LineTuneStage.Waiting or LineTuneStage.Measuring;

    // ------------------------------------------------------------ each cycle

    public void OnSample(in LineTuneSample s)
    {
        if (!Active)
            return;
        _recent.Add((s.TimeS, s.Xte));
        while (_recent.Count > 0 && _recent[0].T < s.TimeS - RecentS)
            _recent.RemoveAt(0);
        _lastSameWay = s.HeadingSameWay;
        _lastTime = s.TimeS;
        _speeds.Add((s.TimeS, s.SpeedKmh));
        while (_speeds.Count > 1 && _speeds[0].T <= s.TimeS - SpeedMeanS)
            _speeds.RemoveAt(0);
        double sum = 0;
        foreach (var v in _speeds) sum += v.Kmh;
        _speedMean = sum / _speeds.Count;

        // The line moved by someone else (the nudge buttons, a snap, the nudge reset), or another
        // line chosen: the operator has taken the line over, and the run ends.
        if (!double.IsNaN(_expectNudge))
        {
            // A move of this run on its way shows as the line where it was before the move, then
            // where the run expects it. Anywhere else, the operator moved the line in that moment
            // too (a nudge button, the reset): taken as the run's own, the move back shifted his line.
            bool pending = Stage == LineTuneStage.Measuring && _phase == Phase.AwaitNudge || s.TimeS < _movingBackUntil;
            bool there = Math.Abs(s.NudgeOffset - _expectNudge) <= 0.005;
            bool ours = pending && Math.Abs(s.NudgeOffset - (_expectNudge - _pendingShift)) <= 0.005;
            if (there)
                _movingBackUntil = 0;
            else if (!ours)
            {
                HandTookTheLine(s, "The line was moved by hand.");
                return;
            }
        }
        if (s.Turning)
            _lastTurningAt = s.TimeS;
        if (Stage == LineTuneStage.Measuring && !s.Turning && (!ReferenceEquals(s.Track, _track) || s.Pass != _pass))
        {
            if (Math.Abs(_outstanding) > 1e-9)
                HandTookTheLine(s, "Another line was chosen.");
            else
                Interrupt("Another line is steered.", s);
            return;
        }

        string? why = WhyNot(s);
        if (Stage == LineTuneStage.Waiting)
        {
            // Another pass or line: the distance from it jumps, and the wait for it starts anew
            // (a tractor that went 2 m past the line it was joining is steered onto the next pass).
            if (!ReferenceEquals(s.Track, _track) || s.Pass != _pass)
                _joinStart = double.NaN;
            _track = s.Track;
            _pass = s.Pass;
            if (s.TimeS < _movingBackUntil)
            {
                Description = "Moving the line back.";
                return;
            }
            _expectNudge = s.NudgeOffset;
            if (why == null && s.TimeS - _lastTurningAt < AfterTurnS)
                why = "Waiting for the turn to end.";
            if (why != null)
            {
                _joinStart = double.NaN;
                Description = WaitText(why);
                return;
            }
            if (s.RoomAheadM < RoomNeeded(_speedMean))
            {
                _joinStart = double.NaN;
                Description = WaitText("Not enough line before the headland: go on, on the next pass.");
                return;
            }
            if (_concludePending)
            {
                Conclude(s);
                return;
            }
            // The operator's own values bring the tractor onto the line first. AutoSteer just
            // turned on, or a pass just joined, is still on its way to the line: a value put in
            // then was judged by that ("could not hold the line", on a tractor).
            var join = JoinCheck(s);
            if (join == Join.Wait)
            {
                Description = (_interrupt.Length > 0 ? _interrupt + " " : "")
                    + string.Create(Inv, $"Waiting for AutoSteer to hold the line (now {Math.Abs(s.Xte):0.00} m from it).")
                    + (RunSpeedKmh == 0
                        ? string.Create(Inv, $" It measures at the speed you drive now, {_speedMean:0.0} km/h: drive at the speed you work at.")
                        : _again ? " Then the value starts again." : "");
                return;
            }
            if (RunSpeedKmh == 0)
                RunSpeedKmh = Math.Round(_speedMean, 1);
            _interrupt = "";
            _again = false;
            _joinStart = double.NaN;
            if (join == Join.Swings)
                StartRecover(s, null);
            else
                BeginLevel(s, joined: true);
            return;
        }

        // Measuring
        if (why != null)
        {
            Interrupt(why, s, lasting: true);
            return;
        }
        if (s.RoomAheadM < RoomStopM)
        {
            Interrupt("The headland is near.", s, lasting: true);
            return;
        }

        switch (_phase)
        {
            case Phase.Settle: Settle(s); break;
            case Phase.AwaitNudge: AwaitNudge(s); break;
            case Phase.Track: TrackMove(s); break;
            case Phase.Recover: Recover(s); break;
        }
    }

    /// <summary>The operator moved the line or chose another: the run ends. Its own move is moved
    /// back only while the line still carries it (a snap or the nudge reset already took it away),
    /// and never on another track: a track has its own nudge, and the run's move is not in it.</summary>
    private void HandTookTheLine(in LineTuneSample s, string why)
    {
        _act.SetGains(null);
        bool sameTrack = ReferenceEquals(s.Track, _track);
        bool stillCarried = Math.Abs(_outstanding) > 1e-9 && sameTrack && Math.Abs(s.NudgeOffset) > 0.005
            && (s.Pass != _pass || Math.Abs(s.NudgeOffset - _expectNudge) > 0.005);
        if (stillCarried)
            MoveBack(s.HeadingSameWay, s.TimeS);
        _outstanding = 0;
        Stage = LineTuneStage.Stopped;
        Description = "";
        ResultText = why + " The run stopped; nothing was changed: your own values steer.";
        NoteEnd();
    }

    /// <summary>Why a value can't be tried now, or null.</summary>
    private string? WhyNot(in LineTuneSample s)
    {
        if (!s.Engaged) return "Turn AutoSteer on, on a long straight line.";
        if (s.Contour) return "Contour guidance: use an AB line or a curve.";
        if (s.Reverse) return "Drive forward.";
        if (!s.FixOk) return "Waiting for an RTK fix.";
        if (s.Turning) return "Waiting for the turn to end.";
        if (RunSpeedKmh == 0)
        {
            if (_speedMean < MinSpeedKmh || _speedMean > MaxSpeedKmh)
                return string.Create(Inv, $"Drive at the speed you work at, {MinSpeedKmh:0} to {MaxSpeedKmh:0} km/h (now {_speedMean:0.0}).");
        }
        else if (Math.Abs(_speedMean - RunSpeedKmh) > SpeedBand)
            return string.Create(Inv, $"Drive at about {RunSpeedKmh:0.0} km/h, as at the start (now {_speedMean:0.0}).");
        return null;
    }

    private double SpeedBand => Math.Max(SpeedBandKmh, 0.15 * RunSpeedKmh);

    /// <summary>Line needed ahead to start a value: <see cref="RoomSeconds"/> of driving, as far at
    /// any speed below <see cref="FullSpeedKmh"/> as at it, plus <see cref="RoomMarginM"/>.</summary>
    internal static double RoomNeeded(double speedKmh) => speedKmh / 3.6 * RoomSeconds * Stretch(speedKmh) + RoomMarginM;

    private enum Join { Wait, Held, Swings }

    /// <summary>Whether a value can start: the operator's own values hold the line. Never while
    /// the tractor is still on its way to the line, whenever Start was pressed; at the line they
    /// have <see cref="JoinTimeoutS"/> to hold it; and when they keep swinging the tractor across
    /// it (<see cref="JoinSwingM"/>, <see cref="JoinSwingTurns"/>) the calm value steers first.</summary>
    private Join JoinCheck(in LineTuneSample s)
    {
        if (double.IsNaN(_joinStart))
        {
            _joinStart = s.TimeS;
            _nearSince = double.NaN;
            _joinSide = 0;
            _joinTurns = 0;
            _phaseStart = s.TimeS;
            _window.Clear();
            Note(string.Create(Inv, $"Waiting for AutoSteer to hold the line: {s.Xte:+0.00;-0.00;0.00} m from it, {_speedMean:0.0} km/h."));
        }
        if (Steady(s, JoinHoldS, SteadyMaxM, out _))
        {
            // The operator's own value, tried next, has held the line already: its move starts
            // at once, and one reading out of the band does not fail it for the whole wait.
            _phaseStart = s.TimeS - PreHoldS * TimeFactor;
            return Join.Held;
        }
        // Well out on one side, then the other, and back again: the operator's values swing it.
        int side = Math.Abs(s.Xte) > JoinSwingM ? Math.Sign(s.Xte) : 0;
        if (side != 0 && side != _joinSide)
        {
            if (_joinSide != 0)
                _joinTurns++;
            _joinSide = side;
            if (_joinTurns >= JoinSwingTurns)
                return Join.Swings;
        }
        if (double.IsNaN(_nearSince))
        {
            if (Math.Abs(s.Xte) > JoinNearM)
                return Join.Wait;
            _nearSince = s.TimeS;
        }
        if (s.TimeS - _nearSince <= JoinTimeoutS * TimeFactor)
            return Join.Wait;
        if (Math.Abs(s.Xte) > JoinNearM)
        {
            // Away again, without a swing: it has to reach the line anew.
            _nearSince = double.NaN;
            return Join.Wait;
        }
        Note(string.Create(Inv, $"Your own values did not hold the line in {JoinTimeoutS * TimeFactor:0} s at it ({Math.Abs(s.Xte):0.00} m from it now): the value starts all the same."));
        return Join.Held;
    }

    // ------------------------------------------------------------ a value

    private void BeginLevel(in LineTuneSample s, bool joined = false)
    {
        if (_level == null || _level.Done)
        {
            var gains = GainsFor(_index, _headingTrial);
            _level = new LineTuneLevel
            {
                Gains = gains,
                Value = _headingTrial >= 0 ? gains.StanleyHeadingGain : _ladder[_index],
                HeadingTrial = _headingTrial >= 0,
            };
        }
        else
        {
            // The same value again after an interruption: from the start.
            _level.Out = null;
            _level.Back = null;
        }
        _act.SetGains(_level.Gains);
        Stage = LineTuneStage.Measuring;
        _phase = Phase.Settle;
        // The operator's own value straight after it held the line: that hold is this value's
        // (and a line it did not hold in time is its failure, at once).
        if (!(joined && _level.Gains == _operator))
        {
            _phaseStart = s.TimeS;
            _window.Clear();
        }
        // Where it is now, or swinging to, after a value that was cut.
        _entryOff = RecentMaxOff(s);
        _stepNo = 0;
        Describe("holding the line");
        Note(string.Create(Inv, $"{Description} {_speedMean:0.0} km/h, {s.Xte:+0.00;-0.00;0.00} m from the line."));
    }

    private GuidanceGainsOverride GainsFor(int index, int headingTrial)
    {
        if (headingTrial >= 0)
        {
            var chosen = Found ?? _operator;
            return chosen with { StanleyHeadingGain = Math.Min(MaxHeadingGain, _operator.StanleyHeadingGain * HeadingTrials[headingTrial]) };
        }
        return Stanley
            ? _operator with { StanleyDistanceGain = _ladder[index] }
            : _operator with { LookAheadHold = _ladder[index] };
    }

    private void Settle(in LineTuneSample s)
    {
        // A value that takes the tractor further from the line than it found it is cut at once.
        if (Math.Abs(s.Xte) > Math.Max(_entryOff, SteadyMaxM) + RunawayM)
        {
            _level!.Failure = RanAway;
            _act.SetGains(null);
            EndLevel(s);
            return;
        }
        if (Held(s, out double mean))
        {
            _baseline = mean;
            RequestMove(s, +StepM);
            return;
        }
        if (s.TimeS - _phaseStart > SteadyTimeoutS * TimeFactor)
        {
            _level!.Failure = "could not hold the line";
            // The operator's values steer the tractor back to the line before the next value.
            _act.SetGains(null);
            EndLevel(s);
        }
    }

    /// <summary>The tractor has held the line for <see cref="PreHoldS"/>: near it, not swinging, not drifting.</summary>
    private bool Held(in LineTuneSample s, out double mean) => Steady(s, PreHoldS, SteadyMaxM, out mean);

    /// <summary>The tractor has run steady for <paramref name="holdS"/> (stretched as every wait:
    /// not swinging, not drifting), no further than <paramref name="maxFromLine"/> from the line.</summary>
    private bool Steady(in LineTuneSample s, double holdS, double maxFromLine, out double mean)
    {
        double hold = holdS * TimeFactor;
        _window.Add((s.TimeS, s.Xte));
        while (_window.Count > 0 && _window[0].T < s.TimeS - hold)
            _window.RemoveAt(0);
        mean = 0;
        bool full = _window.Count > 0 && s.TimeS - _window[0].T >= hold - 0.15 && s.TimeS - _phaseStart >= hold;
        return full && IsSteady(_window, 0, SteadyBandM, out mean) && Math.Abs(mean) <= maxFromLine;
    }

    /// <summary>Every reading from <paramref name="from"/> on within <paramref name="band"/> of
    /// their mean, and the two halves' means within <see cref="SteadyDriftM"/> of each other.</summary>
    private static bool IsSteady(List<(double T, double E)> w, int from, double band, out double mean)
    {
        mean = 0;
        int n = w.Count - from;
        if (n <= 0)
            return false;
        for (int i = from; i < w.Count; i++) mean += w[i].E;
        mean /= n;
        double first = 0, second = 0;
        int half = n / 2;
        for (int i = from; i < w.Count; i++)
        {
            if (Math.Abs(w[i].E - mean) > band) return false;
            if (i - from < half) first += w[i].E; else second += w[i].E;
        }
        return half == 0 || Math.Abs(first / half - second / (n - half)) <= SteadyDriftM;
    }

    private void RequestMove(in LineTuneSample s, double meters)
    {
        double shift = s.HeadingSameWay ? meters : -meters;   // as the cycle will apply it
        _requestAt = s.TimeS;
        _outstanding += shift;
        _expectNudge += shift;
        _pendingShift = shift;
        _act.Nudge(meters);
        _phase = Phase.AwaitNudge;
        Describe(_stepNo == 0 ? "line moved 30 cm to the right" : "line moved back");
    }

    private void AwaitNudge(in LineTuneSample s)
    {
        if (Math.Abs(s.NudgeOffset - _expectNudge) <= 0.005)
        {
            _phase = Phase.Track;
            _t0 = s.TimeS;
            _e1 = s.Xte;
            _risen = false;
            _risenAt = 0;
            _overshoot = 0;
            _swings = 0;
            _lastSide = 1;
            _lastOut = s.TimeS;
            _hold.Clear();
            _move.Clear();
            if (Math.Abs(_e1 - _baseline) < 0.5 * StepM)
            {
                // The distance from the line did not jump with the line: not a line this run can measure.
                Interrupt("The line moved but the distance from it did not change.", s);
            }
            return;
        }
        if (s.TimeS - _requestAt > NudgeTimeoutS)
        {
            // The move never showed (the nudge reset in the same cycle took it away): it is not on
            // the line, so there is nothing to move back. Moved back, it shifted the operator's line.
            _expectNudge -= _pendingShift;
            _outstanding -= _pendingShift;
            _pendingShift = 0;
            Interrupt("The line did not move as asked.", s);
        }
    }

    private void TrackMove(in LineTuneSample s)
    {
        double d = _e1 - _baseline;        // signed size of the move as the error saw it
        double dev = s.Xte - _baseline;    // where it is now, relative to the new line
        double r = dev / d;                // 1 at the move, 0 on the new line, < 0 past it
        double band = Math.Max(BandM, 0.1 * Math.Abs(d));
        double t = s.TimeS - _t0;

        if (!_risen && r <= 0.1)
        {
            _risen = true;
            _risenAt = t;
        }
        if (_risen && r < 0)
            _overshoot = Math.Max(_overshoot, -r * Math.Abs(d));
        if (Math.Abs(dev) > band)
        {
            int side = Math.Sign(r);
            if (_risen && side != _lastSide)
                _swings++;
            _lastSide = side;
            _lastOut = s.TimeS;
            _hold.Clear();
        }
        else
        {
            _hold.Add((s.TimeS, s.Xte));
        }

        // Gone half the move past the new line, or further from it than at the move: it will
        // swing on, so the value is cut before the swing grows (a late steering carries on
        // turning for a while after).
        bool runaway = Math.Abs(dev) > Math.Abs(d) + RunawayM || _overshoot > OvershootCutShare * Math.Abs(d);
        if (runaway || _swings >= CutSwings)
        {
            // Away from the line: the operator's values steer from this cycle.
            _act.SetGains(null);
            Finish(s, settled: false, cut: true);
            return;
        }
        double hold = HoldS * TimeFactor;
        _move.Add((s.TimeS, s.Xte));
        if (_risen && s.TimeS - _lastOut >= hold)
        {
            Finish(s, settled: true, cut: false);
            return;
        }
        if (t < MaxStepS * TimeFactor)
            return;
        // Time is up. A tractor running steady near the new line, though not within the band of
        // where it was before the move, has settled too: a steering with some slack near the
        // middle rests a few centimetres to one side or the other (one tractor at 6 km/h: 4 to
        // 7 cm on one side after a move one way, 3 to 9 on the other after the move back), so
        // where it was is not where it will be. The move is then measured against where it
        // rests. Only when the time is up: sooner, the turning point of a slow swing looks like
        // a rest; by now a swing has crossed that rest and is counted.
        double since = s.TimeS - hold;
        int from = _move.FindIndex(m => m.T >= since);
        if (_move[0].T <= since && IsSteady(_move, from, band, out double rest)
            && Math.Abs(rest - _baseline) <= SteadyMaxM)
            Finish(s, settled: true, cut: false, rest);
        else
            Finish(s, settled: false, cut: false);
    }

    /// <summary>The move so far (<see cref="_move"/>) measured against a line at
    /// <paramref name="line"/>, as <see cref="TrackMove"/> measures it against where the tractor
    /// was before the move.</summary>
    private void MeasureAgainst(double line)
    {
        double d = _e1 - line;
        double band = Math.Max(BandM, 0.1 * Math.Abs(d));
        _risen = false;
        _risenAt = 0;
        _overshoot = 0;
        _swings = 0;
        _lastSide = 1;
        _lastOut = _t0;
        foreach (var (time, e) in _move)
        {
            double dev = e - line, r = dev / d;
            if (!_risen && r <= 0.1)
            {
                _risen = true;
                _risenAt = time - _t0;
            }
            if (_risen && r < 0)
                _overshoot = Math.Max(_overshoot, -r * Math.Abs(d));
            if (Math.Abs(dev) > band)
            {
                int side = Math.Sign(r);
                if (_risen && side != _lastSide)
                    _swings++;
                _lastSide = side;
                _lastOut = time;
            }
        }
    }

    private void Finish(in LineTuneSample s, bool settled, bool cut, double? rest = null)
    {
        double offset = rest ?? 0;
        if (rest != null)
            MeasureAgainst(rest.Value);
        else if (_hold.Count > 0)
        {
            foreach (var h in _hold) offset += h.E;
            offset /= _hold.Count;
        }
        var step = new LineTuneStep(
            SizeM: Math.Abs(_e1 - _baseline),
            RiseS: _risen ? _risenAt : double.PositiveInfinity,
            OvershootM: _overshoot,
            SettleS: settled ? Math.Max(_risenAt, _lastOut - _t0) : double.PositiveInfinity,
            Swings: _swings,
            OffsetM: offset,
            Settled: settled,
            Cut: cut);
        if (_stepNo == 0) _level!.Out = step; else _level!.Back = step;
        Note(string.Create(Inv, $"{NameOf(_level)} {_level.Value:0.0#}, line {(_stepNo == 0 ? "out" : "back")}: moved {step.SizeM * 100:0} cm, ")
            + (_risen ? string.Create(Inv, $"there in {_risenAt:0.0} s, ") : "never got there, ")
            + string.Create(Inv, $"{step.OvershootM * 100:0} cm past, {step.Swings} swing(s), ")
            + (settled ? string.Create(Inv, $"settled in {step.SettleS:0.0} s {offset * 100:+0;-0;0} cm from the line{(rest != null ? string.Create(Inv, $" (where it rests; {(rest.Value - _baseline) * 100:+0;-0;0} cm from where it was before the move)") : "")}.")
                : cut ? "cut." : string.Create(Inv, $"not settled in {MaxStepS * TimeFactor:0} s ({s.Xte * 100:+0;-0;0} cm from the line).")));

        if (_stepNo == 0 && settled && !cut)
        {
            // Back from where it settled: that is the baseline of the second move.
            _baseline = offset;
            _stepNo = 1;
            RequestMove(s, -StepM);
            return;
        }
        if (!settled || cut)
        {
            _level.Failure = cut
                ? (_swings >= CutSwings ? "swung across the line" : RanAway)
                : _swings == 0 && _overshoot <= GoodOvershootShare * StepM ? SlowFailure
                : "did not settle in " + (MaxStepS * TimeFactor).ToString("0", Inv) + " s";
            _act.SetGains(null);
            // A slow value: the line goes back now. One that swung keeps the moved line until a
            // calm value has brought the tractor onto it (StartRecover): moving it under a
            // swinging tractor would be one more push.
            if (_level.Failure == SlowFailure)
                MoveBack(s.HeadingSameWay, s.TimeS);
        }
        EndLevel(s);
    }

    /// <summary>Move the line back where the run found it, at once (before a turn could change
    /// the driving direction and with it the sign the cycle gives a nudge).</summary>
    private void MoveBack(bool headingSameWay, double now)
    {
        if (Math.Abs(_outstanding) < 1e-9)
            return;
        _act.Nudge(headingSameWay ? -_outstanding : _outstanding);
        if (!double.IsNaN(_expectNudge))
            _expectNudge -= _outstanding;
        _pendingShift = -_outstanding;
        _outstanding = 0;
        _movingBackUntil = now + NudgeTimeoutS;
    }

    private void EndLevel(in LineTuneSample s)
    {
        var level = _level!;
        Levels.Add(level);
        if (level.Failure != null)
            Note(string.Create(Inv, $"{NameOf(level)} {level.Value:0.0#}: {level.Failure}."));
        if (!level.HeadingTrial)
            _byIndex[_index] = level;
        _level = null;
        bool more = NextLevel();
        if (level.Failure != null && level.Failure != SlowFailure)
        {
            // It swung or ran away: back to the line with a calm value before anything else.
            _concludePending = !more;
            StartRecover(s, level);
            return;
        }
        if (!more)
        {
            Conclude(s);
            return;
        }
        NextValue(s);
    }

    private void NextValue(in LineTuneSample s)
    {
        // The next value starts at once (the tractor is on the line), unless something waits.
        string? why = WhyNot(s);
        if (why != null || s.RoomAheadM < RoomNeeded(_speedMean))
        {
            _act.SetGains(null);
            Stage = LineTuneStage.Waiting;
            _joinStart = double.NaN;
            Description = why ?? "Not enough line before the headland: go on, on the next pass.";
            Note("Waiting: " + Description);
            return;
        }
        if (s.TimeS < _movingBackUntil)
        {
            // A move back is on its way: Waiting sees it arrive, then starts the value.
            _act.SetGains(null);
            Stage = LineTuneStage.Waiting;
            _joinStart = double.NaN;
            Description = "Moving the line back.";
            return;
        }
        BeginLevel(s);
    }

    /// <summary>The calm value after <paramref name="cut"/> (null: after the operator's own values swung).</summary>
    internal GuidanceGainsOverride RescueFor(LineTuneLevel? cut)
    {
        if (cut is { HeadingTrial: true } && Found != null)
            return Found;
        // Pure Pursuit: the calmest Steer Response there is. A swinging tractor on a steering that
        // answers late is brought back only by a long look-ahead; a short one, even one twice
        // the value that swung, swings it wider (the steering can't turn the wheels as fast as asked).
        // Stanley's distance gain is bounded by its arctangent: a share of the one that swung does.
        if (Stanley)
            return _operator with
            {
                StanleyDistanceGain = Math.Min(_operator.StanleyDistanceGain,
                    Math.Max(RescueMinStanley, (cut?.Gains ?? _operator).StanleyDistanceGain / RescueShare)),
            };
        return _operator with { LookAheadHold = Math.Max(HoldLadder[0], _operator.LookAheadHold) };
    }

    private void StartRecover(in LineTuneSample s, LineTuneLevel? cut)
    {
        var rescue = RescueFor(cut);
        _act.SetGains(rescue);
        Stage = LineTuneStage.Measuring;
        _phase = Phase.Recover;
        _phaseStart = s.TimeS;
        _recSide = 0;
        _recPeak = 0;
        _recLastPeak = 0;
        _window.Clear();
        _entryOff = RecentMaxOff(s);
        Description = cut == null
            ? string.Create(Inv, $"Your own values swing the tractor across the line ({Math.Abs(s.Xte):0.00} m past it): back to the line with {NameOf(cut, rescue)} first.")
            : string.Create(Inv, $"{NameOf(cut)} {cut.Value:0.0#} {cut.Failure}: back to the line with {NameOf(cut, rescue)} first.");
        Note(Description);
    }

    private void Recover(in LineTuneSample s)
    {
        // Swings: the widest point of each, from one side of the line to the other.
        bool growing = false;
        int side = Math.Abs(s.Xte) > SteadyMaxM ? Math.Sign(s.Xte) : 0;
        if (side != 0 && side != _recSide)
        {
            if (_recSide != 0)
            {
                growing = _recLastPeak > 0 && _recPeak > _recLastPeak + 0.05;
                _recLastPeak = _recPeak;
            }
            _recPeak = 0;
            _recSide = side;
        }
        _recPeak = Math.Max(_recPeak, Math.Abs(s.Xte));
        if (growing || Math.Abs(s.Xte) > Math.Max(_entryOff, SteadyMaxM) + RecoverRunawayM
            || s.TimeS - _phaseStart > RecoverTimeoutS * TimeFactor)
        {
            _act.SetGains(null);
            MoveBack(s.HeadingSameWay, s.TimeS);
            Stage = LineTuneStage.Stopped;
            Description = "";
            ResultText = "AutoSteer did not calm down beside the line: the run stopped. Your own values steer; take over if it swings. "
                + WheelsAdvice;
            NoteEnd();
            return;
        }
        // Calm is enough, on the line or beside it: the calm value asks for small steering
        // angles, and a steering with some slack near the middle then runs a hand's width or two
        // beside the line and stays there (a tractor: 0.3 m, until the wait ran out). The next
        // value, or the operator's own, brings it onto the line.
        if (!Steady(s, PreHoldS, double.PositiveInfinity, out _))
            return;
        if (Math.Abs(_outstanding) > 1e-9)
        {
            // On the moved line: now back to where it was, with the calm value still steering.
            MoveBack(s.HeadingSameWay, s.TimeS);
            _entryOff = Math.Abs(s.Xte) + StepM;
            _phaseStart = s.TimeS;
            _window.Clear();
            return;
        }
        if (s.TimeS < _movingBackUntil)
            return;
        if (_concludePending)
        {
            Conclude(s);
            return;
        }
        NextValue(s);
    }

    private string NameOf(LineTuneLevel l) => l.HeadingTrial ? "Overshoot Reduction" : Stanley ? "Aggressiveness" : "Steer Response";

    private string NameOf(LineTuneLevel? cut, GuidanceGainsOverride g) => Stanley
        ? (cut is { HeadingTrial: true }
            ? string.Create(Inv, $"Overshoot Reduction {g.StanleyHeadingGain:0.0#}")
            : string.Create(Inv, $"Aggressiveness {g.StanleyDistanceGain:0.0#}"))
        : string.Create(Inv, $"Steer Response {g.LookAheadHold:0.0#}");

    /// <summary>Choose the next value; false when the run is done.</summary>
    private bool NextLevel()
    {
        if (Levels.Count >= MaxLevels)
            return false;
        if (_headingTrial >= 0)
        {
            // Another heading trial only while the last one helped and it still goes past the line.
            var last = Levels[^1];
            var basis = Levels.First(l => !l.HeadingTrial && l.Gains == Found);
            if (_headingTrial + 1 < HeadingTrials.Length && last.Good(StepM)
                && last.OvershootM < basis.OvershootM && last.OvershootM > HeadingTrialShare * StepM
                && _operator.StanleyHeadingGain * HeadingTrials[_headingTrial + 1] <= MaxHeadingGain)
            {
                _headingTrial++;
                return true;
            }
            return false;
        }

        var cur = _byIndex[_index];
        bool good = cur.Good(StepM);
        bool anyGood = _byIndex.Values.Any(l => l.Good(StepM));
        bool quickerSideBad = _byIndex.Any(kv => kv.Key > _index && !kv.Value.Good(StepM) && !kv.Value.Sluggish(StepM));
        if (!good && cur.Sluggish(StepM))
        {
            // Too slow to get there: quicker, unless a quicker one was already too much.
            if (anyGood || _index + 1 >= _ladder.Length || _byIndex.ContainsKey(_index + 1))
                return false;
            _index++;
            return true;
        }
        if (!good)
        {
            if (anyGood)
                return false;          // going quicker, and this one is too much
            if (_index == 0 || _byIndex.ContainsKey(_index - 1))
                return false;          // nothing calmer to try
            // One that ran away is far from right: two values calmer.
            bool far = cur.Failure == RanAway && _index >= 2 && !_byIndex.ContainsKey(_index - 2);
            _index -= far ? 2 : 1;
            return true;
        }
        if (quickerSideBad)
        {
            // Came from the quick side: one calmer value too, for a margin (once).
            int goods = _byIndex.Values.Count(l => l.Good(StepM));
            if (goods == 1 && _index > 0 && !_byIndex.ContainsKey(_index - 1))
            {
                _index--;
                return true;
            }
            return false;
        }
        // Going quicker: on while each value is clearly quicker than the best before it.
        double bestBefore = _byIndex.Where(kv => kv.Key < _index && kv.Value.Good(StepM))
            .Select(kv => kv.Value.SettleS).DefaultIfEmpty(double.PositiveInfinity).Min();
        if (cur.SettleS > bestBefore * QuickerShare)
            return false;
        if (_index + 1 >= _ladder.Length || SameAsPrevious(_index + 1))
            return false;
        if (_byIndex.ContainsKey(_index + 1))
            return false;
        _index++;
        return true;
    }

    /// <summary>A Steer Response the minimum look-ahead makes the same as the calmer one before it.</summary>
    private bool SameAsPrevious(int index)
    {
        if (Stanley || index == 0 || RunSpeedKmh <= 0)
            return false;
        return Math.Abs(LookAhead(_ladder[index]) - LookAhead(_ladder[index - 1])) < 0.05;
    }

    private double LookAhead(double hold) =>
        Math.Max(_minLookAhead, RunSpeedKmh * 0.05 * _mult * hold + hold);

    private int NearestIndex(double value)
    {
        int best = 0;
        for (int i = 1; i < _ladder.Length; i++)
            if (Math.Abs(_ladder[i] - value) < Math.Abs(_ladder[best] - value)) best = i;
        return best;
    }

    private void Conclude(in LineTuneSample s)
    {
        _concludePending = false;
        _act.SetGains(null);
        MoveBack(s.HeadingSameWay, s.TimeS);
        if (_headingTrial >= 0)
        {
            // Heading trials: the one going least past the line, if it is not slower.
            var chosen = Found!;
            var basis = Levels.First(l => !l.HeadingTrial && l.Gains == chosen);
            var better = Levels.Where(l => l.HeadingTrial && l.Good(StepM)
                    && l.OvershootM < basis.OvershootM && l.SettleS <= basis.SettleS * ChoiceShare + ChoiceSlackS)
                .OrderBy(l => l.OvershootM).FirstOrDefault();
            if (better != null)
                Found = better.Gains;
            Done();
            return;
        }

        var goods = _byIndex.Where(kv => kv.Value.Good(StepM)).OrderBy(kv => kv.Key).ToList();
        // No value came back well, but some came back, both ways, without swinging on: the one
        // that went past the line the least is the answer, and the result says what it is. On
        // a steering with slack every value goes some way past the line (the wheels are not where
        // the sensor says), a calm one as far as a quick one: "no value found" told that
        // operator nothing, and left him on a value the run had measured as worse.
        var cameBack = _byIndex.Values.Where(l => l.CameBack).ToList();
        if (goods.Count == 0 && cameBack.Count > 0)
        {
            double least = cameBack.Min(l => l.OvershootM);
            Found = cameBack.Where(l => l.OvershootM <= least + LeastPastSlackM).OrderBy(l => l.SettleS).First().Gains;
            Done();
            return;
        }
        if (goods.Count == 0)
        {
            Stage = LineTuneStage.Failed;
            Description = "";
            ResultText = (Stanley
                    ? "No Stanley gain tried brought the tractor back to the line well"
                    : "No Steer Response tried brought the tractor back to the line well")
                + "; what each one did is below. Your own values steer. " + WheelsAdvice;
            NoteEnd();
            return;
        }
        double quickest = goods.Min(kv => kv.Value.SettleS);
        var pick = goods.First(kv => kv.Value.SettleS <= quickest * ChoiceShare + ChoiceSlackS);
        Found = pick.Value.Gains;

        if (Stanley && pick.Value.OvershootM > HeadingTrialShare * StepM && Levels.Count < MaxLevels
            && _operator.StanleyHeadingGain * HeadingTrials[0] <= MaxHeadingGain)
        {
            _headingTrial = 0;
            Stage = LineTuneStage.Waiting;
            _joinStart = double.NaN;
            Description = "Now the overshoot reduction.";
            return;
        }
        Done();
    }

    private void Done()
    {
        var f = Found!;
        var level = Levels.Last(l => l.Gains == f);
        Stage = LineTuneStage.Result;
        Description = "";
        var sb = new StringBuilder();
        if (Stanley)
        {
            sb.Append(string.Create(Inv, $"Aggressiveness {f.StanleyDistanceGain:0.0#} (now {_operator.StanleyDistanceGain:0.0#})"));
            if (Math.Abs(f.StanleyHeadingGain - _operator.StanleyHeadingGain) > 1e-9)
                sb.Append(string.Create(Inv, $", Overshoot Reduction {f.StanleyHeadingGain:0.0#} (now {_operator.StanleyHeadingGain:0.0#})"));
        }
        else
        {
            sb.Append(string.Create(Inv, $"Steer Response {f.LookAheadHold:0.0#} (now {_operator.LookAheadHold:0.0#})"));
        }
        sb.Append(string.Create(Inv,
            $": back on a line moved 30 cm in {level.SettleS:0.0} s, {level.OvershootM * 100:0} cm past it, at {RunSpeedKmh:0.0} km/h."));
        if (!level.Good(StepM))
            sb.Append(string.Create(Inv,
                $" No value tried came back without going more than {GoodOvershootShare * StepM * 100:0} cm past the line; this one went past it the least. ")
                + "A calmer value does not cure that: look for slack in the steering (play at the wheels or at the wheel angle sensor, a Minimum To Move that is too low).");
        // The speed is part of the answer: the look-ahead grows little with it, the steering's
        // delay does not shrink at all, so a value found slow goes past the line when driven fast.
        sb.Append(" If you work faster than that, measure again at that speed: a value found slower can swing there.");
        string? advice = OffsetAdvice();
        if (advice != null)
            sb.Append(' ').Append(advice);
        ResultText = sb.ToString();
        NoteEnd();
    }

    /// <summary>What to do when no value holds the line: the wheels' own loop is the wizard's job,
    /// and only if it is the wheels (the run can't tell; it said "first" to an operator who had just done it).</summary>
    private const string WheelsAdvice = "If the wheels themselves swing or answer late, check the module's steering settings in the Steer Wizard (Proportional Gain, Max Limit, Minimum To Move).";

    /// <summary>A steady offset from the line in every settled stretch: not the gains' job.</summary>
    private string? OffsetAdvice()
    {
        var offsets = Levels.SelectMany(l => new[] { l.Out, l.Back })
            .Where(st => st is { Settled: true }).Select(st => st!.OffsetM).ToList();
        if (offsets.Count < 2)
            return null;
        bool oneSide = offsets.All(o => o > OffsetAdviceM) || offsets.All(o => o < -OffsetAdviceM);
        if (!oneSide)
            return null;
        double mean = offsets.Average();
        return string.Create(Inv,
            $"It held the line {Math.Abs(mean) * 100:0} cm to one side every time: check the wheel angle sensor's zero (Smart WAS) first, then the Integral.");
    }

    // ------------------------------------------------------------ interruptions

    /// <summary>The value tried is dropped: the operator's values steer, a move is moved back, and the run waits.</summary>
    /// <param name="lasting">The reason is a state the wait itself goes on telling (the speed, a
    /// turn, the headland), not something that happened once (another line, a move that did not show).</param>
    private void Interrupt(string why, in LineTuneSample s, bool lasting = false)
    {
        if (Stage == LineTuneStage.Measuring)
        {
            _act.SetGains(null);
            MoveBack(s.HeadingSameWay, s.TimeS);
            if (_level != null)
            {
                _level.Out = null;
                _level.Back = null;
            }
            _again = true;
        }
        // What happened stays on the panel while the run waits; a state that still holds (too
        // fast, a turn, no fix) is said by the wait itself, once.
        _interrupt = lasting ? "" : why;
        Description = lasting ? WaitText(why) : why + (_again ? Again : "");
        Note("Waiting: " + Description);
        _joinStart = double.NaN;
        Stage = LineTuneStage.Waiting;
        _track = s.Track;
        _pass = s.Pass;
    }

    private const string Again = " This value starts again when AutoSteer holds the line.";

    /// <summary>The panel's text while the run waits for <paramref name="why"/> to pass.</summary>
    private string WaitText(string why) =>
        (_interrupt.Length > 0 ? _interrupt + " " : "") + why + (_again ? Again : "");

    private void Describe(string what)
    {
        int n = Levels.Count + 1;
        string value = _level!.HeadingTrial
            ? string.Create(Inv, $"Overshoot Reduction {_level.Value:0.0#}")
            : Stanley ? string.Create(Inv, $"Aggressiveness {_level.Value:0.0#}")
                      : string.Create(Inv, $"Steer Response {_level.Value:0.0#}");
        Description = string.Create(Inv, $"Trying {value} ({n} of up to {MaxLevels}): {what}.");
    }

    private string BuildMeasurements()
    {
        var sb = new StringBuilder();
        foreach (var l in Levels)
        {
            string name = l.HeadingTrial ? "Overshoot Reduction" : Stanley ? "Aggressiveness" : "Steer Response";
            sb.Append(string.Create(Inv, $"{name} {l.Value:0.0#}: "));
            if (l.Failure != null && (l.Out == null || l.Back == null || l.Out.Cut || l.Back.Cut || !l.Out.Settled || !l.Back.Settled))
                sb.Append(l.Failure);
            else
                sb.Append(string.Create(Inv,
                    $"{l.SettleS:0.0} s, {l.OvershootM * 100:0} cm past{(l.Swings == 0 ? "" : l.Swings == 1 ? ", 1 swing" : $", {l.Swings} swings")}{(l.Good(StepM) ? "" : " (too much)")}"));
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
