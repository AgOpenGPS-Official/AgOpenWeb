// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.ViewModels.Wizards;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #154: the steer wizard's motor tests steer through Free Drive. When a test ended, PGN 254
/// went back to normal mode while AgOpenWeb's AutoSteer was engaged (by the steer switch or the
/// button) and sent the guidance angle — full right lock in the field. AutoSteer now stays off
/// while the wizard is open, every Free Drive hand-back turns it off, the wheels are back at
/// centre before the hand-back, and Start waits on the module being armed, not on AutoSteer.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class SteerWizardSafetyTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    // PGN 253 byte 11 bit 1 (SteerSwitchActive) is set when the module is NOT steering.
    private static SteerModuleData Module(bool steering, double was = 0) =>
        SteerModuleData.Empty with { ActualSteerAngle = was, SteerSwitchActive = !steering };

    // ── MainViewModel: AutoSteer lockout while the wizard is open ─────────

    [Test]
    public void Opening_the_wizard_disengages_AutoSteer()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerAvailable = true;
        vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(vm.IsAutoSteerEngaged, Is.True);

        vm.StartRemoteSteerWizard();

        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        b.AutoSteerService.Received().Disengage();
    }

    [Test]
    public void AutoSteer_button_cannot_engage_while_the_wizard_is_open()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerAvailable = true;
        string? msg = null; vm.FailureReported += m => msg = m;

        vm.StartRemoteSteerWizard();
        vm.ToggleAutoSteerCommand!.Execute(null);

        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(msg, Does.Contain("steer wizard"));
        b.AutoSteerService.DidNotReceive().Engage();

        vm.EndRemoteSteerWizard();
        vm.ToggleAutoSteerCommand.Execute(null);
        Assert.That(vm.IsAutoSteerEngaged, Is.True, "the lockout lifts when the wizard closes");
    }

    [Test]
    public void Steer_switch_cannot_engage_while_the_wizard_is_open()
    {
        var b = new MainViewModelBuilder();
        b.ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance);
        var vm = b.Build();
        ConfigurationStore.Instance.AutoSteer.ExternalEnable = 1; // switch
        vm.SelectedTrack = new AgOpenWeb.Models.Track.Track
        {
            Name = "AB",
            Points = new List<AgOpenWeb.Models.Base.Vec3> { new(0, 0, 0), new(0, 100, 0) },
        };
        int failures = 0; vm.FailureReported += _ => failures++;

        SwitchCycle(b, vm, steering: false);
        vm.StartRemoteSteerWizard();
        SwitchCycle(b, vm, steering: true);   // switch on / module armed by a Free Drive test

        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(failures, Is.Zero, "the module arming for a test is not an engage request");
        b.AutoSteerService.DidNotReceive().Engage();

        vm.EndRemoteSteerWizard();
        SwitchCycle(b, vm, steering: true);
        Assert.That(vm.IsAutoSteerEngaged, Is.False, "closing the wizard must not engage on a switch left on");
    }

    [Test]
    public void Closing_the_wizard_mid_test_releases_Free_Drive()
    {
        var b = new MainViewModelBuilder { ConfigurationService = Config() };
        var vm = b.Build();
        vm.StartRemoteSteerWizard();
        var w = vm.SteerWizardViewModel!;
        w.GoToStep(w.Steps.IndexOf(w.Steps.OfType<MaxSteeringAngleStepViewModel>().Single()));
        b.AutoSteerService.IsInFreeDriveMode.Returns(true);

        vm.EndRemoteSteerWizard();

        b.AutoSteerService.Received().DisableFreeDrive();
        Assert.That(vm.IsSteerWizardOpen, Is.False);
    }

    private static void SwitchCycle(MainViewModelBuilder b, MainViewModel vm, bool steering)
    {
        b.AutoSteerService.LastSteerData.Returns(Module(steering));
        typeof(MainViewModel).GetMethod("UpdateModuleSwitches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);
    }

    // ── Free Drive steps: arming gate and hand-back ──────────────────────

    private static IConfigurationService Config()
    {
        var c = Substitute.For<IConfigurationService>();
        c.Store.Returns(ConfigurationStore.Instance);
        return c;
    }

    private static MaxSteeringAngleStepViewModel MaxStep(IAutoSteerService autoSteer, List<int>? delays = null)
    {
        var step = new MaxSteeringAngleStepViewModel(Config(), new InlineUiDispatcher(), autoSteer);
        step.DelayFunc = (ms, _) => { delays?.Add(ms); return Task.CompletedTask; };
        return step;
    }

    [Test]
    public async Task Start_runs_with_the_module_armed_and_AgOpenWeb_AutoSteer_off()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.IsEngaged.Returns(false);
        autoSteer.LastSteerData.Returns(Module(steering: true));
        ConfigurationStore.Instance.Tool.IsSteerSwitchEnabled = true;
        var step = MaxStep(autoSteer);

        Assert.That(step.CanStartTest, Is.True);
        await step.RunMaxAngleMeasurementAsync();

        autoSteer.Received().SetFreeDriveAngle(60);
        autoSteer.DidNotReceive().Engage();
    }

    [Test]
    public async Task AIO_v4_not_steering_before_Free_Drive_does_not_block_Start()
    {
        // AIO v4 reports "not steering" while the app sends status 0; it arms once Free Drive
        // sends status 1. Arming is judged after the settle, not before Start.
        var autoSteer = Substitute.For<IAutoSteerService>();
        bool freeDrive = false;
        autoSteer.When(a => a.EnableFreeDrive()).Do(_ => freeDrive = true);
        autoSteer.LastSteerData.Returns(_ => Module(steering: freeDrive));
        var delays = new List<int>();
        var step = MaxStep(autoSteer, delays);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(delays[0], Is.EqualTo(SwitchGatedWizardStep.ArmSettleMs));
        autoSteer.Received().SetFreeDriveAngle(60);
    }

    [Test]
    public async Task Module_not_armed_after_Free_Drive_stops_before_moving_and_says_why()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: false));
        var step = MaxStep(autoSteer);

        await step.RunMaxAngleMeasurementAsync();

        autoSteer.DidNotReceive().SetFreeDriveAngle(60);
        autoSteer.Received().DisableFreeDrive();
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
        Assert.That(step.PhaseResult, Does.Contain("turn on the steer switch").And.Not.Contain("AutoSteer"));
    }

    [Test]
    public async Task Motor_test_not_armed_stops_and_says_why()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: false));
        var step = new AutoMotorCalibrationStepViewModel(Config(), new InlineUiDispatcher(), autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        await step.RunKpRampAsync();

        Assert.That(step.Phase, Is.EqualTo(CalibrationPhase.WaitingToStart));
        Assert.That(step.PhaseResult, Does.Contain("turn on the steer switch"));
        autoSteer.Received().DisableFreeDrive();
    }

    [Test]
    public async Task End_of_test_holds_centre_until_the_WAS_is_back_then_releases()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: true));
        var step = new Probe(autoSteer);
        double was = 20;
        step.Was = () => was;
        int polls = 0;
        step.DelayFunc = (_, _) => { polls++; was /= 3; return Task.CompletedTask; }; // 20 → 6.7 → 2.2 → 0.7

        Assume.That(await step.Begin(), Is.True);
        polls = 0; was = 20;
        bool releasedEarly = false;
        autoSteer.When(a => a.DisableFreeDrive()).Do(_ => releasedEarly = Math.Abs(was) > SwitchGatedWizardStep.CenterToleranceDeg);

        await step.End();

        Assert.That(polls, Is.EqualTo(3));
        Assert.That(releasedEarly, Is.False, "Free Drive must hold 0° until the wheels are centred");
        autoSteer.Received().DisableFreeDrive();
    }

    [Test]
    public async Task End_of_test_gives_up_waiting_for_centre_after_the_timeout()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: true));
        var step = new Probe(autoSteer) { Was = () => 12 }; // stuck
        int waited = 0;
        step.DelayFunc = (ms, _) => { waited += ms; return Task.CompletedTask; };
        await step.Begin();
        waited = 0;

        await step.End();

        Assert.That(waited, Is.EqualTo(SwitchGatedWizardStep.CenterHoldTimeoutMs));
        autoSteer.Received().DisableFreeDrive();
    }

    [Test]
    public async Task Hand_back_turns_an_engaged_AutoSteer_off_first()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: true));
        autoSteer.IsEngaged.Returns(true);
        var step = new Probe(autoSteer);
        await step.Begin();

        await step.End();

        Received.InOrder(() =>
        {
            autoSteer.Disengage();
            autoSteer.DisableFreeDrive();
        });
    }

    [Test]
    public async Task Hint_shows_only_when_the_module_stops_steering_during_a_test()
    {
        var autoSteer = Substitute.For<IAutoSteerService>();
        autoSteer.LastSteerData.Returns(Module(steering: false));
        var step = new Probe(autoSteer);
        Assert.That(step.RecordHint, Is.Empty, "before Start the bit says nothing on AIO v4");

        autoSteer.LastSteerData.Returns(Module(steering: true));
        await step.Begin();
        Assert.That(step.RecordHint, Is.Empty);

        autoSteer.LastSteerData.Returns(Module(steering: false)); // switch turned off mid-test
        Assert.That(step.RecordHint, Does.Contain("steer switch"));
    }

    /// <summary>Drives the shared Free Drive lifecycle directly.</summary>
    private sealed class Probe : SwitchGatedWizardStep
    {
        public Probe(IAutoSteerService autoSteer)
            : base(Config(), autoSteer, new InlineUiDispatcher()) =>
            DelayFunc = (_, _) => Task.CompletedTask;

        public Func<double>? Was { get; set; }
        public override string Title => "Probe";
        public override string Description => "";
        protected override double CurrentWasAngle => Was?.Invoke() ?? base.CurrentWasAngle;
        public Task<bool> Begin() => BeginFreeDriveAsync(CancellationToken.None);
        public Task End() => EndFreeDriveAsync();
    }
}
