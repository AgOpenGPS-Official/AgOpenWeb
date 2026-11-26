// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 8.3: with auto-connect on, the default profile connects when the app starts; the
/// NTRIP on/off button connects it when switched on and disconnects when switched off.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class NtripConnectsOnStartTests
{
    private MainViewModelBuilder _b = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _b.NtripProfileService.LoadProfilesAsync().Returns(Task.CompletedTask);
        _b.NtripService.IsActive.Returns(false);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private NtripProfile DefaultProfile(bool autoConnect)
    {
        var p = new NtripProfile { Name = "Farm", CasterHost = "rtk.example.com", CasterPort = 2101, MountPoint = "VRS", IsDefault = true, AutoConnectOnFieldLoad = autoConnect };
        _b.NtripProfileService.DefaultProfile.Returns(p);
        _b.NtripProfileService.Profiles.Returns(new List<NtripProfile> { p });
        return p;
    }

    [Test]
    public void WithAutoConnectOn_TheDefaultProfileConnectsAtStart()
    {
        ConfigurationStore.Instance.Connections.NtripEnabled = true;
        DefaultProfile(autoConnect: true);
        _b.Build();
        _b.NtripService.Received(1).ConnectAsync(Arg.Is<NtripConfiguration>(c => c.CasterAddress == "rtk.example.com" && c.MountPoint == "VRS"));
    }

    [Test]
    public void WithAutoConnectOff_NothingConnectsAtStart()
    {
        ConfigurationStore.Instance.Connections.NtripEnabled = true;
        DefaultProfile(autoConnect: false);
        _b.Build();
        _b.NtripService.DidNotReceive().ConnectAsync(Arg.Any<NtripConfiguration>());
    }

    [Test]
    public void WithNtripSwitchedOff_NothingConnectsAtStart_EvenWithAutoConnectOn()
    {
        ConfigurationStore.Instance.Connections.NtripEnabled = false;
        DefaultProfile(autoConnect: true);
        _b.Build();
        _b.NtripService.DidNotReceive().ConnectAsync(Arg.Any<NtripConfiguration>());
    }

    [Test]
    public void TheOnOffButton_ConnectsTheDefaultProfile_AndDisconnects()
    {
        ConfigurationStore.Instance.Connections.NtripEnabled = false;
        DefaultProfile(autoConnect: false); // the button connects it even with auto-connect off
        var vm = _b.Build();
        _b.NtripService.DidNotReceive().ConnectAsync(Arg.Any<NtripConfiguration>());

        ConfigurationStore.Instance.Connections.NtripEnabled = true; // conn.ntripEnabled from the web
        _b.NtripService.Received(1).ConnectAsync(Arg.Any<NtripConfiguration>());

        _b.NtripService.IsActive.Returns(true);
        ConfigurationStore.Instance.Connections.NtripEnabled = false;
        _b.NtripService.Received(1).DisconnectAsync();
    }
}
