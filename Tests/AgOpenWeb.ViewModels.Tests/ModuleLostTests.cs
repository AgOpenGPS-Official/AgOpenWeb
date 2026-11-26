// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The machine module or the ISOBUS task controller stops answering mid-job. From the nightly
/// harnesses (20 Nov 2026): the Machine Module harness's K4 (the sprayer module's Wi-Fi down
/// 30 s while spraying) painted 495 m² over 28 s with the valves shut, with nothing said
/// (MODULE-LOST-SILENT); the ISOBUS harness's K4 (the task controller gone 30 s) painted
/// 772 m² no implement was told about, with nothing said (ISOBUS-TC-LOST-SILENT).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ModuleLostTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private readonly List<string> _told = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        _told.Clear();
        _vm.FailureReported += _told.Add;
    }

    private void Spraying(bool on) => _b.SectionControlService.IsAnySectionOn.Returns(on);

    [Test]
    public void MachineModuleGoesSilentWhileSpraying_TheMapStopsPainting_AndTheOperatorIsTold()
    {
        Spraying(true);
        _vm.UpdateMachineModuleLost(machineOk: true);
        Assert.That(_b.SectionControlService.HoldPainting, Is.False);

        _vm.UpdateMachineModuleLost(machineOk: false);

        Assert.That(_b.SectionControlService.HoldPainting, Is.True, "the valves are shut: nothing is painted");
        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.StartWith("Machine module not answering"));
        _b.AudioService.Received(1).Play(SoundEffect.SectionOff);
        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());

        // Still silent: told once.
        _vm.UpdateMachineModuleLost(machineOk: false);
        Assert.That(_told, Has.Count.EqualTo(1));
    }

    [Test]
    public void MachineModuleAnswersAgain_PaintingResumes_AndTheOperatorIsTold()
    {
        Spraying(true);
        _vm.UpdateMachineModuleLost(true);
        _vm.UpdateMachineModuleLost(false);

        _vm.UpdateMachineModuleLost(true);

        Assert.That(_b.SectionControlService.HoldPainting, Is.False);
        Assert.That(_told, Has.Count.EqualTo(2));
        Assert.That(_told[1], Does.StartWith("Machine module answering again"));
    }

    [Test]
    public void NoMachineModuleEverHeard_NothingChanges()
    {
        // A set-up without a machine module (coverage only, or sections by hand): never held.
        Spraying(true);
        _vm.UpdateMachineModuleLost(false);
        _vm.UpdateMachineModuleLost(false);

        Assert.That(_b.SectionControlService.HoldPainting, Is.False);
        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void LostWhileIdle_HeldAtOnce_ToldWhenASectionGoesOn()
    {
        Spraying(false);
        _vm.UpdateMachineModuleLost(true);
        _vm.UpdateMachineModuleLost(false);
        Assert.That(_b.SectionControlService.HoldPainting, Is.True);
        Assert.That(_told, Is.Empty, "nothing is being sprayed");

        Spraying(true);
        _vm.UpdateMachineModuleLost(false);
        Assert.That(_told, Has.Count.EqualTo(1));
    }

    [Test]
    public void MachineModuleUntickedInNetworkIo_PaintsAgain()
    {
        Spraying(true);
        _vm.UpdateMachineModuleLost(true);
        _vm.UpdateMachineModuleLost(false);
        Assume.That(_b.SectionControlService.HoldPainting, Is.True);

        ConfigurationStore.Instance.Connections.IsMachineConfigured = false;
        _vm.UpdateMachineModuleLost(false);

        Assert.That(_b.SectionControlService.HoldPainting, Is.False, "the operator works without it");
    }

    [Test]
    public void TaskControllerGoesSilentWithSectionControlOn_TheOperatorIsTold_PaintingGoesOn()
    {
        _vm.UpdateIsobusLost(new IsobusStatus(true, true, 12, 1, 0));
        _vm.UpdateIsobusLost(default);

        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.StartWith("ISOBUS task controller not answering"));
        _b.AudioService.Received(1).Play(SoundEffect.SectionOff);
        Assert.That(_b.SectionControlService.HoldPainting, Is.False, "the implement keeps its last state: it may still be working");

        _vm.UpdateIsobusLost(default);
        Assert.That(_told, Has.Count.EqualTo(1), "told once");

        _vm.UpdateIsobusLost(new IsobusStatus(true, true, 12, 1, 0));
        Assert.That(_told, Has.Count.EqualTo(2));
        Assert.That(_told[1], Does.StartWith("ISOBUS task controller answering again"));
    }

    [Test]
    public void TaskControllerGoesSilentWithSectionControlOff_NothingSaid()
    {
        _vm.UpdateIsobusLost(new IsobusStatus(true, false, 12, 1, 0));
        _vm.UpdateIsobusLost(default);

        Assert.That(_told, Is.Empty);
    }
}
