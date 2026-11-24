// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.LineTune;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests.LineTune;

/// <summary>
/// The <em>Tune on the line</em> panel's view model: it feeds the cycles to the run, moves the line
/// through the nudge intent, steers the values tried only through the pipeline's override, and
/// changes the settings only on Use these values.
/// </summary>
[TestFixture]
public class LineTuneViewModelTests
{
    private ConfigurationStore _store = null!;
    private IConfigurationService _config = null!;
    private IGpsPipelineService _pipeline = null!;
    private IPipelineIntents _intents = null!;
    private LineTuneViewModel _vm = null!;
    private LineTuneSim _sim = null!;
    private readonly object _track = new();

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        _store.Guidance.IsPurePursuit = true;
        _store.Guidance.GoalPointLookAheadHold = 4.0;
        _store.Guidance.GoalPointLookAheadMult = 1.4;
        _store.Guidance.GoalPointAcquireFactor = 0.9;
        _store.Guidance.MinLookAheadDistance = 2.0;
        _config = Substitute.For<IConfigurationService>();
        _config.Store.Returns(_store);
        _pipeline = Substitute.For<IGpsPipelineService>();
        _intents = Substitute.For<IPipelineIntents>();
        _sim = new LineTuneSim { Hold = 4.0, Mult = 1.4, Acquire = 0.9, MinLookAhead = 2.0 };
        // The VM's actions reach the simulated tractor, as they reach the pipeline.
        _intents.When(i => i.RequestGuidanceNudge(Arg.Any<double>())).Do(c => _sim.Nudge(c.Arg<double>()));
        _pipeline.When(p => p.SetGuidanceGainsOverride(Arg.Any<GuidanceGainsOverride?>()))
            .Do(c => _sim.SetGains(c.Arg<GuidanceGainsOverride?>()));
        _vm = new LineTuneViewModel(_pipeline, _intents, _config) { NowS = () => _sim.Time };
        for (int i = 0; i < 100; i++) _sim.Cycle();
    }

    private GpsCycleResult Cycle()
    {
        var s = _sim.Cycle();
        return new GpsCycleResult
        {
            GpsValid = true,
            FixQuality = 4,
            Speed = s.SpeedKmh / 3.6,
            IsAutoSteerEngaged = s.Engaged,
            Guidance = new GuidanceSnapshot
            {
                IsGuidanceActive = true,
                CrossTrackError = s.Xte,
                NudgeOffset = s.NudgeOffset,
                IsHeadingSameWay = s.HeadingSameWay,
                HowManyPathsAway = s.Pass,
            },
            YouTurn = new YouTurnSnapshot { IsEnabled = false },
        };
    }

    private void Run(double seconds)
    {
        for (double t = 0; t < seconds && (_vm.Active || t == 0); t += LineTuneSim.CycleS)
            _vm.OnCycle(Cycle(), _track);
    }

    [Test]
    public void A_run_finds_a_value_that_goes_into_the_settings_only_on_use()
    {
        _store.ActiveVehicleProfileName = "Maxxum";
        _vm.StartCommand.Execute(null);
        Assert.That(_vm.Active, Is.True);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0), "not before Use these values");
        Assert.That(_sim.Override, Is.Null);
        Assert.That(_vm.Measurements, Does.Contain("Steer Response 4.0"));

        // The value found is the one named in the result, and the one that goes into the settings.
        var found = System.Text.RegularExpressions.Regex.Match(_vm.ResultText, @"Steer Response (\d+(?:\.\d+)?)");
        Assert.That(found.Success, Is.True, _vm.ResultText);
        double proposed = double.Parse(found.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(proposed, Is.Not.EqualTo(4.0), "the scenario: the run found another value");

        _vm.AcceptCommand.Execute(null);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Accepted));
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(proposed).Within(0.006));
        _config.Received(1).SaveProfiles("Maxxum", Arg.Any<string>());
        Assert.That(_vm.ResultText, Does.Contain("saved with the vehicle Maxxum"));
    }

    [Test]
    public void What_the_run_did_goes_to_the_log()
    {
        // Nothing of a run was in a bug report: what it did had to be read from the positions.
        var log = new List<string>();
        _vm.Log = log.Add;
        _vm.StartCommand.Execute(null);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result));
        Assert.That(log[0], Does.StartWith("Started: Pure Pursuit, Steer Response 4.0"));
        Assert.That(log.Any(l => l.StartsWith("Ended (Result):")), Is.True);
        _vm.AcceptCommand.Execute(null);
        Assert.That(log[^1], Does.StartWith("In use now"));
    }

    [Test]
    public void Keep_the_old_values_changes_nothing()
    {
        _vm.StartCommand.Execute(null);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);
        _vm.DiscardCommand.Execute(null);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Accepted), "the result is closed");
        Assert.That(_vm.ResultText, Does.Contain("old values stay"));
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0));
        _config.DidNotReceive().SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
        // And it can't be taken afterwards.
        _vm.AcceptCommand.Execute(null);
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0));
    }

    [Test]
    public void The_integral_changed_during_the_run_stops_it()
    {
        _vm.StartCommand.Execute(null);
        Run(5);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Measuring));
        _store.Guidance.PurePursuitIntegralGain += 0.1;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(_sim.Override, Is.Null);
    }

    [Test]
    public void Another_vehicle_loaded_stops_the_run_whatever_its_values()
    {
        _store.ActiveVehicleProfileName = "Maxxum";
        _vm.StartCommand.Execute(null);
        Run(5);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Measuring));
        _store.ActiveVehicleProfileName = "Puma"; // the same guidance values: no setting changes
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(_sim.Override, Is.Null);
        Assert.That(_vm.ResultText, Does.Contain("vehicle profile"));
    }

    [Test]
    public void A_value_found_is_not_taken_after_another_vehicle_was_loaded_or_a_setting_changed([Values] bool vehicle)
    {
        _store.ActiveVehicleProfileName = "Maxxum";
        _vm.StartCommand.Execute(null);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);
        if (vehicle) _store.ActiveVehicleProfileName = "Puma";
        else _store.Guidance.GoalPointLookAheadMult = 1.6;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));

        _vm.AcceptCommand.Execute(null);
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0), "the value found is not written");
        _config.DidNotReceive().SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void A_save_that_fails_still_says_the_values_are_in_use()
    {
        _store.ActiveVehicleProfileName = "Maxxum";
        _config.When(c => c.SaveProfiles(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new System.IO.IOException("disk full"));
        _vm.StartCommand.Execute(null);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);

        Assert.DoesNotThrow(() => _vm.AcceptCommand.Execute(null));
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Accepted));
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.Not.EqualTo(4.0));
        Assert.That(_vm.ResultText, Does.Contain("In use now").And.Contain("not saved"));
    }

    [Test]
    public void A_steering_setting_changed_during_the_run_stops_it()
    {
        _vm.StartCommand.Execute(null);
        Run(5);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Measuring));
        _store.Guidance.GoalPointLookAheadHold = 3.3;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(_sim.Override, Is.Null);
        Assert.That(_vm.ResultText, Does.Contain("setting was changed"));
    }

    [Test]
    public void A_module_or_vehicle_steering_setting_changed_during_the_run_stops_it([Values] bool module)
    {
        // The sliders beside the panel: the module's gain, the largest steer angle. The wheels
        // answer another way after the change, so the values tried before and after don't compare.
        _vm.StartCommand.Execute(null);
        Run(5);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Measuring));
        if (module) _store.AutoSteer.ProportionalGain += 10;
        else _store.Vehicle.MaxSteerAngle -= 5;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(_sim.Override, Is.Null);
        Assert.That(_vm.ResultText, Does.Contain("setting was changed"));
    }

    [Test]
    public void A_value_found_is_not_taken_after_a_module_or_vehicle_steering_setting_changed([Values] bool module)
    {
        _store.ActiveVehicleProfileName = "Maxxum";
        _vm.StartCommand.Execute(null);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);
        if (module) _store.AutoSteer.MinPwm += 5;
        else _store.Vehicle.Wheelbase += 0.3;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));

        _vm.AcceptCommand.Execute(null);
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0), "the value found is not written");
    }

    [Test]
    public void A_setting_that_is_not_steering_leaves_the_run_alone()
    {
        _vm.StartCommand.Execute(null);
        Run(5);
        _store.AutoSteer.LightbarEnabled = !_store.AutoSteer.LightbarEnabled;
        _store.Vehicle.AntennaHeight += 0.1;
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Measuring));
    }

    [Test]
    public void The_browser_in_control_going_away_stops_the_run()
    {
        _vm.StartCommand.Execute(null);
        Run(5);
        _vm.OnControlEnded();
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Stopped));
        Assert.That(_sim.Override, Is.Null);
        _sim.Cycle();
        Assert.That(_sim.NudgeOffset, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void Stanley_tunes_the_Stanley_gains()
    {
        _store.Guidance.IsPurePursuit = false;
        _store.Guidance.StanleyDistanceErrorGain = 0.8;
        _store.Guidance.StanleyHeadingErrorGain = 1.0;
        _sim.Stanley = true;
        _vm.StartCommand.Execute(null);
        Assert.That(_vm.Stanley, Is.True);
        Run(900);
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Result), _vm.ResultText);
        _vm.AcceptCommand.Execute(null);
        Assert.That(_store.Guidance.GoalPointLookAheadHold, Is.EqualTo(4.0), "Steer Response untouched");
        Assert.That(_store.Guidance.StanleyDistanceErrorGain, Is.Not.EqualTo(0.8));
    }

    [Test]
    public void Contour_guidance_or_no_RTK_fix_waits()
    {
        _vm.StartCommand.Execute(null);
        for (int i = 0; i < 50; i++)
        {
            var r = Cycle() with { FixQuality = 5 };
            _vm.OnCycle(r, _track);
        }
        Assert.That(_vm.Stage, Is.EqualTo(LineTuneStage.Waiting));
        Assert.That(_vm.Description, Does.Contain("RTK"));
        _intents.DidNotReceive().RequestGuidanceNudge(Arg.Any<double>());
    }
}
