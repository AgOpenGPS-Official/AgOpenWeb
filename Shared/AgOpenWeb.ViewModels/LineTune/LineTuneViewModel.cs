// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels.LineTune;

/// <summary>
/// The AutoSteer page's <em>Tune on the line</em> panel: runs a <see cref="LineTuneRun"/> on the
/// GPS cycles (<see cref="OnCycle"/>, host loop), moves the line through the same intent as the
/// nudge buttons, and steers the values it tries through the pipeline's gains override, so the
/// settings change only on <see cref="AcceptCommand"/>.
/// </summary>
public sealed partial class LineTuneViewModel : ObservableObject, ILineTuneActions
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IGpsPipelineService _pipeline;
    private readonly IPipelineIntents _intents;
    private readonly IConfigurationService _configService;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LineTuneRun? _run;
    private bool _settingByUs;

    /// <summary>Injectable clock (seconds) for tests.</summary>
    internal Func<double> NowS { get; set; }

    public LineTuneViewModel(IGpsPipelineService pipeline, IPipelineIntents intents, IConfigurationService configService)
    {
        _pipeline = pipeline;
        _intents = intents;
        _configService = configService;
        NowS = () => _clock.Elapsed.TotalSeconds;
        StartCommand = new RelayCommand(Start);
        StopCommand = new RelayCommand(() => _run?.Stop("Stopped."));
        AcceptCommand = new RelayCommand(Accept);
        DiscardCommand = new RelayCommand(Discard);
        Guidance.PropertyChanged += OnGuidanceSettingChanged;
    }

    private GuidanceConfig Guidance => _configService.Store.Guidance;

    public IRelayCommand StartCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand AcceptCommand { get; }
    public IRelayCommand DiscardCommand { get; }

    public LineTuneStage Stage => _run?.Stage ?? LineTuneStage.Idle;
    public bool Active => _run?.Active == true;
    /// <summary>Stanley steers (else Pure Pursuit): what the run tunes.</summary>
    public bool Stanley => _run?.Stanley ?? Guidance.IsStanley;
    public string Description => _run?.Description ?? "";
    public string ResultText => _run?.ResultText ?? "";
    public string Measurements => _run?.Measurements ?? "";
    /// <summary>Values tried so far.</summary>
    public int LevelsTried => _run?.Levels.Count ?? 0;

    private void Start()
    {
        if (Active)
            return;
        var g = Guidance;
        _run = new LineTuneRun(this,
            new GuidanceGainsOverride(g.GoalPointLookAheadHold, g.StanleyDistanceErrorGain, g.StanleyHeadingErrorGain),
            g.IsStanley, g.GoalPointLookAheadMult, g.GoalPointAcquireFactor, g.MinLookAheadDistance);
        _run.Start();
    }

    private void Accept()
    {
        if (_run is not { Stage: LineTuneStage.Result, Found: { } f })
            return;
        var g = Guidance;
        _settingByUs = true;
        try
        {
            if (_run.Stanley)
            {
                g.StanleyDistanceErrorGain = f.StanleyDistanceGain;
                g.StanleyHeadingErrorGain = f.StanleyHeadingGain;
            }
            else
            {
                g.GoalPointLookAheadHold = f.LookAheadHold;
            }
        }
        finally { _settingByUs = false; }
        var store = _configService.Store;
        store.MarkChanged();
        string vehicle = store.ActiveVehicleProfileName;
        string saved = "";
        if (!string.IsNullOrEmpty(vehicle))
        {
            _configService.SaveProfiles(vehicle, store.ActiveToolProfileName);
            saved = " and saved with the vehicle " + vehicle;
        }
        string values = _run.Stanley
            ? string.Create(Inv, $"Aggressiveness {f.StanleyDistanceGain:0.0#}, Overshoot Reduction {f.StanleyHeadingGain:0.0#}")
            : string.Create(Inv, $"Steer Response {f.LookAheadHold:0.0#}");
        _run.MarkAccepted($"In use now{saved}: {values}.");
    }

    private void Discard()
    {
        if (_run is not { Stage: LineTuneStage.Result })
            return;
        _run.MarkAccepted("The old values stay.");
    }

    /// <summary>The browser holding control went away: AutoSteer goes off, and the run ends.</summary>
    public void OnControlEnded() => _run?.Stop("The browser in control went away.");

    // A steering setting changed by hand while the run measures: what it measures is not
    // what the operator set any more.
    private void OnGuidanceSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settingByUs || !Active)
            return;
        switch (e.PropertyName)
        {
            case nameof(GuidanceConfig.IsPurePursuit):
            case nameof(GuidanceConfig.GoalPointLookAheadHold):
            case nameof(GuidanceConfig.GoalPointLookAheadMult):
            case nameof(GuidanceConfig.GoalPointAcquireFactor):
            case nameof(GuidanceConfig.MinLookAheadDistance):
            case nameof(GuidanceConfig.StanleyDistanceErrorGain):
            case nameof(GuidanceConfig.StanleyHeadingErrorGain):
                _run!.Stop("A steering setting was changed.");
                break;
        }
    }

    /// <summary>One GPS cycle, on the host loop (MainViewModel.ApplyGpsCycleResult).</summary>
    public void OnCycle(GpsCycleResult r, object? selectedTrack)
    {
        if (!Active)
            return;
        var g = r.Guidance;
        var turn = r.YouTurn;
        double room = turn is { IsEnabled: true } && turn.DistanceToHeadland < 1e6 ? turn.DistanceToHeadland : double.PositiveInfinity;
        bool fixOk = r.GpsValid && (r.FixQuality == 4 || r.SentenceType == GpsSentenceType.Simulator);
        var s = new LineTuneSample(
            TimeS: NowS(),
            Engaged: r.IsAutoSteerEngaged,
            SpeedKmh: Math.Abs(r.Speed) * 3.6,
            Xte: g?.CrossTrackError ?? 0,
            NudgeOffset: g?.NudgeOffset ?? 0,
            HeadingSameWay: g?.IsHeadingSameWay ?? true,
            Track: selectedTrack,
            Pass: g?.HowManyPathsAway ?? 0,
            Turning: turn is { IsExecuting: true },
            RoomAheadM: room,
            FixOk: fixOk,
            Reverse: r.IsReverse,
            Contour: g?.IsContourMode == true);
        _run!.OnSample(s);
    }

    // ------------------------------------------------------------ ILineTuneActions

    void ILineTuneActions.Nudge(double meters) => _intents.RequestGuidanceNudge(meters);

    void ILineTuneActions.SetGains(GuidanceGainsOverride? gains) => _pipeline.SetGuidanceGainsOverride(gains);
}
