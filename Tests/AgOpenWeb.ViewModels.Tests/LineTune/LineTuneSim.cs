// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Track;
using AgOpenWeb.ViewModels.LineTune;

namespace AgOpenWeb.ViewModels.Tests.LineTune;

/// <summary>
/// A tractor on an AB line for the tuning run: the real guidance (<see cref="TrackGuidanceService"/>,
/// Pure Pursuit or Stanley) at 10 Hz, a steering that answers late, turns no faster than it can
/// and settles with a lag, and a bicycle model at 100 Hz. Moves of the line and gains overrides
/// reach the next cycle, as the pipeline drains its intents at the start of a cycle.
/// </summary>
internal sealed class LineTuneSim : ILineTuneActions
{
    public const double CycleS = 0.1;
    private const int SubSteps = 10;

    // Machine
    public double Wheelbase = 3.0, MaxSteer = 35;
    /// <summary>Steering: seconds before it answers, degrees per second at most, and lag (s).</summary>
    public double DeadS = 0.10, RateDegS = 30, LagS = 0.10;
    public double SpeedKmh = 8;
    /// <summary>Position noise on the cross-track error (m, uniform ±).</summary>
    public double NoiseM;

    // Operator's settings
    public bool Stanley;
    public double Hold = 4.0, Mult = 1.4, Acquire = 0.9, MinLookAhead = 2.0, StanleyDist = 0.8, StanleyHead = 1.0;

    // What the operator / the field do
    public bool Engaged = true;
    public bool HeadingSameWay = true;
    public bool Turning;
    public double RoomAheadM = double.PositiveInfinity;
    public bool FixOk = true;
    public int Pass;
    public object Track = new();

    // The cycle's state
    public double NudgeOffset;
    private double _pendingNudge;
    public GuidanceGainsOverride? Override;
    public double Time;
    public double E, N, Heading, Wheel;
    private readonly Queue<(double T, double Cmd)> _cmds = new();
    private readonly TrackGuidanceService _svc = new();
    private TrackGuidanceState? _state;
    private double _lastXte;
    private readonly Random _rng = new(7);

    public int NudgeCalls;
    public readonly List<(double T, GuidanceGainsOverride? Gains)> GainsLog = new();
    public double LastXte => _lastXte;

    public LineTuneSim(double startOffsetM = 0)
    {
        E = startOffsetM;
    }

    public void Nudge(double meters) { _pendingNudge += meters; NudgeCalls++; }

    public void SetGains(GuidanceGainsOverride? gains) { Override = gains; GainsLog.Add((Time, gains)); }

    /// <summary>The operator's own nudge button.</summary>
    public void HandNudge(double meters) => _pendingNudge += meters;

    public double Hold_ => Override?.LookAheadHold ?? Hold;

    /// <summary>One GPS cycle: drain the moves, steer, drive 0.1 s; the sample the run sees.</summary>
    public LineTuneSample Cycle()
    {
        if (_pendingNudge != 0)
        {
            NudgeOffset += HeadingSameWay ? _pendingNudge : -_pendingNudge;
            _pendingNudge = 0;
            _state = null;
        }
        double hold = Override?.LookAheadHold ?? Hold;
        double sd = Override?.StanleyDistanceGain ?? StanleyDist;
        double sh = Override?.StanleyHeadingGain ?? StanleyHead;

        // GpsPipelineService.GoalLookAheadFor
        double x = Math.Abs(_lastXte);
        double acq = hold * Acquire;
        double h = x <= 0.1 ? hold : x >= 0.4 ? acq : acq + (1 - (x - 0.1) / 0.3) * (hold - acq);
        double la = Math.Max(MinLookAhead, SpeedKmh * 0.05 * Mult * h + h);

        double lineE = NudgeOffset;
        var line = new Track
        {
            Name = "AB", Type = TrackType.ABLine,
            Points = [new Vec3(lineE, -1000, 0), new Vec3(lineE, 100000, 0)],
        };
        var input = new TrackGuidanceInput
        {
            Track = line,
            PivotPosition = new Vec3(E, N, Heading),
            SteerPosition = new Vec3(E + Math.Sin(Heading) * Wheelbase, N + Math.Cos(Heading) * Wheelbase, Heading),
            UseStanley = Stanley,
            StanleyHeadingErrorGain = sh,
            StanleyDistanceErrorGain = sd,
            IsHeadingSameWay = HeadingSameWay,
            Wheelbase = Wheelbase,
            MaxSteerAngle = MaxSteer,
            GoalPointDistance = la,
            FixHeading = Heading,
            AvgSpeed = SpeedKmh,
            IsAutoSteerOn = true,
            PreviousState = _state,
            FindGlobalNearest = _state == null,
            CurrentLocationIndex = _state?.CurrentLocationIndex ?? 0,
        };
        var output = _svc.CalculateGuidance(input);
        _state = output.State;
        if (_state != null) _state.CurrentLocationIndex = output.CurrentLocationIndex;
        _lastXte = output.CrossTrackError;
        double cmd = Engaged ? Math.Clamp(output.SteerAngle, -MaxSteer, MaxSteer) : 0;
        _cmds.Enqueue((Time, cmd));

        double v = SpeedKmh / 3.6 * (HeadingSameWay ? 1 : 1);
        double dt = CycleS / SubSteps;
        double target = Wheel;
        for (int i = 0; i < SubSteps; i++)
        {
            double t = Time + i * dt;
            while (_cmds.Count > 0 && _cmds.Peek().T <= t - DeadS)
                target = _cmds.Dequeue().Cmd;
            double rate = (target - Wheel) / Math.Max(LagS, dt);
            rate = Math.Clamp(rate, -RateDegS, RateDegS);
            Wheel += rate * dt;
            Heading += v / Wheelbase * Math.Tan(Wheel * Math.PI / 180) * dt;
            E += v * Math.Sin(Heading) * dt;
            N += v * Math.Cos(Heading) * dt;
        }
        Time += CycleS;
        double noise = NoiseM > 0 ? (_rng.NextDouble() * 2 - 1) * NoiseM : 0;
        return new LineTuneSample(Time, Engaged, SpeedKmh, _lastXte + noise, NudgeOffset, HeadingSameWay,
            Track, Pass, Turning, RoomAheadM, FixOk, false, false);
    }

    /// <summary>Distance of the tractor from the line where the run found it (m, east +).</summary>
    public double FromHomeLine(double homeNudge) => E - homeNudge;
}
