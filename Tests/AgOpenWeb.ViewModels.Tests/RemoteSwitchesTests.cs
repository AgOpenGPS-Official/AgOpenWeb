// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// PGN 234 (remote section switches, from a rate controller or a switch box) reaches the
/// master Auto button and the section buttons, with a field open only, as AgOpenGPS acts
/// with a job started only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class RemoteSwitchesTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private SectionControlState[] _states = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.NumSections = 2;

        _b = new MainViewModelBuilder
        {
            ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance),
        };
        _states = new[] { new SectionControlState { Index = 0 }, new SectionControlState { Index = 1 } };
        _b.SectionControlService.SectionStates.Returns(_states);
        _vm = _b.Build();
    }

    // Leave a default store behind: later fixtures read the singleton's section count.
    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private void Receive(byte main = 0, byte onLo = 0, byte offLo = 0)
    {
        var ss = new byte[9];
        ss[ModuleCommunicationService.SW_MAIN] = main;
        ss[ModuleCommunicationService.SW_ON_GR0] = onLo;
        ss[ModuleCommunicationService.SW_OFF_GR0] = offLo;
        _b.AutoSteerService.RemoteSwitchesReceived += Raise.Event<Action<byte[]>>(ss);
    }

    [Test]
    public void NoFieldOpen_NothingChanges()
    {
        Receive(main: 1, onLo: 0b11);

        Assert.That(_vm.IsSectionMasterOn, Is.False);
        _b.SectionControlService.DidNotReceive().SetSectionState(Arg.Any<int>(), Arg.Any<SectionButtonState>());
    }

    [Test]
    public void AutoOn_SetsTheMasterAndTheSectionsAuto()
    {
        _vm.IsFieldOpen = true;

        Receive(main: 1);

        Assert.That(_vm.IsSectionMasterOn, Is.True);
        Assert.That(_vm.IsManualSectionMode, Is.False);
        _b.SectionControlService.Received(1).SetSectionState(0, SectionButtonState.Auto);
        _b.SectionControlService.Received(1).SetSectionState(1, SectionButtonState.Auto);
    }

    [Test]
    public void OnlyChangedSections_AreSet()
    {
        _vm.IsFieldOpen = true;
        _states[0].ButtonState = SectionButtonState.On;

        Receive(onLo: 0b01, offLo: 0b10);

        _b.SectionControlService.DidNotReceive().SetSectionState(0, Arg.Any<SectionButtonState>());
        _b.SectionControlService.DidNotReceive().SetSectionState(1, Arg.Any<SectionButtonState>());
        Assert.That(_vm.IsSectionMasterOn, Is.False, "no main bits, the master stays");
    }
}
